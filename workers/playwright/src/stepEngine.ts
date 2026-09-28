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
import type {
  WorkerClassification,
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
}

export async function executeSteps(
  page: EnginePage,
  steps: WorkerStep[],
  options: {
    stepTimeoutMs: number;
    signal: AbortSignal;
    screenshotOnFailure: boolean;
    callbacks?: EngineCallbacks;
  },
): Promise<EngineRun> {
  const ordered = [...steps].sort((a, b) => a.order - b.order);
  const stepResults: WorkerStepResult[] = [];
  const screenshots: WorkerScreenshot[] = [];
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
      const completedAt = Date.now();
      const classification: WorkerClassification =
        error instanceof EngineError ? error.classification : 'automation';
      const message = error instanceof Error ? error.message : 'Step failed.';
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
      };
    }
  }

  return {
    stepResults,
    screenshots,
    failed: false,
    classification: 'unknown',
  };
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
  constructor(private readonly page: Page) {}

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
