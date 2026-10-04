import { describe, expect, it } from 'vitest';
import { runMobileActions, parseWaitMs, parseSwipe, type ActionEngineDeps } from '../src/actions.js';
import type { IMobileDriver } from '../src/driver.js';
import type { MobileAssignment, MobileLog, MobileStep } from '../src/types.js';

function steps(actions: Array<Partial<MobileStep> & { action: string }>): MobileStep[] {
  return actions.map((s, index) => ({
    order: s.order ?? index + 1,
    action: s.action,
    target: s.target ?? null,
    value: s.value ?? null,
  }));
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
    steps: steps([{ action: 'launchApp' }]),
    timeouts: { executionMs: 30000, stepMs: 5000 },
    screenshotOnFailure: true,
    assignmentToken: 'token-1',
    ...overrides,
  };
}

interface FakeScript {
  /** Throw this value when the named probe fires. Plain {kind,message} included. */
  [probe: string]: unknown;
}

/** Scriptable fake: named probes throw or return scripted values; every call logged. */
function fakeDriver(script: FakeScript = {}, calls: string[] = []): IMobileDriver {
  const probe = async <T>(name: string, fallback: T): Promise<T> => {
    calls.push(name);
    if (Object.hasOwn(script, name)) {
      const scripted = script[name];
      if (typeof scripted === 'function') return await (scripted as () => T | Promise<T>)();
      if (scripted instanceof Error) throw scripted;
      if (scripted !== null && typeof scripted === 'object' && 'kind' in (scripted as object)) {
        throw scripted;
      }
      return scripted as T;
    }
    return fallback;
  };
  return {
    createSession: async () => ({ sessionId: 's1' }),
    deleteSession: async () => undefined,
    hasSession: () => true,
    tap: async () => {
      await probe('tap', undefined);
    },
    setText: async () => {
      await probe('inputText', undefined);
    },
    clearText: async () => {
      await probe('clearText', undefined);
    },
    isDisplayed: async () => probe('assertVisible', true),
    getText: async () => probe('assertText', 'Welcome back'),
    swipe: async () => {
      await probe('swipe', undefined);
    },
    pressBack: async () => {
      await probe('back', undefined);
    },
    hideKeyboard: async () => probe('hideKeyboard', 'closed' as const),
    takeScreenshot: async () => probe('screenshot-capture', Buffer.from('png-bytes').toString('base64')),
    getPageSource: async () => probe('pagesource-capture', '<hierarchy><node text="fake" /></hierarchy>'),
    activateApp: async () => {
      await probe('launchApp', undefined);
    },
    terminateApp: async () => {
      await probe('terminateApp', undefined);
    },
  };
}

function deps(
  driver: IMobileDriver,
  assignmentOverride: Partial<MobileAssignment> = {},
  extra: Partial<ActionEngineDeps> = {},
): { deps: ActionEngineDeps; logs: MobileLog[] } {
  const logs: MobileLog[] = [];
  return {
    logs,
    deps: {
      driver,
      sessionId: 's1',
      assignment: assignment(assignmentOverride),
      stepTimeoutMs: 5000,
      signal: new AbortController().signal,
      pushLog: (level, message) => {
        logs.push({ seq: logs.length + 1, timestampUnixMs: Date.now(), level, message });
      },
      ...extra,
    },
  };
}

