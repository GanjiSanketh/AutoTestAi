/**
 * Mobile worker execution protocol (Slice 3C-4A).
 * Mirrors the C# contract (IMobileWorkerClient DTOs). JSON over HTTP,
 * camelCase throughout. The worker receives structured steps and
 * server-built capabilities only — never credentials, provider keys,
 * database connection strings, or ClaimToken values.
 *
 * Actual Appium driver creation is deferred to the execution slice; this
 * checkpoint validates the envelope and reports a controlled deferred
 * result. It never reports success for work it did not perform.
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

export interface MobileResult {
  status: MobileOutcomeStatus;
  classification: MobileClassification;
  errorType?: string | null;
  errorMessage?: string | null;
  durationMs: number;
  stepResults: MobileStepResult[];
  logs: MobileLog[];
  screenshots: MobileScreenshot[];
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
