import { api } from '../client';

export interface VisualBaseline {
  id: string;
  projectId: string;
  testCaseId: string;
  testCaseVersionId: string;
  stepOrder: number;
  status: string;
  storageKey: string;
  sha256: string;
  width: number;
  height: number;
  contentType: string;
  mismatchThresholdBps: number | null;
  createdBy: string | null;
  approvedBy: string | null;
  createdAt: string;
  approvedAt: string | null;
  updatedAt: string;
}

export interface VisualBaselineDownload {
  downloadUrl: string;
  expiresInSeconds: number;
}

export const visualBaselineKeys = {
  all: ['visual-baselines'] as const,
  list: (projectId: string, versionId?: string, status?: string) =>
    [...visualBaselineKeys.all, 'list', projectId, versionId ?? 'all', status ?? 'all'] as const,
};

export interface ProposeBaselineInput {
  testCaseVersionId: string;
  stepOrder: number;
  imageBase64: string;
  width: number;
  height: number;
}

/** Centralized visual-baseline API surface — no raw fetch calls in components. */
export const visualBaselineEndpoints = {
  list: (projectId: string, versionId?: string, status?: string) => {
    const query = new URLSearchParams();
    if (versionId) query.set('testCaseVersionId', versionId);
    if (status) query.set('status', status);
    const suffix = query.size > 0 ? `?${query.toString()}` : '';
    return api.get<VisualBaseline[]>(`/api/v1/projects/${projectId}/visual-baselines${suffix}`);
  },
  propose: (projectId: string, input: ProposeBaselineInput) =>
    api.post<VisualBaseline>(`/api/v1/projects/${projectId}/visual-baselines`, input),
  approve: (projectId: string, baselineId: string) =>
    api.post<VisualBaseline>(`/api/v1/projects/${projectId}/visual-baselines/${baselineId}/approve`),
  reject: (projectId: string, baselineId: string) =>
    api.delete<void>(`/api/v1/projects/${projectId}/visual-baselines/${baselineId}`),
  download: (projectId: string, baselineId: string) =>
    api.get<VisualBaselineDownload>(
      `/api/v1/projects/${projectId}/visual-baselines/${baselineId}/download`,
    ),
};

export function visualBaselineErrorMessage(status: number, code?: string): string {
  if (status === 401) return 'Your session has expired. Please sign in again.';
  if (status === 403) return 'You do not have permission to manage visual baselines for this project.';
  if (status === 404) return 'The visual baseline could not be found.';
  if (status === 409)
    return 'The baseline changed state elsewhere (already approved or removed). Reload and retry.';
  if (code === 'VALIDATION_ERROR' || status === 400)
    return 'The baseline request is invalid. Review the highlighted fields.';
  return 'Saving the visual baseline failed. Please try again.';
}
