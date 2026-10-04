import { afterEach, describe, expect, it, vi } from 'vitest';
import { createMobileWorkerServer } from '../src/server.js';
import type { IMobileDriver, SessionHandle } from '../src/driver.js';
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

async function boot(cfg: MobileWorkerConfig, driver?: IMobileDriver): Promise<string> {
  const server = createMobileWorkerServer(
    cfg,
    driver ? { createDriver: () => driver } : undefined,
  );
  port += 1;
  await server.listen(port);
  servers.push(() => server.close());
  return `http://localhost:${port}`;
}

/** Deterministic fake driver: scripted session + action behavior, no Appium server. */
function fakeDriver(options?: {
  failCreate?: unknown;
  deleteCalls?: string[];
  neverResolve?: boolean;
  failAction?: { action: string; error: unknown };
  calls?: string[];
  screenshotBase64?: string;
}): IMobileDriver {
  const sessions = new Set<string>();
  let counter = 0;
  const maybeFail = (action: string): void => {
    options?.calls?.push(action);
    if (options?.failAction?.action === action) throw options.failAction.error;
  };
  return {
    async createSession(): Promise<SessionHandle> {
      if (options?.neverResolve) {
        await new Promise(() => undefined);
      }
      if (options?.failCreate !== undefined) throw options.failCreate;
      counter += 1;
      const sessionId = `fake-session-${counter}`;
      sessions.add(sessionId);
      return { sessionId };
    },
    async deleteSession(sessionId: string): Promise<void> {
      options?.deleteCalls?.push(sessionId);
      sessions.delete(sessionId);
    },
    hasSession: (sessionId: string) => sessions.has(sessionId),
    async tap(): Promise<void> {
      maybeFail('tap');
    },
    async setText(): Promise<void> {
      maybeFail('inputText');
    },
    async clearText(): Promise<void> {
      maybeFail('clearText');
    },
    async isDisplayed(): Promise<boolean> {
      maybeFail('assertVisible');
      return true;
    },
    async getText(): Promise<string> {
      maybeFail('assertText');
      return 'fake text content';
    },
    async swipe(): Promise<void> {
      maybeFail('swipe');
    },
    async pressBack(): Promise<void> {
      maybeFail('back');
    },
    async hideKeyboard(): Promise<'closed' | 'absent'> {
      maybeFail('hideKeyboard');
      return 'closed';
    },
    async takeScreenshot(): Promise<string> {
      maybeFail('screenshot');
      return options?.screenshotBase64 ?? Buffer.from('fake-png').toString('base64');
    },
    async getPageSource(): Promise<string> {
      maybeFail('pagesource');
      return '<hierarchy><node text="fake" /></hierarchy>';
    },
    async activateApp(): Promise<void> {
      maybeFail('launchApp');
    },
    async terminateApp(): Promise<void> {
      maybeFail('terminateApp');
    },
  };
}

async function waitForSession(
  base: string,
  id: string,
): Promise<{ status: string; appiumSessionId: string | null }> {
  for (let i = 0; i < 50; i += 1) {
    const response = await fetch(`${base}/v1/assignments/${id}`, {
      headers: authHeaders('test-token'),
    });
    const body = (await response.json()) as {
      status: string;
      appiumSessionId?: string | null;
      result?: { appiumSessionId?: string | null } | null;
    };
    // The transient progress field clears after session close; the terminal
    // result always carries the session id for control-plane binding.
    if (body.appiumSessionId ?? body.result?.appiumSessionId) {
      return { status: body.status, appiumSessionId: (body.appiumSessionId ?? body.result?.appiumSessionId) ?? null };
    }
    await new Promise((resolve) => setTimeout(resolve, 20));
  }
  throw new Error('Timed out waiting for session establishment.');
}

