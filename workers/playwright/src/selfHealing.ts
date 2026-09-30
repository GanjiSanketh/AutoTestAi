/**
 * Self-healing test engine (Phase 2 Slice 11).
 *
 * Deterministic-first recovery around the existing structured-step interpreter:
 * a locator-related failure on a healable action may trigger ONE retry with a
 * recovered locator. Deterministic candidates are generated from observed DOM
 * evidence and validated against the live page; AI is an optional fallback
 * whose output is schema-validated and then validated identically.
 *
 * Hard safety rules (see docs/04-Architecture.md §self-healing):
 * - Healing is disabled unless the assignment policy enables it.
 * - Assertion failures (assertText/assertValue mismatches), navigation,
 *   environment, auth, network and cancellation errors are NEVER eligible.
 * - Every candidate must resolve to EXACTLY ONE visible, enabled,
 *   action-compatible element. Ambiguity (0 or 2+ matches) rejects.
 * - locator.first()/last()/nth() ambiguity suppression is NEVER used.
 * - AI output is data only: never executed as code. Unsupported strategies,
 *   JavaScript, eval/Function, shell or selectors outside the allowlist
 *   are rejected before validation.
 * - At most ONE healing retry per failed step (caller enforces the guard).
 * - The stored test definition is never mutated here; the recovered locator
 *   exists only for the current execution attempt.
 */

import { parseTarget, type LocatorKind } from './locators.js';
import { isSensitiveTarget, REDACTED } from './redaction.js';

/** Policy delivered inside the worker assignment (backend-owned, defaults off). */
export interface SelfHealingPolicy {
  enabled: boolean;
  aiFallbackEnabled: boolean;
  /** Maximum healing retries per failed step. The engine enforces <= 1. */
  maxAttemptsPerStep: number;
  /** Optional minimum deterministic candidate score (0-100). Defaults conservative. */
  minDeterministicScore?: number;
  /** Optional minimum AI confidence (0-1). Advisory only: never overrides validation. */
  minAiConfidence?: number | null;
  /** Allowlisted locator strategies. Defaults to the safe set below. */
  allowedStrategies?: string[];
}

export const DEFAULT_POLICY: SelfHealingPolicy = {
  enabled: false,
  aiFallbackEnabled: false,
  maxAttemptsPerStep: 1,
};

/** Strategies the healer may emit or accept (subset of parseTarget kinds). */
export const ALLOWED_STRATEGIES: readonly string[] = [
  'css',
  'xpath',
  'role',
  'text',
  'testid',
];

/** Actions whose failures may enter healing. Assertions navigate/wait never heal. */
const HEALABLE_ACTIONS: ReadonlySet<string> = new Set([
  'click',
  'fill',
  'type',
  'select',
  'check',
  'uncheck',
  'press',
  'assertvisible',
]);

/** Error-message fragments that plausibly indicate locator/DOM mutation. */
const LOCATOR_FAILURE_PATTERNS: readonly RegExp[] = [
  /timeout/i,
  /waiting for/i,
  / Locat/i,
  /no element/i,
  /element not found/i,
  /could not find/i,
  /strict mode/i,
  /resolved to 0/i,
  /selector/i,
  /Target closed/i,
  /element is not visible/i,
  /element is not enabled/i,
  /not visible/i,
  /detached/i,
  /stale/i,
];

/** Fragments that disqualify healing even when a locator pattern matches. */
const NON_HEALABLE_PATTERNS: readonly RegExp[] = [
  /expected text/i,
  /expected value/i,
  /assertion/i,
  /http\s+\d{3}/i,
  /status code/i,
  /net::/i,
  /navigation/i,
  /401|unauthori[sz]ed/i,
  /403|forbidden/i,
  /cancelled|canceled|aborted/i,
  /execution timeout/i,
  /unsupported action/i,
  /requires a (target|value)/i,
];

/**
 * Narrowly scoped locator-failure predicate (Slice 11 §4).
 * Healing-eligible = healable action + locator-like message + no
 * non-healable signal. Assertion mismatches always return false.
 */
