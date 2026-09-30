/**
 * Controlled structured-step interpreter (Slice 5 §13). This is a security
 * boundary: supported actions map 1:1 to explicit Playwright calls. There is
 * NO eval, NO new Function, NO child_process, NO dynamic import, and the
 * sourceCode field of a TestCaseVersion is never read here, let alone run.
 * Unknown actions fail safely as automation failures — never silently ignored.
 */
import type { Locator, Page, Response } from 'playwright';
import { parseTarget } from './locators.js';
import { isSensitiveTarget, redactStepValue } from './redaction.js';
import {
  DEFAULT_POLICY,
  HealingEvent,
  MAX_CANDIDATES,
  buildEvidenceEnvelope,
  generateDeterministicCandidates,
  isHealingEligible,
  originalParts,
  parseAiCandidates,
  redactSecrets,
  strategyLabel,
  validateCandidate,
  type HealingAttemptRecord,
  type HealingCandidate,
  type HealingElementSnapshot,
  type HealingInspector,
  type SelfHealingPolicy,
} from './selfHealing.js';
import type {
  WorkerClassification,
  WorkerHealingAttempt,
  WorkerLog,
  WorkerScreenshot,
  WorkerStep,
  WorkerStepResult,
} from './types.js';

/** Accessible role names accepted by getByRole (mirrors Playwright's AriaRole). */
type AriaRole = Parameters<Page['getByRole']>[0];

/** Minimal surface the engine needs. Real Playwright objects are adapted. */
export interface EngineLocator {
  click(timeoutMs: number): Promise<void>;
  fill(value: string, timeoutMs: number): Promise<void>;
  pressSequentially(value: string, timeoutMs: number): Promise<void>;
  selectOption(value: string | string[], timeoutMs: number): Promise<void>;
  check(timeoutMs: number): Promise<void>;
  uncheck(timeoutMs: number): Promise<void>;
  press(key: string, timeoutMs: number): Promise<void>;
  waitForVisible(timeoutMs: number): Promise<void>;
  readText(timeoutMs: number): Promise<string | null>;
  readInputValue(timeoutMs: number): Promise<string>;
  captureScreenshot(): Promise<Buffer>;
}

export interface EnginePage {
  goto(url: string, timeoutMs: number): Promise<number | null>;
  locatorFor(target: string): EngineLocator | null;
  pressKey(key: string, timeoutMs: number): Promise<void>;
  readTitle(): Promise<string>;
  captureScreenshot(): Promise<Buffer>;
  /**
   * Slice 11: live-DOM healing inspector. Present on the real Playwright
   * adapter; test fakes supply their own or omit it (deterministic healing
   * is then skipped and the original failure is preserved).
   */
  healing?: HealingInspector | null;
}

/**
 * Slice 11: injectable self-healing hooks. All fields optional — absent
 * policy (or disabled policy) reproduces pre-Slice-11 behavior exactly.
 */
export interface HealingHooks {
  /** Project policy delivered with the assignment. Defaults to disabled. */
  policy?: SelfHealingPolicy | null;
  /**
   * AI fallback provider: receives a bounded redacted evidence envelope and
   * resolves to the RAW provider JSON ({ candidates: [...] }). Undefined
   * means AI fallback is unavailable (never called). The engine validates
   * the output via schema before any DOM validation. Never receives secrets.
   */
  suggestAi?: (
    envelope: { action: string; originalTarget: string; domFragment: string; attributes: string[]; nearbyText: string[] },
  ) => Promise<string>;
  /** AI fallback timeout (bounded; default 15000ms). */
  aiTimeoutMs?: number;
}

export class EngineError extends Error {
  readonly classification: WorkerClassification;
  constructor(classification: WorkerClassification, message: string) {
    super(message);
    this.classification = classification;
  }
}

export class EngineAbort extends Error {
  constructor() {
    super('Assignment aborted.');
  }
}

function automation(message: string): EngineError {
  return new EngineError('automation', message);
}

function testFailure(message: string): EngineError {
  return new EngineError('test', message);
}

/** Structural (fail-fast) validation. Returns blocking problems, if any. */
export function validateSteps(steps: WorkerStep[]): string[] {
  const problems: string[] = [];
  if (!Array.isArray(steps) || steps.length === 0) {
    return ['At least one structured step is required.'];
  }
  if (steps.length > 500) return ['At most 500 steps are accepted.'];
  const seen = new Set<number>();
  steps.forEach((step, index) => {
    const label = `Step ${index + 1}`;
    if (!Number.isInteger(step.order) || step.order < 1) {
      problems.push(`${label}: 'order' must be an integer >= 1.`);
    } else if (seen.has(step.order)) {
      problems.push(`${label}: duplicate step order ${step.order}.`);
    } else {
      seen.add(step.order);
    }
    const action = (step.action ?? '').trim();
    if (!SUPPORTED_ACTION_SET.has(action.toLowerCase())) {
      problems.push(`${label}: unsupported action '${step.action}'.`);
    }
  });
  return problems;
}

