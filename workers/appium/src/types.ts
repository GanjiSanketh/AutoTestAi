/**
 * Mobile worker execution protocol (Slices 3C-4A through 3C-4B-3).
 * Mirrors the C# contract (IMobileWorkerClient DTOs). JSON over HTTP,
 * camelCase throughout. The worker receives structured steps and
 * server-built capabilities only — never credentials, provider keys,
 * database connection strings, or ClaimToken values.
 *
 * Slice 3C-4B-2 executes the closed action set against a real Appium
 * session (Android-first) and reports passed/failed/error/cancelled/
 * timedOut results with screenshots. It never reports success for work
 * it did not perform.
 *
 * Slice 3C-4B-3 adds bounded failure evidence: redacted page-source
 * snapshots (text/xml) and the redacted worker log tail (text/plain).
 * Evidence is best-effort and bounded; capture failures never escalate
 * to infrastructure retries.
 */

export interface MobileStep {
  order: number;
  action: string;
  target?: string | null;
  value?: string | null;
}

export interface MobileTimeouts {
  executionMs: number;
  stepMs: number;
}

export interface MobileDeviceTarget {
  udid?: string | null;
  model?: string | null;
  platformVersion?: string | null;
}

export interface MobileAppTarget {
  packageId?: string | null;
  bundleId?: string | null;
  version?: string | null;
  installPolicy: string;
  launchActivity?: string | null;
  deepLink?: string | null;
  downloadUrl?: string | null;
}

export interface MobileCapabilities {
  platformName: string;
  automationName: string;
  deviceName?: string | null;
  udid?: string | null;
  appPackage?: string | null;
  appActivity?: string | null;
  bundleId?: string | null;
  app?: string | null;
  noReset: boolean;
  fullReset: boolean;
  newCommandTimeout: number;
}

export interface MobileAssignment {
  assignmentId: string;
  executionId: string;
  framework: string;
  platform: string;
  device: MobileDeviceTarget;
  app: MobileAppTarget;
  capabilities: MobileCapabilities;
  steps: MobileStep[];
  timeouts: MobileTimeouts;
  screenshotOnFailure: boolean;
  assignmentToken: string;
  /** Self-healing policy (Slice 3C-4C). Absent/disabled = pre-healing behavior. */
  healing?: MobileHealingPolicy | null;
}

/**
 * Worker-facing self-healing policy fragment (Slice 3C-4C, mirrors the
 * Playwright WorkerHealingPolicy shape). Backend-owned, defaults off.
 */
export interface MobileHealingPolicy {
  enabled: boolean;
  aiFallbackEnabled: boolean;
  /** Maximum healing retries per failed step. The engine enforces <= 1. */
  maxAttemptsPerStep: number;
  /** Optional minimum deterministic candidate score (0-100). Defaults conservative. */
  minDeterministicScore?: number | null;
  /** Optional minimum AI confidence (0-1). Advisory only: never overrides validation. */
  minAiConfidence?: number | null;
  /** Allowlisted locator strategies. Defaults to the safe mobile set. */
  allowedStrategies?: string[] | null;
}

export type MobileStepStatus = 'passed' | 'failed' | 'skipped' | 'error';
export type MobileOutcomeStatus =
  | 'passed'
  | 'failed'
  | 'timedOut'
  | 'cancelled'
  | 'error';
export type MobileClassification =
  | 'test'
  | 'application'
  | 'environment'
  | 'automation'
  | 'unknown';

export interface MobileStepResult {
  order: number;
  action: string;
  target?: string | null;
  status: MobileStepStatus;
  startedAtUnixMs: number;
  completedAtUnixMs: number;
  durationMs: number;
  errorMessage?: string | null;
  /** Slice 3C-4C: the stored target is never mutated; a healed step reports both. */
  healed?: boolean | null;
  recoveredTarget?: string | null;
  healingStrategy?: string | null;
  aiAssisted?: boolean | null;
}

export interface MobileLog {
  seq: number;
  timestampUnixMs: number;
  level: 'debug' | 'info' | 'warning' | 'error';
  message: string;
}

export interface MobileScreenshot {
  stepOrder?: number | null;
  fileName: string;
  contentType: string;
  base64Content: string;
}

/**
 * Bounded, redacted page-source snapshot (Slice 3C-4B-3). Captured
 * best-effort at the failure evidence point only; xmlContent is already
 * secret-masked, heuristically redacted, and hard-bounded to 1 MB before
 * it enters the result. Never carries tokens, credentials, or URLs.
 */
export interface MobilePageSource {
  stepOrder?: number | null;
  fileName: string;
  contentType: string;
  xmlContent: string;
}

/**
 * Bounded, redacted worker log tail (Slice 3C-4B-3). Serialized from the
 * assignment's in-memory log ring at terminal time, most-recent tail only,
 * hard-bounded to 256 KB. Attached to non-passed results as failure
 * evidence; never carries raw secrets, tokens, or presigned URLs.
 */
export interface MobileServerLog {
  fileName: string;
  contentType: string;
  textContent: string;
}

/** Persisted healing outcome for one step (worker → backend, execution-scoped). */
export interface MobileHealingAttempt {
  stepOrder: number;
  stepAction: string;
  originalStrategy: string | null;
  originalValue: string | null;
  recoveredStrategy: string | null;
  recoveredValue: string | null;
  healingStrategy: string;
  status: string;
  candidateCount: number;
  wasApplied: boolean;
  isAiAssisted: boolean;
  errorMessage: string | null;
}

export interface MobileResult {
  status: MobileOutcomeStatus;
  classification: MobileClassification;
  errorType?: string | null;
  errorMessage?: string | null;
  durationMs: number;
  stepResults: MobileStepResult[];
  logs: MobileLog[];
  screenshots: MobileScreenshot[];
  pageSources: MobilePageSource[];
  serverLogs: MobileServerLog[];
  /** Slice 3C-4C: one outcome record per healed-attempted step (append-only, execution-scoped). */
  healingAttempts?: MobileHealingAttempt[] | null;
  appiumSessionId?: string | null;
}

export type MobileAssignmentStatus =
  | 'queued'
  | 'running'
  | MobileOutcomeStatus;

export interface MobileAssignmentProgress {
  assignmentId: string;
  status: MobileAssignmentStatus;
  currentStepOrder?: number | null;
  stepResults: MobileStepResult[];
  logs: MobileLog[];
  result?: MobileResult | null;
  /** Present once the Appium session exists; echoed so the control plane
   * can bind the runtime session row without trusting worker logs. */
  appiumSessionId?: string | null;
}

/** MVP closed action set (Slice 3C-4A). Unknown actions fail safely. */
export const MOBILE_ACTIONS = new Set([
  'launchApp',
  'tap',
  'inputText',
  'clearText',
  'assertVisible',
  'assertText',
  'swipe',
  'back',
  'hideKeyboard',
  'wait',
  'screenshot',
  'terminateApp',
]);
