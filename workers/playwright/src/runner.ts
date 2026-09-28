import type { WorkerConfig } from './config.js';

export interface ExecutionAssignment {
  executionId: string;
  testCaseVersionId: string;
  environmentId?: string;
}

/**
 * Phase-1 runner seam. Receives only the execution id, approved test revision,
 * environment reference and secret references from the Temporal workflow —
 * never raw credentials (docs/04 §5). The API must never execute test code
 * directly; this isolated worker is the only execution site.
 */
export async function runAssignment(
  _config: WorkerConfig,
  assignment: ExecutionAssignment,
  signal: AbortSignal,
): Promise<void> {
  if (signal.aborted) throw new Error('Assignment aborted before start.');
  // Phase 1: launch Playwright, execute the approved revision, stream logs,
  // upload screenshots/traces/video to MinIO, return the structured result.
  throw new Error(
    `Phase-0 placeholder: execution ${assignment.executionId} not executed (runner lands in Phase 1).`,
  );
}
