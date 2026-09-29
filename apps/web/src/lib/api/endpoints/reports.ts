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

function toQuery(params: Record<string, string | number | undefined>): string {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== '') query.set(key, String(value));
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
};
