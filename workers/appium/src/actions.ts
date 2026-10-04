/**
 * Mobile action engine (Slice 3C-4B-2).
 *
 * Executes the closed, explicit action set in deterministic order against
 * IMobileDriver — never WebdriverIO directly. Stops at the first terminal
 * failure, marks the rest skipped, captures failure evidence best-effort,
 * and never reports success for incomplete work. No eval, no Function, no
 * child_process, no dynamic import, no executeScript, no arbitrary commands.
 *
 * Observability: values (inputText, assertText expectation, element text)
 * are NEVER logged. Logs carry order/action/outcome only.
 */
import type { IMobileDriver, InteractionError, SwipeDirection } from './driver.js';
import {
  buildMobileEvidenceEnvelope,
  generateMobileCandidates,
  isMobileHealingEligible,
  mobileStrategyLabel,
  normalizeMobileHealingPolicy,
  parseHierarchySnapshots,
  parseMobileAiCandidates,
  sanitizeRecoveredValue,
  validateMobileCandidate,
  type MobileHealingCandidate,
  type MobileHealingEvidenceEnvelope,
  type MobileHealingInspector,
} from './mobileHealing.js';
import { sanitizePageSource, type EvidenceSecrets } from './evidence.js';
import { parseMobileTarget, type ResolvedMobileLocator } from './locators.js';
import { isSensitiveTarget, REDACTED } from './redaction.js';
import type {
  MobileAssignment,
  MobileHealingAttempt,
  MobileLog,
  MobilePageSource,
  MobileScreenshot,
  MobileStep,
  MobileStepResult,
} from './types.js';

export type ActionFailureKind = 'test' | 'environment' | 'automation';

export interface ActionEngineResult {
  stepResults: MobileStepResult[];
  screenshots: MobileScreenshot[];
  /** Bounded, redacted page-source snapshots (failure evidence only). */
  pageSources: MobilePageSource[];
  /** One outcome record per healed-attempted step (append-only, execution-scoped). */
  healingAttempts: MobileHealingAttempt[];
  /** Terminal status for the run: never 'passed' unless every step completed. */
  status: 'passed' | 'failed' | 'error';
  classification: 'test' | 'environment' | 'automation';
  errorType: string | null;
  errorMessage: string | null;
}

export interface ActionEngineDeps {
  driver: IMobileDriver;
  sessionId: string;
  assignment: MobileAssignment;
  /** Per-step element timeout bound. */
  stepTimeoutMs: number;
  signal: AbortSignal;
  pushLog: (level: MobileLog['level'], message: string) => void;
  onStepStart?: (order: number) => void;
  /**
   * Slice 3C-4C AI fallback: POSTs a redacted evidence envelope to the
   * control-plane healing/suggest endpoint and returns the raw contract.
   * Absent (or policy-disabled) means deterministic-only healing.
   */
  suggestAi?: (envelope: MobileHealingEvidenceEnvelope) => Promise<string>;
}

const MAX_WAIT_MS = 30000;
const MAX_SCREENSHOT_BASE64 = 4 * 1024 * 1024;
const SWIPE_VALUE = /^(up|down|left|right)(?::(\d{1,5}))?$/i;

function truncate(message: string): string {
  return message.length <= 4000 ? message : message.slice(0, 4000);
}

/** wait value: integer milliseconds, default 1000, hard cap 30000. */
export function parseWaitMs(value: string | null | undefined): number {
  const raw = (value ?? '').trim();
  if (raw.length === 0) return 1000;
  const parsed = Number(raw);
  if (!Number.isFinite(parsed) || parsed < 0) {
    throw { kind: 'automation', message: `wait value must be a non-negative millisecond count, got '${raw.slice(0, 50)}'.` };
  }
  return Math.min(MAX_WAIT_MS, Math.floor(parsed));
}

export function parseSwipe(value: string | null | undefined): { direction: SwipeDirection; durationMs: number } {
  const match = SWIPE_VALUE.exec((value ?? '').trim());
  if (!match) {
    throw { kind: 'automation', message: "swipe value must look like 'up', 'down:500', 'left', or 'right'." };
  }
  return {
    direction: match[1]!.toLowerCase() as SwipeDirection,
    durationMs: match[2] !== undefined ? Math.min(5000, Math.max(100, Number(match[2]))) : 800,
  };
}

function failureKind(error: unknown): { kind: ActionFailureKind; message: string } {
  if (error && typeof error === 'object' && 'kind' in error) {
    const kind = (error as { kind: unknown }).kind;
    if (kind === 'test' || kind === 'environment' || kind === 'automation') {
      return { kind, message: truncate(String((error as { message?: unknown }).message ?? 'Mobile action failed.')) };
    }
  }
  return { kind: 'environment', message: truncate(error instanceof Error ? error.message : String(error ?? 'Unknown failure.')) };
}