const SUPPORTED_ACTION_SET = new Set([
  'navigate',
  'click',
  'fill',
  'type',
  'select',
  'check',
  'uncheck',
  'press',
  'wait',
  'assertvisible',
  'asserttext',
  'assertvalue',
  'screenshot',
]);

const TARGET_REQUIRED = new Set([
  'navigate',
  'click',
  'fill',
  'type',
  'select',
  'check',
  'uncheck',
  'assertvisible',
  'asserttext',
  'assertvalue',
]);

export interface EngineCallbacks {
  onStepStart?: (order: number, action: string) => void;
  onLog?: (level: WorkerLog['level'], message: string) => void;
}

export interface EngineRun {
  stepResults: WorkerStepResult[];
  screenshots: WorkerScreenshot[];
  failed: boolean;
  classification: WorkerClassification;
  errorType?: string | null;
  errorMessage?: string | null;
  /** Slice 11: one outcome record per healing-attempted step (empty when disabled). */
  healingAttempts: WorkerHealingAttempt[];
}

export async function executeSteps(
  page: EnginePage,
  steps: WorkerStep[],
  options: {
    stepTimeoutMs: number;
    signal: AbortSignal;
    screenshotOnFailure: boolean;
    callbacks?: EngineCallbacks;
    healing?: HealingHooks | null;
  },
): Promise<EngineRun> {
  const ordered = [...steps].sort((a, b) => a.order - b.order);
  const stepResults: WorkerStepResult[] = [];
  const screenshots: WorkerScreenshot[] = [];
  const healingAttempts: WorkerHealingAttempt[] = [];
  /** One-heal guard: a failed step may receive at most ONE healing retry (§11). */
  const healingAttempted = new Set<number>();
  const log = (
    level: WorkerLog['level'],
    message: string,
  ): void => {
    options.callbacks?.onLog?.(level, message);
  };

  for (const step of ordered) {
    if (options.signal.aborted) throw new EngineAbort();
    const action = step.action.trim().toLowerCase();
    const startedAt = Date.now();
    options.callbacks?.onStepStart?.(step.order, action);
    log('info', `step ${step.order}: ${action}`);
    try {
      await runStep(page, step, action, options.stepTimeoutMs, options.signal, log);
      if (action === 'screenshot') {
        try {
          const png = await captureForStep(page, step);
          screenshots.push({
            stepOrder: step.order,
            fileName: `step-${step.order}.png`,
            contentType: 'image/png',
            base64Content: png.toString('base64'),
          });
        } catch (screenshotError) {
          log(
            'warning',
            `screenshot unavailable: ${screenshotError instanceof Error ? screenshotError.message : 'unknown'}`,
          );
        }
      }
      const completedAt = Date.now();
      stepResults.push({
        order: step.order,
        action: step.action,
        target: step.target ?? null,
        status: 'passed',
        startedAtUnixMs: startedAt,
        completedAtUnixMs: completedAt,
        durationMs: completedAt - startedAt,
      });
    } catch (error) {
      if (error instanceof EngineAbort) throw error;
      if (options.signal.aborted) throw new EngineAbort();
      const completedAt = Date.now();
      const classification: WorkerClassification =
        error instanceof EngineError ? error.classification : 'automation';
      const message = error instanceof Error ? error.message : 'Step failed.';
      // Slice 11: eligible locator failures get ONE healing retry before the
      // run is marked failed. The original failure is preserved when healing
      // cannot safely recover.
      const healed = await tryHealStep(
        page,
        step,
        action,
        message,
        {
          stepTimeoutMs: options.stepTimeoutMs,
          signal: options.signal,
          policy: options.healing?.policy ?? null,
          suggestAi: options.healing?.suggestAi,
          aiTimeoutMs: options.healing?.aiTimeoutMs,
          alreadyAttempted: healingAttempted.has(step.order),
        },
        log,
      );
      if (healed.applied && healed.record) {
        healingAttempted.add(step.order);
        healingAttempts.push(toWorkerAttempt(healed.record));
        const healedAt = Date.now();
        stepResults.push({
          order: step.order,
          action: step.action,
          target: step.target ?? null,
          status: 'passed',
          startedAtUnixMs: startedAt,
          completedAtUnixMs: healedAt,
          durationMs: healedAt - startedAt,
          healed: true,
          recoveredTarget: healed.recoveredTarget ?? null,
          healingStrategy: healed.record.healingStrategy,
          aiAssisted: healed.record.isAiAssisted,
        });
        log('info', `step ${step.order} recovered using self-healing (${healed.record.healingStrategy})`);
        continue;
      }
      if (healed.record) {
        healingAttempted.add(step.order);
        healingAttempts.push(toWorkerAttempt(healed.record));
      }
      stepResults.push({
        order: step.order,
        action: step.action,
        target: step.target ?? null,
        status: 'failed',
        startedAtUnixMs: startedAt,
        completedAtUnixMs: completedAt,
        durationMs: completedAt - startedAt,
        errorMessage: message,
      });
      log('error', `step ${step.order} failed: ${message}`);
      if (options.screenshotOnFailure) {
        try {
          const png = await page.captureScreenshot();
          screenshots.push({
            stepOrder: step.order,
            fileName: `step-${step.order}-failure.png`,
            contentType: 'image/png',
            base64Content: png.toString('base64'),
          });
        } catch (screenshotError) {
          log(
            'warning',
            `failure screenshot unavailable: ${screenshotError instanceof Error ? screenshotError.message : 'unknown'}`,
          );
        }
      }
      for (const skipped of ordered.filter((s) => s.order > step.order)) {
        stepResults.push({
          order: skipped.order,
          action: skipped.action,
          target: skipped.target ?? null,
          status: 'skipped',
          startedAtUnixMs: completedAt,
          completedAtUnixMs: completedAt,
          durationMs: 0,
        });
      }
      return {
        stepResults,
        screenshots,
        failed: true,
        classification,
        errorType: classification === 'test' ? 'AssertionError' : 'StepError',
        errorMessage: `Step ${step.order} (${step.action}) failed: ${message}`,
        healingAttempts,
      };
    }
  }

  return {
    stepResults,
    screenshots,
    failed: false,
    classification: 'unknown',
    healingAttempts,
  };
}

