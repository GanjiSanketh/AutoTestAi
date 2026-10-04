/**
 * Failure-evidence sanitizer (Slice 3C-4B-3).
 *
 * Page-source snapshots and worker log tails can embed typed secrets,
 * tokens, and presigned URLs. Nothing in this module ever returns raw
 * evidence. Required flow, in order:
 *
 *   raw evidence
 *   → exact known-secret masking (assignment token, download URL, typed values)
 *   → heuristic sensitive-value redaction (bearer tokens, password shapes, signatures)
 *   → hard size bound (page source: head 1 MB; server logs: most-recent 256 KB tail)
 *
 * Raw evidence is never logged, never thrown, and never placed in artifact
 * metadata — only file names (no user input, secrets, tokens, or URLs).
 * Capture failures are best-effort: callers log a bounded warning and
 * continue; evidence problems never escalate to infrastructure retries.
 */
import { REDACTED } from './redaction.js';

/** Hard bound for persisted page-source snapshots (chars, XML text). */
export const MAX_PAGE_SOURCE_CHARS = 1024 * 1024;

/** Hard bound for persisted worker log-tail artifacts (chars). */
export const MAX_SERVER_LOG_CHARS = 256 * 1024;

/** Exact values that must never survive in evidence. */
export interface EvidenceSecrets {
  /** Worker-side assignment token (authenticates the assignment API). */
  assignmentToken?: string | null;
  /** Server-minted presigned binary URL (Install/Reinstall only). */
  downloadUrl?: string | null;
  /** Values typed through text-entry steps (may be secrets). */
  typedValues?: Array<string | null | undefined>;
}

function exactMask(text: string, secrets: EvidenceSecrets): string {
  const values = new Set<string>();
  if (secrets.assignmentToken && secrets.assignmentToken.trim().length >= 4) {
    values.add(secrets.assignmentToken.trim());
  }
  if (secrets.downloadUrl && secrets.downloadUrl.trim().length >= 4) {
    values.add(secrets.downloadUrl.trim());
  }
  for (const typed of secrets.typedValues ?? []) {
    if (typeof typed === 'string' && typed.trim().length >= 4) values.add(typed);
  }
  let out = text;
  for (const value of values) {
    // Plain split/join: no regex metacharacter hazards from secret content.
    out = out.split(value).join(REDACTED);
  }
  return out;
}

const BEARER = /(Bearer\s+)[A-Za-z0-9\-._~+/=]{8,}/gi;
const PASSWORD_SHAPE =
  /((?:password|passwd|pwd|secret|api[_-]?key|access[_-]?key)\s*[:=]\s*)(["']?)[^"'<>\s]+/gi;
const SIGNATURE_PARAM = /([?&](?:X-Amz-Signature|Signature|sig|token)=)[^&\s"'<>]+/gi;

function heuristicRedact(text: string): string {
  return text
    .replace(BEARER, `$1${REDACTED}`)
    .replace(PASSWORD_SHAPE, `$1${REDACTED}`)
    .replace(SIGNATURE_PARAM, `$1${REDACTED}`);
}

/**
 * Full sanitize pipeline: exact masking, then heuristic redaction, then
 * the hard bound. Page sources keep the head (document structure);
 * server-log tails keep the most recent entries (pass tail: true).
 */
export function sanitizeEvidenceText(
  raw: string,
  secrets: EvidenceSecrets,
  maxChars: number,
  tail: boolean,
): string {
  const masked = heuristicRedact(exactMask(raw, secrets));
  if (masked.length <= maxChars) return masked;
  return tail ? masked.slice(masked.length - maxChars) : masked.slice(0, maxChars);
}

/** Page-source snapshot filename for a step failure (no user input). */
export function pageSourceFileName(order: number): string {
  return `step-${order}-pagesource.xml`;
}

/** Server-log artifact filename (assignment scope, no step/user input). */
export function serverLogFileName(): string {
  return 'appium.log';
}

export interface SanitizedPageSource {
  fileName: string;
  xmlContent: string;
}

/** Best-effort snapshot: returns null only when capture itself failed. */
export function sanitizePageSource(
  raw: string,
  order: number,
  secrets: EvidenceSecrets,
): SanitizedPageSource {
  return {
    fileName: pageSourceFileName(order),
    xmlContent: sanitizeEvidenceText(raw, secrets, MAX_PAGE_SOURCE_CHARS, false),
  };
}

export interface SanitizedServerLog {
  fileName: string;
  textContent: string;
}

/** Serializes assignment log lines and keeps the redacted tail. */
export function sanitizeServerLogTail(
  lines: string[],
  secrets: EvidenceSecrets,
): SanitizedServerLog {
  const joined = lines.join('\n');
  return {
    fileName: serverLogFileName(),
    textContent: sanitizeEvidenceText(joined, secrets, MAX_SERVER_LOG_CHARS, true),
  };
}