function requireLocator(step: MobileStep): ResolvedMobileLocator {
  const locator = parseMobileTarget(step.target);
  if (!locator) {
    throw {
      kind: 'automation',
      message: `Step ${step.order} (${step.action}) requires a locator target ('accessibilityId=' or 'resourceId=').`,
    };
  }
  return locator;
}

function requireValue(step: MobileStep): string {
  if (typeof step.value !== 'string' || step.value.trim().length === 0) {
    throw {
      kind: 'automation',
      message: `Step ${step.order} (${step.action}) requires a non-empty value.`,
    };
  }
  return step.value;
}

export async function runMobileActions(deps: ActionEngineDeps): Promise<ActionEngineResult> {
  const { driver, sessionId, assignment, stepTimeoutMs, signal, pushLog, onStepStart, suggestAi } = deps;
  const steps = [...assignment.steps].sort((a, b) => a.order - b.order);
  const stepResults: MobileStepResult[] = [];
  const screenshots: MobileScreenshot[] = [];
  const pageSources: MobilePageSource[] = [];
  const healingAttempts: MobileHealingAttempt[] = [];
  // Slice 3C-4C: normalized once per run; disabled/absent preserves
  // pre-healing behavior exactly (no records, no retries).
  const healingPolicy = normalizeMobileHealingPolicy(assignment.healing);
  // Exact-mask material for evidence: values typed through text-entry
  // steps plus the assignment token and binary URL. Never logged.
  const evidenceSecrets: EvidenceSecrets = {
    assignmentToken: assignment.assignmentToken,
    downloadUrl: assignment.app.downloadUrl,
    typedValues: steps.map((s) => s.value),
  };

  const captureScreenshot = async (order: number | null, suffix: string): Promise<void> => {
    try {
      const base64 = await driver.takeScreenshot(sessionId);
      if (base64.length > MAX_SCREENSHOT_BASE64) {
        pushLog('warning', `screenshot for step ${order ?? 'finish'} exceeded the size bound and was dropped`);
        return;
      }
      screenshots.push({
        stepOrder: order,
        fileName: order === null ? `finish-${suffix}.png` : `step-${order}-${suffix}.png`,
        contentType: 'image/png',
        base64Content: base64,
      });
    } catch (error) {
      pushLog('warning', `screenshot capture failed: ${(error as InteractionError)?.message?.slice(0, 200) ?? 'unknown'}`);
    }
  };

  const capturePageSource = async (order: number): Promise<void> => {
    try {
      const raw = await driver.getPageSource(sessionId);
      const sanitized = sanitizePageSource(raw, order, evidenceSecrets);
      pageSources.push({
        stepOrder: order,
        fileName: sanitized.fileName,
        contentType: 'text/xml',
        xmlContent: sanitized.xmlContent,
      });
    } catch (error) {
      // Best-effort evidence: a failed snapshot must never change the
      // primary failure or escalate to an infrastructure retry.
      pushLog('warning', `page source capture failed: ${(error as InteractionError)?.message?.slice(0, 200) ?? 'unknown'}`);
    }
  };

  const throwIfAborted = (): void => {
    if (signal.aborted) throw new Error('Cancelled via API.');
  };

  const recordHealingAttempt = (
    step: MobileStep,
    candidate: MobileHealingCandidate | null,
    candidateCount: number,
    wasApplied: boolean,
    isAiAssisted: boolean,
    errorMessage: string | null,
  ): void => {
    const original = parseMobileTarget(step.target);
    const sensitive = isSensitiveTarget(step.target);
    healingAttempts.push({
      stepOrder: step.order,
      stepAction: step.action,
      originalStrategy: original?.kind ?? null,
      originalValue: original === null ? null : sensitive ? REDACTED : original.value.slice(0, 2000),
      recoveredStrategy: candidate?.strategy ?? null,
      recoveredValue:
        candidate === null ? null : sanitizeRecoveredValue(sensitive, `${candidate.strategy}=${candidate.value}`),
      healingStrategy: mobileStrategyLabel(candidate),
      status: wasApplied ? 'Applied' : 'Failed',
      candidateCount,
      wasApplied,
      isAiAssisted,
      errorMessage: errorMessage?.slice(0, 2000) ?? null,
    });
  };

  const retryWithCandidate = async (
    step: MobileStep,
    candidate: MobileHealingCandidate,
    candidateCount: number,
    isAiAssisted: boolean,
    startedAt: number,
  ): Promise<boolean> => {
    const recoveredTarget = `${candidate.strategy}=${candidate.value}`;
    const healedStep: MobileStep = { ...step, target: recoveredTarget };
    try {
      throwIfAborted();
      await runStep(driver, sessionId, assignment, healedStep, stepTimeoutMs, signal, captureScreenshot);
    } catch (retryError) {
      // Cancellation during the heal retry stays cancellation; any other
      // retry failure keeps the ORIGINAL failure authoritative.
      if (signal.aborted || (retryError instanceof Error && /cancelled via api/i.test(retryError.message))) {
        throw retryError;
      }
      return false;
    }
    const completedAt = Date.now();
    const sensitive = isSensitiveTarget(step.target);
    stepResults.push({
      order: step.order,
      action: step.action,
      target: step.target ?? null,
      status: 'passed',
      startedAtUnixMs: startedAt,
      completedAtUnixMs: completedAt,
      durationMs: completedAt - startedAt,
      healed: true,
      recoveredTarget: sanitizeRecoveredValue(sensitive, recoveredTarget),
      healingStrategy: candidate.strategy,
      aiAssisted: isAiAssisted,
    });
    recordHealingAttempt(step, candidate, candidateCount, true, isAiAssisted, null);
    pushLog('info', `step ${step.order} (${step.action}) healed`);
    return true;
  };

  /**
   * Slice 3C-4C deterministic-first healing: exactly one validated retry
   * with a recovered locator. Returns true when the step healed (passed
   * result already pushed); false leaves the original failure authoritative.
   * Never consumes MaxAttempts, never creates assignments or sessions.
   */
  const tryHealStep = async (
    step: MobileStep,
    failureMessage: string,
    startedAt: number,
  ): Promise<boolean> => {
    if (!healingPolicy.enabled || healingPolicy.maxAttemptsPerStep < 1) return false;
    if (!isMobileHealingEligible(step.action, failureMessage)) return false;
    const action = step.action.trim().toLowerCase();
    pushLog('info', `step ${step.order} (${step.action}) healing started`);
    let raw: string;
    try {
      throwIfAborted();
      raw = await driver.getPageSource(sessionId);
    } catch {
      pushLog('warning', `step ${step.order} healing skipped: hierarchy unavailable`);
      recordHealingAttempt(step, null, 0, false, false, 'Hierarchy snapshot unavailable.');
      return false;
    }
    const snapshots = parseHierarchySnapshots(raw);
    const inspector: MobileHealingInspector = {
      snapshot: async () => snapshots,
      describe: (node) => ({
        enabled: node.enabled ?? false,
        actionable: node.clickable ?? node.enabled ?? false,
      }),
    };
    const isAssertText = action === 'asserttext';
    const deterministic = generateMobileCandidates(
      snapshots,
      step.target,
      isAssertText ? (step.value ?? null) : null,
      isAssertText,
      healingPolicy,
    );
    for (const candidate of deterministic) {
      throwIfAborted();
      const validation = await validateMobileCandidate(candidate, action, inspector, healingPolicy, signal);
      if (!validation.ok) continue;
      if (await retryWithCandidate(step, candidate, deterministic.length, false, startedAt)) return true;
    }
    if (healingPolicy.aiFallbackEnabled && suggestAi) {
      try {
        throwIfAborted();
        const response = await suggestAi(
          buildMobileEvidenceEnvelope(action, step.target, snapshots, evidenceSecrets),
        );
        const parsed = parseMobileAiCandidates(response, healingPolicy);
        for (const candidate of parsed.candidates) {
          throwIfAborted();
          const validation = await validateMobileCandidate(candidate, action, inspector, healingPolicy, signal);
          if (!validation.ok) continue;
          if (await retryWithCandidate(step, candidate, parsed.candidates.length, true, startedAt)) return true;
        }
      } catch (error) {
        // Controlled healing failure: the suggest round trip (or abort
        // surfacing as a throw above, rethrown below) never escalates.
        if (signal.aborted || (error instanceof Error && /cancelled via api/i.test(error.message))) throw error;
        pushLog('warning', `step ${step.order} AI healing skipped: suggest unavailable`);
      }
    }
    recordHealingAttempt(step, null, deterministic.length, false, false, 'No validated recovery candidate.');
    pushLog('info', `step ${step.order} (${step.action}) healing failed`);
    return false;
  };

  for (const step of steps) {
    throwIfAborted();
    onStepStart?.(step.order);
    const startedAt = Date.now();
    pushLog('info', `step ${step.order} (${step.action}) started`);
    try {
      throwIfAborted();
      await runStep(driver, sessionId, assignment, step, stepTimeoutMs, signal, captureScreenshot);
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
      pushLog('info', `step ${step.order} (${step.action}) passed`);
    } catch (error) {
      const completedAt = Date.now();
      if (signal.aborted || (error instanceof Error && /cancelled via api/i.test(error.message))) {
        throw error;
      }
      const failure = failureKind(error);
      // Slice 3C-4C: exactly one validated heal retry before terminal
      // failure. A healed step passes and the run continues; otherwise the
      // original failure below remains authoritative.
      if (await tryHealStep(step, failure.message, startedAt)) continue;
      stepResults.push({
        order: step.order,
        action: step.action,
        target: step.target ?? null,
        status: 'failed',
        startedAtUnixMs: startedAt,
        completedAtUnixMs: completedAt,
        durationMs: completedAt - startedAt,
        errorMessage: failure.message,
      });
      pushLog('error', `step ${step.order} (${step.action}) failed`);
      if (assignment.screenshotOnFailure) {
        await captureScreenshot(step.order, 'failure');
        // Failure-evidence gate (shared with screenshots): a bounded,
        // redacted page-source snapshot for the failing step.
        await capturePageSource(step.order);
      }
      // Skip everything after the terminal failure; never execute further.
      for (const remaining of steps) {
        if (remaining.order > step.order) {
          stepResults.push({
            order: remaining.order,
            action: remaining.action,
            target: remaining.target ?? null,
            status: 'skipped',
            startedAtUnixMs: completedAt,
            completedAtUnixMs: completedAt,
            durationMs: 0,
          });
        }
      }
      return {
        stepResults,
        screenshots,
        pageSources,
        healingAttempts,
        status: failure.kind === 'test' ? 'failed' : 'error',
        classification: failure.kind,
        errorType: failure.kind === 'test' ? 'AssertionError' : 'StepError',
        errorMessage: `Step ${step.order} (${step.action}) failed: ${failure.message}`,
      };
    }
  }

  return {
    stepResults,
    screenshots,
    pageSources,
    healingAttempts,
    status: 'passed',
    classification: 'test',
    errorType: null,
    errorMessage: null,
  };
}