function toWorkerAttempt(record: HealingAttemptRecord): WorkerHealingAttempt {
  return {
    stepOrder: record.stepOrder,
    stepAction: record.stepAction,
    originalStrategy: record.originalStrategy,
    originalValue: record.originalValue,
    recoveredStrategy: record.recoveredStrategy,
    recoveredValue: record.recoveredValue,
    healingStrategy: record.healingStrategy,
    status: record.status,
    candidateCount: record.candidateCount,
    wasApplied: record.wasApplied,
    isAiAssisted: record.isAiAssisted,
    errorMessage: record.errorMessage,
  };
}

/**
 * Slice 11 healing attempt for one failed step. Returns applied=true only when
 * a validated candidate retried successfully. Never throws for healing-internal
 * failures: those become a Failed record and the original failure is preserved.
 * Cancellation (signal.aborted) rethrows EngineAbort instead of healing.
 */
async function tryHealStep(
  page: EnginePage,
  step: WorkerStep,
  action: string,
  originalMessage: string,
  healing: {
    stepTimeoutMs: number;
    signal: AbortSignal;
    policy?: SelfHealingPolicy | null;
    suggestAi?: HealingHooks['suggestAi'];
    aiTimeoutMs?: number;
    alreadyAttempted: boolean;
  },
  log: (level: WorkerLog['level'], message: string) => void,
): Promise<{ applied: boolean; recoveredTarget?: string | null; record?: HealingAttemptRecord }> {
  const policy: SelfHealingPolicy = {
    ...DEFAULT_POLICY,
    ...(healing.policy ?? {}),
    maxAttemptsPerStep: 1,
  };
  const original = originalParts(step.target);
  const baseRecord: HealingAttemptRecord = {
    stepOrder: step.order,
    stepAction: step.action,
    originalStrategy: original.strategy,
    originalValue: original.value,
    recoveredStrategy: null,
    recoveredValue: null,
    healingStrategy: 'none',
    status: 'Skipped',
    candidateCount: 0,
    wasApplied: false,
    isAiAssisted: false,
    errorMessage: null,
  };

  if (!policy.enabled) {
    return { applied: false };
  }
  if (healing.alreadyAttempted) {
    // One-heal guard: never heal the same step twice (§11).
    return {
      applied: false,
      record: {
        ...baseRecord,
        status: 'Failed',
        errorMessage: 'Healing already attempted once for this step; original failure preserved.',
      },
    };
  }
  if (!isHealingEligible(action, originalMessage)) {
    log('info', `${HealingEvent.Skipped}: step ${step.order} failure is not healing-eligible.`);
    return { applied: false };
  }
  if (healing.signal.aborted) throw new EngineAbort();
  const startedAt = Date.now();
  log('info', `${HealingEvent.Started}: step ${step.order} locator failure is healing-eligible; searching alternate locators.`);

  const inspector = page.healing ?? null;
  let candidates: HealingCandidate[] = [];
  let snapshots: HealingElementSnapshot[] = [];
  if (inspector) {
    try {
      snapshots = (await inspector.collectSnapshots(60)).slice(0, 60);
    } catch (error) {
      log(
        'warning',
        `${HealingEvent.Failed}: step ${step.order} evidence collection failed: ${error instanceof Error ? error.message.split('\n')[0] : 'unknown'}`,
      );
    }
    candidates = generateDeterministicCandidates(snapshots, step.target ?? '', policy);
    for (const candidate of candidates) {
      log('info', `${HealingEvent.CandidateGenerated}: step ${step.order} deterministic candidate ${candidate.strategy}=${truncateForLog(candidate.value)} (${candidate.reason})`);
    }
  } else {
    log('info', `${HealingEvent.Skipped}: step ${step.order} has no DOM inspector; deterministic candidates unavailable.`);
  }

  let candidateCount = 0;
  // Validate deterministic candidates first (deterministic-first strategy §7).
  for (const candidate of candidates.slice(0, MAX_CANDIDATES)) {
    if (healing.signal.aborted) throw new EngineAbort();
    candidateCount += 1;
    const validation = await validateCandidate(candidate, action, inspector!, policy, healing.signal);
    if (!validation.ok) {
      log('info', `${HealingEvent.CandidateRejected}: step ${step.order} candidate ${candidate.strategy} rejected: ${redactSecrets(validation.reason).slice(0, 300)}`);
      continue;
    }
    log('info', `${HealingEvent.CandidateValidated}: step ${step.order} candidate ${candidate.strategy} validated.`);
    const applied = await retryWithCandidate(page, step, action, candidate, healing);
    if (applied.ok) {
      const recovered = `${candidate.strategy}=${candidate.value}`;
      log('info', `${HealingEvent.Applied}: step ${step.order} recovered locator applied (${strategyLabel(candidate)}, ${Date.now() - startedAt}ms).`);
      return {
        applied: true,
        recoveredTarget: recovered,
        record: {
          ...baseRecord,
          recoveredStrategy: candidate.strategy,
          recoveredValue: candidate.value.slice(0, 2000),
          healingStrategy: strategyLabel(candidate),
          status: 'Applied',
          candidateCount,
          wasApplied: true,
          isAiAssisted: false,
        },
      };
    }
    log('info', `${HealingEvent.Failed}: step ${step.order} recovered locator failed on retry: ${redactSecrets(applied.error ?? 'unknown').slice(0, 300)}`);
    return {
      applied: false,
      record: {
        ...baseRecord,
        recoveredStrategy: candidate.strategy,
        recoveredValue: candidate.value.slice(0, 2000),
        healingStrategy: strategyLabel(candidate),
        status: 'Failed',
        candidateCount,
        errorMessage: `Original failure: ${originalMessage.slice(0, 500)} Healing retry failed: ${(applied.error ?? 'unknown').slice(0, 500)}`,
      },
    };
  }

  // AI fallback: optional, policy-controlled, bounded, schema-validated (§12-13).
  if (policy.aiFallbackEnabled && healing.suggestAi && inspector) {
    if (healing.signal.aborted) throw new EngineAbort();
    try {
      const envelope = buildEvidenceEnvelope(action, step.target ?? '', snapshots);
      const raw = await withTimeout(
        healing.suggestAi(envelope),
        Math.min(Math.max(healing.aiTimeoutMs ?? 15000, 1000), 60000),
      );
      const parsed = parseAiCandidates(raw, policy);
      for (const rejection of parsed.rejected) {
        log('info', `${HealingEvent.CandidateRejected}: step ${step.order} AI candidate rejected: ${rejection.slice(0, 300)}`);
      }
      for (const candidate of parsed.candidates) {
        if (healing.signal.aborted) throw new EngineAbort();
        candidateCount += 1;
        log('info', `${HealingEvent.CandidateGenerated}: step ${step.order} AI candidate ${candidate.strategy}=${truncateForLog(candidate.value)}`);
        const validation = await validateCandidate(candidate, action, inspector, policy, healing.signal);
        if (!validation.ok) {
          log('info', `${HealingEvent.CandidateRejected}: step ${step.order} AI candidate rejected: ${redactSecrets(validation.reason).slice(0, 300)}`);
          continue;
        }
        log('info', `${HealingEvent.CandidateValidated}: step ${step.order} AI candidate validated.`);
        const applied = await retryWithCandidate(page, step, action, candidate, healing);
        if (applied.ok) {
          const recovered = `${candidate.strategy}=${candidate.value}`;
          log('info', `${HealingEvent.Applied}: step ${step.order} AI recovered locator applied (${Date.now() - startedAt}ms).`);
          return {
            applied: true,
            recoveredTarget: recovered,
            record: {
              ...baseRecord,
              recoveredStrategy: candidate.strategy,
              recoveredValue: candidate.value.slice(0, 2000),
              healingStrategy: 'ai',
              status: 'Applied',
              candidateCount,
              wasApplied: true,
              isAiAssisted: true,
            },
          };
        }
        return {
          applied: false,
          record: {
            ...baseRecord,
            recoveredStrategy: candidate.strategy,
            recoveredValue: candidate.value.slice(0, 2000),
            healingStrategy: 'ai',
            status: 'Failed',
            candidateCount,
            isAiAssisted: true,
            errorMessage: `Original failure: ${originalMessage.slice(0, 500)} AI healing retry failed: ${(applied.error ?? 'unknown').slice(0, 500)}`,
          },
        };
      }
    } catch (error) {
      if (error instanceof EngineAbort) throw error;
      // AI timeout/failure is a controlled healing failure, never an execution hang.
      log(
        'warning',
        `${HealingEvent.Failed}: step ${step.order} AI fallback failed safely: ${error instanceof Error ? error.message.split('\n')[0]?.slice(0, 300) : 'unknown'}`,
      );
    }
  }

  if (candidateCount === 0) {
    log('info', `${HealingEvent.Skipped}: step ${step.order} produced no usable candidates; original failure preserved.`);
    return {
      applied: false,
      record: { ...baseRecord, status: 'Failed', candidateCount, errorMessage: `Original failure preserved: ${originalMessage.slice(0, 500)}` },
    };
  }
  log('info', `${HealingEvent.Failed}: step ${step.order} self-healing could not safely recover the locator; original failure preserved.`);
  return {
    applied: false,
    record: { ...baseRecord, status: 'Failed', candidateCount, errorMessage: `Original failure preserved: ${originalMessage.slice(0, 500)}` },
  };
}