export function isHealingEligible(action: string, errorMessage: string | null | undefined): boolean {
  const normalizedAction = (action ?? '').trim().toLowerCase();
  if (!HEALABLE_ACTIONS.has(normalizedAction)) return false;
  if (normalizedAction === 'asserttext' || normalizedAction === 'assertvalue') return false;
  if (!errorMessage || errorMessage.length === 0) return false;
  if (NON_HEALABLE_PATTERNS.some((pattern) => pattern.test(errorMessage))) return false;
  return LOCATOR_FAILURE_PATTERNS.some((pattern) => pattern.test(errorMessage));
}

/** One observed element: only stable, non-volatile attributes are collected. */
export interface HealingElementSnapshot {
  testId?: string | null;
  dataTest?: string | null;
  dataQa?: string | null;
  ariaLabel?: string | null;
  role?: string | null;
  accessibleName?: string | null;
  label?: string | null;
  id?: string | null;
  inputName?: string | null;
  text?: string | null;
  tagName?: string | null;
  inputType?: string | null;
  visible?: boolean;
  enabled?: boolean;
}

/** A recovery candidate: locator data only, never code. */
export interface HealingCandidate {
  strategy: string;
  value: string;
  reason: string;
  /** deterministic | ai */
  source: 'deterministic' | 'ai';
  /** 0-100 deterministic score; AI candidates carry the provider confidence separately. */
  score: number;
  aiConfidence?: number | null;
}

/** Live-DOM inspector. Real Playwright backing in server wiring; fakes in tests. */
export interface HealingInspector {
  /** Bounded snapshots of interactable elements for deterministic generation. */
  collectSnapshots(limit: number): Promise<HealingElementSnapshot[]>;
  /** Exact match count for a candidate target (never first()/nth()). */
  countMatches(target: string): Promise<number>;
  /** Compatibility probe for a candidate target. */
  describeMatch(
    target: string,
  ): Promise<{ count: number; tagName: string | null; visible: boolean; enabled: boolean }>;
}

export interface HealingValidation {
  ok: boolean;
  reason: string;
}

/** Maximum candidates generated per healing attempt (bounded work). */
export const MAX_CANDIDATES = 8;
/** Maximum DOM snapshots inspected per healing attempt (bounded evidence). */
export const MAX_SNAPSHOTS = 60;
/** Maximum characters of evidence text sent to an AI provider. */
export const MAX_EVIDENCE_CHARS = 4000;

/** Volatile/generated attribute values that must never seed a candidate. */
const VOLATILE_PATTERNS: readonly RegExp[] = [
  /^[a-f0-9]{8,}(-[a-f0-9]{4,})?$/i,
  /\d{10,}/,
  /__BV_|ember\d+|react-select|mui-|chakra-|css-[a-z0-9]+/i,
  /^[a-z]+-\d+-\d+$/i,
];

function isVolatile(value: string): boolean {
  return VOLATILE_PATTERNS.some((pattern) => pattern.test(value));
}

function strategyAllowed(strategy: string, policy: SelfHealingPolicy): boolean {
  const allowed = policy.allowedStrategies ?? [...ALLOWED_STRATEGIES];
  return allowed.some((s) => s.toLowerCase() === strategy.toLowerCase());
}

/**
 * Deterministic candidate generation (Slice 11 §7-8, strategy order
 * testid → role → label → text → stable id/name → structural).
 * Only observed evidence seeds candidates; volatile values are skipped.
 */
