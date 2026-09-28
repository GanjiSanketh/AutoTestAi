import { loadConfig } from './config.js';
import { createWorkerServer } from './server.js';

const config = loadConfig();
const startedAt = new Date().toISOString();
let shuttingDown = false;

const worker = createWorkerServer(config);

if (!config.apiToken) {
  // Category-only warning: never log the token itself.
  console.log(
    JSON.stringify({
      level: 'warning',
      msg: 'WORKER_API_TOKEN is empty: assignment API runs without authentication (local dev only)',
      workerId: config.workerId,
    }),
  );
}

function shutdown(signal: string): void {
  if (shuttingDown) return;
  shuttingDown = true;
  console.log(JSON.stringify({ level: 'info', msg: `received ${signal}, draining`, workerId: config.workerId }));
  void worker.close().then(() => process.exit(0));
  setTimeout(() => process.exit(0), 5000).unref();
}

process.on('SIGTERM', () => shutdown('SIGTERM'));
process.on('SIGINT', () => shutdown('SIGINT'));

void worker.listen().then(() => {
  console.log(
    JSON.stringify({
      level: 'info',
      msg: 'playwright worker started',
      workerId: config.workerId,
      apiBaseUrl: config.apiBaseUrl,
      temporalAddress: config.temporalAddress,
      taskQueue: config.taskQueue,
      browser: config.browser,
      startedAt,
    }),
  );
});
