/**
 * Step-value hygiene (Slice 5 §16). Mirrors the backend ExecutionValueRedactor:
 * values written into password-like targets are masked everywhere (logs,
 * results, persisted history). The frontend never receives sensitive values.
 */

const SENSITIVE_TARGET = /passw|passwd|pwd|secret|token|private[_-]?key/i;

export const REDACTED = '[REDACTED]';

export function isSensitiveTarget(target: string | null | undefined): boolean {
  return !!target && SENSITIVE_TARGET.test(target);
}

export function redactStepValue(
  action: string,
  target: string | null | undefined,
  value: string | null | undefined,
): string | null | undefined {
  if (value == null) return value;
  if (!isSensitiveTarget(target)) return value;
  switch (action.trim().toLowerCase()) {
    case 'fill':
    case 'type':
    case 'select':
    case 'press':
      return REDACTED;
    default:
      return value;
  }
}
