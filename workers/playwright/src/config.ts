import os from 'node:os';

/** Environment-driven worker configuration. No secrets are logged. */
export interface WorkerConfig {
  workerId: string;
  /** Stable grid identity (Phase 2 Slice 9). Defaults to the host name so
   * scaled replicas register distinctly without extra configuration. */
  workerKey: string;
  displayName: string;
  /** Worker type for grid registration (Phase 2 Slice 9). */
  workerType: string;
  /** Framework for grid registration (Phase 2 Slice 9). */
  framework: string;
  /** Max concurrent assignments (Phase 2 Slice 9). Enforced here and server-side. */
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
  browser: 'chromium' | 'firefox' | 'webkit';
  executionTimeoutMs: number;
  stepTimeoutMs: number;
  /**
   * Slice 11: bound for the AI healing-suggest round trip (control plane
   * resolves the provider). A timeout is a controlled healing failure,
   * never an execution hang.
   */
  healingAiTimeoutMs: number;
}

function required(name: string, fallback?: string): string {
  const value = process.env[name] ?? fallback;
  if (!value) throw new Error(`Missing required environment variable ${name}.`);
  return value;
}

function numbered(name: string, fallback: number, min: number, max: number): number {
  const raw = process.env[name];
  if (!raw) return fallback;
  const parsed = Number(raw);
  if (!Number.isFinite(parsed)) return fallback;
  return Math.min(max, Math.max(min, Math.floor(parsed)));
}

export function loadConfig(): WorkerConfig {
  const browserRaw = (process.env.BROWSER ?? 'chromium').toLowerCase();
  const browser: WorkerConfig['browser'] =
    browserRaw === 'firefox' || browserRaw === 'webkit' ? browserRaw : 'chromium';
  const hostname: string = process.env.HOSTNAME ?? os.hostname();
  const fallbackKey = `worker-${hostname.toLowerCase().replace(/[^a-z0-9._-]/g, '-').slice(0, 60) || 'local'}`;
  const healthPort = numbered('WORKER_HEALTH_PORT', 8090, 1, 65535);
  return {
    workerId: process.env.WORKER_ID ?? `worker-${Date.now().toString(36)}`,
    workerKey: process.env.WORKER_KEY ?? process.env.WORKER_ID ?? fallbackKey,
    displayName: process.env.WORKER_DISPLAY_NAME ?? process.env.WORKER_ID ?? fallbackKey,
    workerType: process.env.WORKER_TYPE ?? 'playwright',
    framework: process.env.WORKER_FRAMEWORK ?? 'playwright',
    capacity: numbered('WORKER_CAPACITY', 4, 1, 16),
    provisioningToken: process.env.GRID_PROVISIONING_TOKEN ?? '',
    callbackBaseUrl: process.env.WORKER_CALLBACK_URL ?? `http://localhost:${healthPort}`,
    heartbeatIntervalMs: numbered('WORKER_HEARTBEAT_INTERVAL_MS', 30000, 5000, 300000),
    version: process.env.WORKER_VERSION ?? 'phase2-slice11',
    apiBaseUrl: required('API_BASE_URL', 'http://localhost:5193'),
    temporalAddress: required('TEMPORAL_ADDRESS', 'localhost:7233'),
    taskQueue: required('TEMPORAL_TASK_QUEUE', 'autotestai-execution'),
    healthPort: numbered('WORKER_HEALTH_PORT', 8090, 1, 65535),
    apiToken: process.env.WORKER_API_TOKEN ?? '',
    browser,
    executionTimeoutMs: numbered('WORKER_EXECUTION_TIMEOUT_MS', 300000, 10000, 3600000),
    stepTimeoutMs: numbered('WORKER_STEP_TIMEOUT_MS', 30000, 1000, 300000),
    healingAiTimeoutMs: numbered('SELF_HEALING_AI_TIMEOUT_MS', 15000, 1000, 60000),
  };
}
