/**
 * Deterministic-first mobile self-healing (Slice 3C-4C).
 *
 * Mirrors the Playwright self-healing engine concepts for the Android
 * locator contract: a locator-related failure on a healable action may
 * trigger ONE retry with a recovered locator. Deterministic candidates are
 * generated from the redacted failure page-source snapshot through
 * deterministic cross-strategy relationships (never guesses) and validated
 * against a fresh snapshot; AI is an optional fallback whose output is
 * schema-validated and then validated identically.
 *
 * Hard safety rules (same as web):
 * - Healing is disabled unless the assignment policy enables it.
 * - Assertion text-match semantics, navigation, environment, auth and
 *   cancellation errors are NEVER eligible.
 * - Every candidate must resolve to EXACTLY ONE enabled, compatible node.
 *   Ambiguity (0 or 2+ matches) rejects.
 * - AI output is data only: never executed as code. Unsupported strategies
 *   and executable content are rejected before validation.
 * - At most ONE healing retry per failed step (caller enforces the guard).
 * - The stored test definition is never mutated here; the recovered locator
 *   exists only for the current execution attempt.
 *
 * Closed strategy set: accessibilityId and resourceId only — the approved
 * mobile locator contract (locators.ts). Element text is a matching signal
 * for anchoring, never an executable strategy. No XPath, no CSS, no
 * UIAutomator strings, no predicates.
 */
import { REDACTED } from './redaction.js';
import { sanitizeEvidenceText, type EvidenceSecrets } from './evidence.js';

/** Policy delivered inside the worker assignment (backend-owned, defaults off). */
export interface MobileHealingPolicy {
  enabled: boolean;
  aiFallbackEnabled: boolean;
  /** Maximum healing retries per failed step. The engine enforces <= 1. */
  maxAttemptsPerStep: number;
  /** Optional minimum deterministic candidate score (0-100). Defaults conservative. */
  minDeterministicScore?: number | null;
  /** Optional minimum AI confidence (0-1). Advisory only: never overrides validation. */
  minAiConfidence?: number | null;
  /** Allowlisted locator strategies. Defaults to the safe mobile set below. */
  allowedStrategies?: string[] | null;
}

export const DEFAULT_MOBILE_HEALING_POLICY: MobileHealingPolicy = {
  enabled: false,
  aiFallbackEnabled: false,
  maxAttemptsPerStep: 1,
};

/**
 * Strategies the healer may emit or accept. Closed: exactly the approved
 * mobile locator contract. Element text anchors candidates but is never
 * emitted as an executable locator.
 */
export const MOBILE_HEALING_STRATEGIES: readonly string[] = [
  'accessibilityid',
  'resourceid',
];

/** Actions whose failures may enter healing. Assertions of text-match, navigation, waits never heal. */
const MOBILE_HEALABLE_ACTIONS: ReadonlySet<string> = new Set([
  'tap',
  'inputtext',
  'cleartext',
  'assertvisible',
  'asserttext',
]);

/** Error-message fragments that plausibly indicate locator/hierarchy mutation. */
const LOCATOR_FAILURE_PATTERNS: readonly RegExp[] = [
  /timeout/i,
  /waiting for/i,
  /locat/i,
  /no element/i,
  /element not found/i,
  /could not find/i,
  /not interactable/i,
  /not visible/i,
  /not enabled/i,
  /resolved to 0/i,
  /selector/i,
  /stale/i,
  /detached/i,
];

/** Fragments that disqualify healing even when a locator pattern matches. */
const NON_HEALABLE_PATTERNS: readonly RegExp[] = [
  /did not match/i,
  /expected text/i,
  /assertion/i,
  /invalid session/i,
  /no such session/i,
  /session .*deleted/i,
  /econnrefused/i,
  /fetch failed/i,
  /socket hang up/i,
  /device offline/i,
  /device unauthorized/i,
  /device not found/i,
  /cancelled via api/i,
  /execution timeout/i,
  /unsupported action/i,
  /requires a /i,
  /only android/i,
  /no longer active/i,
  /no keyboard/i,
];

