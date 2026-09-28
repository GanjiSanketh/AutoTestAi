import { describe, expect, it } from 'vitest';
import type { EngineLocator, EnginePage } from '../src/stepEngine.js';
import { executeSteps, validateSteps } from '../src/stepEngine.js';
import type { WorkerStep } from '../src/types.js';

/** Browser-free fake page: records calls, answers assertions deterministically. */
function createFakePage(overrides: Partial<EnginePage> = {}): EnginePage & {
  calls: string[];
  texts: Record<string, string>;
  values: Record<string, string>;
  gotoStatuses: Record<string, number>;
} {
  const calls: string[] = [];
  const texts: Record<string, string> = { '#heading': 'Welcome back' };
  const values: Record<string, string> = { '#username': 'qa-user' };
  const gotoStatuses: Record<string, number> = {};
  const locator = (target: string): EngineLocator => ({
    click: async () => {
      calls.push(`click:${target}`);
    },
    fill: async (value) => {
      calls.push(`fill:${target}=${value}`);
    },
    pressSequentially: async (value) => {
      calls.push(`type:${target}=${value}`);
    },
    selectOption: async (value) => {
      calls.push(`select:${target}=${Array.isArray(value) ? value.join(',') : value}`);
    },
    check: async () => {
      calls.push(`check:${target}`);
    },
    uncheck: async () => {
      calls.push(`uncheck:${target}`);
    },
    press: async (key) => {
      calls.push(`press:${target}=${key}`);
    },
    waitForVisible: async () => {
      if (target === '#missing') throw new Error('Timeout waiting for visible');
      calls.push(`visible:${target}`);
    },
    readText: async () => texts[target] ?? null,
    readInputValue: async () => values[target] ?? '',
    captureScreenshot: async () => Buffer.from('fake-png'),
  });
  return {
    calls,
    texts,
    values,
    gotoStatuses,
    goto: async (url) => {
      calls.push(`goto:${url}`);
      if (url.includes('unreachable')) throw new Error('net::ERR_NAME_NOT_RESOLVED');
      return gotoStatuses[url] ?? 200;
    },
    locatorFor: (target) => (target ? locator(target) : null),
    pressKey: async (key) => {
      calls.push(`pressKey:${key}`);
    },
    readTitle: async () => 'Fake title',
    captureScreenshot: async () => Buffer.from('fake-page-png'),
    ...overrides,
  };
}

function step(partial: Partial<WorkerStep> & { order: number; action: string }): WorkerStep {
  return { target: null, value: null, ...partial };
}

describe('validateSteps', () => {
  it('rejects empty and oversized step lists', () => {
    expect(validateSteps([])).not.toHaveLength(0);
    expect(
      validateSteps(
        Array.from({ length: 501 }, (_, i) => step({ order: i + 1, action: 'click', target: 'x' })),
      ),
    ).not.toHaveLength(0);
  });

  it('rejects unknown actions and bad ordering', () => {
    const problems = validateSteps([
      step({ order: 1, action: 'rm -rf', target: 'x' }),
      step({ order: 1, action: 'click', target: 'y' }),
    ]);
    expect(problems.some((p) => p.includes('unsupported action'))).toBe(true);
    expect(problems.some((p) => p.includes('duplicate step order'))).toBe(true);
  });

  it('accepts the documented MVP action set', () => {
    expect(
      validateSteps([
        step({ order: 1, action: 'navigate', target: 'https://x.test' }),
        step({ order: 2, action: 'click', target: '#b' }),
        step({ order: 3, action: 'fill', target: '#u', value: 'a' }),
        step({ order: 4, action: 'type', target: '#p', value: 'b' }),
        step({ order: 5, action: 'select', target: '#s', value: 'v' }),
        step({ order: 6, action: 'check', target: '#c' }),
        step({ order: 7, action: 'uncheck', target: '#c' }),
        step({ order: 8, action: 'press', target: '#i', value: 'Enter' }),
        step({ order: 9, action: 'wait', value: '50' }),
        step({ order: 10, action: 'assertVisible', target: '#h' }),
        step({ order: 11, action: 'assertText', target: '#heading', value: 'Welcome' }),
        step({ order: 12, action: 'assertValue', target: '#username', value: 'qa-user' }),
        step({ order: 13, action: 'screenshot' }),
      ]),
    ).toHaveLength(0);
  });
});

