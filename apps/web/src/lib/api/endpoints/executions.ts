import { api } from '../client';

export interface StartExecutionInput {
  testCaseVersionId: string;
  environmentId?: string;
  browser?: string;
  idempotencyKey?: string;
  mobileDevicePoolId?: string;
  mobileAppId?: string;
}

export interface StartExecutionResult {
  executionId: string;
  executionTestId: string;
  projectId: string;
  testCaseId: string;
  testCaseVersionId: string;
  status: string;
  workflowId: string | null;
  createdAt: string;
  duplicated: boolean;
}

export interface ExecutionListItem {
  id: string;
  projectId: string;
  status: string;
  triggerType: string;
  testCaseId: string;
  testKey: string;
  testTitle: string;
  testCaseVersionNumber: number;
  browser: string | null;
  failureClassification: string | null;
  durationMs: number | null;
  startedAt: string | null;
  completedAt: string | null;
  createdAt: string;
}

export interface PagedExecutions {
  items: ExecutionListItem[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface ExecutionFilters {
  status?: string;
  testCaseId?: string;
}

export interface ExecutionStep {
  order: number;
  action: string;
  target: string | null;
  status: string;
  startedAt: string | null;
  completedAt: string | null;
  durationMs: number | null;
  errorMessage: string | null;
}

export interface ExecutionLogEntry {
  id: number;
  timestamp: string;
  level: string;
  message: string;
}

export interface ExecutionArtifact {
  id: string;
  artifactType: string;
  fileName: string | null;
  stepOrder: number | null;
  contentType: string | null;
  sizeBytes: number | null;
  createdAt: string;
}

export interface ExecutionTestDetail {
  id: string;
  testCaseId: string;
  testKey: string;
  testTitle: string;
  testSourceType: string | null;
  testCaseVersionId: string;
  testCaseVersionNumber: number;
  reviewStatus: string;
  status: string;
  framework: string | null;
  browser: string | null;
  failureClassification: string | null;
  attempt: number;
  durationMs: number | null;
  errorType: string | null;
  errorMessage: string | null;
  steps: ExecutionStep[];
  createdAt: string;
  updatedAt: string;
}

export interface ExecutionDetail {
  id: string;
  projectId: string;
  status: string;
  triggerType: string;
  environmentId: string | null;
  workflowId: string | null;
  startedAt: string | null;
  completedAt: string | null;
  createdBy: string | null;
  createdAt: string;
  test: ExecutionTestDetail;
}

export interface CancelExecutionResult {
  executionId: string;
  status: string;
  cancellationRequested: boolean;
}

export interface ArtifactDownload {
  downloadUrl: string;
  expiresInSeconds: number;
}

function toQuery(params: Record<string, string | number | undefined>): string {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== '') query.set(key, String(value));
  }
  const text = query.toString();
  return text ? `?${text}` : '';
}

export const executionKeys = {
  all: ['executions'] as const,
  list: (projectId: string, filters: ExecutionFilters, page: number) =>
    [...executionKeys.all, 'list', projectId, filters, page] as const,
  details: (projectId: string, id: string) =>
    [...executionKeys.all, 'details', projectId, id] as const,
  steps: (projectId: string, id: string) =>
    [...executionKeys.all, 'steps', projectId, id] as const,
  logs: (projectId: string, id: string) =>
    [...executionKeys.all, 'logs', projectId, id] as const,
  artifacts: (projectId: string, id: string) =>
    [...executionKeys.all, 'artifacts', projectId, id] as const,
};

/** Centralized execution API surface — no raw fetch calls in components. */
export const executionEndpoints = {
  start: (projectId: string, input: StartExecutionInput) =>
    api.post<StartExecutionResult>(`/api/v1/projects/${projectId}/executions`, input),
  list: (projectId: string, filters: ExecutionFilters, page: number, pageSize = 25) =>
    api.get<PagedExecutions>(
      `/api/v1/projects/${projectId}/executions${toQuery({ ...filters, page, pageSize })}`,
    ),
  get: (projectId: string, id: string) =>
    api.get<ExecutionDetail>(`/api/v1/projects/${projectId}/executions/${id}`),
  steps: (projectId: string, id: string) =>
    api.get<ExecutionStep[]>(`/api/v1/projects/${projectId}/executions/${id}/steps`),
  logs: (projectId: string, id: string, afterId?: number, take = 200) =>
    api.get<ExecutionLogEntry[]>(
      `/api/v1/projects/${projectId}/executions/${id}/logs${toQuery({ afterId, take })}`,
    ),
  artifacts: (projectId: string, id: string) =>
    api.get<ExecutionArtifact[]>(`/api/v1/projects/${projectId}/executions/${id}/artifacts`),
  download: (projectId: string, id: string, artifactId: string) =>
    api.get<ArtifactDownload>(
      `/api/v1/projects/${projectId}/executions/${id}/artifacts/${artifactId}/download`,
    ),
  cancel: (projectId: string, id: string) =>
    api.post<CancelExecutionResult>(`/api/v1/projects/${projectId}/executions/${id}/cancel`),
};