/**
 * Narrowly scoped locator-failure predicate (mirrors web isHealingEligible).
 * Healing-eligible = healable action + locator-like message + no
 * non-healable signal. Text-match mismatches always return false; for
 * assertText only locator recovery (never expectation rewriting) heals.
 */
export function isMobileHealingEligible(action: string, errorMessage: string | null | undefined): boolean {
  const normalizedAction = (action ?? '').trim().toLowerCase();
  if (!MOBILE_HEALABLE_ACTIONS.has(normalizedAction)) return false;
  if (!errorMessage || errorMessage.length === 0) return false;
  if (NON_HEALABLE_PATTERNS.some((pattern) => pattern.test(errorMessage))) return false;
  return LOCATOR_FAILURE_PATTERNS.some((pattern) => pattern.test(errorMessage));
}

/** Normalizes an untrusted policy to safe values. Healing stays off unless explicitly enabled. */
export function normalizeMobileHealingPolicy(raw: unknown): MobileHealingPolicy {
  if (!raw || typeof raw !== 'object') return { ...DEFAULT_MOBILE_HEALING_POLICY };
  const input = raw as Partial<MobileHealingPolicy>;
  const enabled = input.enabled === true;
  const allowed = Array.isArray(input.allowedStrategies)
    ? input.allowedStrategies
        .filter((s): s is string => typeof s === 'string')
        .map((s) => s.trim().toLowerCase())
        .filter((s) => MOBILE_HEALING_STRATEGIES.includes(s))
    : [...MOBILE_HEALING_STRATEGIES];
  return {
    enabled,
    aiFallbackEnabled: enabled && input.aiFallbackEnabled === true,
    maxAttemptsPerStep: 1,
    minDeterministicScore:
      typeof input.minDeterministicScore === 'number' && Number.isFinite(input.minDeterministicScore)
        ? Math.min(100, Math.max(0, Math.floor(input.minDeterministicScore)))
        : null,
    minAiConfidence:
      typeof input.minAiConfidence === 'number' && Number.isFinite(input.minAiConfidence)
        ? Math.min(1, Math.max(0, input.minAiConfidence))
        : null,
    allowedStrategies: allowed,
  };
}

/** One observed hierarchy node: stable, non-volatile attributes only. */
export interface MobileElementSnapshot {
  accessibilityId?: string | null;
  resourceId?: string | null;
  text?: string | null;
  className?: string | null;
  enabled?: boolean;
  clickable?: boolean;
}

/** A recovery candidate: locator data only, never code. */
export interface MobileHealingCandidate {
  strategy: 'accessibilityId' | 'resourceId';
  value: string;
  reason: string;
  /** deterministic | ai */
  source: 'deterministic' | 'ai';
  /** 0-100 deterministic score; AI candidates carry the provider confidence separately. */
  score: number;
  aiConfidence?: number | null;
}

/** Fresh-snapshot inspector. Driver backing in action wiring; fakes in tests. */
export interface MobileHealingInspector {
  /** Fresh hierarchy snapshots for generation and uniqueness probing. */
  snapshot(): Promise<MobileElementSnapshot[]>;
  /** Compatibility probe for one node shape. */
  describe(node: MobileElementSnapshot, action: string): { enabled: boolean; actionable: boolean };
}

/** Maximum candidates generated per healing attempt (bounded work). */
export const MAX_MOBILE_CANDIDATES = 8;
/** Maximum hierarchy nodes inspected per healing attempt (bounded evidence). */
export const MAX_MOBILE_SNAPSHOTS = 60;
/** Maximum characters of evidence text sent for AI suggestion. */
export const MAX_MOBILE_EVIDENCE_CHARS = 4000;

/** Volatile/generated values that must never seed a candidate. */
const MOBILE_VOLATILE_PATTERNS: readonly RegExp[] = [
  /^[a-f0-9]{8,}(-[a-f0-9]{4,})?$/i,
  /\d{10,}/,
  /^[a-z]+-\d+-\d+$/i,
];

