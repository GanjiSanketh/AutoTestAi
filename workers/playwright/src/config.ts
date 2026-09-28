/** Environment-driven worker configuration. No secrets are logged. */
export interface WorkerConfig {
  workerId: string;
  apiBaseUrl: string;
  temporalAddress: string;
  taskQueue: string;
  healthPort: number;
  /** Shared token protecting the assignment API. Empty disables auth (local dev only). */
  apiToken: string;
  browser: 'chromium' | 'firefox' | 'webkit';
  executionTimeoutMs: number;
  stepTimeoutMs: number;
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
  return {
    workerId: process.env.WORKER_ID ?? `worker-${Date.now().toString(36)}`,
    apiBaseUrl: required('API_BASE_URL', 'http://localhost:5193'),
    temporalAddress: required('TEMPORAL_ADDRESS', 'localhost:7233'),
    taskQueue: required('TEMPORAL_TASK_QUEUE', 'autotestai-execution'),
    healthPort: numbered('WORKER_HEALTH_PORT', 8090, 1, 65535),
    apiToken: process.env.WORKER_API_TOKEN ?? '',
    browser,
    executionTimeoutMs: numbered('WORKER_EXECUTION_TIMEOUT_MS', 300000, 10000, 3600000),
    stepTimeoutMs: numbered('WORKER_STEP_TIMEOUT_MS', 30000, 1000, 300000),
  };
}
