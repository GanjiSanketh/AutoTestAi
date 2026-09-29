/**
 * Execution-grid client (Phase 2 Slice 9).
 *
 * Registers this worker with the control plane, then heartbeats on an
 * interval. The credential is held in memory only — never logged, never
 * written to disk. All failures degrade to standalone mode or retried
 * heartbeats; assignment execution itself is unchanged (Slice 5 contract).
 */

export interface GridRegistration {
  workerId: string;
  credential: string;
  heartbeatIntervalSeconds: number;
  leaseDurationSeconds: number;
}

export interface GridHeartbeatResponse {
  workerId: string;
  effectiveStatus: string;
  draining: boolean;
  serverTimeUnixMs: number;
  heartbeatIntervalSeconds: number;
}

export interface GridClientConfig {
  apiBaseUrl: string;
  workerKey: string;
  displayName: string;
  workerType: string;
  framework: string;
  browsers: string[];
  version: string;
  capacity: number;
  callbackBaseUrl: string;
  provisioningToken: string;
  heartbeatIntervalMs: number;
  activeAssignments: () => number;
  onDraining: (draining: boolean) => void;
  log: (level: 'debug' | 'info' | 'warning' | 'error', message: string) => void;
}

async function postJson(url: string, body: unknown, credential?: string): Promise<Response> {
  const headers: Record<string, string> = { 'Content-Type': 'application/json' };
  if (credential) headers['Authorization'] = `Bearer ${credential}`;
  return fetch(url, {
    method: 'POST',
    headers,
    body: JSON.stringify(body),
  });
}

export async function registerWorker(config: GridClientConfig): Promise<GridRegistration | null> {
  if (!config.provisioningToken) {
    config.log('warning', 'grid provisioning token is empty: running standalone (local dev only)');
    return null;
  }
  let response: Response;
  try {
    response = await postJson(
      `${config.apiBaseUrl}/api/v1/execution-grid/workers/register`,
      {
        workerKey: config.workerKey,
        displayName: config.displayName,
        workerType: config.workerType,
        framework: config.framework,
        browsers: config.browsers,
        version: config.version,
        capacity: config.capacity,
        baseUrl: config.callbackBaseUrl,
        provisioningToken: config.provisioningToken,
      },
    );
  } catch (error) {
    config.log('warning', `grid registration unreachable: ${error instanceof Error ? error.message : 'unknown'}`);
    return null;
  }
  if (!response.ok) {
    config.log('warning', `grid registration rejected (HTTP ${response.status})`);
    return null;
  }
  const body = (await response.json()) as Partial<GridRegistration>;
  if (!body.workerId || !body.credential) {
    config.log('warning', 'grid registration returned an invalid response');
    return null;
  }
  return {
    workerId: body.workerId,
    credential: body.credential,
    heartbeatIntervalSeconds: body.heartbeatIntervalSeconds ?? 30,
    leaseDurationSeconds: body.leaseDurationSeconds ?? 300,
  };
}

export async function sendHeartbeat(
  config: GridClientConfig,
  registration: GridRegistration,
): Promise<GridHeartbeatResponse | null> {
  let response: Response;
  try {
    response = await postJson(
      `${config.apiBaseUrl}/api/v1/execution-grid/workers/${registration.workerId}/heartbeat`,
      {
        capacity: config.capacity,
        activeAssignmentCount: config.activeAssignments(),
        version: config.version,
      },
      registration.credential,
    );
  } catch (error) {
    config.log('warning', `grid heartbeat unreachable: ${error instanceof Error ? error.message : 'unknown'}`);
    return null;
  }
  if (response.status === 401 || response.status === 403) {
    config.log('warning', `grid heartbeat rejected (HTTP ${response.status}): re-registering`);
    return null;
  }
  if (!response.ok) {
    config.log('warning', `grid heartbeat failed (HTTP ${response.status})`);
    return null;
  }
  const body = (await response.json()) as Partial<GridHeartbeatResponse>;
  if (!body.effectiveStatus) return null;
  return {
    workerId: registration.workerId,
    effectiveStatus: body.effectiveStatus,
    draining: body.draining ?? false,
    serverTimeUnixMs: body.serverTimeUnixMs ?? Date.now(),
    heartbeatIntervalSeconds: body.heartbeatIntervalSeconds ?? registration.heartbeatIntervalSeconds,
  };
}

/**
 * Runs register → heartbeat-loop until the stop flag is set. Re-registers
 * when the credential is rejected. Never throws: grid connectivity loss
 * must not kill assignment execution.
 */
export function startGridLoop(config: GridClientConfig): { stop: () => void } {
  let stopped = false;
  let timer: ReturnType<typeof setTimeout> | null = null;
  let registration: GridRegistration | null = null;

  async function tick(): Promise<void> {
    if (stopped) return;
    try {
      if (!registration) {
        registration = await registerWorker(config);
        if (!registration) {
          schedule(config.heartbeatIntervalMs);
          return;
        }
        config.log('info', `registered with execution grid as ${config.workerKey}`);
      }
      const beat = await sendHeartbeat(config, registration);
      if (!beat) {
        registration = null; // credential rejected or unreachable: re-register next tick
      } else {
        config.onDraining(beat.draining);
      }
    } catch (error) {
      config.log('warning', `grid loop error: ${error instanceof Error ? error.message : 'unknown'}`);
    }
    schedule(config.heartbeatIntervalMs);
  }

  function schedule(delayMs: number): void {
    if (stopped) return;
    timer = setTimeout(() => void tick(), Math.max(1000, delayMs));
  }

  schedule(1000);
  return {
    stop: () => {
      stopped = true;
      if (timer) clearTimeout(timer);
    },
  };
}
