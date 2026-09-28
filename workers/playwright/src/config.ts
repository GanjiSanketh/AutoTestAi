/** Environment-driven worker configuration. No secrets are logged. */
export interface WorkerConfig {
  workerId: string;
  apiBaseUrl: string;
  temporalAddress: string;
  taskQueue: string;
  healthPort: number;
}

function required(name: string, fallback?: string): string {
  const value = process.env[name] ?? fallback;
  if (!value) throw new Error(`Missing required environment variable ${name}.`);
  return value;
}

export function loadConfig(): WorkerConfig {
  return {
    workerId: process.env.WORKER_ID ?? `worker-${Date.now().toString(36)}`,
    apiBaseUrl: required('API_BASE_URL', 'http://localhost:5193'),
    temporalAddress: required('TEMPORAL_ADDRESS', 'localhost:7233'),
    taskQueue: required('TEMPORAL_TASK_QUEUE', 'autotestai-execution'),
    healthPort: Number(process.env.WORKER_HEALTH_PORT ?? 8090),
  };
}
