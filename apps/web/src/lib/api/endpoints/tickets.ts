import { api } from '../client';

export interface JiraTicket {
  id: string;
  projectId: string;
  defectId: string | null;
  integrationId: string | null;
  provider: string;
  externalId: string | null;
  externalKey: string | null;
  externalUrl: string | null;
  title: string;
  syncStatus: string;
  createdBy: string | null;
  createdAt: string;
  updatedAt: string;
  alreadyExisted?: boolean;
}

export interface JiraIntegrationStatus {
  provider: string;
  configured: boolean;
  enabled: boolean;
  projectKey: string | null;
  baseUrl: string | null;
  issueType: string | null;
}

export const ticketKeys = {
  all: ['tickets'] as const,
  defectTicket: (projectId: string, defectId: string) =>
    [...ticketKeys.all, 'defect', projectId, defectId] as const,
  jiraStatus: (projectId: string) => [...ticketKeys.all, 'jira-status', projectId] as const,
};

/** Centralized ticket API surface — no raw fetch calls in components. */
export const ticketEndpoints = {
  getForDefect: (projectId: string, defectId: string) =>
    api.get<JiraTicket | null>(`/api/v1/projects/${projectId}/defects/${defectId}/ticket`),
  createForDefect: (projectId: string, defectId: string) =>
    api.post<JiraTicket>(`/api/v1/projects/${projectId}/defects/${defectId}/ticket`),
  jiraStatus: (projectId: string) =>
    api.get<JiraIntegrationStatus>(`/api/v1/projects/${projectId}/integrations/jira/status`),
};

export function ticketErrorMessage(status: number, code?: string): string {
  if (status === 401) return 'Your session has expired. Please sign in again.';
  if (status === 403) return 'You do not have permission to create Jira tickets for this project.';
  if (status === 404) return 'The defect or Jira integration could not be found.';
  if (status === 409) return 'A Jira ticket already exists for this defect.';
  if (status === 429) return 'Jira rate-limited the request. Please try again shortly.';
  if (status === 503 || status === 502) return 'Jira is currently unavailable. Please try again later.';
  if (code === 'VALIDATION_ERROR' || status === 400)
    return 'Jira rejected the ticket details. Review the integration configuration.';
  return 'Creating the Jira ticket failed. Please try again.';
}
