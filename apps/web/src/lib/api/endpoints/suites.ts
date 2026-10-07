import { api } from '../client';

export interface SuiteListItem {
  id: string;
  projectId: string;
  name: string;
  description: string | null;
  status: string;
  testCount: number;
  updatedAt: string;
}

export interface PagedSuiteList {
  items: SuiteListItem[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface SuiteMember {
  testCaseId: string;
  testKey: string;
  title: string;
  executionOrder: number;
  jiraIssueKey: string | null;
  freshnessState: string | null;
}

export interface SuiteDetails {
  id: string;
  projectId: string;
  name: string;
  description: string | null;
  status: string;
  createdAt: string;
  updatedAt: string;
  members: SuiteMember[];
}

export interface SuiteFilters {
  search?: string;
  status?: string;
}

export interface CreateSuiteInput {
  name: string;
  description?: string;
  status?: string;
  members?: CreateSuiteMemberInput[];
}

export interface CreateSuiteMemberInput {
  testCaseId: string;
  executionOrder: number;
}

export interface UpdateSuiteInput {
  name: string;
  description?: string;
  status?: string;
}

export interface ReorderSuiteMembersInput {
  members: SuiteMemberReorderItem[];
}

export interface SuiteMemberReorderItem {
  testCaseId: string;
  executionOrder: number;
}

export interface ExecuteSuiteInput {
  projectId: string;
  suiteId: string;
  idempotencyKey?: string;
}

export interface ExecuteSuiteResult {
  executionId: string;
  suiteId: string;
  testCount: number;
  status: string;
  createdAt: string;
}

export interface SuiteExecutionSummary {
  executionId: string;
  status: string;
  triggerType: string;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  testCount: number;
  passedCount: number;
  failedCount: number;
}

export interface PagedSuiteExecutions {
  items: SuiteExecutionSummary[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface SuiteReport {
  suiteId: string;
  suiteName: string;
  totalExecutions: number;
  passedCount: number;
  failedCount: number;
  cancelledCount: number;
  timedOutCount: number;
  errorCount: number;
  passRate: number | null;
  totalDurationMs: number;
  averageDurationMs: number;
  latestExecutionAt: string | null;
}

export interface SuiteExecutionHistoryFilters {
  status?: string;
  triggerType?: string;
}

export interface SuiteReportFilters {
  from?: string;
  to?: string;
}

function toQuery(params: Record<string, string | number | undefined>): string {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== '') query.set(key, String(value));
  }
  const text = query.toString();
  return text ? `?${text}` : '';
}

function reportFiltersToQuery(filters: SuiteReportFilters): string {
  return toQuery({ from: filters.from, to: filters.to });
}

export const suiteKeys = {
  all: ['test-suites'] as const,
  list: (projectId: string, filters: SuiteFilters, page: number) =>
    [...suiteKeys.all, 'list', projectId, filters, page] as const,
  details: (id: string) => [...suiteKeys.all, 'details', id] as const,
  executions: (suiteId: string, filters: SuiteExecutionHistoryFilters, page: number) =>
    [...suiteKeys.all, 'executions', suiteId, filters, page] as const,
  report: (suiteId: string, filters: SuiteReportFilters) =>
    [...suiteKeys.all, 'report', suiteId, filters] as const,
};

export const suitesEndpoints = {
  list: (projectId: string, filters: SuiteFilters, page: number, pageSize = 25) =>
    api.get<PagedSuiteList>(
      `/api/v1/projects/${projectId}/test-suites${toQuery({ ...filters, page, pageSize })}`,
    ),
  get: (suiteId: string) =>
    api.get<SuiteDetails>(`/api/v1/test-suites/${suiteId}`),
  create: (projectId: string, input: CreateSuiteInput) =>
    api.post<{ id: string; projectId: string; name: string; testKey: string }>(
      `/api/v1/projects/${projectId}/test-suites`,
      input,
    ),
  update: (suiteId: string, input: UpdateSuiteInput) =>
    api.put<SuiteDetails>(`/api/v1/test-suites/${suiteId}`, input),
  archive: (suiteId: string) =>
    api.delete<void>(`/api/v1/test-suites/${suiteId}`),
  addTestCase: (suiteId: string, input: CreateSuiteMemberInput) =>
    api.post<{ suiteId: string; testCaseId: string; executionOrder: number }>(
      `/api/v1/test-suites/${suiteId}/test-cases`,
      input,
    ),
  removeTestCase: (suiteId: string, testCaseId: string) =>
    api.delete<void>(`/api/v1/test-suites/${suiteId}/test-cases/${testCaseId}`),
  reorder: (suiteId: string, input: ReorderSuiteMembersInput) =>
    api.put<void>(`/api/v1/test-suites/${suiteId}/test-cases/order`, input),
  execute: (input: ExecuteSuiteInput) =>
    api.post<ExecuteSuiteResult>(`/api/v1/test-suites/${input.suiteId}/execute`, input),
  getExecutions: (suiteId: string, filters: SuiteExecutionHistoryFilters, page = 1, pageSize = 25) =>
    api.get<PagedSuiteExecutions>(
      `/api/v1/test-suites/${suiteId}/executions${toQuery({ ...filters, page, pageSize })}`,
    ),
  getReport: (suiteId: string, filters: SuiteReportFilters) =>
    api.get<SuiteReport | null>(`/api/v1/test-suites/${suiteId}/report${reportFiltersToQuery(filters)}`),
};