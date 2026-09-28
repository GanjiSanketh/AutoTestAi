/**
 * Client-side mirrors of the server project-key rules (docs/06 §5).
 * Server validation is authoritative; this is UX-only.
 */
export function normalizeProjectKey(key: string): string {
  return key.trim().toUpperCase();
}

export function isValidProjectKey(key: string): boolean {
  return /^[A-Z][A-Z0-9_-]{1,31}$/.test(key);
}

export function isAbsoluteHttpUrl(value: string): boolean {
  if (!value.trim()) return true; // optional fields accept blank
  try {
    const url = new URL(value.trim());
    return url.protocol === 'http:' || url.protocol === 'https:';
  } catch {
    return false;
  }
}
