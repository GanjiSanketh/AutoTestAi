import { describe, expect, it } from 'vitest';
import {
  buildMobileEvidenceEnvelope,
  generateMobileCandidates,
  isMobileHealingEligible,
  MOBILE_HEALING_STRATEGIES,
  mobileStrategyLabel,
  normalizeMobileHealingPolicy,
  parseHierarchySnapshots,
  parseMobileAiCandidates,
  sanitizeRecoveredValue,
  validateMobileCandidate,
  type MobileElementSnapshot,
  type MobileHealingInspector,
  type MobileHealingPolicy,
} from '../src/mobileHealing.js';

const ENABLED: MobileHealingPolicy = {
  enabled: true,
  aiFallbackEnabled: false,
  maxAttemptsPerStep: 1,
};

const HIERARCHY = `
<hierarchy rotation="0">
  <node index="0" text="" resource-id="com.shop:id/toolbar" class="android.view.ViewGroup" content-desc="" enabled="true" clickable="false" />
  <node index="1" text="" resource-id="com.shop:id/login_btn" class="android.widget.Button" content-desc="Sign in" enabled="true" clickable="true" />
  <node index="2" text="Welcome back" resource-id="com.shop:id/title" class="android.widget.TextView" content-desc="" enabled="true" clickable="false" />
  <node index="3" text="" resource-id="com.shop:id/ghost" class="android.widget.Button" content-desc="Sign in" enabled="false" clickable="false" />
</hierarchy>`;

function inspector(nodes: MobileElementSnapshot[]): MobileHealingInspector {
  return {
    snapshot: async () => nodes,
    describe: (node) => ({
      enabled: node.enabled ?? false,
      actionable: node.clickable ?? node.enabled ?? false,
    }),
  };
}

describe('mobile healing eligibility matrix', () => {
  it.each([
    ['tap', 'Cannot tap: element is not interactable (gone)', true],
    ['inputText', 'Cannot inputText: waiting for element timed out', true],
    ['clearText', 'Cannot clearText: no element found for locator', true],
    ['assertVisible', 'Cannot assertVisible: element is not visible', true],
    ['assertText', 'Cannot assertText: element is not interactable (gone)', true],
    ['launchApp', 'Cannot launchApp: element is not interactable (gone)', false],
    ['swipe', 'Cannot swipe: element is not interactable (gone)', false],
    ['back', 'Cannot back: element is not interactable (gone)', false],
    ['hideKeyboard', 'Cannot hideKeyboard: element is not interactable (gone)', false],
    ['wait', 'Cannot wait: element is not interactable (gone)', false],
    ['screenshot', 'Cannot screenshot: element is not interactable (gone)', false],
    ['terminateApp', 'Cannot terminateApp: element is not interactable (gone)', false],
    ['executeScript', 'Cannot executeScript: element is not interactable (gone)', false],
  ])('action %s eligible=%s', (action, message, expected) => {
    expect(isMobileHealingEligible(action, message)).toBe(expected);
  });

  it('rejects text-match rewrites and infrastructure signals', () => {
    // assertText mismatch: locator recovery is healable, expectation rewriting is not.
    expect(isMobileHealingEligible('assertText', 'Step 1 (assertText) failed: text did not match expectation.')).toBe(false);
    expect(isMobileHealingEligible('tap', 'Cannot tap: Appium session failure: invalid session id')).toBe(false);
    expect(isMobileHealingEligible('tap', 'Cannot tap: Device unavailable: emulator offline')).toBe(false);
    expect(isMobileHealingEligible('tap', 'Cancelled via API.')).toBe(false);
    expect(isMobileHealingEligible('tap', 'Mobile execution timed out.')).toBe(false);
    expect(isMobileHealingEligible('tap', '')).toBe(false);
    expect(isMobileHealingEligible('tap', null)).toBe(false);
  });
});

describe('mobile healing policy normalization', () => {
  it('stays off unless explicitly enabled', () => {
    expect(normalizeMobileHealingPolicy(undefined).enabled).toBe(false);
    expect(normalizeMobileHealingPolicy(null).enabled).toBe(false);
    expect(normalizeMobileHealingPolicy({}).enabled).toBe(false);
    expect(normalizeMobileHealingPolicy({ enabled: false, aiFallbackEnabled: true }).aiFallbackEnabled).toBe(false);
  });

  it('clamps attempts to one and filters strategies to the closed set', () => {
    const policy = normalizeMobileHealingPolicy({
      enabled: true,
      aiFallbackEnabled: true,
      maxAttemptsPerStep: 5,
      allowedStrategies: ['accessibilityId', 'xpath', 'css', 'text'],
    });
    expect(policy.enabled).toBe(true);
    expect(policy.aiFallbackEnabled).toBe(true);
    expect(policy.maxAttemptsPerStep).toBe(1);
    expect(policy.allowedStrategies).toEqual(['accessibilityid']);
  });

  it('exposes the closed mobile strategy set', () => {
    expect([...MOBILE_HEALING_STRATEGIES].sort()).toEqual(['accessibilityid', 'resourceid']);
  });
});

