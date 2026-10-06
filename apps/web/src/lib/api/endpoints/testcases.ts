import { api } from '../client';

export interface TestCaseListItem {
  id: string;
  projectId: string;
  testKey: string;
  title: string;
  module: string | null;
  framework: string | null;
  platform: string | null;
  priority: string;
  status: string;
  latestVersionNumber: number;
  latestReviewStatus: string;
  updatedAt: string;
}

export interface PagedTestCases {
  items: TestCaseListItem[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface TestStep {
  order: number;
  action: string;
  target: string | null;
  value: string | null;
}

export interface JiraProvenance {
  origin: string;
  jiraIssueKey: string;
  jiraIssueType: string | null;
  jiraBaseUrlHost: string | null;
  jiraFetchedAt: string | null;
}

export interface TestCaseVersion {
  id: string;
  testCaseId: string;
  versionNumber: number;
  sourceCode: string | null;
  structuredSteps: TestStep[];
  generationProvider: string | null;
  generationModel: string | null;
  generationLatencyMs: number | null;
  reviewStatus: string;
  createdBy: string | null;
  createdAt: string;
  /** Safe Jira origin shaped server-side; absent/null for non-Jira versions. */
  jiraProvenance?: JiraProvenance | null;
}

export interface JiraChangeCheckResult {
  status: 'current' | 'changed';
  changedFields: string[];
  jiraIssueKey: string;
  checkedAt: string;
}

export interface JiraBulkCheckItemResult {
  versionId: string;
  status?: 'current' | 'changed';
  changedFields?: string[];
  jiraIssueKey?: string;
  checkedAt?: string;
  error?: {
    code: string;
    message: string;
  };
}

export interface JiraBulkCheckSummary {
  current: number;
  changed: number;
  errors: number;
}

export interface JiraBulkCheckResult {
  results: JiraBulkCheckItemResult[];
  summary: JiraBulkCheckSummary;
}

export interface JiraBulkCheckSummary {
  current: number;
  changed: number;
  errors: number;
}

export interface JiraBulkCheckRequest {
  versionIds: string[];
}

export interface JiraStalenessKpis {
  totalJiraTests: number;
  checkedToday: number;
  checkedThisWeek: number;
  stale: number;
  changed: number;
  neverChecked: number;
}

export interface StaleJiraTestItem {
  testCaseId: string;
  testKey: string;
  title: string;
  versionId: string;
  versionNumber: number;
  jiraIssueKey: string;
  freshnessState: 'changed' | 'stale' | 'neverChecked' | 'current';
  lastCheckedAt: string | null;
  changedFieldCount: number | null;
}

export interface StaleJiraTestsFilters {
  freshnessState?: string;
  search?: string;
}

export interface PagedStaleJiraTests {
  items: StaleJiraTestItem[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface TestCaseDetails {
  id: string;
  projectId: string;
  testKey: string;
  title: string;
  description: string | null;
  module: string | null;
  framework: string | null;
  platform: string | null;
  priority: string;
  status: string;
  sourceType: string | null;
  latestVersionNumber: number;
  latestReviewStatus: string;
  createdBy: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface TestCaseFilters {
  search?: string;
  status?: string;
  priority?: string;
  framework?: string;
  platform?: string;
  reviewStatus?: string;
  jiraIssueKey?: string;
}

export interface CreateTestCaseInput {
  testKey: string;
  title: string;
  description?: string;
  module?: string;
  framework?: string;
  platform?: string;
  priority?: string;
  status?: string;
  sourceType?: string;
  sourceCode?: string;
  structuredSteps?: TestStep[];
  generationProvider?: string;
  generationModel?: string;
  generationLatencyMs?: number;
  generationRequest?: Record<string, unknown>;
}

export interface UpdateTestCaseInput {
  title: string;
  description?: string;
  module?: string;
  framework?: string;
  platform?: string;
  priority?: string;
  status?: string;
  sourceType?: string;
  sourceCode?: string;
  structuredSteps?: TestStep[];
  hasSourceCode: boolean;
  hasStructuredSteps: boolean;
}

function toQuery(params: Record<string, string | number | undefined>): string {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== '') query.set(key, String(value));
  }
  const text = query.toString();
  return text ? `?${text}` : '';
}

export const testcaseKeys = {
  all: ['test-cases'] as const,
  list: (projectId: string, filters: TestCaseFilters, page: number) =>
    [...testcaseKeys.all, 'list', projectId, filters, page] as const,
  details: (id: string) => [...testcaseKeys.all, 'details', id] as const,
  versions: (id: string) => [...testcaseKeys.all, 'versions', id] as const,
  version: (id: string, versionId: string) =>
    [...testcaseKeys.all, 'version', id, versionId] as const,
};

/** Centralized test-repository API surface — no raw fetch calls in components. */
export const testcasesEndpoints = {
  list: (projectId: string, filters: TestCaseFilters, page: number, pageSize = 25) =>
    api.get<PagedTestCases>(
      `/api/v1/projects/${projectId}/test-cases${toQuery({ ...filters, page, pageSize })}`,
    ),
  get: (id: string) => api.get<TestCaseDetails>(`/api/v1/test-cases/${id}`),
  create: (projectId: string, input: CreateTestCaseInput) =>
    api.post<TestCaseDetails>(`/api/v1/projects/${projectId}/test-cases`, input),
  update: (id: string, input: UpdateTestCaseInput) =>
    api.put<TestCaseDetails>(`/api/v1/test-cases/${id}`, input),
  remove: (id: string) => api.delete<void>(`/api/v1/test-cases/${id}`),
  versions: (id: string) =>
    api.get<TestCaseVersion[]>(`/api/v1/test-cases/${id}/versions`),
  version: (id: string, versionId: string) =>
    api.get<TestCaseVersion>(`/api/v1/test-cases/${id}/versions/${versionId}`),
  review: (id: string, versionId: string, reviewStatus: string) =>
    api.post<TestCaseVersion>(`/api/v1/test-cases/${id}/review`, { versionId, reviewStatus }),
  checkJiraChanges: (id: string, versionId: string) =>
    api.post<JiraChangeCheckResult>(`/api/v1/test-cases/${id}/versions/${versionId}/jira-change-check`),
  bulkCheckJiraChanges: (versionIds: string[]) =>
    api.post<JiraBulkCheckResult>(`/api/v1/test-cases/jira-change-check/bulk`, { versionIds }),
  getStaleJiraTests: (projectId: string, filters: StaleJiraTestsFilters, page = 1, pageSize = 25) =>
    api.get<PagedStaleJiraTests>(`/api/v1/projects/${projectId}/test-cases/stale-jira${toQuery({ ...filters, page, pageSize })}`),
};