/** Retries the original action ONCE with the recovered locator (§11). */
async function retryWithCandidate(
  page: EnginePage,
  step: WorkerStep,
  action: string,
  candidate: HealingCandidate,
  healing: { stepTimeoutMs: number; signal: AbortSignal },
): Promise<{ ok: boolean; error?: string }> {
  if (healing.signal.aborted) throw new EngineAbort();
  const recoveredTarget = `${candidate.strategy}=${candidate.value}`;
  const locator = page.locatorFor(recoveredTarget);
  if (!locator) return { ok: false, error: 'Recovered locator could not be resolved.' };
  try {
    // Reuses the exact structured-step interpreter (no new action surface):
    // the value/assertion comes from the ORIGINAL step; only the locator differs.
    await runLocatorStep(action, locator, step, healing.stepTimeoutMs, healing.signal, () => undefined);
    return { ok: true };
  } catch (error) {
    if (error instanceof EngineAbort) throw error;
    return { ok: false, error: error instanceof Error ? error.message : 'Healing retry failed.' };
  }
}

function withTimeout<T>(promise: Promise<T>, timeoutMs: number): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  const timeout = new Promise<never>((_, reject) => {
    timer = setTimeout(() => reject(new Error(`AI healing timed out after ${timeoutMs}ms.`)), timeoutMs);
  });
  return Promise.race([promise, timeout]).finally(() => {
    if (timer) clearTimeout(timer);
  });
}

