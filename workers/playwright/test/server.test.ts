import { afterEach, describe, expect, it } from 'vitest';
import { createWorkerServer } from '../src/server.js';
import type { WorkerConfig } from '../src/config.js';

function config(overrides: Partial<WorkerConfig> = {}): WorkerConfig {
  return {
    workerId: 'test-worker',
    apiBaseUrl: 'http://localhost:5193',
    temporalAddress: 'localhost:7233',
    taskQueue: 'autotestai-execution',
    healthPort: 0,
    apiToken: 'test-token',
    browser: 'chromium',
    executionTimeoutMs: 30000,
    stepTimeoutMs: 5000,
    ...overrides,
  };
}

let port = 18100;
const servers: Array<() => Promise<void>> = [];

afterEach(async () => {
  while (servers.length > 0) {
    const close = servers.pop();
    if (close) await close();
  }
});

async function boot(cfg: WorkerConfig): Promise<string> {
  const server = createWorkerServer(cfg);
  port += 1;
  await server.listen(port);
  servers.push(() => server.close());
  return `http://localhost:${port}`;
}

function authHeaders(token: string): Record<string, string> {
  return { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' };
}

describe('worker assignment API', () => {
  it('serves health without authentication', async () => {
    const base = await boot(config());
    const response = await fetch(`${base}/health`);
    expect(response.status).toBe(200);
    const body = (await response.json()) as { status: string };
    expect(body.status).toBe('healthy');
  });

  it('rejects unauthenticated assignment requests', async () => {
    const base = await boot(config());
    const response = await fetch(`${base}/v1/assignments`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ assignmentId: 'x' }),
    });
    expect(response.status).toBe(401);
  });

  it('rejects malformed assignments with a clear contract error', async () => {
    const base = await boot(config());
    const response = await fetch(`${base}/v1/assignments`, {
      method: 'POST',
      headers: authHeaders('test-token'),
      body: JSON.stringify({ assignmentId: 'bad-1', executionId: 'e1', browser: 'chromium', steps: [] }),
    });
    expect(response.status).toBe(400);
    const body = (await response.json()) as { details: string[] };
    expect(body.details.length).toBeGreaterThan(0);
  });

  it('rejects unknown actions at the boundary without launching a browser', async () => {
    const base = await boot(config());
    const headers = authHeaders('test-token');
    const created = await fetch(`${base}/v1/assignments`, {
      method: 'POST',
      headers,
      body: JSON.stringify({
        assignmentId: 'auto-1',
        executionId: 'exec-1',
        framework: 'playwright',
        browser: 'chromium',
        steps: [{ order: 1, action: 'eval', target: 'alert(1)' }],
        timeouts: { executionMs: 10000, stepMs: 2000 },
        screenshotOnFailure: false,
        screenshotOnFinish: false,
      }),
    });
    expect(created.status).toBe(400);
    const body = (await created.json()) as { details: string[] };
    expect(body.details.some((d) => d.includes('unsupported action'))).toBe(true);
  });

  it('returns 404 for unknown assignments', async () => {
    const base = await boot(config());
    const response = await fetch(`${base}/v1/assignments/nope`, {
      headers: authHeaders('test-token'),
    });
    expect(response.status).toBe(404);
  });

  it('redacts password values at the boundary', async () => {
    const base = await boot(config());
    const headers = authHeaders('test-token');
    await fetch(`${base}/v1/assignments`, {
      method: 'POST',
      headers,
      body: JSON.stringify({
        assignmentId: 'redact-1',
        executionId: 'exec-1',
        framework: 'playwright',
        browser: 'chromium',
        steps: [{ order: 1, action: 'fill', target: '#password', value: 'hunter2' }],
        timeouts: { executionMs: 10000, stepMs: 2000 },
        screenshotOnFailure: false,
        screenshotOnFinish: false,
      }),
    });
    // The stored progress must never contain the plaintext secret, even in logs.
    const response = await fetch(`${base}/v1/assignments/redact-1`, { headers });
    const body = (await response.json()) as { logs: Array<{ message: string }> };
    expect(JSON.stringify(body)).not.toContain('hunter2');
  });
});
