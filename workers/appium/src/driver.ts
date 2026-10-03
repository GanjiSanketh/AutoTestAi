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

export class WebdriverIoDriver implements IMobileDriver {
  private readonly browsers = new Map<string, Browser>();

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
    }
  }

  hasSession(sessionId: string): boolean {
    return this.browsers.has(sessionId);
  }
}
