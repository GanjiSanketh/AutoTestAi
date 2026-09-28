import { createServer } from 'node:http';
import { loadConfig } from './config.js';

const config = loadConfig();
const startedAt = new Date().toISOString();
let shuttingDown = false;

// Minimal health endpoint for Docker HEALTHCHECK / orchestrator probes.
const server = createServer((req, res) => {
  if (req.url === '/health') {
    res.writeHead(shuttingDown ? 503 : 200, { 'Content-Type': 'application/json' });
    res.end(
      JSON.stringify({
        status: shuttingDown ? 'draining' : 'healthy',
        workerId: config.workerId,
        taskQueue: config.taskQueue,
        startedAt,
      }),
    );
    return;
  }
  res.writeHead(404).end();
});

server.listen(config.healthPort, () => {
  // Structured startup log. Never log credentials or tokens here.
  console.log(
    JSON.stringify({
      level: 'info',
      msg: 'playwright worker started (Phase-0 skeleton)',
      workerId: config.workerId,
      apiBaseUrl: config.apiBaseUrl,
      temporalAddress: config.temporalAddress,
      taskQueue: config.taskQueue,
      healthPort: config.healthPort,
    }),
  );
});

function shutdown(signal: string): void {
  if (shuttingDown) return;
  shuttingDown = true;
  console.log(JSON.stringify({ level: 'info', msg: `received ${signal}, draining`, workerId: config.workerId }));
  server.close(() => process.exit(0));
  setTimeout(() => process.exit(0), 5000).unref();
}

process.on('SIGTERM', () => shutdown('SIGTERM'));
process.on('SIGINT', () => shutdown('SIGINT'));
