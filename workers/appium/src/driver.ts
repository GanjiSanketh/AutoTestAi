/**
 * Narrow Appium driver boundary (Slice 3C-4B-1).
 *
 * The rest of the worker depends ONLY on IMobileDriver — never on
 * WebdriverIO APIs directly. Exposed surface is deliberately minimal:
 * create/delete a session and track known session ids. No executeScript,
 * no arbitrary commands, no capability mutation, no global driver handle.
 *
 * Secrets/credentials must never flow through here: capabilities arrive
 * pre-built from the control plane (server-side MobileCapabilityBuilder)
 * and this module never logs capability values, URLs, or session ids.
 */
import { remote, type Browser } from 'webdriverio';
import type { MobileCapabilities } from './types.js';
import type { ResolvedMobileLocator } from './locators.js';

export interface AppiumServerEndpoint {
  protocol: 'http:' | 'https:';
  hostname: string;
  port: number;
  path: string;
}

export interface SessionHandle {
  sessionId: string;
}

export interface CreateSessionOptions {
  endpoint: AppiumServerEndpoint;
  /** Upper bound for the whole session-creation attempt. */
  newCommandTimeoutMs: number;
  /** Aborts a pending creation; best-effort cleanup follows. */
  signal?: AbortSignal;
}

export type DriverFailureKind = 'environment' | 'automation';

export interface ClassifiedDriverError {
  kind: DriverFailureKind;
  message: string;
}

export interface IMobileDriver {
  createSession(
    capabilities: MobileCapabilities,
    opts: CreateSessionOptions,
  ): Promise<SessionHandle>;
  deleteSession(sessionId: string): Promise<void>;
  hasSession(sessionId: string): boolean;
  /** Tap exactly once. Throws InteractionError (test/environment/automation). */
  tap(sessionId: string, locator: ResolvedMobileLocator, timeoutMs: number): Promise<void>;
  /** Enter text. The value is never logged by this module. */
  setText(sessionId: string, locator: ResolvedMobileLocator, value: string, timeoutMs: number): Promise<void>;
  clearText(sessionId: string, locator: ResolvedMobileLocator, timeoutMs: number): Promise<void>;
  /** False when absent within timeout; throws only for automation/infra faults. */
  isDisplayed(sessionId: string, locator: ResolvedMobileLocator, timeoutMs: number): Promise<boolean>;
  getText(sessionId: string, locator: ResolvedMobileLocator, timeoutMs: number): Promise<string>;
  swipe(sessionId: string, direction: SwipeDirection, durationMs: number): Promise<void>;
  pressBack(sessionId: string): Promise<void>;
  /** 'closed' when a keyboard was hidden, 'absent' when none was open. */
  hideKeyboard(sessionId: string): Promise<'closed' | 'absent'>;
  /** Base64 PNG bytes. Never logged. */
  takeScreenshot(sessionId: string): Promise<string>;
  activateApp(sessionId: string, packageId: string): Promise<void>;
  terminateApp(sessionId: string, packageId: string): Promise<void>;
}

export type SwipeDirection = 'up' | 'down' | 'left' | 'right';

/** Narrow interaction failure: test = app-state fault, never retried upstream. */
export interface InteractionError {
  kind: 'test' | 'environment' | 'automation';
  message: string;
}

const MAX_MESSAGE_LENGTH = 4000;

function truncate(message: string): string {
  return message.length <= MAX_MESSAGE_LENGTH ? message : message.slice(0, MAX_MESSAGE_LENGTH);
}

/**
 * Parses the worker-local Appium server endpoint. Only http(s) absolute
 * URLs are accepted; anything else is a deterministic automation failure
 * (misconfiguration), never retried as infrastructure trouble.
 */
export function parseAppiumServerUrl(raw: string | null | undefined): AppiumServerEndpoint {
  const text = (raw ?? '').trim();
  let url: URL;
  try {
    url = new URL(text);
  } catch {
    throw { kind: 'automation', message: 'Appium server URL is not a valid absolute URL.' } as ClassifiedDriverError;
  }
  if (url.protocol !== 'http:' && url.protocol !== 'https:') {
    throw { kind: 'automation', message: 'Appium server URL must use http or https.' } as ClassifiedDriverError;
  }
  const port = url.port ? Number(url.port) : url.protocol === 'https:' ? 443 : 80;
  if (!Number.isInteger(port) || port < 1 || port > 65535) {
    throw { kind: 'automation', message: 'Appium server URL has an invalid port.' } as ClassifiedDriverError;
  }
  return {
    protocol: url.protocol,
    hostname: url.hostname,
    port,
    path: url.pathname && url.pathname !== '/' ? url.pathname : '/',
  };
}