export function generateDeterministicCandidates(
  snapshots: HealingElementSnapshot[],
  originalTarget: string,
  policy: SelfHealingPolicy,
  minScore?: number,
): HealingCandidate[] {
  const threshold = minScore ?? policy.minDeterministicScore ?? 50;
  const out: HealingCandidate[] = [];
  const seen = new Set<string>();
  const push = (candidate: HealingCandidate): void => {
    if (out.length >= MAX_CANDIDATES) return;
    if (!strategyAllowed(candidate.strategy, policy)) return;
    if (candidate.score < threshold) return;
    const key = `${candidate.strategy}=${candidate.value}`;
    if (key === originalTarget.trim() || seen.has(key)) return;
    seen.add(key);
    out.push(candidate);
  };

  for (const snapshot of snapshots.slice(0, MAX_SNAPSHOTS)) {
    const testId = snapshot.testId ?? snapshot.dataTest ?? snapshot.dataQa;
    if (testId && !isVolatile(testId)) {
      push({
        strategy: 'testid',
        value: testId,
        reason: 'Stable test attribute observed in current DOM.',
        source: 'deterministic',
        score: 95,
      });
    }
    if (snapshot.role && snapshot.accessibleName) {
      push({
        strategy: 'role',
        value: `${snapshot.role}|${snapshot.accessibleName}`,
        reason: 'Accessibility role with accessible name observed in current DOM.',
        source: 'deterministic',
        score: 90,
      });
    } else if (snapshot.role && !snapshot.accessibleName) {
      push({
        strategy: 'role',
        value: snapshot.role,
        reason: 'Accessibility role observed in current DOM.',
        source: 'deterministic',
        score: 60,
      });
    }
    if (snapshot.label) {
      push({
        strategy: 'css',
        value: `label:has-text("${snapshot.label.slice(0, 80)}")`,
        reason: 'Associated label observed in current DOM.',
        source: 'deterministic',
        score: 75,
      });
    }
    if (snapshot.ariaLabel && !isVolatile(snapshot.ariaLabel)) {
      push({
        strategy: 'css',
        value: `[aria-label="${snapshot.ariaLabel.slice(0, 120)}"]`,
        reason: 'Stable aria-label observed in current DOM.',
        source: 'deterministic',
        score: 85,
      });
    }
    if (snapshot.text && snapshot.text.trim().length > 0 && snapshot.text.trim().length <= 80) {
      push({
        strategy: 'text',
        value: snapshot.text.trim(),
        reason: 'Exact visible text observed in current DOM.',
        source: 'deterministic',
        score: 70,
      });
    }
    if (snapshot.id && !isVolatile(snapshot.id)) {
      push({
        strategy: 'css',
        value: `#${snapshot.id}`,
        reason: 'Stable element id observed in current DOM.',
        source: 'deterministic',
        score: 80,
      });
    }
    if (snapshot.inputName && !isVolatile(snapshot.inputName)) {
      push({
        strategy: 'css',
        value: `[name="${snapshot.inputName}"]`,
        reason: 'Stable input name observed in current DOM.',
        source: 'deterministic',
        score: 78,
      });
    }
  }
  return out;
}

/** Element types compatible with each healable action (Slice 11 §10). */
function isActionCompatible(action: string, tagName: string | null, inputType: string | null): boolean {
  const tag = (tagName ?? '').toUpperCase();
  switch (action) {
    case 'click':
    case 'press':
    case 'assertvisible':
      return true;
    case 'fill':
    case 'type':
      return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || inputType != null;
    case 'select':
      return tag === 'SELECT';
    case 'check':
    case 'uncheck':
      return tag === 'INPUT';
    default:
      return false;
  }
}

function candidateTarget(candidate: HealingCandidate): string {
  return candidate.strategy === 'css' || candidate.strategy === 'xpath'
    ? `${candidate.strategy}=${candidate.value}`
    : `${candidate.strategy}=${candidate.value}`;
}

/**
 * Candidate validation against the live DOM (Slice 11 §9).
 * Acceptance requires: exactly one match, visible + enabled when the action
 * needs interaction, compatible element, supported strategy, bounded probe.
 * Zero matches, ambiguity, hidden/disabled or incompatible elements reject.
 */
export async function validateCandidate(
  candidate: HealingCandidate,
  action: string,
  inspector: HealingInspector,
  policy: SelfHealingPolicy,
  signal?: AbortSignal,
): Promise<HealingValidation> {
  if (signal?.aborted) return { ok: false, reason: 'Healing cancelled.' };
  if (!ALLOWED_STRATEGIES.some((s) => s.toLowerCase() === candidate.strategy.toLowerCase())) {
    return { ok: false, reason: `Unsupported locator strategy '${candidate.strategy}'.` };
  }
  if (!strategyAllowed(candidate.strategy, policy)) {
    return { ok: false, reason: `Strategy '${candidate.strategy}' is not allowed by policy.` };
  }
  const parsed = parseTarget(candidateTarget(candidate));
  if (!parsed || parsed.selector.length === 0 || parsed.selector.length > 2000) {
    return { ok: false, reason: 'Candidate locator is empty or exceeds bounds.' };
  }
  if (looksLikeCode(parsed.selector)) {
    return { ok: false, reason: 'Candidate contains executable content and was rejected.' };
  }
  let probe: { count: number; tagName: string | null; visible: boolean; enabled: boolean };
  try {
    probe = await inspector.describeMatch(candidateTarget(candidate));
  } catch (error) {
    return {
      ok: false,
      reason: `Candidate probe failed: ${error instanceof Error ? error.message.split('\n')[0] : 'unknown'}`,
    };
  }
  if (probe.count === 0) return { ok: false, reason: 'Candidate matched zero elements.' };
  if (probe.count > 1) {
    return { ok: false, reason: `Candidate matched ${probe.count} elements; ambiguous candidates are rejected.` };
  }
  const needsActionable = action !== 'assertvisible';
  if (!probe.visible && needsActionable) {
    return { ok: false, reason: 'Candidate resolved to a hidden element.' };
  }
  if (!probe.enabled && needsActionable) {
    return { ok: false, reason: 'Candidate resolved to a disabled element.' };
  }
  if (!isActionCompatible(action, probe.tagName, null)) {
    return { ok: false, reason: `Candidate element <${probe.tagName ?? 'unknown'}> is incompatible with action '${action}'.` };
  }
  return { ok: true, reason: 'Candidate uniquely resolved to a compatible actionable element.' };
}

