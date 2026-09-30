import { api } from '../client';

export interface SelfHealingPolicy {
  projectId: string;
  enabled: boolean;
  aiFallbackEnabled: boolean;
  maxAttemptsPerStep: number;
  minDeterministicScore: number | null;
  minAiConfidence: number | null;
  allowedStrategies: string[];
  updatedAt: string;
}

export interface SelfHealingPolicyStatus {
  projectId: string;
  enabled: boolean;
  configured: boolean;
  aiFallbackEnabled: boolean;
  attemptCount: number;
  appliedCount: number;
  lastHealedAt: string | null;
}

export interface UpsertSelfHealingPolicyInput {
  enabled: boolean;
  aiFallbackEnabled?: boolean;
  minDeterministicScore?: number | null;
  minAiConfidence?: number | null;
  allowedStrategies?: string[];
}

export interface HealingAttempt {
  id: string;
  executionId: string;
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
  createdAt: string;
}

export const selfHealingKeys = {
  all: ['self-healing'] as const,
  policy: (projectId: string) => [...selfHealingKeys.all, 'policy', projectId] as const,
  status: (projectId: string) => [...selfHealingKeys.all, 'status', projectId] as const,
  attempts: (projectId: string, executionId: string) =>
    [...selfHealingKeys.all, 'attempts', projectId, executionId] as const,
};

/** Centralized self-healing API surface — no raw fetch calls in components. */
export const selfHealingEndpoints = {
  getPolicy: (projectId: string) =>
    api.get<SelfHealingPolicy | null>(`/api/v1/projects/${projectId}/self-healing-policy`),
  upsertPolicy: (projectId: string, input: UpsertSelfHealingPolicyInput) =>
    api.put<SelfHealingPolicy>(`/api/v1/projects/${projectId}/self-healing-policy`, input),
  getStatus: (projectId: string) =>
    api.get<SelfHealingPolicyStatus>(`/api/v1/projects/${projectId}/self-healing-policy/status`),
  listAttempts: (projectId: string, executionId: string) =>
    api.get<HealingAttempt[]>(`/api/v1/projects/${projectId}/executions/${executionId}/healing`),
};

export function selfHealingErrorMessage(status: number, code?: string): string {
  if (status === 401) return 'Your session has expired. Please sign in again.';
  if (status === 403) return 'You do not have permission to configure self-healing for this project.';
  if (status === 404) return 'The project or execution could not be found.';
  if (code === 'VALIDATION_ERROR' || status === 400)
    return 'The self-healing policy is invalid. Check thresholds and strategies.';
  return 'Saving the self-healing policy failed. Please try again.';
}
