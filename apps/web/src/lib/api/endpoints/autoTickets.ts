import { api } from '../client';

export interface AutoTicketPolicy {
  projectId: string;
  enabled: boolean;
  integrationId: string | null;
  severities: string[];
  defectStatuses: string[];
  classifications: string[];
  minimumConfidence: number | null;
  updatedAt: string;
}

export interface AutoTicketPolicyStatus {
  projectId: string;
  enabled: boolean;
  configured: boolean;
  integrationId: string | null;
  severities: string[];
  defectStatuses: string[];
  classifications: string[];
  minimumConfidence: number | null;
  pendingCount: number;
  failedCount: number;
  syncedAutomaticCount: number;
  lastAutomationAt: string | null;
}

export interface UpsertAutoTicketPolicyInput {
  enabled: boolean;
  integrationId?: string | null;
  severities?: string[];
  defectStatuses?: string[];
  classifications?: string[];
  minimumConfidence?: number | null;
}

export const autoTicketKeys = {
  all: ['auto-tickets'] as const,
  policy: (projectId: string) => [...autoTicketKeys.all, 'policy', projectId] as const,
  status: (projectId: string) => [...autoTicketKeys.all, 'status', projectId] as const,
};

/** Centralized automatic-ticketing API surface — no raw fetch calls in components. */
export const autoTicketEndpoints = {
  getPolicy: (projectId: string) =>
    api.get<AutoTicketPolicy | null>(`/api/v1/projects/${projectId}/auto-ticket-policy`),
  upsertPolicy: (projectId: string, input: UpsertAutoTicketPolicyInput) =>
    api.put<AutoTicketPolicy>(`/api/v1/projects/${projectId}/auto-ticket-policy`, input),
  getStatus: (projectId: string) =>
    api.get<AutoTicketPolicyStatus>(`/api/v1/projects/${projectId}/auto-ticket-policy/status`),
  retryAutomation: (projectId: string, defectId: string) =>
    api.post(`/api/v1/projects/${projectId}/defects/${defectId}/ticket/automation/retry`),
};

export function autoTicketErrorMessage(status: number, code?: string): string {
  if (status === 401) return 'Your session has expired. Please sign in again.';
  if (status === 403) return 'You do not have permission to configure automatic ticketing for this project.';
  if (status === 404) return 'The project or defect could not be found.';
  if (status === 409) return 'There is no failed automatic ticket to retry, or a ticket already exists.';
  if (code === 'VALIDATION_ERROR' || status === 400)
    return 'The automation policy is invalid. Select at least one severity, status and classification.';
  return 'Saving the automation policy failed. Please try again.';
}
