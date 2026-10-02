import { describe, expect, it } from 'vitest';
import { parseMobileTarget } from '../src/locators.js';

describe('mobile locators', () => {
  it('parses accessibilityId targets', () => {
    expect(parseMobileTarget('accessibilityId=login-button')).toEqual({
      kind: 'accessibilityId',
      value: 'login-button',
    });
  });

  it('parses resourceId targets case-insensitively on the prefix', () => {
    expect(parseMobileTarget('ResourceId=com.example:id/login')).toEqual({
      kind: 'resourceId',
      value: 'com.example:id/login',
    });
  });

  it('rejects bare, xpath, and empty targets', () => {
    expect(parseMobileTarget('#login')).toBeNull();
    expect(parseMobileTarget('xpath=//button')).toBeNull();
    expect(parseMobileTarget('')).toBeNull();
    expect(parseMobileTarget(null)).toBeNull();
    expect(parseMobileTarget('accessibilityId=')).toBeNull();
    expect(parseMobileTarget('accessibilityId=   ')).toBeNull();
  });

  it('rejects overlong and control-character values', () => {
    expect(parseMobileTarget(`accessibilityId=${'x'.repeat(501)}`)).toBeNull();
    expect(parseMobileTarget('accessibilityId=a\u0007b')).toBeNull();
  });
});
