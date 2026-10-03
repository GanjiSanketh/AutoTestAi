import { existsSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { isHttpAppUrl, stageAppBinary } from '../src/appBinary.js';

describe('app binary staging', () => {
  it('preinstalled (null) is not an http url', () => {
    expect(isHttpAppUrl(null)).toBe(false);
    expect(isHttpAppUrl(undefined)).toBe(false);
    expect(isHttpAppUrl('/local/path.apk')).toBe(false);
    expect(isHttpAppUrl('http://insecure.example/app.apk')).toBe(false);
    expect(isHttpAppUrl('https://artifacts.example/app.apk?exp=900')).toBe(true);
  });

  it('rejects non-https urls without fetching', async () => {
    await expect(stageAppBinary('http://insecure.example/app.apk')).rejects.toMatchObject({
      kind: 'automation',
    });
    await expect(stageAppBinary('not-a-url')).rejects.toMatchObject({ kind: 'automation' });
  });

  it('downloads to a temp path and cleans up', async () => {
    const originalFetch = globalThis.fetch;
    (globalThis as { fetch: typeof fetch }).fetch = (async () =>
      new Response(Buffer.from('fake-apk-bytes'))) as typeof fetch;
    try {
      const staged = await stageAppBinary('https://artifacts.example/app.apk?exp=900');
      expect(staged.path).toContain('appium-app-');
      expect(existsSync(staged.path)).toBe(true);
      await staged.cleanup();
      expect(existsSync(staged.path)).toBe(false);
      await staged.cleanup(); // idempotent
    } finally {
      (globalThis as { fetch: typeof fetch }).fetch = originalFetch;
    }
  });

  it('maps download http failures to environment without leaking the url', async () => {
    const originalFetch = globalThis.fetch;
    (globalThis as { fetch: typeof fetch }).fetch = (async () =>
      new Response('nope', { status: 403 })) as typeof fetch;
    try {
      const error = await stageAppBinary('https://artifacts.example/app.apk?exp=900').catch(
        (e: unknown) => e as { kind: string; message: string },
      );
      expect(error.kind).toBe('environment');
      expect(error.message).not.toContain('artifacts.example');
    } finally {
      (globalThis as { fetch: typeof fetch }).fetch = originalFetch;
    }
  });
});