function truncateForLog(value: string): string {
  const redacted = redactSecrets(value);
  return redacted.length <= 120 ? redacted : `${redacted.slice(0, 120)}…`;
}

async function runStep(
  page: EnginePage,
  step: WorkerStep,
  action: string,
  stepTimeoutMs: number,
  signal: AbortSignal,
  log: (level: WorkerLog['level'], message: string) => void,
): Promise<void> {
  throwIfAborted(signal);
  if (TARGET_REQUIRED.has(action)) {
    const parsed = parseTarget(step.target);
    if (!parsed || parsed.selector.length === 0) {
      throw automation(`Action '${step.action}' requires a target.`);
    }
  }

  switch (action) {
    case 'navigate': {
      const url = (step.target ?? '').trim();
      let status: number | null;
      try {
        status = await page.goto(url, stepTimeoutMs);
      } catch (error) {
        throw new EngineError('environment', `Navigation to '${url}' failed: ${shortMessage(error)}`);
      }
      if (status !== null && status >= 500) {
        throw new EngineError('application', `Navigation to '${url}' returned HTTP ${status}.`);
      }
      log('info', `navigated to '${url}'${status ? ` (HTTP ${status})` : ''}`);
      return;
    }
    case 'click':
    case 'check':
    case 'uncheck':
    case 'assertvisible':
    case 'asserttext':
    case 'assertvalue':
    case 'fill':
    case 'type':
    case 'select': {
      const locator = page.locatorFor(step.target ?? '');
      if (!locator) throw automation(`Action '${step.action}' requires a target.`);
      await runLocatorStep(action, locator, step, stepTimeoutMs, signal, log);
      return;
    }
    case 'press': {
      const key = (step.value ?? 'Enter').trim() || 'Enter';
      const locator = page.locatorFor(step.target ?? '');
      if (locator) await locator.press(key, stepTimeoutMs);
      else await page.pressKey(key, stepTimeoutMs);
      return;
    }
    case 'wait': {
      const ms = parseWaitMs(step.value);
      await delay(ms, signal);
      return;
    }
    case 'screenshot': {
      // Captured by the caller via screenshots; here we only validate.
      return;
    }
    default:
      throw automation(`Unsupported action '${step.action}'.`);
  }
}