/** Rejects anything that smells like code rather than a locator value. */
function looksLikeCode(value: string): boolean {
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
    /\b(settimeout|setinterval)\s*\(/.test(lower)
  );
}

export interface ParsedAiCandidates {
  candidates: HealingCandidate[];
  rejected: string[];
}

/**
 * Schema validation for AI locator output (Slice 11 §13).
 * Accepts ONLY { candidates: [{ strategy, value, reason? }] } with allowlisted
 * strategies and bounded lengths. Malformed JSON, code, unsupported strategies
 * and oversized payloads are rejected with reasons. Never throws.
 */
export function parseAiCandidates(raw: string, policy: SelfHealingPolicy): ParsedAiCandidates {
  const rejected: string[] = [];
  const accepted: HealingCandidate[] = [];
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
  for (const entry of list.slice(0, MAX_CANDIDATES)) {
    if (accepted.length >= MAX_CANDIDATES) break;
    if (!entry || typeof entry !== 'object') {
      rejected.push('AI candidate entry was not an object.');
      continue;
    }
    const record = entry as Record<string, unknown>;
    const strategy = typeof record.strategy === 'string' ? record.strategy.trim().toLowerCase() : '';
    const value = typeof record.value === 'string' ? record.value.trim() : '';
    const reason = typeof record.reason === 'string' ? record.reason.slice(0, 300) : 'AI-suggested locator.';
    const confidence =
      typeof record.confidence === 'number' && Number.isFinite(record.confidence)
        ? record.confidence
        : null;
    if (!ALLOWED_STRATEGIES.includes(strategy)) {
      rejected.push(`Unsupported AI strategy '${String(record.strategy ?? '').slice(0, 40)}'.`);
      continue;
    }
    if (!strategyAllowed(strategy, policy)) {
      rejected.push(`AI strategy '${strategy}' is not allowed by policy.`);
      continue;
    }
    if (value.length === 0 || value.length > 2000) {
      rejected.push('AI candidate value was empty or exceeded bounds.');
      continue;
    }
    if (looksLikeCode(value) || looksLikeCode(strategy)) {
      rejected.push('AI candidate contained executable content and was rejected.');
      continue;
    }
    if (
      policy.minAiConfidence != null &&
      confidence != null &&
      confidence < policy.minAiConfidence
    ) {
      rejected.push(`AI candidate confidence ${confidence} below policy minimum.`);
      continue;
    }
    // xpath= prefix inside an xpath value would double-prefix; normalize.
    const normalizedValue =
      strategy === 'xpath' && value.toLowerCase().startsWith('xpath=')
        ? value.slice('xpath='.length)
        : value;
    accepted.push({ strategy, value: normalizedValue, reason, source: 'ai', score: 0, aiConfidence: confidence });
  }
  return { candidates: accepted, rejected };
}

/** Bounded redacted evidence envelope handed to an AI provider (never secrets). */
export interface HealingEvidenceEnvelope {
  action: string;
  originalTarget: string;
  domFragment: string;
  attributes: string[];
  nearbyText: string[];
}

