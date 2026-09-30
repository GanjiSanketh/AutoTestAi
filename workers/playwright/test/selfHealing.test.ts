import { describe, expect, it, vi } from 'vitest';
import type { EngineLocator, EnginePage } from '../src/stepEngine.js';
import { executeSteps } from '../src/stepEngine.js';
import {
  buildEvidenceEnvelope,
  generateDeterministicCandidates,
  isHealingEligible,
  parseAiCandidates,
  redactSecrets,
  type HealingElementSnapshot,
  type HealingInspector,
  type SelfHealingPolicy,
} from '../src/selfHealing.js';
import type { WorkerStep } from '../src/types.js';

const ENABLED: SelfHealingPolicy = { enabled: true, aiFallbackEnabled: false, maxAttemptsPerStep: 1 };
const AI_ENABLED: SelfHealingPolicy = { enabled: true, aiFallbackEnabled: true, maxAttemptsPerStep: 1 };

function step(partial: Partial<WorkerStep> & { order: number; action: string }): WorkerStep {
  return { target: null, value: null, ...partial };
}

function locatorStub(impl: Partial<EngineLocator>): EngineLocator {
  return {
    click: async () => undefined,
    fill: async () => undefined,
    pressSequentially: async () => undefined,
    selectOption: async () => undefined,
    check: async () => undefined,
    uncheck: async () => undefined,
    press: async () => undefined,
    waitForVisible: async () => undefined,
    readText: async () => null,
    readInputValue: async () => '',
    captureScreenshot: async () => Buffer.from('fake'),
    ...impl,
  };
}

interface MatchInfo {
  count: number;
  tagName: string | null;
  visible: boolean;
  enabled: boolean;
}

/** Fake inspector backed by an explicit match table (no browser needed). */
function fakeInspector(
  snapshots: HealingElementSnapshot[],
  matches: Record<string, MatchInfo>,
  tracker?: { snapshots: number; probes: string[] },
): HealingInspector {
  return {
    collectSnapshots: async (limit: number) => {
      if (tracker) tracker.snapshots += 1;
      return snapshots.slice(0, limit);
    },
    countMatches: async (target: string) => {
      if (tracker) tracker.probes.push(target);
      return matches[target]?.count ?? 0;
    },
    describeMatch: async (target: string) => {
      if (tracker) tracker.probes.push(target);
      const hit = matches[target];
      if (!hit) return { count: 0, tagName: null, visible: false, enabled: false };
      return { count: hit.count, tagName: hit.tagName, visible: hit.visible, enabled: hit.enabled };
    },
  };
}

/**
 * Fake page: the ORIGINAL target always fails with a locator timeout;
 * recovered targets succeed unless listed in failingTargets. Records every
 * locatorFor call so tests can prove no ambiguity suppression (first/nth)
 * and no extra retries happened.
 */
function healingPage(options: {
  inspector: HealingInspector;
  originalTarget: string;
  failingTargets?: Set<string>;
  locatorCalls?: string[];
}): EnginePage {
  const failing = options.failingTargets ?? new Set<string>();
  return {
    goto: async () => 200,
    locatorFor: (target: string) => {
      options.locatorCalls?.push(target);
      if (target === options.originalTarget || failing.has(target)) {
        return locatorStub({
          click: async () => {
            throw new Error(`Timeout waiting for locator '${target}'.`);
          },
          fill: async () => {
            throw new Error(`Timeout waiting for locator '${target}'.`);
          },
          waitForVisible: async () => {
            throw new Error(`Timeout waiting for locator '${target}'.`);
          },
        });
      }
      return locatorStub({});
    },
    pressKey: async () => undefined,
    readTitle: async () => 't',
    captureScreenshot: async () => Buffer.from('fake'),
    healing: options.inspector,
  };
}

