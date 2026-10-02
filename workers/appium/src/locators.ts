/**
 * Controlled mobile target resolution (Slice 3C-4A). Targets are data, never
 * code: only explicit accessibility-id/resource-id prefixes resolve. Unlike
 * the web worker there is no bare-selector fallback — an unprefixed target
 * is rejected so locator strategy stays explicit and auditable.
 * No XPath, no UIAutomator strings, no iOS predicates in this checkpoint.
 *
 * Supported:
 *   accessibilityId=<value>   accessibility identifier
 *   resourceId=<value>        Android resource-id
 */

export type MobileLocatorKind = 'accessibilityId' | 'resourceId';

export interface ResolvedMobileLocator {
  kind: MobileLocatorKind;
  value: string;
}

export const MAX_LOCATOR_VALUE_LENGTH = 500;

const CONTROL_CHARS = /[\u0000-\u001F\u007F]/;

/** Parses a target string. Returns null when no usable locator was supplied. */
export function parseMobileTarget(target: string | null | undefined): ResolvedMobileLocator | null {
  if (!target || target.trim().length === 0) return null;
  const text = target.trim();
  const lower = text.toLowerCase();
  for (const prefix of ['accessibilityid=', 'resourceid='] as const) {
    if (lower.startsWith(prefix)) {
      const value = text.slice(prefix.length).trim();
      if (value.length === 0) return null;
      if (value.length > MAX_LOCATOR_VALUE_LENGTH) return null;
      if (CONTROL_CHARS.test(value)) return null;
      return {
        kind: prefix === 'accessibilityid=' ? 'accessibilityId' : 'resourceId',
        value,
      };
    }
  }
  return null;
}