function isClassified(error: unknown): error is ClassifiedDriverError {
  if (!error || typeof error !== 'object') return false;
  const kind = (error as { kind?: unknown }).kind;
  return kind === 'environment' || kind === 'automation';
}

/**
 * Maps a driver/session failure to a deterministic bucket. Connectivity and
 * device-availability problems are environment failures (retryable upstream);
 * capability/shape problems are automation failures (rejected, never retried).
 */
export function classifyDriverError(error: unknown): ClassifiedDriverError {
  if (isClassified(error)) {
    return { kind: error.kind, message: truncate(String(error.message ?? 'Appium driver failure.')) };
  }
  const message = error instanceof Error ? error.message : String(error ?? 'Unknown driver failure.');
  const normalized = message.toLowerCase();
  if (
    normalized.includes('invalid argument') ||
    normalized.includes('invalid capabilities') ||
    normalized.includes('capability') ||
    normalized.includes('invalid selector') ||
    normalized.includes('bad request')
  ) {
    return { kind: 'automation', message: truncate(`Appium rejected the session request: ${message}`) };
  }
  if (
    normalized.includes('device') &&
    (normalized.includes('not found') ||
      normalized.includes('offline') ||
      normalized.includes('unauthorized') ||
      normalized.includes('no such device'))
  ) {
    return { kind: 'environment', message: truncate(`Device unavailable: ${message}`) };
  }
  return { kind: 'environment', message: truncate(`Appium session failure: ${message}`) };
}

/** Translates server-built capabilities 1:1 into WebdriverIO options. No added keys. */
export function toWebdriverCapabilities(capabilities: MobileCapabilities): Record<string, unknown> {
  const out: Record<string, unknown> = {
    platformName: capabilities.platformName,
    'appium:automationName': capabilities.automationName,
    'appium:noReset': capabilities.noReset,
    'appium:fullReset': capabilities.fullReset,
    'appium:newCommandTimeout': capabilities.newCommandTimeout,
  };
  if (capabilities.deviceName != null) out['appium:deviceName'] = capabilities.deviceName;
  if (capabilities.udid != null) out['appium:udid'] = capabilities.udid;
  if (capabilities.appPackage != null) out['appium:appPackage'] = capabilities.appPackage;
  if (capabilities.appActivity != null) out['appium:appActivity'] = capabilities.appActivity;
  if (capabilities.bundleId != null) out['appium:bundleId'] = capabilities.bundleId;
  if (capabilities.app != null) out['appium:app'] = capabilities.app;
  return out;
}

interface SessionContext {
  browser: Browser;
  /** Lowercased platformName from the server-built capabilities. */
  platform: string;
  /** Trusted app package for launch/terminate; null when absent (iOS/preinstalled gaps fail closed). */
  packageId: string | null;
}

export class WebdriverIoDriver implements IMobileDriver {
  private readonly browsers = new Map<string, Browser>();
  private readonly contexts = new Map<string, SessionContext>();

