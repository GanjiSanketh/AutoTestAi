import { afterEach, describe, expect, it } from 'vitest';
import { createMobileWorkerServer } from '../src/server.js';
import type { MobileWorkerConfig } from '../src/config.js';
import type { MobileAssignment } from '../src/types.js';

function config(overrides: Partial<MobileWorkerConfig> = {}): MobileWorkerConfig {
  return {
    workerId: 'test-worker',
    workerKey: 'test-worker',
    displayName: 'test-worker',
    workerType: 'appium',
    framework: 'appium',
    capacity: 4,
    provisioningToken: '',
    callbackBaseUrl: 'http://localhost:8091',
    heartbeatIntervalMs: 30000,
    version: 'phase3-slice3c-4a',
    apiBaseUrl: 'http://localhost:5193',
    temporalAddress: 'localhost:7233',
    taskQueue: 'autotestai-execution',
    healthPort: 0,
    apiToken: 'test-token',
    executionTimeoutMs: 30000,
    stepTimeoutMs: 5000,
    appiumServerUrl: 'http://localhost:4723',
    ...overrides,
  };
}

function assignment(overrides: Partial<MobileAssignment> = {}): MobileAssignment {
  return {
    assignmentId: 'a1',
    executionId: 'e1',
    framework: 'appium',
    platform: 'android',
    device: { udid: 'emulator-5554' },
    app: { packageId: 'com.example.shop', installPolicy: 'Preinstalled' },
    capabilities: {
      platformName: 'Android',
      automationName: 'UiAutomator2',
      deviceName: null,
      udid: 'emulator-5554',
      appPackage: 'com.example.shop',
      appActivity: null,
      bundleId: null,
      app: null,
      noReset: true,
      fullReset: false,
      newCommandTimeout: 120,
    },
    steps: [{ order: 1, action: 'launchApp', target: null, value: null }],
    timeouts: { executionMs: 30000, stepMs: 5000 },
    screenshotOnFailure: true,
    assignmentToken: 'token-1',
    ...overrides,
  };
}

let port = 18200;
const servers: Array<() => Promise<void>> = [];

afterEach(async () => {
  while (servers.length > 0) {
    const close = servers.pop();
    if (close) await close();
  }
});

async function boot(cfg: MobileWorkerConfig): Promise<string> {
  const server = createMobileWorkerServer(cfg);
  port += 1;
  await server.listen(port);
  servers.push(() => server.close());
  return `http://localhost:${port}`;
}

function authHeaders(token: string): Record<string, string> {
  return { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' };
}

async function waitForResult(
  base: string,
  id: string,
): Promise<{ result: { status: string; classification: string; errorType: string } }> {
  for (let i = 0; i < 50; i += 1) {
    const response = await fetch(`${base}/v1/assignments/${id}`, {
      headers: authHeaders('test-token'),
    });
    const body = (await response.json()) as { result?: {
      status: string;
      classification: string;
      errorType: string;
    } | null };
    if (body.result) return body as never;
    await new Promise((resolve) => setTimeout(resolve, 20));
  }
  throw new Error('Timed out waiting for assignment result.');
}

describe('mobile worker assignment API', () => {
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

  it('rejects non-appium frameworks', async () => {
    const base = await boot(config());
    const response = await fetch(`${base}/v1/assignments`, {
      method: 'POST',
      headers: authHeaders('test-token'),
      body: JSON.stringify({ ...assignment(), framework: 'playwright' }),
    });
    expect(response.status).toBe(400);
  });

  it('rejects unknown actions without executing anything', async () => {
    const base = await boot(config());
    const response = await fetch(`${base}/v1/assignments`, {
      method: 'POST',
      headers: authHeaders('test-token'),
      body: JSON.stringify({
        ...assignment(),
        steps: [{ order: 1, action: 'executeScript', target: null, value: null }],
      }),
    });
    expect(response.status).toBe(400);
    const body = (await response.json()) as { details: string[] };
    expect(body.details.length).toBeGreaterThan(0);
  });

  it('rejects tap without an explicit locator strategy', async () => {
    const base = await boot(config());
    const response = await fetch(`${base}/v1/assignments`, {
      method: 'POST',
      headers: authHeaders('test-token'),
      body: JSON.stringify({
        ...assignment(),
        steps: [{ order: 1, action: 'tap', target: '#login', value: null }],
      }),
    });
    expect(response.status).toBe(400);
  });

  it('accepts a valid envelope and reports deferred error, never success', async () => {
    const base = await boot(config());
    const response = await fetch(`${base}/v1/assignments`, {
      method: 'POST',
      headers: authHeaders('test-token'),
      body: JSON.stringify(assignment()),
    });
    expect(response.status).toBe(202);
    const { result } = await waitForResult(base, 'a1');
    expect(result.status).toBe('error');
    expect(result.classification).toBe('automation');
    expect(result.errorType).toBe('NotImplemented');
    expect(result.status).not.toBe('passed');
  });

  it('returns existing progress for duplicate submissions', async () => {
    const base = await boot(config());
    const headers = authHeaders('test-token');
    const first = await fetch(`${base}/v1/assignments`, {
      method: 'POST',
      headers,
      body: JSON.stringify(assignment({ assignmentId: 'dup-1' })),
    });
    expect(first.status).toBe(202);
    const second = await fetch(`${base}/v1/assignments`, {
      method: 'POST',
      headers,
      body: JSON.stringify(assignment({ assignmentId: 'dup-1' })),
    });
    expect(second.status).toBe(200);
  });

  it('aborts via DELETE and reports cancellation', async () => {
    const base = await boot(config());
    const headers = authHeaders('test-token');
    await fetch(`${base}/v1/assignments`, {
      method: 'POST',
      headers,
      body: JSON.stringify(assignment({ assignmentId: 'cancel-1' })),
    });
    const response = await fetch(`${base}/v1/assignments/cancel-1`, { method: 'DELETE', headers });
    expect(response.status).toBe(200);
  });

  it('refuses oversized bodies', async () => {
    const base = await boot(config());
    let accepted = false;
    try {
      const response = await fetch(`${base}/v1/assignments`, {
        method: 'POST',
        headers: authHeaders('test-token'),
        body: 'x'.repeat(5 * 1024 * 1024),
      });
      accepted = response.status >= 200 && response.status < 300;
    } catch {
      accepted = false;
    }
    // Either a 4xx rejection or a reset connection: the body is never buffered.
    expect(accepted).toBe(false);
  });
});
