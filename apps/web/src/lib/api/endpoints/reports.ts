import { api } from '../client';
import type { DateRange } from './dashboard';

export interface ExecutionReportItem {
  id: string;
  status: string;
  testCaseId: string | null;
  testKey: string | null;
  testTitle: string | null;
  testCaseVersionNumber: number | null;
  failureClassification: string | null;
  durationMs: number | null;
  startedAt: string | null;
  completedAt: string | null;
  createdAt: string;
}

export interface DefectReportItem {
  id: string;
  title: string;
  severity: string;
  status: string;
  failureClassification: string | null;
  jiraKey: string | null;
  createdAt: string;
}

export interface TicketReportItem {
  id: string;
  provider: string;
  externalKey: string | null;
  externalUrl: string | null;
  syncStatus: string;
  defectId: string | null;
  defectTitle: string | null;
  createdAt: string;
}

export interface Paged<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface ExecutionReportFilters extends DateRange {
  status?: string;
  testCaseId?: string;
  classification?: string;
}

export interface DefectReportFilters extends DateRange {
  status?: string;
  severity?: string;
  classification?: string;
  search?: string;
}

export interface TicketReportFilters extends DateRange {
  provider?: string;
  syncStatus?: string;
}

export interface FlakyTest {
  testCaseId: string;
  testKey: string;
  title: string;
  module: string | null;
  priority: string;
  framework: string | null;
  platform: string | null;
  totalExecutions: number;
  passed: number;
  failed: number;
  other: number;
  isFlaky: boolean;
  /** Null when fewer than 2 eligible verdicts — never render as zero. */
  flakinessRate: number | null;
  lastOutcome: string | null;
  lastRunAt: string | null;
  healingAttempts: number;
  healedRuns: number;
  /** Advisory-only deterministic forecast (null = insufficient history). */
  riskScore: number | null;
  riskBand: string | null;
  riskFactors: string[];
}

export interface FlakinessReportFilters extends DateRange {
  search?: string;
  flakyOnly?: boolean;
  minExecutions?: number;
  module?: string;
  priority?: string;
  framework?: string;
  healedOnly?: boolean;
  sort?: string;
  descending?: boolean;
}

function toQuery(params: Record<string, string | number | boolean | undefined>): string {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== '' && value !== false) query.set(key, String(value));
  }
  const text = query.toString();
  return text ? `?${text}` : '';
}

export const reportKeys = {
  all: ['reports'] as const,
  executions: (projectId: string, filters: ExecutionReportFilters, page: number) =>
    [...reportKeys.all, 'executions', projectId, filters, page] as const,
  defects: (projectId: string, filters: DefectReportFilters, page: number) =>
    [...reportKeys.all, 'defects', projectId, filters, page] as const,
  tickets: (projectId: string, filters: TicketReportFilters, page: number) =>
    [...reportKeys.all, 'tickets', projectId, filters, page] as const,
  flakiness: (projectId: string, filters: FlakinessReportFilters, page: number) =>
    [...reportKeys.all, 'flakiness', projectId, filters, page] as const,
};

/** Centralized reports API surface — no raw fetch calls in components. */
export const reportEndpoints = {
  executions: (
    projectId: string,
    filters: ExecutionReportFilters,
    page: number,
    pageSize = 25,
  ) =>
    api.get<Paged<ExecutionReportItem>>(
      `/api/v1/projects/${projectId}/reports/executions${toQuery({ ...filters, page, pageSize })}`,
    ),
  defects: (projectId: string, filters: DefectReportFilters, page: number, pageSize = 25) =>
    api.get<Paged<DefectReportItem>>(
      `/api/v1/projects/${projectId}/reports/defects${toQuery({ ...filters, page, pageSize })}`,
    ),
  tickets: (projectId: string, filters: TicketReportFilters, page: number, pageSize = 25) =>
    api.get<Paged<TicketReportItem>>(
      `/api/v1/projects/${projectId}/reports/tickets${toQuery({ ...filters, page, pageSize })}`,
    ),
  flakiness: (
    projectId: string,
    filters: FlakinessReportFilters,
    page: number,
    pageSize = 25,
  ) =>
    api.get<Paged<FlakyTest>>(
      `/api/v1/projects/${projectId}/reports/flakiness${toQuery({ ...filters, page, pageSize })}`,
    ),
  flakinessExportUrl: (projectId: string, filters: FlakinessReportFilters) =>
    `/api/v1/projects/${projectId}/reports/flakiness/export${toQuery({ ...filters })}`,
};
