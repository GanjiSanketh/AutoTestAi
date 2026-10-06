import { api } from '../client';

export interface MaintenanceProposal {
  id: string;
  projectId: string;
  testCaseId: string;
  testKey: string;
  title: string;
  testCaseVersionId: string;
  testCaseVersionNumber: number;
  stepOrder: number;
  stepAction: string;
  originalStrategy: string | null;
  originalValue: string | null;
  proposedStrategy: string | null;
  proposedValue: string | null;
  healingStrategy: string;
  signalType: string;
  confidence: number;
  occurrenceCount: number;
  status: string;
  reviewedBy: string | null;
  reviewedAt: string | null;
  rejectionReason: string | null;
  createdVersionId: string | null;
  createdVersionNumber: number | null;
  createdAt: string;
  updatedAt: string;
}

export interface MaintenanceEvidence {
  occurrenceCount: number;
  failedCorroborationCount: number;
  healingSuccessRatio: number;
  forecastBand: string | null;
  confidenceFactors: string[];
  executionIds: string[];
  healingAttemptIds: string[];
  firstSeen: string;
  lastSeen: string;
}

export interface MaintenanceProposalDetail {
  proposal: MaintenanceProposal;
  evidence: MaintenanceEvidence;
}

export interface Paged<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface MaintenanceFilters {
  status?: string;
  signal?: string;
  search?: string;
}

export interface MaintenanceScanResult {
  candidatesDetected: number;
  proposalsCreated: number;
  proposalsAlreadyExisting: number;
  proposalsSkipped: number;
  scannedAt: string;
}

export interface MaintenanceApproveResult {
  proposalId: string;
  status: string;
  createdVersionId: string;
  createdVersionNumber: number;
}

function toQuery(params: Record<string, string | number | undefined>): string {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== '') query.set(key, String(value));
  }
  const text = query.toString();
  return text ? `?${text}` : '';
}

export const maintenanceKeys = {
  all: ['maintenance'] as const,
  list: (projectId: string, filters: MaintenanceFilters, page: number) =>
    [...maintenanceKeys.all, 'list', projectId, filters, page] as const,
  detail: (projectId: string, proposalId: string) =>
    [...maintenanceKeys.all, 'detail', projectId, proposalId] as const,
};

/** Centralized maintenance API surface — no raw fetch calls in components. */
export const maintenanceEndpoints = {
  list: (projectId: string, filters: MaintenanceFilters, page: number, pageSize = 25) =>
    api.get<Paged<MaintenanceProposal>>(
      `/api/v1/projects/${projectId}/maintenance/proposals${toQuery({ ...filters, page, pageSize })}`,
    ),
  detail: (projectId: string, proposalId: string) =>
    api.get<MaintenanceProposalDetail>(
      `/api/v1/projects/${projectId}/maintenance/proposals/${proposalId}`,
    ),
  scan: (projectId: string) =>
    api.post<MaintenanceScanResult>(`/api/v1/projects/${projectId}/maintenance/scan`),
  approve: (projectId: string, proposalId: string) =>
    api.post<MaintenanceApproveResult>(
      `/api/v1/projects/${projectId}/maintenance/proposals/${proposalId}/approve`,
    ),
  reject: (projectId: string, proposalId: string, reason: string) =>
    api.post<MaintenanceProposal>(
      `/api/v1/projects/${projectId}/maintenance/proposals/${proposalId}/reject`,
      { reason },
    ),
};
