import { describe, expect, it } from 'vitest';
import { REDACTED, isSensitiveTarget, redactStepValue } from '../src/redaction.js';

describe('execution value redaction', () => {
  it('detects password-like targets', () => {
    expect(isSensitiveTarget('#password')).toBe(true);
    expect(isSensitiveTarget('input[name="passwd"]')).toBe(true);
    expect(isSensitiveTarget('[data-secret-token]')).toBe(true);
    expect(isSensitiveTarget('#username')).toBe(false);
    expect(isSensitiveTarget(null)).toBe(false);
  });

  it('masks values written into sensitive targets', () => {
    expect(redactStepValue('fill', '#password', 'hunter2')).toBe(REDACTED);
    expect(redactStepValue('type', '#password', 'hunter2')).toBe(REDACTED);
    expect(redactStepValue('fill', '#username', 'qa-user')).toBe('qa-user');
    expect(redactStepValue('click', '#password', null)).toBeNull();
  });

  it('leaves read-only actions untouched', () => {
    expect(redactStepValue('assertText', '#password-hint', 'hint')).toBe('hint');
  });
});