describe('hierarchy snapshots', () => {
  it('parses stable node attributes', () => {
    const snapshots = parseHierarchySnapshots(HIERARCHY);
    expect(snapshots).toHaveLength(4);
    expect(snapshots[1]).toMatchObject({
      accessibilityId: 'Sign in',
      resourceId: 'com.shop:id/login_btn',
      enabled: true,
      clickable: true,
    });
  });

  it('fails closed on malformed input', () => {
    expect(parseHierarchySnapshots('')).toHaveLength(0);
    expect(parseHierarchySnapshots('not xml at all')).toHaveLength(0);
  });

  it('bounds snapshot counts', () => {
    const big = '<hierarchy>' + '<node resource-id="com.shop:id/a" />'.repeat(200) + '</hierarchy>';
    expect(parseHierarchySnapshots(big, 10)).toHaveLength(10);
  });
});

describe('deterministic candidate generation', () => {
  const snapshots = parseHierarchySnapshots(HIERARCHY);

  it('matches a failed token across strategies', () => {
    const candidates = generateMobileCandidates(
      snapshots,
      'resourceId=Sign in',
      null,
      false,
      ENABLED,
    );
    expect(candidates.map((c) => `${c.strategy}=${c.value}`)).toContain('accessibilityId=Sign in');
    expect(candidates.every((c) => c.source === 'deterministic')).toBe(true);
  });

  it('anchors assertText recovery on the expected text', () => {
    const candidates = generateMobileCandidates(
      snapshots,
      'accessibilityId=missing-title',
      'Welcome back',
      true,
      ENABLED,
    );
    expect(candidates.map((c) => `${c.strategy}=${c.value}`)).toContain('resourceId=com.shop:id/title');
  });

  it('never emits the original target or guesses without a relationship', () => {
    const unrelated = generateMobileCandidates(
      snapshots,
      'accessibilityId=gone',
      null,
      false,
      ENABLED,
    );
    expect(unrelated).toHaveLength(0);
    const same = generateMobileCandidates(
      snapshots,
      'accessibilityId=Sign in',
      null,
      false,
      ENABLED,
    );
    expect(same.map((c) => `${c.strategy}=${c.value}`)).not.toContain('accessibilityId=Sign in');
  });

  it('rejects volatile values and enforces the score threshold', () => {
    const nodes: MobileElementSnapshot[] = [
      { accessibilityId: 'abcdef1234567890', resourceId: null, enabled: true, clickable: true },
    ];
    expect(generateMobileCandidates(nodes, 'accessibilityId=gone', null, false, ENABLED)).toHaveLength(0);
    expect(
      generateMobileCandidates(snapshots, 'resourceId=Sign in', null, false, ENABLED, 99),
    ).toHaveLength(0);
  });

  it('respects the policy strategy allowlist', () => {
    const restricted: MobileHealingPolicy = { ...ENABLED, allowedStrategies: ['resourceid'] };
    const candidates = generateMobileCandidates(snapshots, 'resourceId=Sign in', null, false, restricted);
    expect(candidates.every((c) => c.strategy === 'resourceId')).toBe(true);
  });
});