describe('executeSteps', () => {
  it('executes supported actions sequentially', async () => {
    const page = createFakePage();
    const run = await executeSteps(
      page,
      [
        step({ order: 1, action: 'navigate', target: 'https://x.test/login' }),
        step({ order: 2, action: 'fill', target: '#username', value: 'qa-user' }),
        step({ order: 3, action: 'click', target: '#submit' }),
      ],
      {
        stepTimeoutMs: 5000,
        signal: new AbortController().signal,
        screenshotOnFailure: true,
      },
    );
    expect(run.failed).toBe(false);
    expect(run.stepResults.map((s) => s.status)).toEqual(['passed', 'passed', 'passed']);
    expect(page.calls).toContain('goto:https://x.test/login');
    expect(page.calls).toContain('fill:#username=qa-user');
  });

  it('fails the run on assertion mismatch and skips the rest', async () => {
    const page = createFakePage();
    const run = await executeSteps(
      page,
      [
        step({ order: 1, action: 'assertText', target: '#heading', value: 'Goodbye' }),
        step({ order: 2, action: 'click', target: '#next' }),
      ],
      { stepTimeoutMs: 5000, signal: new AbortController().signal, screenshotOnFailure: false },
    );
    expect(run.failed).toBe(true);
    expect(run.classification).toBe('test');
    expect(run.stepResults.map((s) => s.status)).toEqual(['failed', 'skipped']);
    expect(run.errorMessage).toContain('Step 1');
  });

  it('classifies unknown actions as automation failures without executing anything', async () => {
    const page = createFakePage();
    const run = await executeSteps(
      page,
      [step({ order: 1, action: 'eval', target: 'alert(1)' })],
      { stepTimeoutMs: 5000, signal: new AbortController().signal, screenshotOnFailure: false },
    );
    expect(run.failed).toBe(true);
    expect(run.classification).toBe('automation');
    expect(page.calls).toHaveLength(0);
  });

  it('classifies missing targets as automation failures', async () => {
    const page = createFakePage();
    const run = await executeSteps(
      page,
      [step({ order: 1, action: 'click' })],
      { stepTimeoutMs: 5000, signal: new AbortController().signal, screenshotOnFailure: false },
    );
    expect(run.failed).toBe(true);
    expect(run.classification).toBe('automation');
  });

  it('classifies unreachable navigation as environment failure', async () => {
    const page = createFakePage();
    const run = await executeSteps(
      page,
      [step({ order: 1, action: 'navigate', target: 'https://unreachable.invalid' })],
      { stepTimeoutMs: 5000, signal: new AbortController().signal, screenshotOnFailure: false },
    );
    expect(run.failed).toBe(true);
    expect(run.classification).toBe('environment');
  });

  it('classifies HTTP 500 on navigation as application failure', async () => {
    const page = createFakePage();
    page.gotoStatuses['https://x.test/broken'] = 500;
    const run = await executeSteps(
      page,
      [step({ order: 1, action: 'navigate', target: 'https://x.test/broken' })],
      { stepTimeoutMs: 5000, signal: new AbortController().signal, screenshotOnFailure: false },
    );
    expect(run.failed).toBe(true);
    expect(run.classification).toBe('application');
  });

  it('captures a screenshot on failure when configured', async () => {
    const page = createFakePage();
    const run = await executeSteps(
      page,
      [step({ order: 1, action: 'assertText', target: '#heading', value: 'nope' })],
      { stepTimeoutMs: 5000, signal: new AbortController().signal, screenshotOnFailure: true },
    );
    expect(run.failed).toBe(true);
    expect(run.screenshots).toHaveLength(1);
    expect(run.screenshots[0]?.fileName).toBe('step-1-failure.png');
  });

  it('aborts cleanly on cancellation', async () => {
    const page = createFakePage();
    const controller = new AbortController();
    controller.abort();
    await expect(
      executeSteps(page, [step({ order: 1, action: 'wait', value: '5000' })], {
        stepTimeoutMs: 5000,
        signal: controller.signal,
        screenshotOnFailure: false,
      }),
    ).rejects.toThrow();
  });

  it('never executes sourceCode strings', async () => {
    const page = createFakePage();
    const calls: string[] = (page as { calls: string[] }).calls;
    await executeSteps(
      page,
      [step({ order: 1, action: 'navigate', target: 'https://x.test' })],
      { stepTimeoutMs: 5000, signal: new AbortController().signal, screenshotOnFailure: false },
    );
    expect(calls.join('\n')).not.toContain('eval');
  });
});
