import { api } from '../client';

export interface FailureAnalysis {
  id: string;
  executionId: string;
  executionTestId: string;
  attempt: number;
  status: string;
  classification: string;
  summary: string | null;
  probableCause: string | null;
  confidence: number | null;
  evidence: string[];
  assumptions: string[];
  warnings: string[];
  recommendedAction: string | null;
  isLikelyDefect: boolean;
  provider: string | null;
  model: string | null;
  promptVersion: string | null;
  latencyMs: number | null;
  inputTokens: number | null;
  outputTokens: number | null;
  totalTokens: number | null;
  createdAt: string;
}

export const failureAnalysisKeys = {
  all: ['failure-analysis'] as const,
  latest: (projectId: string, executionId: string) =>
    [...failureAnalysisKeys.all, 'latest', projectId, executionId] as const,
  attempts: (projectId: string, executionId: string) =>
    [...failureAnalysisKeys.all, 'attempts', projectId, executionId] as const,
};

/** Centralized failure-analysis API surface — no raw fetch calls in components. */
export const failureAnalysisEndpoints = {
  analyze: (projectId: string, executionId: string) =>
    api.post<FailureAnalysis>(`/api/v1/projects/${projectId}/executions/${executionId}/failure-analysis`),
  latest: (projectId: string, executionId: string) =>
    api.get<FailureAnalysis>(`/api/v1/projects/${projectId}/executions/${executionId}/failure-analysis`),
  attempts: (projectId: string, executionId: string) =>
    api.get<FailureAnalysis[]>(
      `/api/v1/projects/${projectId}/executions/${executionId}/failure-analysis/attempts`,
    ),
};