function authHeaders(token: string): Record<string, string> {
  return { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' };
}

async function waitForResult(
  base: string,
  id: string,
): Promise<{ result: {
  status: string;
  classification: string;
  errorType: string;
  pageSources?: Array<{ stepOrder: number | null; fileName: string; contentType: string; xmlContent: string }>;
  serverLogs?: Array<{ fileName: string; contentType: string; textContent: string }>;
} }> {
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

  it('establishes a session, executes steps, and reports passed', async () => {
    const deleteCalls: string[] = [];
    const base = await boot(config(), fakeDriver({ deleteCalls }));
    const response = await fetch(`${base}/v1/assignments`, {
      method: 'POST',
      headers: authHeaders('test-token'),
      body: JSON.stringify(assignment()),
    });
    expect(response.status).toBe(202);
    const progress = await waitForSession(base, 'a1');
    expect(progress.appiumSessionId).toBe('fake-session-1');
    const { result } = await waitForResult(base, 'a1');
    expect(result.status).toBe('passed');
    expect(deleteCalls).toEqual(['fake-session-1']);
  });

  it('maps action failure to a failed test result, never success', async () => {
    const base = await boot(
      config(),
      fakeDriver({ failAction: { action: 'launchApp', error: { kind: 'test', message: 'element is not interactable' } } }),
    );
    const response = await fetch(`${base}/v1/assignments`, {
      method: 'POST',
      headers: authHeaders('test-token'),
      body: JSON.stringify(assignment({ assignmentId: 'action-fail-1' })),
    });
    expect(response.status).toBe(202);
    const { result } = await waitForResult(base, 'action-fail-1');
    expect(result.status).toBe('failed');
    expect(result.classification).toBe('test');
    expect(result.status).not.toBe('passed');
  });

  it('attaches bounded page-source and log-tail evidence to failed results', async () => {
    const base = await boot(
      config(),
      fakeDriver({ failAction: { action: 'launchApp', error: { kind: 'test', message: 'element is not interactable' } } }),
    );
    const response = await fetch(`${base}/v1/assignments`, {
      method: 'POST',
      headers: authHeaders('test-token'),
      body: JSON.stringify(assignment({ assignmentId: 'evidence-1' })),
    });
    expect(response.status).toBe(202);
    const { result } = await waitForResult(base, 'evidence-1');
    expect(result.status).toBe('failed');
    expect(result.pageSources).toHaveLength(1);
    expect(result.pageSources![0]).toMatchObject({
      stepOrder: 1,
      fileName: 'step-1-pagesource.xml',
      contentType: 'text/xml',
    });
    expect(result.pageSources![0]!.xmlContent.length).toBeGreaterThan(0);
    expect(result.serverLogs).toHaveLength(1);
    expect(result.serverLogs![0]).toMatchObject({ fileName: 'appium.log', contentType: 'text/plain' });
    expect(result.serverLogs![0]!.textContent.length).toBeGreaterThan(0);
    // The assignment token authenticates the API but must never survive
    // in persisted evidence.
    expect(JSON.stringify(result)).not.toContain('token-1');
  });

  it('passed results carry no failure evidence', async () => {
    const base = await boot(config(), fakeDriver());
    const response = await fetch(`${base}/v1/assignments`, {
      method: 'POST',
      headers: authHeaders('test-token'),
      body: JSON.stringify(assignment({ assignmentId: 'evidence-pass-1' })),
    });
    expect(response.status).toBe(202);
    const { result } = await waitForResult(base, 'evidence-pass-1');
    expect(result.status).toBe('passed');
    expect(result.pageSources ?? []).toHaveLength(0);
    expect(result.serverLogs ?? []).toHaveLength(0);
  });

  it('maps session creation failure to a deterministic error', async () => {
    const base = await boot(
      config(),
      fakeDriver({ failCreate: { kind: 'environment', message: 'Device unavailable: emulator offline' } }),
    );
    const response = await fetch(`${base}/v1/assignments`, {
      method: 'POST',
      headers: authHeaders('test-token'),
      body: JSON.stringify(assignment({ assignmentId: 'fail-1' })),
    });
    expect(response.status).toBe(202);
    const { result } = await waitForResult(base, 'fail-1');
    expect(result.status).toBe('error');
    expect(result.classification).toBe('environment');
    expect(result.status).not.toBe('passed');
  });

  it('deletes the session on cancel', async () => {
    const deleteCalls: string[] = [];
    const base = await boot(config(), fakeDriver({ deleteCalls }));
    const headers = authHeaders('test-token');
    await fetch(`${base}/v1/assignments`, {
      method: 'POST',
      headers,
      // Long wait keeps the run alive so DELETE lands mid-execution.
      body: JSON.stringify(
        assignment({ assignmentId: 'cancel-1', steps: [{ order: 1, action: 'wait', target: null, value: '8000' }] }),
      ),
    });
    await waitForSession(base, 'cancel-1');
    const response = await fetch(`${base}/v1/assignments/cancel-1`, { method: 'DELETE', headers });
    expect(response.status).toBe(200);
    const { result } = await waitForResult(base, 'cancel-1');
    expect(result.status).toBe('cancelled');
    expect(deleteCalls).toEqual(['fake-session-1']);
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
    const base = await boot(config(), fakeDriver());
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
