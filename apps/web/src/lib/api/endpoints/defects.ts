import { api } from '../client';

export interface CreateDefectInput {
  executionId: string;
  title: string;
  description?: string;
  severity?: string;
  failureAnalysisId?: string;
}

export interface UpdateDefectInput {
  title: string;
  description?: string;
  severity?: string;
}

export interface DefectListItem {
  id: string;
  projectId: string;
  title: string;
  severity: string;
  status: string;
  failureClassification: string | null;
  executionId: string | null;
  testKey: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface PagedDefects {
  items: DefectListItem[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface DefectFilters {
  status?: string;
  severity?: string;
  classification?: string;
  testCaseId?: string;
  search?: string;
}

export interface DefectAnalysisRef {
  id: string;
  attempt: number;
  status: string;
  classification: string;
  summary: string | null;
  confidence: number | null;
  provider: string | null;
  model: string | null;
}

export interface DefectDetails {
  id: string;
  projectId: string;
  title: string;
  description: string | null;
  severity: string;
  status: string;
  failureClassification: string | null;
  executionId: string | null;
  executionTestId: string | null;
  testCaseId: string | null;
  testKey: string | null;
  testTitle: string | null;
  testCaseVersionId: string | null;
  testCaseVersionNumber: number | null;
  failureAnalysisId: string | null;
  analysis: DefectAnalysisRef | null;
  aiConfidence: number | null;
  createdBy: string | null;
  createdAt: string;
  updatedAt: string;
}

function toQuery(params: Record<string, string | number | undefined>): string {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== '') query.set(key, String(value));
  }
  const text = query.toString();
  return text ? `?${text}` : '';
}

export const defectKeys = {
  all: ['defects'] as const,
  list: (projectId: string, filters: DefectFilters, page: number) =>
    [...defectKeys.all, 'list', projectId, filters, page] as const,
  details: (projectId: string, id: string) => [...defectKeys.all, 'details', projectId, id] as const,
};

/** Centralized defect API surface — no raw fetch calls in components. */
export const defectEndpoints = {
  list: (projectId: string, filters: DefectFilters, page: number, pageSize = 25) =>
    api.get<PagedDefects>(`/api/v1/projects/${projectId}/defects${toQuery({ ...filters, page, pageSize })}`),
  get: (projectId: string, id: string) =>
    api.get<DefectDetails>(`/api/v1/projects/${projectId}/defects/${id}`),
  create: (projectId: string, input: CreateDefectInput) =>
    api.post<DefectDetails>(`/api/v1/projects/${projectId}/defects`, input),
  update: (projectId: string, id: string, input: UpdateDefectInput) =>
    api.put<DefectDetails>(`/api/v1/projects/${projectId}/defects/${id}`, input),
  changeStatus: (projectId: string, id: string, status: string) =>
    api.post<DefectDetails>(`/api/v1/projects/${projectId}/defects/${id}/status`, { status }),
};
