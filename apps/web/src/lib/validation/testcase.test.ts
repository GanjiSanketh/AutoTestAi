import { describe, expect, it } from 'vitest';
import {
  isValidTestCaseKey,
  normalizeTestCaseKey,
  validateStepsClient,
} from './testcase';

describe('test-case key validation (client mirror)', () => {
  it('normalizes keys to the predictable format', () => {
    expect(normalizeTestCaseKey('  login-001 ')).toBe('LOGIN-001');
  });

  it('accepts server-valid keys', () => {
    expect(isValidTestCaseKey('AB')).toBe(true);
    expect(isValidTestCaseKey('LOGIN-001')).toBe(true);
    expect(isValidTestCaseKey('CHECKOUT_FLOW-2')).toBe(true);
  });

  it('rejects keys the server would reject', () => {
    expect(isValidTestCaseKey('A')).toBe(false);
    expect(isValidTestCaseKey('1ABC')).toBe(false);
    expect(isValidTestCaseKey('lower')).toBe(false);
    expect(isValidTestCaseKey('HAS SPACE')).toBe(false);
  });
});

describe('validateStepsClient', () => {
  it('accepts well-formed steps', () => {
    expect(
      validateStepsClient([{ action: 'click', target: '#x', value: '' }]),
    ).toEqual([]);
  });

  it('flags missing actions', () => {
    const problems = validateStepsClient([{ action: '  ', target: '', value: '' }]);
    expect(problems.some((p) => p.includes('Step 1'))).toBe(true);
  });
});