describe('isHealingEligible', () => {
  it('accepts locator-like failures on healable actions', () => {
    expect(isHealingEligible('click', "Timeout waiting for locator '#x'.")).toBe(true);
    expect(isHealingEligible('fill', 'Element not found: #user.')).toBe(true);
    expect(isHealingEligible('assertVisible', 'Selector no longer matching.')).toBe(true);
  });

  it('rejects assertion failures, navigation, environment and auth errors', () => {
    expect(isHealingEligible('assertText', "Expected text containing 'Hi'.")).toBe(false);
    expect(isHealingEligible('assertValue', "Expected value 'a'.")).toBe(false);
    expect(isHealingEligible('navigate', "Navigation to 'https://x' failed: net::ERR.")).toBe(false);
    expect(isHealingEligible('click', 'Request failed with HTTP 500.')).toBe(false);
    expect(isHealingEligible('click', 'Unauthorized (401).')).toBe(false);
    expect(isHealingEligible('click', 'Cancelled via API.')).toBe(false);
    expect(isHealingEligible('wait', 'Timeout waiting.')).toBe(false);
    expect(isHealingEligible('screenshot', 'Timeout.')).toBe(false);
    expect(isHealingEligible('eval', 'Timeout.')).toBe(false);
  });
});

describe('healing disabled (Slice 11 §15, test A)', () => {
  it('preserves the normal failure without candidate generation or AI calls', async () => {
    const tracker = { snapshots: 0, probes: [] as string[] };
    const inspector = fakeInspector([{ testId: 'submit-btn' }], {}, tracker);
    const suggestAi = vi.fn(async () => '{"candidates":[]}');
    const locatorCalls: string[] = [];
    const page = healingPage({ inspector, originalTarget: 'css=#old-submit', locatorCalls });
    const run = await executeSteps(
      page,
      [step({ order: 1, action: 'click', target: 'css=#old-submit' })],
      {
        stepTimeoutMs: 1000,
        signal: new AbortController().signal,
        screenshotOnFailure: false,
        healing: { policy: { enabled: false, aiFallbackEnabled: false, maxAttemptsPerStep: 1 }, suggestAi },
      },
    );
    expect(run.failed).toBe(true);
    expect(run.stepResults.map((s) => s.status)).toEqual(['failed']);
    expect(run.healingAttempts).toHaveLength(0);
    expect(tracker.snapshots).toBe(0);
    expect(suggestAi).not.toHaveBeenCalled();
  });
});

describe('deterministic recovery (test B)', () => {
  it('retries once with the validated alternate locator and records Applied', async () => {
    const inspector = fakeInspector(
      [{ testId: 'submit-btn', tagName: 'BUTTON', visible: true, enabled: true }],
      { 'testid=submit-btn': { count: 1, tagName: 'BUTTON', visible: true, enabled: true } },
    );
    const locatorCalls: string[] = [];
    const page = healingPage({ inspector, originalTarget: 'css=#old-submit', locatorCalls });
    const run = await executeSteps(
      page,
      [step({ order: 1, action: 'click', target: 'css=#old-submit' })],
      {
        stepTimeoutMs: 1000,
        signal: new AbortController().signal,
        screenshotOnFailure: false,
        healing: { policy: ENABLED },
      },
    );
    expect(run.failed).toBe(false);
    expect(run.stepResults[0]?.status).toBe('passed');
    expect(run.stepResults[0]?.healed).toBe(true);
    expect(run.stepResults[0]?.target).toBe('css=#old-submit');
    expect(run.stepResults[0]?.recoveredTarget).toBe('testid=submit-btn');
    expect(run.healingAttempts).toHaveLength(1);
    expect(run.healingAttempts[0]?.status).toBe('Applied');
    expect(run.healingAttempts[0]?.wasApplied).toBe(true);
    expect(run.healingAttempts[0]?.isAiAssisted).toBe(false);
    // Exactly one healing retry: original + one recovered attempt.
    expect(locatorCalls.filter((t) => t === 'testid=submit-btn')).toHaveLength(1);
  });
});

