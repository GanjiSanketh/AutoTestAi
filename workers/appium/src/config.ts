import os from 'node:os';

/** Environment-driven mobile worker configuration. No secrets are logged. */
export interface MobileWorkerConfig {
  workerId: string;
  /** Stable grid identity. Defaults to the host name so scaled replicas
   * register distinctly without extra configuration. */
  workerKey: string;
  displayName: string;
  /** Worker type for grid registration. Always 'appium' for this worker. */
  workerType: string;
  /** Framework for grid registration. Always 'appium' for this worker. */
  framework: string;
  /** Max concurrent assignments. Enforced here and server-side. */
  capacity: number;
  /** Provisioning secret for grid registration. Empty runs standalone (local dev only). */
  provisioningToken: string;
  /** How the control plane reaches this worker for assignments. */
  callbackBaseUrl: string;
  heartbeatIntervalMs: number;
  version: string;
  apiBaseUrl: string;
  temporalAddress: string;
  taskQueue: string;
  healthPort: number;
  /** Shared token protecting the assignment API. Empty disables auth (local dev only). */
  apiToken: string;
  executionTimeoutMs: number;
  stepTimeoutMs: number;
  /**
   * Slice 3C-4A: worker-local Appium server endpoint reserved for the later
   * Docker/Appium sidecar architecture. Not user-configurable through any
   * project/test/execution API, and not consumed until the execution slice.
   */
  appiumServerUrl: string;
}

function numbered(name: string, fallback: number, min: number, max: number): number {
  const raw = process.env[name];
  if (!raw) return fallback;
  const parsed = Number(raw);
  if (!Number.isFinite(parsed)) return fallback;
  return Math.min(max, Math.max(min, Math.floor(parsed)));
}

export function loadMobileConfig(): MobileWorkerConfig {
  const hostname: string = process.env.HOSTNAME ?? os.hostname();
  const fallbackKey = `worker-${hostname.toLowerCase().replace(/[^a-z0-9._-]/g, '-').slice(0, 60) || 'local'}`;
  const healthPort = numbered('WORKER_HEALTH_PORT', 8091, 1, 65535);
  return {
    workerId: process.env.WORKER_ID ?? `worker-${Date.now().toString(36)}`,
    workerKey: process.env.WORKER_KEY ?? process.env.WORKER_ID ?? fallbackKey,
    displayName: process.env.WORKER_DISPLAY_NAME ?? process.env.WORKER_ID ?? fallbackKey,
    workerType: 'appium',
    framework: 'appium',
    capacity: numbered('WORKER_CAPACITY', 4, 1, 16),
    provisioningToken: process.env.GRID_PROVISIONING_TOKEN ?? '',
    callbackBaseUrl: process.env.WORKER_CALLBACK_URL ?? `http://localhost:${healthPort}`,
    heartbeatIntervalMs: numbered('WORKER_HEARTBEAT_INTERVAL_MS', 30000, 5000, 300000),
    version: process.env.WORKER_VERSION ?? 'phase3-slice3c-4a',
    apiBaseUrl: process.env.API_BASE_URL ?? 'http://localhost:5193',
    temporalAddress: process.env.TEMPORAL_ADDRESS ?? 'localhost:7233',
    taskQueue: process.env.TEMPORAL_TASK_QUEUE ?? 'autotestai-execution',
    healthPort,
    apiToken: process.env.WORKER_API_TOKEN ?? '',
    executionTimeoutMs: numbered('WORKER_EXECUTION_TIMEOUT_MS', 300000, 10000, 3600000),
    stepTimeoutMs: numbered('WORKER_STEP_TIMEOUT_MS', 30000, 1000, 300000),
    appiumServerUrl: process.env.APPIUM_SERVER_URL ?? 'http://localhost:4723',
  };
}
