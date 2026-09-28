import { describe, expect, it } from 'vitest';
import { isAbsoluteHttpUrl, isValidProjectKey, normalizeProjectKey } from './project';

describe('project key validation (client mirror)', () => {
  it('normalizes keys to the predictable format', () => {
    expect(normalizeProjectKey('  cust-portal ')).toBe('CUST-PORTAL');
  });

  it('accepts server-valid keys', () => {
    expect(isValidProjectKey('AB')).toBe(true);
    expect(isValidProjectKey('CUSTPORTAL')).toBe(true);
    expect(isValidProjectKey('SHOP_2026-X')).toBe(true);
  });

  it('rejects keys the server would reject', () => {
    expect(isValidProjectKey('A')).toBe(false);
    expect(isValidProjectKey('1ABC')).toBe(false);
    expect(isValidProjectKey('lower')).toBe(false);
    expect(isValidProjectKey('HAS SPACE')).toBe(false);
  });
});

describe('isAbsoluteHttpUrl', () => {
  it('accepts blank (optional fields)', () => {
    expect(isAbsoluteHttpUrl('')).toBe(true);
    expect(isAbsoluteHttpUrl('   ')).toBe(true);
  });

  it('accepts http(s) URLs and rejects the rest', () => {
    expect(isAbsoluteHttpUrl('https://qa.example.com')).toBe(true);
    expect(isAbsoluteHttpUrl('http://localhost:3000/x')).toBe(true);
    expect(isAbsoluteHttpUrl('not-a-url')).toBe(false);
    expect(isAbsoluteHttpUrl('ftp://files.example.com')).toBe(false);
  });
});
