/**
 * Trusted app-binary staging (Slice 3C-4B-1).
 *
 * Install/Reinstall assignments carry a server-minted short-lived https
 * download URL in capabilities.app. The worker downloads it to a
 * worker-controlled temporary file (never a user-provided path) and hands
 * the temp path to Appium. Temp files are always cleaned up. Preinstalled
 * apps (app == null) skip this entirely. The URL itself is never logged.
 */
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

export interface StagedAppBinary {
  path: string;
  cleanup: () => Promise<void>;
}

export function isHttpAppUrl(value: string | null | undefined): boolean {
  if (!value) return false;
  try {
    const url = new URL(value);
    return url.protocol === 'https:';
  } catch {
    return false;
  }
}

export async function stageAppBinary(
  appUrl: string,
  signal?: AbortSignal,
): Promise<StagedAppBinary> {
  let url: URL;
  try {
    url = new URL(appUrl);
  } catch {
    throw { kind: 'automation', message: 'App download URL is not a valid URL.' };
  }
  if (url.protocol !== 'https:') {
    throw { kind: 'automation', message: 'App download URL must use https.' };
  }
  if (signal?.aborted) {
    throw { kind: 'environment', message: 'App download aborted before it started.' };
  }
  let response: Response;
  try {
    response = await fetch(url, { signal });
  } catch (error) {
    throw {
      kind: 'environment',
      message: `App download failed: ${error instanceof Error ? error.message.slice(0, 200) : 'network error'}`,
    };
  }
  if (!response.ok) {
    throw {
      kind: 'environment',
      message: `App download failed with status ${response.status}.`,
    };
  }
  const bytes = Buffer.from(await response.arrayBuffer());
  if (bytes.length === 0) {
    throw { kind: 'environment', message: 'App download returned an empty binary.' };
  }
  const dir = await mkdtemp(join(tmpdir(), 'appium-app-'));
  const path = join(dir, 'app.apk');
  try {
    await writeFile(path, bytes);
  } catch (error) {
    await rm(dir, { recursive: true, force: true }).catch(() => undefined);
    throw error;
  }
  let cleaned = false;
  const cleanup = async (): Promise<void> => {
    if (cleaned) return;
    cleaned = true;
    await rm(dir, { recursive: true, force: true }).catch(() => undefined);
  };
  return { path, cleanup };
}