  async createSession(
    capabilities: MobileCapabilities,
    opts: CreateSessionOptions,
  ): Promise<SessionHandle> {
    const wdioCapabilities = toWebdriverCapabilities(capabilities);
    const create = (async (): Promise<Browser> => {
      try {
        return await remote({
          protocol: opts.endpoint.protocol.replace(/:$/, ''),
          hostname: opts.endpoint.hostname,
          port: opts.endpoint.port,
          path: opts.endpoint.path,
          capabilities: wdioCapabilities,
          connectionRetryTimeout: Math.max(1000, Math.min(opts.newCommandTimeoutMs, 120000)),
          connectionRetryCount: 0,
          logLevel: 'silent',
        });
      } catch (error) {
        throw classifyDriverError(error);
      }
    })();

    let browser: Browser;
    if (opts.signal) {
      if (opts.signal.aborted) {
        throw { kind: 'environment', message: 'Session creation aborted before it started.' } as ClassifiedDriverError;
      }
      browser = await Promise.race([
        create,
        new Promise<never>((_, reject) => {
          opts.signal!.addEventListener('abort', () => reject(new Error('Session creation aborted.')), {
            once: true,
          });
        }),
      ]).catch(async (error: unknown) => {
        // Aborted while the server may still have created a session:
        // attach-best-effort cleanup happens below via the known id when
        // available; otherwise the server-side command timeout bounds leakage.
        const settled = await Promise.resolve(create.then(
          (b) => ({ ok: true as const, browser: b }),
          () => ({ ok: false as const, browser: null }),
        ));
        if (settled.ok && settled.browser) {
          await settled.browser.deleteSession().catch(() => undefined);
        }
        throw error;
      });
    } else {
      browser = await create;
    }

    try {
      const sessionId = browser.sessionId;
      if (!sessionId) {
        await browser.deleteSession().catch(() => undefined);
        throw { kind: 'environment', message: 'Appium server returned no session id.' } as ClassifiedDriverError;
      }
      this.browsers.set(sessionId, browser);
      this.contexts.set(sessionId, {
        browser,
        platform: (capabilities.platformName ?? '').toLowerCase(),
        packageId: capabilities.appPackage ?? null,
      });
      return { sessionId };
    } catch (error) {
      if (isClassified(error)) throw error;
      throw classifyDriverError(error);
    }
  }

  async deleteSession(sessionId: string): Promise<void> {
    const browser = this.browsers.get(sessionId);
    if (!browser) return; // idempotent: unknown or already closed
    try {
      await browser.deleteSession();
    } catch {
      // Best effort: a dead server yields the same end state.
    } finally {
      this.browsers.delete(sessionId);
      this.contexts.delete(sessionId);
    }
  }

  hasSession(sessionId: string): boolean {
    return this.browsers.has(sessionId);
  }

  /** Trusted package for launch/terminate; null when the capabilities carry none. */
  sessionPackageId(sessionId: string): string | null {
    return this.contexts.get(sessionId)?.packageId ?? null;
  }

  async tap(sessionId: string, locator: ResolvedMobileLocator, timeoutMs: number): Promise<void> {
    const element = await this.resolve(sessionId, locator, timeoutMs, 'tap');
    try {
      await element.click();
    } catch (error) {
      throw this.interaction('tap', error);
    }
  }

  async setText(
    sessionId: string,
    locator: ResolvedMobileLocator,
    value: string,
    timeoutMs: number,
  ): Promise<void> {
    const element = await this.resolve(sessionId, locator, timeoutMs, 'inputText');
    try {
      await element.setValue(value);
    } catch (error) {
      throw this.interaction('inputText', error);
    }
  }

  async clearText(sessionId: string, locator: ResolvedMobileLocator, timeoutMs: number): Promise<void> {
    const element = await this.resolve(sessionId, locator, timeoutMs, 'clearText');
    try {
      await element.clearValue();
    } catch (error) {
      throw this.interaction('clearText', error);
    }
  }

  async isDisplayed(
    sessionId: string,
    locator: ResolvedMobileLocator,
    timeoutMs: number,
  ): Promise<boolean> {
    const context = this.requireAndroid(sessionId, 'assertVisible');
    const selector = toSelector(locator);
    try {
      const element = await context.browser.$(selector);
      await element.waitForDisplayed({ timeout: boundTimeout(timeoutMs) });
      return true;
    } catch (error) {
      if (isInfraError(error)) throw this.interaction('assertVisible', error);
      return false;
    }
  }

  async getText(
    sessionId: string,
    locator: ResolvedMobileLocator,
    timeoutMs: number,
  ): Promise<string> {
    const element = await this.resolve(sessionId, locator, timeoutMs, 'assertText');
    try {
      return await element.getText();
    } catch (error) {
      throw this.interaction('assertText', error);
    }
  }

  async swipe(sessionId: string, direction: SwipeDirection, durationMs: number): Promise<void> {
    const context = this.requireAndroid(sessionId, 'swipe');
    try {
      await context.browser.swipe({
        direction,
        duration: Math.min(5000, Math.max(100, Math.floor(durationMs))),
        percent: 0.75,
      });
    } catch (error) {
      throw this.interaction('swipe', error);
    }
  }

