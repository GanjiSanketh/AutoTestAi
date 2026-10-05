import { api } from '../client';

export interface GenerateTestInput {
  title: string;
  description?: string;
  requirements: string[];
  targetUrl?: string;
  framework: string;
  platform: string;
  module?: string;
  priority?: string;
  additionalContext?: string;
}

export interface GeneratedStep {
  order: number;
  action: string;
  target: string | null;
  value: string | null;
}

export interface GenerateTestResult {
  generationId: string;
  testCaseId: string;
  testKey: string;
  versionId: string;
  versionNumber: number;
  status: string;
  title: string;
  description: string | null;
  framework: string;
  platform: string;
  structuredSteps: GeneratedStep[];
  sourceCode: string;
  assumptions: string[];
  warnings: string[];
  provider: string;
  model: string | null;
  promptVersion: string;
  latencyMs: number;
  inputTokens: number | null;
  outputTokens: number | null;
  totalTokens: number | null;
  reviewStatus: string;
}

export interface AiProviderStatus {
  provider: string;
  model: string | null;
  configured: boolean;
  detail: string | null;
  promptVersion: string;
}

export interface StoryTestGenerationInput {
  storyTitle: string;
  storyDescription?: string;
  acceptanceCriteria: string[];
  targetUrl?: string;
  framework: string;
  platform: string;
  module?: string;
  priority?: string;
  additionalContext?: string;
  maxProposals?: number;
}

export interface StoryTestProposal {
  proposalId: string;
  index: number;
  status: string;
  title: string | null;
  description: string | null;
  framework: string | null;
  platform: string | null;
  module: string | null;
  priority: string | null;
  focusCriterion: string | null;
  focusCriterionIndex: number;
  structuredSteps: GeneratedStep[];
  sourceCode: string | null;
  assumptions: string[];
  warnings: string[];
  provider: string | null;
  model: string | null;
  promptVersion: string;
  latencyMs: number;
  /** Redacted story provenance echoed back on save; null for failed proposals. */
  provenance: Record<string, unknown> | null;
  errorCode: string | null;
  errorMessage: string | null;
}

export interface StoryTestGenerationResult {
  generationId: string;
  promptVersion: string;
  proposalCount: number;
  successCount: number;
  failureCount: number;
  proposals: StoryTestProposal[];
}

export const testGenerationKeys = {
  all: ['test-generation'] as const,
  status: (projectId: string) => [...testGenerationKeys.all, 'status', projectId] as const,
};

/** Centralized AI generation API surface — no raw fetch calls in components. */
export const testGenerationEndpoints = {
  generate: (projectId: string, input: GenerateTestInput) =>
    api.post<GenerateTestResult>(`/api/v1/projects/${projectId}/test-generation`, input),
  status: (projectId: string) =>
    api.get<AiProviderStatus>(`/api/v1/projects/${projectId}/ai-provider-status`),
  generateFromStory: (projectId: string, input: StoryTestGenerationInput) =>
    api.post<StoryTestGenerationResult>(`/api/v1/projects/${projectId}/story-test-generation`, input),
};