export function buildEvidenceEnvelope(
  action: string,
  originalTarget: string,
  snapshots: HealingElementSnapshot[],
): HealingEvidenceEnvelope {
  const attributes: string[] = [];
  const nearbyText: string[] = [];
  for (const snapshot of snapshots.slice(0, MAX_SNAPSHOTS)) {
    const parts: string[] = [];
    if (snapshot.testId) parts.push(`testid=${truncate(snapshot.testId, 80)}`);
    if (snapshot.ariaLabel) parts.push(`aria-label=${truncate(snapshot.ariaLabel, 80)}`);
    if (snapshot.role) {
      parts.push(snapshot.accessibleName ? `role=${snapshot.role}|${truncate(snapshot.accessibleName, 80)}` : `role=${snapshot.role}`);
    }
    if (snapshot.id && !isVolatile(snapshot.id)) parts.push(`id=${snapshot.id}`);
    if (snapshot.inputName) parts.push(`name=${snapshot.inputName}`);
    if (parts.length > 0) attributes.push(parts.join(' '));
    if (snapshot.text && snapshot.text.trim().length > 0) {
      nearbyText.push(truncate(snapshot.text.trim(), 80));
    }
    if (attributes.length >= 20 && nearbyText.length >= 20) break;
  }
  const domFragment = [...attributes, ...nearbyText.map((t) => `text=${t}`)]
    .join('\n')
    .slice(0, MAX_EVIDENCE_CHARS);
  return {
    action,
    originalTarget: redactTarget(originalTarget),
    domFragment: redactSecrets(domFragment),
    attributes: attributes.map((a) => redactSecrets(a)).slice(0, 20),
    nearbyText: nearbyText.map((t) => redactSecrets(t)).slice(0, 20),
  };
}

function truncate(value: string, max: number): string {
  return value.length <= max ? value : `${value.slice(0, max)}…`;
}

function redactTarget(target: string): string {
  return isSensitiveTarget(target) ? REDACTED : target.slice(0, 500);
}

/** Redacts credential-looking fragments before AI transmission/persistence/logging. */
export function redactSecrets(value: string): string {
  return value
    .replace(/("(?:password|passwd|pwd|api[_-]?key|secret|client[_-]?secret|access[_-]?token|refresh[_-]?token|auth[_-]?token|id[_-]?token|session[_-]?token|private[_-]?key)"\s*:\s*")[^"\\]*(")/gi, `$1${REDACTED}$2`)
    .replace(/\b(password|passwd|pwd|api[_-]?key|secret|client[_-]?secret|access[_-]?token|refresh[_-]?token|auth[_-]?token)\b\s*[:=]\s*[^\s,;}"']+/gi, `$1=${REDACTED}`)
    .replace(/\bBearer\s+[A-Za-z0-9\-._~+/=]{8,}/gi, `Bearer ${REDACTED}`);
}

/** Structured healing log event names (emitted through the existing log stream). */
export const HealingEvent = {
  Started: 'self-healing.started',
  CandidateGenerated: 'self-healing.candidate_generated',
  CandidateRejected: 'self-healing.candidate_rejected',
  CandidateValidated: 'self-healing.candidate_validated',
  Applied: 'self-healing.applied',
  Failed: 'self-healing.failed',
  Skipped: 'self-healing.skipped',
} as const;

export type HealingEventName = (typeof HealingEvent)[keyof typeof HealingEvent];

/** Persisted healing outcome for one step (worker → backend, execution-scoped). */
export interface HealingAttemptRecord {
  stepOrder: number;
  stepAction: string;
  originalStrategy: string | null;
  originalValue: string | null;
  recoveredStrategy: string | null;
  recoveredValue: string | null;
  healingStrategy: string;
  status: string;
  candidateCount: number;
  wasApplied: boolean;
  isAiAssisted: boolean;
  errorMessage: string | null;
}

export function originalParts(target: string | null | undefined): {
  strategy: string | null;
  value: string | null;
} {
  const parsed = parseTarget(target);
  if (!parsed) return { strategy: null, value: null };
  return { strategy: parsed.kind as string, value: parsed.selector.slice(0, 2000) };
}

/** Maps an internal generation source to the persisted healing-strategy label. */
export function strategyLabel(candidate: HealingCandidate | null): string {
  if (!candidate) return 'none';
  if (candidate.source === 'ai') return 'ai';
  switch (candidate.strategy) {
    case 'testid':
      return 'test-attribute';
    case 'role':
      return 'role';
    case 'text':
      return 'text';
    default:
      return 'structural';
  }
}

export type LocatorKindName = LocatorKind;