  async pressBack(sessionId: string): Promise<void> {
    const context = this.requireAndroid(sessionId, 'back');
    try {
      // Android KEYCODE_BACK; no coordinate/script surface involved.
      await context.browser.pressKeyCode(4);
    } catch (error) {
      throw this.interaction('back', error);
    }
  }

  async hideKeyboard(sessionId: string): Promise<'closed' | 'absent'> {
    const context = this.requireAndroid(sessionId, 'hideKeyboard');
    try {
      await context.browser.hideKeyboard();
      return 'closed';
    } catch (error) {
      const message = error instanceof Error ? error.message : String(error ?? '');
      if (/no keyboard|keyboard.*not (present|visible|open)|not present/i.test(message)) {
        return 'absent';
      }
      throw this.interaction('hideKeyboard', error);
    }
  }

  async takeScreenshot(sessionId: string): Promise<string> {
    const context = this.requireAndroid(sessionId, 'screenshot');
    try {
      return await context.browser.takeScreenshot();
    } catch (error) {
      throw this.interaction('screenshot', error);
    }
  }

  async activateApp(sessionId: string, packageId: string): Promise<void> {
    const context = this.requireAndroid(sessionId, 'launchApp');
    try {
      await context.browser.activateApp(packageId);
    } catch (error) {
      throw this.interaction('launchApp', error);
    }
  }

  async terminateApp(sessionId: string, packageId: string): Promise<void> {
    const context = this.requireAndroid(sessionId, 'terminateApp');
    try {
      await context.browser.terminateApp(packageId);
    } catch (error) {
      throw this.interaction('terminateApp', error);
    }
  }

  private requireAndroid(sessionId: string, action: string): SessionContext {
    const context = this.contexts.get(sessionId);
    if (!context) {
      throw { kind: 'environment', message: `Cannot ${action}: the Appium session is no longer active.` } as InteractionError;
    }
    if (context.platform !== 'android') {
      throw { kind: 'automation', message: `Cannot ${action}: only Android is supported in this build.` } as InteractionError;
    }
    return context;
  }

  private async resolve(
    sessionId: string,
    locator: ResolvedMobileLocator,
    timeoutMs: number,
    action: string,
  ) {
    const context = this.requireAndroid(sessionId, action);
    try {
      const element = await context.browser.$(toSelector(locator));
      await element.waitForExist({ timeout: boundTimeout(timeoutMs) });
      return element;
    } catch (error) {
      throw this.interaction(action, error);
    }
  }

  private interaction(action: string, error: unknown): InteractionError {
    if (isInteractionError(error)) return error;
    if (isInfraError(error)) {
      const classified = classifyDriverError(error);
      return {
        kind: 'environment',
        message: truncate(`Cannot ${action}: ${classified.message}`),
      };
    }
    const message = error instanceof Error ? error.message : String(error ?? 'Unknown interaction failure.');
    return { kind: 'test', message: truncate(`Cannot ${action}: element is not interactable (${message})`) };
  }
}

/** Selector construction: data only. resourceId values are quoted for UiSelector. */
function toSelector(locator: ResolvedMobileLocator): string {
  if (locator.kind === 'accessibilityId') return `~${locator.value}`;
  const escaped = locator.value.replace(/\\/g, '\\\\').replace(/"/g, '\\"');
  return `android=new UiSelector().resourceId("${escaped}")`;
}

function boundTimeout(timeoutMs: number): number {
  if (!Number.isFinite(timeoutMs)) return 5000;
  return Math.min(120000, Math.max(1000, Math.floor(timeoutMs)));
}

/** Transport/session death vs. app-state absence. */
function isInfraError(error: unknown): boolean {
  const message = (error instanceof Error ? error.message : String(error ?? '')).toLowerCase();
  return (
    message.includes('invalid session') ||
    message.includes('no such session') ||
    message.includes('session') && message.includes('deleted') ||
    message.includes('econnrefused') ||
    message.includes('fetch failed') ||
    message.includes('socket hang up') ||
    message.includes('device offline') ||
    message.includes('device unauthorized') ||
    message.includes('device not found')
  );
}

function isInteractionError(error: unknown): error is InteractionError {
  if (!error || typeof error !== 'object') return false;
  const kind = (error as { kind?: unknown }).kind;
  return kind === 'test' || kind === 'environment' || kind === 'automation';
}