async function runStep(
  driver: IMobileDriver,
  sessionId: string,
  assignment: MobileAssignment,
  step: MobileStep,
  stepTimeoutMs: number,
  signal: AbortSignal,
  captureScreenshot: (order: number | null, suffix: string) => Promise<void>,
): Promise<void> {
  switch (step.action) {
    case 'launchApp': {
      const packageId = assignment.app.packageId?.trim();
      if (!packageId) {
        throw { kind: 'automation', message: 'launchApp requires a trusted application package from the assignment.' };
      }
      await driver.activateApp(sessionId, packageId);
      return;
    }
    case 'tap': {
      await driver.tap(sessionId, requireLocator(step), stepTimeoutMs);
      return;
    }
    case 'inputText': {
      await driver.setText(sessionId, requireLocator(step), requireValue(step), stepTimeoutMs);
      return;
    }
    case 'clearText': {
      await driver.clearText(sessionId, requireLocator(step), stepTimeoutMs);
      return;
    }
    case 'assertVisible': {
      const visible = await driver.isDisplayed(sessionId, requireLocator(step), stepTimeoutMs);
      if (!visible) {
        throw { kind: 'test', message: `Step ${step.order} (assertVisible) failed: element is not visible.` };
      }
      return;
    }
    case 'assertText': {
      const locator = requireLocator(step);
      const expected = requireValue(step);
      const actual = await driver.getText(sessionId, locator, stepTimeoutMs);
      if (!actual.includes(expected)) {
        // Values stay out of messages: secrets may flow through inputs.
        throw { kind: 'test', message: `Step ${step.order} (assertText) failed: text did not match expectation.` };
      }
      return;
    }
    case 'swipe': {
      const swipe = parseSwipe(step.value);
      await driver.swipe(sessionId, swipe.direction, swipe.durationMs);
      return;
    }
    case 'back': {
      await driver.pressBack(sessionId);
      return;
    }
    case 'hideKeyboard': {
      await driver.hideKeyboard(sessionId);
      return;
    }
    case 'wait': {
      const ms = parseWaitMs(step.value);
      await new Promise<void>((resolve, reject) => {
        if (signal.aborted) {
          reject(new Error('Cancelled via API.'));
          return;
        }
        const timer = setTimeout(() => {
          signal.removeEventListener('abort', onAbort);
          resolve();
        }, ms);
        const onAbort = (): void => {
          clearTimeout(timer);
          reject(new Error('Cancelled via API.'));
        };
        signal.addEventListener('abort', onAbort, { once: true });
      });
      return;
    }
    case 'screenshot': {
      await captureScreenshot(step.order, 'explicit');
      return;
    }
    case 'terminateApp': {
      const packageId = assignment.app.packageId?.trim();
      if (!packageId) {
        throw { kind: 'automation', message: 'terminateApp requires a trusted application package from the assignment.' };
      }
      await driver.terminateApp(sessionId, packageId);
      return;
    }
    default:
      throw { kind: 'automation', message: `Step ${step.order} has unsupported action '${step.action}'.` };
  }
}