describe('candidate validation', () => {
  const snapshots = parseHierarchySnapshots(HIERARCHY);

  it('accepts a unique enabled actionable node', async () => {
    const validation = await validateMobileCandidate(
      { strategy: 'resourceId', value: 'com.shop:id/login_btn', reason: 'r', source: 'deterministic', score: 90 },
      'tap',
      inspector(snapshots),
      ENABLED,
    );
    expect(validation.ok).toBe(true);
  });

  it('rejects zero and ambiguous matches', async () => {
    const zero = await validateMobileCandidate(
      { strategy: 'accessibilityId', value: 'nope', reason: 'r', source: 'deterministic', score: 92 },
      'tap',
      inspector(snapshots),
      ENABLED,
    );
    expect(zero.ok).toBe(false);
    // 'Sign in' appears on two nodes (one disabled): ambiguous.
    const ambiguous = await validateMobileCandidate(
      { strategy: 'accessibilityId', value: 'Sign in', reason: 'r', source: 'deterministic', score: 92 },
      'tap',
      inspector([
        { accessibilityId: 'Sign in', enabled: true, clickable: true },
        { accessibilityId: 'Sign in', enabled: true, clickable: true },
      ]),
      ENABLED,
    );
    expect(ambiguous.ok).toBe(false);
    expect(ambiguous.reason).toContain('ambiguous');
  });

  it('rejects disabled nodes for interaction and unknown strategies', async () => {
    const disabled = await validateMobileCandidate(
      { strategy: 'resourceId', value: 'com.shop:id/ghost', reason: 'r', source: 'deterministic', score: 90 },
      'tap',
      inspector(snapshots),
      ENABLED,
    );
    expect(disabled.ok).toBe(false);
    const xpath = await validateMobileCandidate(
      { strategy: 'xpath' as never, value: '//node', reason: 'r', source: 'ai', score: 0 },
      'tap',
      inspector(snapshots),
      ENABLED,
    );
    expect(xpath.ok).toBe(false);
  });

  it('rejects executable content and honours cancellation', async () => {
    const code = await validateMobileCandidate(
      { strategy: 'accessibilityId', value: 'x"); eval(1);//', reason: 'r', source: 'ai', score: 0 },
      'tap',
      inspector(snapshots),
      ENABLED,
    );
    expect(code.ok).toBe(false);
    const controller = new AbortController();
    controller.abort();
    const cancelled = await validateMobileCandidate(
      { strategy: 'accessibilityId', value: 'Sign in', reason: 'r', source: 'deterministic', score: 92 },
      'tap',
      inspector(snapshots),
      ENABLED,
      controller.signal,
    );
    expect(cancelled.ok).toBe(false);
  });
});

describe('AI candidate parsing', () => {
  it('accepts well-formed mobile candidates', () => {
    const parsed = parseMobileAiCandidates(
      JSON.stringify({ candidates: [{ strategy: 'accessibilityId', value: 'Sign in', confidence: 0.9 }] }),
      ENABLED,
    );
    expect(parsed.candidates).toHaveLength(1);
    expect(parsed.candidates[0]!.source).toBe('ai');
    expect(parsed.rejected).toHaveLength(0);
  });

  it('rejects web strategies, code, malformed JSON, and low confidence', () => {
    expect(parseMobileAiCandidates('not json', ENABLED).rejected).toHaveLength(1);
    const mixed = parseMobileAiCandidates(
      JSON.stringify({
        candidates: [
          { strategy: 'xpath', value: '//node' },
          { strategy: 'css', value: '#x' },
          { strategy: 'text', value: 'hello' },
          { strategy: 'accessibilityId', value: 'x"); executeScript(1);//' },
          { strategy: 'resourceId', value: '' },
          { strategy: 'accessibilityId', value: 'low', confidence: 0.1 },
        ],
      }),
      { ...ENABLED, minAiConfidence: 0.5 },
    );
    expect(mixed.candidates).toHaveLength(0);
    expect(mixed.rejected.length).toBeGreaterThanOrEqual(5);
  });
});

describe('healing evidence and labels', () => {
  it('builds a bounded redacted envelope without raw secrets', () => {
    const envelope = buildMobileEvidenceEnvelope(
      'tap',
      'accessibilityId=gone',
      parseHierarchySnapshots(HIERARCHY),
      { assignmentToken: 'token-1', downloadUrl: null, typedValues: ['Welcome back'] },
    );
    expect(envelope.action).toBe('tap');
    expect(envelope.domFragment.length).toBeLessThanOrEqual(4000);
    expect(envelope.domFragment).not.toContain('Welcome back');
    expect(envelope.domFragment).not.toContain('token-1');
  });

  it('labels strategies for the persisted enum surface', () => {
    expect(mobileStrategyLabel(null)).toBe('none');
    expect(
      mobileStrategyLabel({ strategy: 'accessibilityId', value: 'x', reason: 'r', source: 'deterministic', score: 1 }),
    ).toBe('TestAttribute');
    expect(
      mobileStrategyLabel({ strategy: 'resourceId', value: 'x', reason: 'r', source: 'deterministic', score: 1 }),
    ).toBe('Structural');
    expect(
      mobileStrategyLabel({ strategy: 'accessibilityId', value: 'x', reason: 'r', source: 'ai', score: 0 }),
    ).toBe('Ai');
  });

  it('sanitizes recovered values for sensitive targets', () => {
    expect(sanitizeRecoveredValue(true, 'accessibilityId=hunter2')).toBe('[REDACTED]');
    expect(sanitizeRecoveredValue(false, 'accessibilityId=login')).toBe('accessibilityId=login');
  });
});