function isVolatile(value: string): boolean {
  return MOBILE_VOLATILE_PATTERNS.some((pattern) => pattern.test(value));
}

function strategyAllowed(strategy: string, policy: MobileHealingPolicy): boolean {
  const allowed = policy.allowedStrategies ?? [...MOBILE_HEALING_STRATEGIES];
  return allowed.some((s) => s.toLowerCase() === strategy.toLowerCase());
}

const NODE_PATTERN = /<node\b([^>]*?)\/?>/g;

function attr(attrs: string, name: string): string | null {
  const match = new RegExp(`${name}="([^"]*)"`).exec(attrs);
  const value = match?.[1]?.trim() ?? '';
  return value.length > 0 ? value : null;
}

function flag(attrs: string, name: string): boolean {
  return attr(attrs, name)?.toLowerCase() === 'true';
}

/**
 * Parses Android hierarchy XML into bounded node snapshots. Best-effort:
 * malformed input yields no snapshots (healing then fails closed).
 */
export function parseHierarchySnapshots(xml: string, limit: number = MAX_MOBILE_SNAPSHOTS): MobileElementSnapshot[] {
  const out: MobileElementSnapshot[] = [];
  if (!xml || xml.length === 0) return out;
  NODE_PATTERN.lastIndex = 0;
  let match: RegExpExecArray | null;
  while ((match = NODE_PATTERN.exec(xml)) !== null && out.length < Math.max(1, Math.min(limit, MAX_MOBILE_SNAPSHOTS))) {
    const attrs = match[1] ?? '';
    out.push({
      accessibilityId: attr(attrs, 'content-desc'),
      resourceId: attr(attrs, 'resource-id'),
      text: attr(attrs, 'text'),
      className: attr(attrs, 'class'),
      enabled: flag(attrs, 'enabled'),
      clickable: flag(attrs, 'clickable'),
    });
  }
  return out;
}

function originalParts(target: string | null | undefined): { strategy: string | null; value: string | null } {
  if (!target) return { strategy: null, value: null };
  const lower = target.trim().toLowerCase();
  for (const prefix of ['accessibilityid=', 'resourceid=']) {
    if (lower.startsWith(prefix)) {
      const value = target.trim().slice(prefix.length).trim();
      return value.length === 0 ? { strategy: null, value: null } : { strategy: prefix.slice(0, -1), value };
    }
  }
  return { strategy: null, value: null };
}

/**
 * Deterministic candidate generation. Candidates express deterministic
 * relationships to the failed locator — never guesses:
 * - cross-strategy token match (same token under the other strategy),
 * - text anchor (assertText only: the expected text names the node whose
 *   stable identifiers are emitted).
 * Values equal to the original target and volatile values never qualify.
 */
