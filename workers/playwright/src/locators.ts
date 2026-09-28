/**
 * Controlled target resolution (Slice 5 §14). Targets are data, never code:
 * only explicit prefixes map to Playwright locators. Anything else is treated
 * as a CSS selector. No JavaScript evaluation, no API smuggling.
 *
 * Supported:
 *   css=<selector>      explicit CSS
 *   xpath=<expression>  explicit XPath
 *   role=<role>|<name>  accessible role, optional accessible name after |
 *   text=<text>         visible text (exact=false substring)
 *   testid=<id>         data-testid attribute
 *   <bare>              CSS selector
 */

export type LocatorKind = 'css' | 'xpath' | 'role' | 'text' | 'testid';

export interface ResolvedLocator {
  kind: LocatorKind;
  selector: string;
  name?: string;
}

/** Parses a target string. Returns null when no usable target was supplied. */
export function parseTarget(target: string | null | undefined): ResolvedLocator | null {
  if (!target || target.trim().length === 0) return null;
  const text = target.trim();
  const lower = text.toLowerCase();
  for (const prefix of ['css=', 'xpath=', 'role=', 'text=', 'testid='] as const) {
    if (lower.startsWith(prefix)) {
      const rest = text.slice(prefix.length);
      if (prefix === 'role=') {
        const pipe = rest.indexOf('|');
        if (pipe >= 0) {
          return {
            kind: 'role',
            selector: rest.slice(0, pipe).trim(),
            name: rest.slice(pipe + 1).trim() || undefined,
          };
        }
        return { kind: 'role', selector: rest.trim() };
      }
      return { kind: prefix.slice(0, -1) as LocatorKind, selector: rest };
    }
  }
  return { kind: 'css', selector: text };
}