describe('mobile action dispatch', () => {
  it.each([
    ['launchApp', {}],
    ['tap', { target: 'accessibilityId=login' }],
    ['inputText', { target: 'resourceId=com.example.shop:id/pass', value: 's3cret' }],
    ['clearText', { target: 'accessibilityId=field' }],
    ['assertVisible', { target: 'accessibilityId=title' }],
    ['assertText', { target: 'accessibilityId=title', value: 'Welcome' }],
    ['swipe', { value: 'up' }],
    ['swipe', { value: 'down:500' }],
    ['back', {}],
    ['hideKeyboard', {}],
    ['wait', { value: '50' }],
    ['screenshot', {}],
    ['terminateApp', {}],
  ])('executes %s and reports passed', async (action, step) => {
    const { deps: d } = deps(fakeDriver(), { steps: steps([{ action, ...step }]) });
    const result = await runMobileActions(d);
    expect(result.status).toBe('passed');
    expect(result.errorMessage).toBeNull();
    expect(result.stepResults).toHaveLength(1);
    expect(result.stepResults[0]!.status).toBe('passed');
  });

  it('rejects unsupported actions at the engine boundary', async () => {
    const calls: string[] = [];
    const { deps: d } = deps(fakeDriver({}, calls), {
      steps: steps([{ action: 'launchApp' }, { action: 'executeScript', target: null, value: null }]),
      screenshotOnFailure: false,
    });
    const result = await runMobileActions(d);
    expect(result.status).toBe('error');
    expect(result.classification).toBe('automation');
    expect(result.stepResults.map((s) => s.status)).toEqual(['passed', 'failed']);
    expect(calls).toEqual(['launchApp']);
  });

  it('rejects tap without an explicit locator strategy', async () => {
    const calls: string[] = [];
    const { deps: d } = deps(fakeDriver({}, calls), {
      steps: steps([{ action: 'tap', target: '#login' }]),
      screenshotOnFailure: false,
    });
    const result = await runMobileActions(d);
    expect(result.status).toBe('error');
    expect(result.classification).toBe('automation');
    expect(calls).toHaveLength(0);
  });

  it('rejects malformed steps without touching the driver', async () => {
    const calls: string[] = [];
    const { deps: d } = deps(fakeDriver({}, calls), {
      steps: steps([{ action: 'inputText', target: 'accessibilityId=x' }]),
      screenshotOnFailure: false,
    });
    const result = await runMobileActions(d);
    expect(result.status).toBe('error');
    expect(result.classification).toBe('automation');
    expect(calls).toHaveLength(0);
  });

  it('fails assertVisible absence as test failure and skips the rest', async () => {
    const { deps: d } = deps(fakeDriver({ assertVisible: false }), {
      steps: steps([
        { action: 'assertVisible', target: 'accessibilityId=missing' },
        { action: 'tap', target: 'accessibilityId=other' },
      ]),
    });
    const result = await runMobileActions(d);
    expect(result.status).toBe('failed');
    expect(result.classification).toBe('test');
    expect(result.errorType).toBe('AssertionError');
    expect(result.stepResults.map((s) => s.status)).toEqual(['failed', 'skipped']);
  });

  it('fails assertText mismatch as test failure without values in the message', async () => {
    const { deps: d } = deps(fakeDriver({ assertText: 'Something else entirely' }), {
      steps: steps([{ action: 'assertText', target: 'accessibilityId=t', value: 'Expected text' }]),
    });
    const result = await runMobileActions(d);
    expect(result.status).toBe('failed');
    expect(result.classification).toBe('test');
    expect(result.errorMessage).not.toContain('Expected text');
    expect(result.errorMessage).not.toContain('Something else');
  });

  it('passes assertText on substring match', async () => {
    const { deps: d } = deps(fakeDriver({ assertText: 'Welcome back, shopper' }), {
      steps: steps([{ action: 'assertText', target: 'accessibilityId=t', value: 'back' }]),
    });
    const result = await runMobileActions(d);
    expect(result.status).toBe('passed');
  });

  it('maps element interaction faults to test failures', async () => {
    const { deps: d } = deps(
      fakeDriver({ tap: { kind: 'test', message: 'Cannot tap: element is not interactable (gone)' } }),
      { steps: steps([{ action: 'tap', target: 'accessibilityId=gone' }]) },
    );
    const result = await runMobileActions(d);
    expect(result.status).toBe('failed');
    expect(result.classification).toBe('test');
  });

  it('maps session death to environment errors', async () => {
    const { deps: d } = deps(
      fakeDriver({ tap: { kind: 'environment', message: 'Cannot tap: Appium session failure: invalid session id' } }),
      { steps: steps([{ action: 'tap', target: 'accessibilityId=x' }]) },
    );
    const result = await runMobileActions(d);
    expect(result.status).toBe('error');
    expect(result.classification).toBe('environment');
  });

  it('launchApp uses the trusted package and fails closed without one', async () => {
    const calls: string[] = [];
    const ok = await runMobileActions(
      deps(fakeDriver({}, calls), { steps: steps([{ action: 'launchApp' }]) }).deps,
    );
    expect(ok.status).toBe('passed');
    expect(calls).toContain('launchApp');

    const noPackage = await runMobileActions(
      deps(fakeDriver(), {
        steps: steps([{ action: 'launchApp' }]),
        app: { packageId: null, installPolicy: 'Preinstalled' },
      }).deps,
    );
    expect(noPackage.status).toBe('error');
    expect(noPackage.classification).toBe('automation');
  });

  it('terminateApp fails closed without a trusted package', async () => {
    const result = await runMobileActions(
      deps(fakeDriver(), {
        steps: steps([{ action: 'terminateApp' }]),
        app: { packageId: null, installPolicy: 'Preinstalled' },
      }).deps,
    );
    expect(result.status).toBe('error');
    expect(result.classification).toBe('automation');
  });

  it('executes in order and stops after terminal failure', async () => {
    const calls: string[] = [];
    const result = await runMobileActions(
      deps(fakeDriver({ assertVisible: false }, calls), {
        steps: steps([
          { action: 'launchApp' },
          { action: 'assertVisible', target: 'accessibilityId=missing' },
          { action: 'tap', target: 'accessibilityId=never' },
        ]),
      }).deps,
    );
    expect(calls).toEqual(['launchApp', 'assertVisible', 'screenshot-capture', 'pagesource-capture']);
    expect(result.stepResults.map((s) => `${s.order}:${s.status}`)).toEqual([
      '1:passed',
      '2:failed',
      '3:skipped',
    ]);
  });

  it('cancellation stops future actions', async () => {
    const controller = new AbortController();
    const pending = runMobileActions(
      deps(
        fakeDriver(),
        { steps: steps([{ action: 'wait', value: '5000' }, { action: 'tap', target: 'accessibilityId=x' }]) },
        { signal: controller.signal },
      ).deps,
    );
    controller.abort();
    await expect(pending).rejects.toThrow(/cancelled via api/i);
  });

  it('captures a redacted page source on failure without values in the artifact', async () => {
    const result = await runMobileActions(
      deps(
        fakeDriver({ assertVisible: false, 'pagesource-capture': '<hierarchy><node text="hunter2-secret" /></hierarchy>' }),
        {
          steps: steps([
            { action: 'inputText', target: 'resourceId=com.shop:id/password', value: 'hunter2-secret' },
            { action: 'assertVisible', target: 'accessibilityId=missing' },
          ]),
        },
      ).deps,
    );
    expect(result.status).toBe('failed');
    // inputText passed first, so the typed secret is exact-masked in the snapshot.
    expect(result.pageSources).toHaveLength(1);
    expect(result.pageSources[0]).toMatchObject({ stepOrder: 2, contentType: 'text/xml' });
    expect(result.pageSources[0]!.fileName).toBe('step-2-pagesource.xml');
    expect(result.pageSources[0]!.xmlContent).not.toContain('hunter2-secret');
    expect(JSON.stringify(result)).not.toContain('hunter2-secret');
  });

  it('masks the assignment token in page-source evidence', async () => {
    const result = await runMobileActions(
      deps(
        fakeDriver({ assertVisible: false, 'pagesource-capture': 'session token-1 established <hierarchy/>' }),
        { steps: steps([{ action: 'assertVisible', target: 'accessibilityId=missing' }]) },
      ).deps,
    );
    expect(result.status).toBe('failed');
    expect(result.pageSources).toHaveLength(1);
    expect(result.pageSources[0]!.xmlContent).not.toContain('token-1');
    expect(JSON.stringify(result)).not.toContain('token-1');
  });

  it('page source capture failure keeps the primary failure and never retries', async () => {
    const logged: string[] = [];
    const { deps: d } = deps(
      fakeDriver({ assertVisible: false, 'pagesource-capture': { kind: 'environment', message: 'invalid session id' } }),
      { steps: steps([{ action: 'assertVisible', target: 'accessibilityId=missing' }]) },
    );
    const withLogs: ActionEngineDeps = {
      ...d,
      pushLog: (level, message) => {
        logged.push(message);
        d.pushLog(level, message);
      },
    };
    const result = await runMobileActions(withLogs);
    expect(result.status).toBe('failed');
    expect(result.classification).toBe('test');
    expect(result.errorType).toBe('AssertionError');
    expect(result.pageSources).toHaveLength(0);
    expect(logged.join('\n')).toContain('page source capture failed');
  });

  it('passed runs carry no page-source evidence', async () => {
    const result = await runMobileActions(
      deps(fakeDriver(), { steps: steps([{ action: 'launchApp' }]) }).deps,
    );
    expect(result.status).toBe('passed');
    expect(result.pageSources).toHaveLength(0);
  });

  it('captures screenshot-on-failure without replacing the primary failure', async () => {
    const result = await runMobileActions(
      deps(fakeDriver({ assertVisible: false }), {
        steps: steps([{ action: 'assertVisible', target: 'accessibilityId=missing' }]),
      }).deps,
    );
    expect(result.status).toBe('failed');
    expect(result.errorType).toBe('AssertionError');
    expect(result.screenshots).toHaveLength(1);
    expect(result.screenshots[0]!.fileName).toBe('step-1-failure.png');
    expect(result.screenshots[0]!.contentType).toBe('image/png');
  });

  it('screenshot action produces an explicit artifact descriptor', async () => {
    const result = await runMobileActions(
      deps(fakeDriver(), { steps: steps([{ action: 'launchApp' }, { action: 'screenshot' }]) }).deps,
    );
    expect(result.status).toBe('passed');
    expect(result.screenshots).toHaveLength(1);
    expect(result.screenshots[0]).toMatchObject({ stepOrder: 2, contentType: 'image/png' });
    expect(result.screenshots[0]!.base64Content.length).toBeGreaterThan(0);
  });

  it('never logs secret values', async () => {
    const logged: string[] = [];
    const result = await runMobileActions({
      ...deps(
        fakeDriver(),
        { steps: steps([{ action: 'inputText', target: 'resourceId=com.shop:id/password', value: 'hunter2-secret' }]) },
      ).deps,
      pushLog: (_level, message) => {
        logged.push(message);
      },
    });
    expect(result.status).toBe('passed');
    expect(logged.join('\n')).not.toContain('hunter2-secret');
    expect(JSON.stringify(result)).not.toContain('hunter2-secret');
  });

  it('swipe parses direction and bounded duration', () => {
    expect(parseSwipe('up')).toEqual({ direction: 'up', durationMs: 800 });
    expect(parseSwipe('down:500')).toEqual({ direction: 'down', durationMs: 500 });
    expect(parseSwipe('LEFT:99999')).toEqual({ direction: 'left', durationMs: 5000 });
    expect(() => parseSwipe('diagonal')).toThrow();
    expect(() => parseSwipe('')).toThrow();
  });

  it('wait is bounded', () => {
    expect(parseWaitMs('50')).toBe(50);
    expect(parseWaitMs('')).toBe(1000);
    expect(parseWaitMs('999999')).toBe(30000);
    expect(() => parseWaitMs('-5')).toThrow();
    expect(() => parseWaitMs('forever')).toThrow();
  });

  it('hideKeyboard absence still passes', async () => {
    const result = await runMobileActions(
      deps(fakeDriver({ hideKeyboard: 'absent' }), { steps: steps([{ action: 'hideKeyboard' }]) }).deps,
    );
    expect(result.status).toBe('passed');
  });

  it('reports current step order through the callback', async () => {
    const seen: number[] = [];
    await runMobileActions({
      ...deps(fakeDriver(), {
        steps: steps([{ action: 'launchApp' }, { action: 'back' }, { action: 'wait', value: '10' }]),
      }).deps,
      onStepStart: (order) => {
        seen.push(order);
      },
    });
    expect(seen).toEqual([1, 2, 3]);
  });

  it('concurrent assignments remain isolated', async () => {
    const callsA: string[] = [];
    const callsB: string[] = [];
    const [resultA, resultB] = await Promise.all([
      runMobileActions(
        deps(fakeDriver({}, callsA), { steps: steps([{ action: 'tap', target: 'accessibilityId=a' }]) }).deps,
      ),
      runMobileActions(
        deps(fakeDriver({ tap: { kind: 'test', message: 'Cannot tap: gone' } }, callsB), {
          steps: steps([{ action: 'tap', target: 'accessibilityId=b' }]),
        }).deps,
      ),
    ]);
    expect(resultA.status).toBe('passed');
    expect(resultB.status).toBe('failed');
    expect(callsA).toEqual(['tap']);
    expect(callsB).toEqual(['tap', 'screenshot-capture', 'pagesource-capture']);
  });
});