export function generateMobileCandidates(
  snapshots: MobileElementSnapshot[],
  originalTarget: string | null | undefined,
  expectedText: string | null | undefined,
  isAssertText: boolean,
  policy: MobileHealingPolicy,
  minScore?: number,
): MobileHealingCandidate[] {
  const threshold = minScore ?? policy.minDeterministicScore ?? 50;
  const out: MobileHealingCandidate[] = [];
  const seen = new Set<string>();
  const original = originalParts(originalTarget);
  const push = (candidate: MobileHealingCandidate): void => {
    if (out.length >= MAX_MOBILE_CANDIDATES) return;
    if (!strategyAllowed(candidate.strategy, policy)) return;
    if (candidate.score < threshold) return;
    if (candidate.value.length === 0 || candidate.value.length > 500) return;
    if (isVolatile(candidate.value)) return;
    const key = `${candidate.strategy}=${candidate.value}`;
    if (original.strategy !== null && `${original.strategy}=${original.value}`.toLowerCase() === key.toLowerCase()) return;
    if (seen.has(key.toLowerCase())) return;
    seen.add(key.toLowerCase());
    out.push(candidate);
  };

  for (const snapshot of snapshots.slice(0, MAX_MOBILE_SNAPSHOTS)) {
    // Cross-strategy token match: the failed token observed under the
    // sibling strategy on a live node.
    if (original.value && original.value.length > 0) {
      if (
        original.strategy !== 'accessibilityid' &&
        snapshot.accessibilityId === original.value
      ) {
        push({
          strategy: 'accessibilityId',
          value: snapshot.accessibilityId,
          reason: 'Failed token observed as accessibility id on a live node.',
          source: 'deterministic',
          score: 92,
        });
      }
      if (
        original.strategy !== 'resourceid' &&
        snapshot.resourceId === original.value
      ) {
        push({
          strategy: 'resourceId',
          value: snapshot.resourceId,
          reason: 'Failed token observed as resource id on a live node.',
          source: 'deterministic',
          score: 90,
        });
      }
    }
    // Text anchor (assertText only): the expected text names the node.
    const expected = (expectedText ?? '').trim();
    if (isAssertText && expected.length > 0 && expected.length <= 200 && snapshot.text === expected) {
      if (snapshot.accessibilityId && !isVolatile(snapshot.accessibilityId)) {
        push({
          strategy: 'accessibilityId',
          value: snapshot.accessibilityId,
          reason: 'Expected text observed on a node with a stable accessibility id.',
          source: 'deterministic',
          score: 88,
        });
      }
      if (snapshot.resourceId && !isVolatile(snapshot.resourceId)) {
        push({
          strategy: 'resourceId',
          value: snapshot.resourceId,
          reason: 'Expected text observed on a node with a stable resource id.',
          source: 'deterministic',
          score: 85,
        });
      }
    }
  }
  return out;
}

/** Element compatibility per healable action (tap/input need actionable nodes; asserts need existence). */
function isActionCompatible(action: string, node: { enabled: boolean; actionable: boolean }): boolean {
  switch (action) {
    case 'tap':
      return node.actionable;
    case 'inputtext':
    case 'cleartext':
      return node.enabled;
    case 'assertvisible':
    case 'asserttext':
      return true;
    default:
      return false;
  }
}

export interface MobileHealingValidation {
  ok: boolean;
  reason: string;
}

/**
 * Candidate validation against a fresh snapshot. Acceptance requires:
 * exactly one matching node, enabled/actionable as the action needs,
 * supported strategy, bounded value, no executable content.
 */
export async function validateMobileCandidate(
  candidate: MobileHealingCandidate,
  action: string,
  inspector: MobileHealingInspector,
  policy: MobileHealingPolicy,
  signal?: AbortSignal,
): Promise<MobileHealingValidation> {
  if (signal?.aborted) return { ok: false, reason: 'Healing cancelled.' };
  if (!MOBILE_HEALING_STRATEGIES.some((s) => s.toLowerCase() === candidate.strategy.toLowerCase())) {
    return { ok: false, reason: `Unsupported locator strategy '${candidate.strategy}'.` };
  }
  if (!strategyAllowed(candidate.strategy, policy)) {
    return { ok: false, reason: `Strategy '${candidate.strategy}' is not allowed by policy.` };
  }
  if (candidate.value.length === 0 || candidate.value.length > 500) {
    return { ok: false, reason: 'Candidate locator is empty or exceeds bounds.' };
  }
  if (looksLikeMobileCode(candidate.value) || looksLikeMobileCode(candidate.strategy)) {
    return { ok: false, reason: 'Candidate contains executable content and was rejected.' };
  }
  let snapshots: MobileElementSnapshot[];
  try {
    snapshots = await inspector.snapshot();
  } catch (error) {
    return {
      ok: false,
      reason: `Candidate probe failed: ${error instanceof Error ? error.message.split('\n')[0] : 'unknown'}`,
    };
  }
  const key = candidate.strategy.toLowerCase();
  const matches = snapshots.filter((node) =>
    key === 'accessibilityid' ? node.accessibilityId === candidate.value : node.resourceId === candidate.value,
  );
  if (matches.length === 0) return { ok: false, reason: 'Candidate matched zero nodes.' };
  if (matches.length > 1) {
    return { ok: false, reason: `Candidate matched ${matches.length} nodes; ambiguous candidates are rejected.` };
  }
  const probe = inspector.describe(matches[0]!, action);
  if (!probe.enabled && action !== 'assertvisible' && action !== 'asserttext') {
    return { ok: false, reason: 'Candidate resolved to a disabled node.' };
  }
  if (!isActionCompatible(action, probe)) {
    return { ok: false, reason: `Candidate node is incompatible with action '${action}'.` };
  }
  return { ok: true, reason: 'Candidate uniquely resolved to a compatible actionable node.' };
}