describe('ambiguous candidate (test C)', () => {
  it('rejects multi-match candidates without first()/nth() and preserves failure', async () => {
    const tracker = { snapshots: 0, probes: [] as string[] };
    const inspector = fakeInspector(
      [{ text: 'Submit', tagName: 'BUTTON', visible: true, enabled: true }],
      { 'text=Submit': { count: 3, tagName: 'BUTTON', visible: true, enabled: true } },
      tracker,
    );
    const locatorCalls: string[] = [];
    const page = healingPage({ inspector, originalTarget: 'css=#old-submit', locatorCalls });
    const run = await executeSteps(
      page,
      [step({ order: 1, action: 'click', target: 'css=#old-submit' })],
      {
        stepTimeoutMs: 1000,
        signal: new AbortController().signal,
        screenshotOnFailure: false,
        healing: { policy: ENABLED },
      },
    );
    expect(run.failed).toBe(true);
    expect(run.errorMessage).toContain('css=#old-submit');
    expect(run.healingAttempts).toHaveLength(1);
    expect(run.healingAttempts[0]?.status).toBe('Failed');
    expect(run.healingAttempts[0]?.wasApplied).toBe(false);
    // No ambiguity suppression: the ambiguous target was probed but never acted on.
    expect(tracker.probes).toContain('text=Submit');
    expect(locatorCalls.filter((t) => t === 'text=Submit')).toHaveLength(0);
    expect(locatorCalls.join('\n')).not.toMatch(/\.first\(|\.nth\(|\.last\(/);
  });
});

describe('zero-match and wrong-type candidates (tests D/E)', () => {
  it('rejects zero-match candidates', async () => {
    const inspector = fakeInspector(
      [{ testId: 'ghost-btn', tagName: 'BUTTON', visible: true, enabled: true }],
      {},
    );
    const page = healingPage({ inspector, originalTarget: 'css=#old' });
    const run = await executeSteps(
      page,
      [step({ order: 1, action: 'click', target: 'css=#old' })],
      {
        stepTimeoutMs: 1000,
        signal: new AbortController().signal,
        screenshotOnFailure: false,
        healing: { policy: ENABLED },
      },
    );
    expect(run.failed).toBe(true);
    expect(run.healingAttempts[0]?.status).toBe('Failed');
  });

  it('rejects candidates incompatible with the action (fill on a button)', async () => {
    const inspector = fakeInspector(
      [{ testId: 'cta', tagName: 'BUTTON', visible: true, enabled: true }],
      { 'testid=cta': { count: 1, tagName: 'BUTTON', visible: true, enabled: true } },
    );
    const locatorCalls: string[] = [];
    const page = healingPage({ inspector, originalTarget: 'css=#old-input', locatorCalls });
    const run = await executeSteps(
      page,
      [step({ order: 1, action: 'fill', target: 'css=#old-input', value: 'hello' })],
      {
        stepTimeoutMs: 1000,
        signal: new AbortController().signal,
        screenshotOnFailure: false,
        healing: { policy: ENABLED },
      },
    );
    expect(run.failed).toBe(true);
    expect(run.healingAttempts[0]?.status).toBe('Failed');
    expect(locatorCalls.filter((t) => t === 'testid=cta')).toHaveLength(0);
  });

  it('rejects hidden elements for actions requiring visibility', async () => {
    const inspector = fakeInspector(
      [{ testId: 'hidden-btn', tagName: 'BUTTON', visible: false, enabled: true }],
      { 'testid=hidden-btn': { count: 1, tagName: 'BUTTON', visible: false, enabled: true } },
    );
    const page = healingPage({ inspector, originalTarget: 'css=#old' });
    const run = await executeSteps(
      page,
      [step({ order: 1, action: 'click', target: 'css=#old' })],
      {
        stepTimeoutMs: 1000,
        signal: new AbortController().signal,
        screenshotOnFailure: false,
        healing: { policy: ENABLED },
      },
    );
    expect(run.failed).toBe(true);
    expect(run.healingAttempts[0]?.status).toBe('Failed');
  });
});

describe('AI fallback (tests F/G/H/I)', () => {
  it('never calls the provider when AI fallback is disabled', async () => {
    const suggestAi = vi.fn(async () => '{"candidates":[]}');
    const inspector = fakeInspector([], {});
    const page = healingPage({ inspector, originalTarget: 'css=#old' });
    const run = await executeSteps(
      page,
      [step({ order: 1, action: 'click', target: 'css=#old' })],
      {
        stepTimeoutMs: 1000,
        signal: new AbortController().signal,
        screenshotOnFailure: false,
        healing: { policy: ENABLED, suggestAi },
      },
    );
    expect(run.failed).toBe(true);
    expect(suggestAi).not.toHaveBeenCalled();
  });

  it('validates an AI candidate and applies it when deterministic healing fails', async () => {
    const inspector = fakeInspector(
      [],
      { 'testid=ai-btn': { count: 1, tagName: 'BUTTON', visible: true, enabled: true } },
    );
    const suggestAi = vi.fn(async () =>
      JSON.stringify({ candidates: [{ strategy: 'testid', value: 'ai-btn', reason: 'Renamed id.' }] }),
    );
    const page = healingPage({ inspector, originalTarget: 'css=#old' });
    const run = await executeSteps(
      page,
      [step({ order: 1, action: 'click', target: 'css=#old' })],
      {
        stepTimeoutMs: 1000,
        signal: new AbortController().signal,
        screenshotOnFailure: false,
        healing: { policy: AI_ENABLED, suggestAi },
      },
    );
    expect(suggestAi).toHaveBeenCalledTimes(1);
    expect(run.failed).toBe(false);
    expect(run.healingAttempts[0]?.status).toBe('Applied');
    expect(run.healingAttempts[0]?.isAiAssisted).toBe(true);
    expect(run.stepResults[0]?.aiAssisted).toBe(true);
  });

  it('rejects malformed AI output without executing anything', async () => {
    const inspector = fakeInspector([], {});
    const suggestAi = vi.fn(async () => 'not-json{{{eval(process.env.SECRET)}}}');
    const locatorCalls: string[] = [];
    const page = healingPage({ inspector, originalTarget: 'css=#old', locatorCalls });
    const run = await executeSteps(
      page,
      [step({ order: 1, action: 'click', target: 'css=#old' })],
      {
        stepTimeoutMs: 1000,
        signal: new AbortController().signal,
        screenshotOnFailure: false,
        healing: { policy: AI_ENABLED, suggestAi },
      },
    );
    expect(run.failed).toBe(true);
    expect(run.errorMessage).toContain('css=#old');
    expect(locatorCalls).toHaveLength(1); // only the original attempt
  });

  it('rejects ambiguous AI candidates', async () => {
    const inspector = fakeInspector(
      [],
      { 'css=.btn': { count: 4, tagName: 'BUTTON', visible: true, enabled: true } },
    );
    const suggestAi = vi.fn(async () =>
      JSON.stringify({ candidates: [{ strategy: 'css', value: '.btn', reason: 'Generic.' }] }),
    );
    const page = healingPage({ inspector, originalTarget: 'css=#old' });
    const run = await executeSteps(
      page,
      [step({ order: 1, action: 'click', target: 'css=#old' })],
      {
        stepTimeoutMs: 1000,
        signal: new AbortController().signal,
        screenshotOnFailure: false,
        healing: { policy: AI_ENABLED, suggestAi },
      },
    );
    expect(run.failed).toBe(true);
    expect(run.healingAttempts[0]?.status).toBe('Failed');
    expect(run.healingAttempts[0]?.isAiAssisted).toBe(false);
  });
});

describe('one-heal guard (test J)', () => {
  it('never heals recursively: a failing recovered locator ends the attempt', async () => {
    const tracker = { snapshots: 0, probes: [] as string[] };
    const inspector = fakeInspector(
      [{ testId: 'also-broken', tagName: 'BUTTON', visible: true, enabled: true }],
      { 'testid=also-broken': { count: 1, tagName: 'BUTTON', visible: true, enabled: true } },
      tracker,
    );
    const page = healingPage({
      inspector,
      originalTarget: 'css=#old',
      failingTargets: new Set(['testid=also-broken']),
    });
    const run = await executeSteps(
      page,
      [step({ order: 1, action: 'click', target: 'css=#old' })],
      {
        stepTimeoutMs: 1000,
        signal: new AbortController().signal,
        screenshotOnFailure: false,
        healing: { policy: ENABLED },
      },
    );
    expect(run.failed).toBe(true);
    expect(tracker.snapshots).toBe(1);
    expect(run.healingAttempts).toHaveLength(1);
    expect(run.healingAttempts[0]?.errorMessage).toContain('Original failure');
  });
});

describe('cancellation and AI timeout (tests K/L)', () => {
  it('preserves cancellation instead of healing', async () => {
    const controller = new AbortController();
    const inspector = fakeInspector(
      [{ testId: 'x', tagName: 'BUTTON', visible: true, enabled: true }],
      { 'testid=x': { count: 1, tagName: 'BUTTON', visible: true, enabled: true } },
    );
    const page = healingPage({ inspector, originalTarget: 'css=#old' });
    controller.abort();
    await expect(
      executeSteps(page, [step({ order: 1, action: 'click', target: 'css=#old' })], {
        stepTimeoutMs: 1000,
        signal: controller.signal,
        screenshotOnFailure: false,
        healing: { policy: ENABLED },
      }),
    ).rejects.toThrow();
  });

  it('bounds AI fallback with a timeout and preserves the original failure', async () => {
    const inspector = fakeInspector([], {});
    const suggestAi = vi.fn(() => new Promise<string>(() => undefined));
    const page = healingPage({ inspector, originalTarget: 'css=#old' });
    const started = Date.now();
    const run = await executeSteps(
      page,
      [step({ order: 1, action: 'click', target: 'css=#old' })],
      {
        stepTimeoutMs: 1000,
        signal: new AbortController().signal,
        screenshotOnFailure: false,
        healing: { policy: AI_ENABLED, suggestAi, aiTimeoutMs: 50 },
      },
    );
    expect(run.failed).toBe(true);
    expect(Date.now() - started).toBeLessThan(5000);
    expect(run.healingAttempts[0]?.status).toBe('Failed');
  }, 10000);
});

describe('redaction and AI contract (tests M + §13)', () => {
  it('redacts credential-looking fragments before AI transmission', () => {
    expect(redactSecrets('password=secret123')).toContain('[REDACTED]');
    expect(redactSecrets('password=secret123')).not.toContain('secret123');
    expect(redactSecrets('Bearer abcdefgh1234')).toContain('Bearer [REDACTED]');
    const envelope = buildEvidenceEnvelope('click', 'css=#login', [
      { text: 'api-key=zzz-hidden', tagName: 'DIV' },
    ]);
    expect(JSON.stringify(envelope)).not.toContain('zzz-hidden');
  });

  it('rejects code-like and unsupported AI strategies', () => {
    const parsed = parseAiCandidates(
      JSON.stringify({
        candidates: [
          { strategy: 'css', value: 'javascript:alert(1)', reason: 'x' },
          { strategy: 'js', value: 'document.querySelector(1)', reason: 'y' },
          { strategy: 'css', value: 'eval(foo)', reason: 'z' },
          { strategy: 'testid', value: 'ok-btn', reason: 'stable' },
        ],
      }),
      ENABLED,
    );
    expect(parsed.candidates).toHaveLength(1);
    expect(parsed.candidates[0]?.value).toBe('ok-btn');
    expect(parsed.rejected.length).toBeGreaterThanOrEqual(3);
  });

  it('never generates candidates from volatile identifiers', () => {
    const candidates = generateDeterministicCandidates(
      [
        { id: 'ember48291-x', tagName: 'BUTTON', visible: true, enabled: true },
        { id: 'submit-form', tagName: 'BUTTON', visible: true, enabled: true },
      ],
      'css=#old',
      ENABLED,
    );
    expect(candidates.some((c) => c.value.includes('ember'))).toBe(false);
    expect(candidates.some((c) => c.value === '#submit-form')).toBe(true);
  });
});
