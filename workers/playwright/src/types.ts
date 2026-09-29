/**
 * Worker execution protocol (Slice 5 §12). Mirrors the C# contract
 * (IPlaywrightWorkerClient DTOs). JSON over HTTP, camelCase throughout.
 * The worker receives steps only — never credentials, provider keys, or
 * database connection strings.
 */

export interface WorkerStep {
  order: number;
  action: string;
  target?: string | null;
  value?: string | null;
}

export interface WorkerTimeouts {
  executionMs: number;
  stepMs: number;
}

export interface WorkerAssignment {
  assignmentId: string;
  executionId: string;
  framework: string;
  browser: string;
  targetUrl?: string | null;
  steps: WorkerStep[];
  timeouts: WorkerTimeouts;
  screenshotOnFailure: boolean;
  screenshotOnFinish: boolean;
  assignmentToken: string;
}

export type WorkerStepStatus = 'passed' | 'failed' | 'skipped' | 'error';
export type WorkerOutcomeStatus =
  | 'passed'
  | 'failed'
  | 'timedOut'
  | 'cancelled'
  | 'error';
export type WorkerClassification =
  | 'test'
  | 'application'
  | 'environment'
  | 'automation'
  | 'unknown';

export interface WorkerStepResult {
  order: number;
  action: string;
  target?: string | null;
  status: WorkerStepStatus;
  startedAtUnixMs: number;
  completedAtUnixMs: number;
  durationMs: number;
  errorMessage?: string | null;
}

export interface WorkerLog {
  seq: number;
  timestampUnixMs: number;
  level: 'debug' | 'info' | 'warning' | 'error';
  message: string;
}

export interface WorkerScreenshot {
  stepOrder?: number | null;
  fileName: string;
  contentType: string;
  base64Content: string;
}

export interface WorkerResult {
  status: WorkerOutcomeStatus;
  classification: WorkerClassification;
  errorType?: string | null;
  errorMessage?: string | null;
  durationMs: number;
  stepResults: WorkerStepResult[];
  logs: WorkerLog[];
  screenshots: WorkerScreenshot[];
}

export type AssignmentStatus =
  | 'queued'
  | 'running'
  | WorkerOutcomeStatus;

export interface AssignmentProgress {
  assignmentId: string;
  status: AssignmentStatus;
  currentStepOrder?: number | null;
  stepResults: WorkerStepResult[];
  logs: WorkerLog[];
  result?: WorkerResult | null;
}

/** MVP supported actions (Slice 5 §13). Unknown actions fail safely. */
export const SUPPORTED_ACTIONS = new Set([
  'navigate',
  'click',
  'fill',
  'type',
  'select',
  'check',
  'uncheck',
  'press',
  'wait',
  'assertVisible',
  'assertText',
  'assertValue',
  'screenshot',
]);