async function runLocatorStep(
  action: string,
  locator: EngineLocator,
  step: WorkerStep,
  stepTimeoutMs: number,
  signal: AbortSignal,
  log: (level: WorkerLog['level'], message: string) => void,
): Promise<void> {
  throwIfAborted(signal);
  const displayValue = redactStepValue(step.action, step.target, step.value);
  try {
    switch (action) {
      case 'click':
        await locator.click(stepTimeoutMs);
        break;
      case 'fill': {
        const value = requireValue(step, 'fill');
        await locator.fill(value, stepTimeoutMs);
        log('info', `filled '${step.target}'`);
        break;
      }
      case 'type': {
        const value = requireValue(step, 'type');
        await locator.pressSequentially(value, stepTimeoutMs);
        log('info', `typed into '${step.target}'`);
        break;
      }
      case 'select': {
        const value = requireValue(step, 'select');
        await locator.selectOption(
          value.includes(',') ? value.split(',').map((v) => v.trim()) : value,
          stepTimeoutMs,
        );
        break;
      }
      case 'check':
        await locator.check(stepTimeoutMs);
        break;
      case 'uncheck':
        await locator.uncheck(stepTimeoutMs);
        break;
      case 'assertvisible':
        await locator.waitForVisible(stepTimeoutMs);
        break;
      case 'asserttext': {
        const expected = requireValue(step, 'assertText');
        const actual = await locator.readText(stepTimeoutMs);
        if (actual === null || !actual.includes(expected)) {
          throw testFailure(
            `Expected text containing '${truncate(expected, 200)}' but found '${truncate(maskIfSensitive(step.target, actual) ?? '', 200)}'.`,
          );
        }
        break;
      }
      case 'assertvalue': {
        const expected = requireValue(step, 'assertValue');
        const actual = await locator.readInputValue(stepTimeoutMs);
        if (actual !== expected) {
          throw testFailure(
            `Expected value '${truncate(displayValue ?? '', 200)}' but found '${truncate(maskIfSensitive(step.target, actual) ?? '', 200)}'.`,
          );
        }
        break;
      }
      default:
        throw automation(`Unsupported action '${step.action}'.`);
    }
  } catch (error) {
    if (error instanceof EngineAbort || error instanceof EngineError) throw error;
    throw testFailure(`${step.action} '${step.target}' failed: ${shortMessage(error)}`);
  }
}

function requireValue(step: WorkerStep, action: string): string {
  if (step.value === null || step.value === undefined || step.value.length === 0) {
    throw automation(`Action '${action}' requires a value.`);
  }
  return step.value;
}

function parseWaitMs(value: string | null | undefined): number {
  if (!value || value.trim().length === 0) return 1000;
  const ms = Number(value.trim());
  if (!Number.isFinite(ms) || ms < 0) {
    throw automation(`Action 'wait' requires a millisecond value, got '${value}'.`);
  }
  return Math.min(30000, Math.floor(ms));
}