/** Rejects anything that smells like code rather than a locator value. */
function looksLikeMobileCode(value: string): boolean {
  const lower = value.toLowerCase();
  return (
    lower.includes('javascript:') ||
    lower.includes('<script') ||
    lower.includes('eval(') ||
    /(^|[^a-z])function\s*\(/.test(lower) ||
    lower.includes('child_process') ||
    lower.includes('process.env') ||
    lower.includes('require(') ||
    lower.includes('import(') ||
    lower.includes('xpath') ||
    lower.includes('uiautomator') ||
    lower.includes('execute') ||
    /\b(settimeout|setinterval)\s*\(/.test(lower)
  );
}

export interface ParsedMobileAiCandidates {
  candidates: MobileHealingCandidate[];
  rejected: string[];
}

/**
 * Schema validation for AI locator output. Accepts ONLY
 * { candidates: [{ strategy, value, reason? }] } with mobile-allowlisted
 * strategies and bounded lengths. Malformed JSON, code, unsupported
 * strategies and oversized payloads are rejected with reasons. Never throws.
 */
export function parseMobileAiCandidates(raw: string, policy: MobileHealingPolicy): ParsedMobileAiCandidates {
  const rejected: string[] = [];
  const accepted: MobileHealingCandidate[] = [];
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return { candidates: [], rejected: ['AI output was not valid JSON.'] };
  }
  const list = (parsed as { candidates?: unknown }).candidates;
  if (!Array.isArray(list)) {
    return { candidates: [], rejected: ['AI output did not contain a candidates array.'] };
  }
  for (const entry of list.slice(0, MAX_MOBILE_CANDIDATES)) {
    if (accepted.length >= MAX_MOBILE_CANDIDATES) break;
    if (!entry || typeof entry !== 'object') {
      rejected.push('AI candidate entry was not an object.');
      continue;
    }
    const record = entry as Record<string, unknown>;
    const strategy = typeof record.strategy === 'string' ? record.strategy.trim().toLowerCase() : '';
    const value = typeof record.value === 'string' ? record.value.trim() : '';
    const reason = typeof record.reason === 'string' ? record.reason.slice(0, 300) : 'AI-suggested locator.';
    const confidence =
      typeof record.confidence === 'number' && Number.isFinite(record.confidence) ? record.confidence : null;
    if (!MOBILE_HEALING_STRATEGIES.includes(strategy)) {
      rejected.push(`Unsupported AI strategy '${String(record.strategy ?? '').slice(0, 40)}'.`);
      continue;
    }
    if (!strategyAllowed(strategy, policy)) {
      rejected.push(`AI strategy '${strategy}' is not allowed by policy.`);
      continue;
    }
    if (value.length === 0 || value.length > 500) {
      rejected.push('AI candidate value was empty or exceeded bounds.');
      continue;
    }
    if (looksLikeMobileCode(value) || looksLikeMobileCode(strategy)) {
      rejected.push('AI candidate contained executable content and was rejected.');
      continue;
    }
    if (policy.minAiConfidence != null && confidence != null && confidence < policy.minAiConfidence) {
      rejected.push(`AI candidate confidence ${confidence} below policy minimum.`);
      continue;
    }
    accepted.push({
      strategy: strategy === 'accessibilityid' ? 'accessibilityId' : 'resourceId',
      value,
      reason,
      source: 'ai',
      score: 0,
      aiConfidence: confidence,
    });
  }
  return { candidates: accepted, rejected };
}

/** Bounded redacted evidence envelope handed to the suggest endpoint (never secrets). */
export interface MobileHealingEvidenceEnvelope {
  action: string;
  originalTarget: string;
  domFragment: string;
  attributes: string[];
  nearbyText: string[];
}

export function buildMobileEvidenceEnvelope(
  action: string,
  originalTarget: string | null | undefined,
  snapshots: MobileElementSnapshot[],
  secrets: EvidenceSecrets,
): MobileHealingEvidenceEnvelope {
  const attributes: string[] = [];
  const nearbyText: string[] = [];
  for (const snapshot of snapshots.slice(0, MAX_MOBILE_SNAPSHOTS)) {
    const parts: string[] = [];
    if (snapshot.accessibilityId) parts.push(`accessibilityId=${snapshot.accessibilityId.slice(0, 120)}`);
    if (snapshot.resourceId) parts.push(`resourceId=${snapshot.resourceId.slice(0, 120)}`);
    if (parts.length > 0) attributes.push(parts.join(' '));
    if (snapshot.text && snapshot.text.trim().length > 0 && snapshot.text.trim().length <= 80) {
      nearbyText.push(snapshot.text.trim());
    }
    if (attributes.length >= 20 && nearbyText.length >= 20) break;
  }
  const target = originalTarget ?? '';
  return {
    action,
    originalTarget: target.length <= 500 ? target : target.slice(0, 500),
    domFragment: sanitizeEvidenceText(
      [...attributes, ...nearbyText.map((t) => `text=${t}`)].join('\n'),
      secrets,
      MAX_MOBILE_EVIDENCE_CHARS,
      false,
    ),
    attributes: attributes.slice(0, 20),
    nearbyText: nearbyText.slice(0, 20),
  };
}

/** Structured healing log event names (emitted through the existing log stream). */
export const MobileHealingEvent = {
  Started: 'self-healing.started',
  CandidateValidated: 'self-healing.candidate_validated',
  Applied: 'self-healing.applied',
  Failed: 'self-healing.failed',
  Skipped: 'self-healing.skipped',
} as const;

export type MobileHealingEventName = (typeof MobileHealingEvent)[keyof typeof MobileHealingEvent];

/** Persisted healing outcome for one step (worker → backend, execution-scoped). */
export interface MobileHealingAttemptRecord {
  stepOrder: number;
  stepAction: string;
  originalStrategy: string | null;
  originalValue: string | null;
  recoveredStrategy: string | null;
  recoveredValue: string | null;
  healingStrategy: string;
  status: 'Applied' | 'Failed';
  candidateCount: number;
  wasApplied: boolean;
  isAiAssisted: boolean;
  errorMessage: string | null;
}

/**
 * Maps an internal candidate to the persisted healing-strategy label.
 * Labels reuse the backend SelfHealingStrategy enum surface (parseable,
 * no model change): accessibilityId → TestAttribute, resourceId →
 * Structural, ai → Ai, none → none.
 */
export function mobileStrategyLabel(candidate: MobileHealingCandidate | null): string {
  if (!candidate) return 'none';
  if (candidate.source === 'ai') return 'Ai';
  switch (candidate.strategy.toLowerCase()) {
    case 'accessibilityid':
      return 'TestAttribute';
    case 'resourceid':
      return 'Structural';
    default:
      return 'none';
  }
}

/** Sanitizes a recovered locator value for results: sensitive targets never travel readable. */
export function sanitizeRecoveredValue(isSensitive: boolean, target: string): string {
  return isSensitive ? REDACTED : target.slice(0, 500);
}