describe('mobile healing retries', () => {
  const healing = {
    enabled: true,
    aiFallbackEnabled: false,
    maxAttemptsPerStep: 1,
  };

  const hierarchyWith = (attrs: string): string =>
    `<hierarchy><node index="0" class="android.widget.Button" enabled="true" clickable="true" ${attrs} /></hierarchy>`;

  it('healing disabled preserves existing behavior with no records', async () => {
    const calls: string[] = [];
    const result = await runMobileActions(
      deps(fakeDriver({ assertVisible: false }, calls), {
        steps: steps([{ action: 'assertVisible', target: 'accessibilityId=missing' }]),
      }).deps,
    );
    expect(result.status).toBe('failed');
    expect(result.healingAttempts).toHaveLength(0);
    expect(result.stepResults[0]).not.toMatchObject({ healed: true });
    expect(calls).toEqual(['assertVisible', 'screenshot-capture', 'pagesource-capture']);
  });

  it('deterministic cross-strategy recovery heals the step exactly once', async () => {
    let taps = 0;
    const calls: string[] = [];
    const result = await runMobileActions(
      deps(
        fakeDriver(
          {
            tap: () => {
              taps += 1;
              if (taps === 1) throw { kind: 'test', message: 'Cannot tap: element is not interactable (gone)' };
            },
            'pagesource-capture': hierarchyWith('resource-id="submit" content-desc="other"'),
          },
          calls,
        ),
        {
          steps: steps([{ action: 'tap', target: 'accessibilityId=submit' }]),
          healing,
        },
      ).deps,
    );
    expect(result.status).toBe('passed');
    expect(taps).toBe(2);
    expect(result.stepResults[0]).toMatchObject({
      status: 'passed',
      healed: true,
      recoveredTarget: 'resourceId=submit',
      healingStrategy: 'resourceId',
      aiAssisted: false,
    });
    expect(result.healingAttempts).toHaveLength(1);
    expect(result.healingAttempts[0]).toMatchObject({
      stepOrder: 1,
      stepAction: 'tap',
      originalStrategy: 'accessibilityId',
      recoveredStrategy: 'resourceId',
      healingStrategy: 'Structural',
      status: 'Applied',
      wasApplied: true,
      isAiAssisted: false,
    });
  });

  it('failed recovery keeps the original failure and records the attempt', async () => {
    const calls: string[] = [];
    const result = await runMobileActions(
      deps(
        fakeDriver(
          {
            tap: () => {
              throw { kind: 'test', message: 'Cannot tap: element is not interactable (gone)' };
            },
            'pagesource-capture': hierarchyWith('resource-id="com.shop:id/other"'),
          },
          calls,
        ),
        {
          steps: steps([{ action: 'tap', target: 'accessibilityId=vanished' }]),
          healing,
        },
      ).deps,
    );
    expect(result.status).toBe('failed');
    expect(result.classification).toBe('test');
    expect(result.errorMessage).toContain('element is not interactable');
    expect(result.healingAttempts).toHaveLength(1);
    expect(result.healingAttempts[0]).toMatchObject({ status: 'Failed', wasApplied: false });
    // Evidence still captured for the terminal failure.
    expect(result.pageSources).toHaveLength(1);
  });

  it('retries at most once per step', async () => {
    let taps = 0;
    const result = await runMobileActions(
      deps(
        fakeDriver({
          tap: () => {
            taps += 1;
            throw { kind: 'test', message: 'Cannot tap: element is not interactable (gone)' };
          },
          'pagesource-capture': hierarchyWith('resource-id="submit"'),
        }),
        {
          steps: steps([{ action: 'tap', target: 'accessibilityId=submit' }]),
          healing,
        },
      ).deps,
    );
    expect(result.status).toBe('failed');
    // Initial attempt + exactly one healing retry; the recovered locator
    // fails the same way, and no further retries follow.
    expect(taps).toBe(2);
  });

  it('assertText mismatches never heal the expectation', async () => {
    let texts = 0;
    const result = await runMobileActions(
      deps(
        fakeDriver({
          assertText: () => {
            texts += 1;
            return 'Something else entirely';
          },
          'pagesource-capture': hierarchyWith('resource-id="com.shop:id/t" text="Expected text"'),
        }),
        {
          steps: steps([{ action: 'assertText', target: 'accessibilityId=t', value: 'Expected text' }]),
          healing,
        },
      ).deps,
    );
    expect(result.status).toBe('failed');
    expect(texts).toBe(1);
    expect(result.healingAttempts).toHaveLength(0);
  });

  it('assertText locator recovery heals without rewriting the expectation', async () => {
    const result = await runMobileActions(
      deps(
        fakeDriver(
          {
            assertText: (() => {
              let calls = 0;
              return () => {
                calls += 1;
                if (calls === 1) throw { kind: 'test', message: 'Cannot assertText: element is not interactable (gone)' };
                return 'Welcome back, shopper';
              };
            })(),
            'pagesource-capture': hierarchyWith('resource-id="com.shop:id/title" text="Welcome back"'),
          },
        ),
        {
          steps: steps([{ action: 'assertText', target: 'accessibilityId=old-title', value: 'Welcome back' }]),
          healing,
        },
      ).deps,
    );
    expect(result.status).toBe('passed');
    expect(result.stepResults[0]).toMatchObject({ healed: true, aiAssisted: false });
    expect(result.healingAttempts[0]).toMatchObject({ status: 'Applied', healingStrategy: 'Structural' });
  });

  it('rejected AI candidates keep the original failure without retry', async () => {
    let taps = 0;
    const result = await runMobileActions({
      ...deps(
        fakeDriver(
          {
            tap: () => {
              taps += 1;
              throw { kind: 'test', message: 'Cannot tap: element is not interactable (gone)' };
            },
            'pagesource-capture': hierarchyWith('resource-id="com.shop:id/other"'),
          },
        ),
        {
          steps: steps([{ action: 'tap', target: 'accessibilityId=vanished' }]),
          healing: { ...healing, aiFallbackEnabled: true },
        },
      ).deps,
      suggestAi: async () =>
        JSON.stringify({ candidates: [{ strategy: 'xpath', value: '//node', confidence: 0.9 }] }),
    });
    // Deterministic generation finds nothing and the xpath AI candidate is
    // rejected by the closed strategy set: no retry, original failure stands.
    expect(result.status).toBe('failed');
    expect(taps).toBe(1);
    expect(result.healingAttempts).toHaveLength(1);
    expect(result.healingAttempts[0]).toMatchObject({ status: 'Failed', wasApplied: false });
  });

  it('AI success marks aiAssisted true', async () => {
    let taps = 0;
    const result = await runMobileActions({
      ...deps(
        fakeDriver(
          {
            tap: () => {
              taps += 1;
              if (taps === 1) throw { kind: 'test', message: 'Cannot tap: element is not interactable (gone)' };
            },
            'pagesource-capture': hierarchyWith('resource-id="com.shop:id/ai-target"'),
          },
        ),
        {
          steps: steps([{ action: 'tap', target: 'accessibilityId=vanished' }]),
          healing: { ...healing, aiFallbackEnabled: true },
        },
      ).deps,
      suggestAi: async () =>
        JSON.stringify({ candidates: [{ strategy: 'resourceId', value: 'com.shop:id/ai-target', confidence: 0.9 }] }),
    });
    // Deterministic generation finds nothing ('vanished' relates to nothing
    // live); the validated AI candidate drives the single retry to success.
    expect(result.status).toBe('passed');
    expect(taps).toBe(2);
    expect(result.stepResults[0]).toMatchObject({
      healed: true,
      recoveredTarget: 'resourceId=com.shop:id/ai-target',
      aiAssisted: true,
    });
    expect(result.healingAttempts).toHaveLength(1);
    expect(result.healingAttempts[0]).toMatchObject({
      status: 'Applied',
      wasApplied: true,
      isAiAssisted: true,
      healingStrategy: 'Ai',
    });
  });

  it('sanitizes recovered values for sensitive targets', async () => {
    let sets = 0;
    const result = await runMobileActions(
      deps(
        fakeDriver(
          {
            inputText: () => {
              sets += 1;
              if (sets === 1) throw { kind: 'test', message: 'Cannot inputText: element is not interactable (gone)' };
            },
            'pagesource-capture': hierarchyWith('content-desc="com.shop:id/password" resource-id="hunter2-secret"'),
          },
        ),
        {
          steps: steps([{ action: 'inputText', target: 'resourceId=com.shop:id/password', value: 'hunter2-secret' }]),
          healing,
        },
      ).deps,
    );
    expect(result.status).toBe('passed');
    // The recovered locator echoes a secret: the whole target is masked.
    expect(result.stepResults[0]).toMatchObject({ healed: true, recoveredTarget: '[REDACTED]' });
    expect(result.healingAttempts[0]!.recoveredValue).toBe('[REDACTED]');
    expect(JSON.stringify(result)).not.toContain('hunter2-secret');
  });
});