function delay(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal.aborted) {
      reject(new EngineAbort());
      return;
    }
    const timer = setTimeout(() => {
      signal.removeEventListener('abort', onAbort);
      resolve();
    }, ms);
    const onAbort = (): void => {
      clearTimeout(timer);
      reject(new EngineAbort());
    };
    signal.addEventListener('abort', onAbort, { once: true });
  });
}

function throwIfAborted(signal: AbortSignal): void {
  if (signal.aborted) throw new EngineAbort();
}

async function captureForStep(page: EnginePage, step: WorkerStep): Promise<Buffer> {
  const locator = page.locatorFor(step.target ?? '');
  if (locator) {
    try {
      return await locator.captureScreenshot();
    } catch {
      return page.captureScreenshot();
    }
  }
  return page.captureScreenshot();
}

function shortMessage(error: unknown): string {
  const message = error instanceof Error ? error.message : 'Unknown error.';
  return message.split('\n')[0] ?? 'Unknown error.';
}

function truncate(value: string, max: number): string {
  return value.length <= max ? value : `${value.slice(0, max)}…`;
}

/** Masks observed values on sensitive targets (defense in depth). */
function maskIfSensitive(
  target: string | null | undefined,
  actual: string | null,
): string | null {
  if (actual == null) return actual;
  return isSensitiveTarget(target) ? '[REDACTED]' : actual;
}

/** Adapts a real Playwright Page to the engine surface (test fakes implement EnginePage directly). */
export class PlaywrightPageAdapter implements EnginePage {
  readonly healing: HealingInspector;

  constructor(private readonly page: Page) {
    this.healing = new PlaywrightHealingInspector(page);
  }

  async goto(url: string, timeoutMs: number): Promise<number | null> {
    const response: Response | null = await this.page.goto(url, {
      waitUntil: 'domcontentloaded',
      timeout: timeoutMs,
    });
    return response?.status() ?? null;
  }

  locatorFor(target: string): EngineLocator | null {
    const parsed = parseTarget(target);
    if (!parsed || parsed.selector.length === 0) return null;
    let locator: Locator;
    switch (parsed.kind) {
      case 'css':
        locator = this.page.locator(parsed.selector);
        break;
      case 'xpath':
        locator = this.page.locator(`xpath=${parsed.selector}`);
        break;
      case 'text':
        locator = this.page.getByText(parsed.selector);
        break;
      case 'testid':
        locator = this.page.getByTestId(parsed.selector);
        break;
      case 'role':
        locator = parsed.name
          ? this.page.getByRole(parsed.selector as AriaRole, { name: parsed.name })
          : this.page.getByRole(parsed.selector as AriaRole);
        break;
    }
    return new PlaywrightLocatorAdapter(locator);
  }

  async pressKey(key: string, timeoutMs: number): Promise<void> {
    // keyboard.press supports only { delay }; bound it with an explicit race.
    let timer: NodeJS.Timeout | undefined;
    try {
      await Promise.race([
        this.page.keyboard.press(key),
        new Promise<never>((_, reject) => {
          timer = setTimeout(
            () => reject(new Error(`keyboard press timed out after ${timeoutMs}ms.`)),
            timeoutMs,
          );
        }),
      ]);
    } finally {
      if (timer) clearTimeout(timer);
    }
  }

  async readTitle(): Promise<string> {
    return this.page.title();
  }

  async captureScreenshot(): Promise<Buffer> {
    return this.page.screenshot({ fullPage: false });
  }
}

/**
 * Live-DOM healing inspector (Slice 11 §8-9). All reads are bounded and
 * deterministic: only allowlisted attributes are collected, snapshots are
 * capped, and candidate probes never use first()/last()/nth() — a candidate
 * with != 1 matches is rejected without further probing. The evaluate
 * callbacks below are hardcoded attribute readers, never caller-supplied code.
 */
export class PlaywrightHealingInspector implements HealingInspector {
  constructor(private readonly page: Page) {}

