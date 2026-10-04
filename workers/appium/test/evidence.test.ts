import { describe, expect, it } from 'vitest';
import {
  MAX_PAGE_SOURCE_CHARS,
  MAX_SERVER_LOG_CHARS,
  pageSourceFileName,
  sanitizeEvidenceText,
  sanitizePageSource,
  sanitizeServerLogTail,
  serverLogFileName,
  type EvidenceSecrets,
} from '../src/evidence.js';

const SECRETS: EvidenceSecrets = {
  assignmentToken: 'token-abc-123',
  downloadUrl: 'https://artifacts.example/mobile-apps/shop.apk?X-Amz-Signature=deadbeef',
  typedValues: ['hunter2-secret', 's3cret-value'],
};

describe('evidence bounds', () => {
  it('caps page sources at 1 MB keeping the head', () => {
    expect(MAX_PAGE_SOURCE_CHARS).toBe(1024 * 1024);
    const raw = `HEAD-${'x'.repeat(MAX_PAGE_SOURCE_CHARS + 100)}`;
    const out = sanitizeEvidenceText(raw, {}, MAX_PAGE_SOURCE_CHARS, false);
    expect(out.length).toBe(MAX_PAGE_SOURCE_CHARS);
    expect(out.startsWith('HEAD-')).toBe(true);
  });

  it('caps server-log tails at 256 KB keeping the most recent entries', () => {
    expect(MAX_SERVER_LOG_CHARS).toBe(256 * 1024);
    const raw = `FIRST-${'y'.repeat(MAX_SERVER_LOG_CHARS + 100)}-LAST`;
    const out = sanitizeEvidenceText(raw, {}, MAX_SERVER_LOG_CHARS, true);
    expect(out.length).toBe(MAX_SERVER_LOG_CHARS);
    expect(out.endsWith('-LAST')).toBe(true);
    expect(out.startsWith('FIRST-')).toBe(false);
  });

  it('passes small evidence through untouched', () => {
    expect(sanitizeEvidenceText('<ok/>', {}, MAX_PAGE_SOURCE_CHARS, false)).toBe('<ok/>');
  });
});

describe('evidence secret masking', () => {
  it('masks the assignment token exactly', () => {
    const out = sanitizeEvidenceText('session token-abc-123 active', SECRETS, MAX_PAGE_SOURCE_CHARS, false);
    expect(out).not.toContain('token-abc-123');
    expect(out).toContain('[REDACTED]');
  });

  it('masks the presigned download URL exactly', () => {
    const out = sanitizeEvidenceText(
      `binary ${SECRETS.downloadUrl} staged`,
      SECRETS,
      MAX_PAGE_SOURCE_CHARS,
      false,
    );
    expect(out).not.toContain('deadbeef');
    expect(out).not.toContain('artifacts.example');
  });

  it('masks typed step values exactly', () => {
    const out = sanitizeEvidenceText(
      '<node text="hunter2-secret" />',
      SECRETS,
      MAX_PAGE_SOURCE_CHARS,
      false,
    );
    expect(out).not.toContain('hunter2-secret');
  });

  it('redacts bearer credentials heuristically', () => {
    const out = sanitizeEvidenceText(
      'auth Bearer eyJhbGciOiJIUzI1NiJ9.payload here',
      {},
      MAX_SERVER_LOG_CHARS,
      true,
    );
    expect(out).not.toContain('eyJhbGciOiJIUzI1NiJ9');
    expect(out).toContain('Bearer [REDACTED]');
  });

  it('redacts password shapes heuristically', () => {
    const out = sanitizeEvidenceText(
      '<node password="supersecret" />',
      {},
      MAX_PAGE_SOURCE_CHARS,
      false,
    );
    expect(out).not.toContain('supersecret');
  });

  it('redacts signature query parameters heuristically', () => {
    const out = sanitizeEvidenceText(
      'get https://cdn.example/app.apk?X-Amz-Signature=abcdef&exp=9 failed',
      {},
      MAX_SERVER_LOG_CHARS,
      true,
    );
    expect(out).not.toContain('abcdef');
  });

  it('masks before bounding so secrets cannot survive truncation', () => {
    const secret = 'hunter2-secret';
    const raw = `${'z'.repeat(MAX_PAGE_SOURCE_CHARS)}${secret}`;
    const out = sanitizeEvidenceText(raw, { typedValues: [secret] }, MAX_PAGE_SOURCE_CHARS, false);
    expect(out).not.toContain(secret);
  });
});

describe('evidence artifacts', () => {
  it('names page sources deterministically without user input', () => {
    expect(pageSourceFileName(3)).toBe('step-3-pagesource.xml');
    const snap = sanitizePageSource('<hierarchy/>', 3, SECRETS);
    expect(snap.fileName).toBe('step-3-pagesource.xml');
    expect(snap.xmlContent).toBe('<hierarchy/>');
  });

  it('serializes the log tail under a fixed name', () => {
    expect(serverLogFileName()).toBe('appium.log');
    const tail = sanitizeServerLogTail(['[1 info] a', '[2 error] b'], SECRETS);
    expect(tail.fileName).toBe('appium.log');
    expect(tail.textContent).toBe('[1 info] a\n[2 error] b');
  });

  it('redacts secrets across the whole log tail', () => {
    const tail = sanitizeServerLogTail(
      ['[1 info] accepted', '[2 info] token token-abc-123 seen', '[3 info] done'],
      SECRETS,
    );
    expect(tail.textContent).not.toContain('token-abc-123');
    expect(tail.textContent).toContain('done');
  });
});
