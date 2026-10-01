import { api } from '../client';

export interface CiIntegration {
  id: string;
  projectId: string;
  provider: string;
  enabled: boolean;
  configured: boolean;
  defaultSuiteId: string | null;
  defaultEnvironmentId: string | null;
  eventAllowlist: string[];
  branchAllowlist: string[];
  repositoryAllowlist: string[];
  variableMapping: Record<string, string>;
  username: string | null;
  secretMapping: Record<string, string>;
  hasSecret: boolean;
  webhookUrl: string;
  updatedAt: string;
}

export interface CiIntegrationInput {
  provider: string;
  enabled: boolean;
  defaultSuiteId?: string | null;
  defaultEnvironmentId?: string | null;
  eventAllowlist?: string[];
  branchAllowlist?: string[];
  repositoryAllowlist?: string[];
  variableMapping?: Record<string, string>;
  username?: string | null;
  secretMapping?: Record<string, string>;
  webhookSecret?: string | null;
}

export interface WebhookDelivery {
  id: string;
  integrationId: string;
  projectId: string;
  provider: string;
  deliveryId: string;
  eventType: string;
  verificationStatus: string;
  processingStatus: string;
  executionId: string | null;
  triggeredCount: number;
  failureReason: string | null;
  receivedAt: string;
  processedAt: string | null;
  createdAt: string;
}

export interface PagedDeliveries {
  items: WebhookDelivery[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export const ciCdKeys = {
  all: ['cicd'] as const,
  list: (projectId: string) => [...ciCdKeys.all, 'list', projectId] as const,
  integration: (projectId: string, provider: string) =>
    [...ciCdKeys.all, 'integration', projectId, provider] as const,
  deliveries: (projectId: string, provider: string, page: number) =>
    [...ciCdKeys.all, 'deliveries', projectId, provider, page] as const,
};

/** Centralized CI/CD webhook API surface — no raw fetch calls in components. */
export const ciCdEndpoints = {
  list: (projectId: string) =>
    api.get<CiIntegration[]>(`/api/v1/projects/${projectId}/integrations/cicd`),
  get: (projectId: string, provider: string) =>
    api.get<CiIntegration>(`/api/v1/projects/${projectId}/integrations/cicd/${provider}`),
  upsert: (projectId: string, input: CiIntegrationInput) =>
    api.put<CiIntegration>(`/api/v1/projects/${projectId}/integrations/cicd`, input),
  deliveries: (projectId: string, provider: string, page = 1, pageSize = 25) =>
    api.get<PagedDeliveries>(
      `/api/v1/projects/${projectId}/integrations/cicd/${provider}/deliveries?page=${page}&pageSize=${pageSize}`,
    ),
  retry: (projectId: string, provider: string, deliveryId: string) =>
    api.post<void>(
      `/api/v1/projects/${projectId}/integrations/cicd/${provider}/deliveries/${deliveryId}/retry`,
    ),
};

export const PROVIDERS = ['github', 'gitlab', 'jenkins', 'azure'] as const;

export const PROVIDER_EVENTS: Record<string, string[]> = {
  github: ['push', 'pull_request'],
  gitlab: ['Push Hook', 'Merge Request Hook', 'Pipeline Hook'],
  jenkins: ['jenkins.notification'],
  azure: ['git.push', 'ms.vss-code.git-push-event', 'build.complete', 'ms.vss-build.build-completed-event'],
};

export function ciCdErrorMessage(status: number, code?: string): string {
  if (status === 401) return 'Your session has expired. Please sign in again.';
  if (status === 403) return 'You do not have permission to configure CI/CD integrations for this project.';
  if (status === 404) return 'The CI/CD integration could not be found.';
  if (status === 409) return 'The CI/CD integration conflicts with an existing configuration.';
  if (status === 429) return 'Webhook rate limit exceeded. Please try again shortly.';
  if (status === 413) return 'The webhook payload is too large.';
  if (code === 'VALIDATION_ERROR' || status === 400)
    return 'The CI/CD configuration is invalid. Review the highlighted fields.';
  return 'Saving the CI/CD integration failed. Please try again.';
}
