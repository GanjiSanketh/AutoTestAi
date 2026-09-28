import { describe, expect, it } from 'vitest';
import { parseTarget } from '../src/locators.js';

describe('parseTarget', () => {
  it('returns null for missing targets', () => {
    expect(parseTarget(null)).toBeNull();
    expect(parseTarget(undefined)).toBeNull();
    expect(parseTarget('   ')).toBeNull();
  });

  it('treats bare selectors as CSS', () => {
    expect(parseTarget('#username')).toEqual({ kind: 'css', selector: '#username' });
  });

  it('parses explicit prefixes', () => {
    expect(parseTarget('css=.btn')).toEqual({ kind: 'css', selector: '.btn' });
    expect(parseTarget('xpath=//button')).toEqual({ kind: 'xpath', selector: '//button' });
    expect(parseTarget('text=Submit')).toEqual({ kind: 'text', selector: 'Submit' });
    expect(parseTarget('testid=login-btn')).toEqual({ kind: 'testid', selector: 'login-btn' });
  });

  it('parses role with optional accessible name', () => {
    expect(parseTarget('role=button')).toEqual({ kind: 'role', selector: 'button' });
    expect(parseTarget('role=button|Submit')).toEqual({
      kind: 'role',
      selector: 'button',
      name: 'Submit',
    });
  });

  it('matches prefixes case-insensitively but preserves selector case', () => {
    expect(parseTarget('CSS=#UserName')).toEqual({ kind: 'css', selector: '#UserName' });
  });
});