  async collectSnapshots(limit: number): Promise<HealingElementSnapshot[]> {
    const capped = Math.min(Math.max(limit, 1), 60);
    try {
      const raw = await this.page
        .locator(
          'button, a, input, select, textarea, [role], [data-testid], [data-test], [data-qa], [aria-label], label, [id], [name]',
        )
        .evaluateAll((nodes, max) => {
          const out: Array<Record<string, string | boolean | null>> = [];
          for (const node of nodes.slice(0, max)) {
            const el = node as HTMLElement;
            const get = (name: string): string | null => el.getAttribute?.(name);
            out.push({
              testId: get('data-testid'),
              dataTest: get('data-test'),
              dataQa: get('data-qa'),
              ariaLabel: get('aria-label'),
              role: get('role'),
              accessibleName: get('aria-label') ?? el.getAttribute?.('aria-labelledby') ?? null,
              label: el.tagName === 'LABEL' ? (el.innerText ?? '').slice(0, 120) : null,
              id: get('id'),
              inputName: get('name'),
              text: (el.innerText ?? '').slice(0, 120) || null,
              tagName: el.tagName ?? null,
              inputType: get('type'),
              visible: el.offsetParent !== null || el.tagName === 'BODY',
              enabled: !(el as HTMLInputElement).disabled,
            });
          }
          return out;
        }, capped);
      return raw.map((entry) => ({
        testId: asText(entry.testId),
        dataTest: asText(entry.dataTest),
        dataQa: asText(entry.dataQa),
        ariaLabel: asText(entry.ariaLabel),
        role: asText(entry.role),
        accessibleName: asText(entry.accessibleName),
        label: asText(entry.label),
        id: asText(entry.id),
        inputName: asText(entry.inputName),
        text: asText(entry.text),
        tagName: asText(entry.tagName),
        inputType: asText(entry.inputType),
        visible: entry.visible === true,
        enabled: entry.enabled !== false,
      }));
    } catch {
      return [];
    }
  }

  async countMatches(target: string): Promise<number> {
    return this.buildLocator(target).count();
  }

  async describeMatch(target: string): Promise<{
    count: number;
    tagName: string | null;
    visible: boolean;
    enabled: boolean;
  }> {
    const locator = this.buildLocator(target);
    const count = await locator.count();
    // Ambiguity suppression (first/nth) is never used: anything but exactly
    // one match is rejected by the caller without further probing.
    if (count !== 1) return { count, tagName: null, visible: false, enabled: false };
    const [visible, enabled, tagName] = await Promise.all([
      locator.isVisible().catch(() => false),
      locator.isEnabled().catch(() => false),
      locator.evaluate((el) => el.tagName).catch(() => null as string | null),
    ]);
    return { count, tagName, visible, enabled };
  }

  private buildLocator(target: string): Locator {
    const parsed = parseTarget(target);
    if (!parsed || parsed.selector.length === 0) throw new Error('Empty healing target.');
    switch (parsed.kind) {
      case 'css':
        return this.page.locator(parsed.selector);
      case 'xpath':
        return this.page.locator(`xpath=${parsed.selector}`);
      case 'text':
        return this.page.getByText(parsed.selector);
      case 'testid':
        return this.page.getByTestId(parsed.selector);
      case 'role':
        return parsed.name
          ? this.page.getByRole(parsed.selector as Parameters<Page['getByRole']>[0], { name: parsed.name })
          : this.page.getByRole(parsed.selector as Parameters<Page['getByRole']>[0]);
    }
  }
}

function asText(value: string | boolean | null | undefined): string | null {
  return typeof value === 'string' && value.length > 0 ? value : null;
}

class PlaywrightLocatorAdapter implements EngineLocator {
  constructor(private readonly locator: Locator) {}

  click(timeoutMs: number): Promise<void> {
    return this.locator.click({ timeout: timeoutMs }).then(() => undefined);
  }

  fill(value: string, timeoutMs: number): Promise<void> {
    return this.locator.fill(value, { timeout: timeoutMs }).then(() => undefined);
  }

  pressSequentially(value: string, timeoutMs: number): Promise<void> {
    return this.locator.pressSequentially(value, { timeout: timeoutMs }).then(() => undefined);
  }

  selectOption(value: string | string[], timeoutMs: number): Promise<void> {
    return this.locator.selectOption(value, { timeout: timeoutMs }).then(() => undefined);
  }

  check(timeoutMs: number): Promise<void> {
    return this.locator.check({ timeout: timeoutMs }).then(() => undefined);
  }

  uncheck(timeoutMs: number): Promise<void> {
    return this.locator.uncheck({ timeout: timeoutMs }).then(() => undefined);
  }

  press(key: string, timeoutMs: number): Promise<void> {
    return this.locator.press(key, { timeout: timeoutMs }).then(() => undefined);
  }

  async waitForVisible(timeoutMs: number): Promise<void> {
    await this.locator.waitFor({ state: 'visible', timeout: timeoutMs });
  }

  readText(timeoutMs: number): Promise<string | null> {
    return this.locator.textContent({ timeout: timeoutMs });
  }

  readInputValue(timeoutMs: number): Promise<string> {
    return this.locator.inputValue({ timeout: timeoutMs });
  }

  captureScreenshot(): Promise<Buffer> {
    return this.locator.screenshot();
  }
}
