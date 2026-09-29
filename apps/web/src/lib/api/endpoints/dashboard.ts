import { api } from '../client';

export interface TestCaseKpis {
  total: number;
  approved: number;
}

export interface ExecutionKpis {
  total: number;
  passed: number;
  failed: number;
  cancelled: number;
  timedOut: number;
  error: number;
  queuedOrRunning: number;
  /** Null when there are no terminal executions — never NaN. */
  passRate: number | null;
}

export interface DefectKpis {
  total: number;
  open: number;
  inProgress: number;
  resolved: number;
  closed: number;
  rejected: number;
  highSeverity: number;
}

export interface TicketKpis {
  total: number;
  synced: number;
  failed: number;
  pending: number;
}

export interface CountItem {
  name: string;
  count: number;
}

export interface TrendPoint {
  date: string;
  total: number;
  passed: number;
  failed: number;
  cancelled: number;
  timedOut: number;
  error: number;
}

export interface RecentExecution {
  id: string;
  status: string;
  testKey: string | null;
  testTitle: string | null;
  testCaseVersionNumber: number | null;
  failureClassification: string | null;
  durationMs: number | null;
  startedAt: string | null;
  completedAt: string | null;
  createdAt: string;
}

export interface RecentDefect {
  id: string;
  title: string;
  severity: string;
  status: string;
  failureClassification: string | null;
  jiraKey: string | null;
  createdAt: string;
}

export interface RecentTicket {
  id: string;
  provider: string;
  externalKey: string | null;
  syncStatus: string;
  defectId: string | null;
  createdAt: string;
}

export interface ActivityEntry {
  action: string;
  entityType: string;
  entityId: string | null;
  createdAt: string;
}

export interface DashboardSummary {
  projectId: string;
  from: string;
  to: string;
  testCases: TestCaseKpis;
  executions: ExecutionKpis;
  defects: DefectKpis;
  tickets: TicketKpis;
  recentExecutions: RecentExecution[];
  recentDefects: RecentDefect[];
  recentTickets: RecentTicket[];
  recentActivity: ActivityEntry[];
}

export interface ExecutionTrend {
  projectId: string;
  from: string;
  to: string;
  granularity: string;
  points: TrendPoint[];
}

export interface FailureBreakdown {
  projectId: string;
  from: string;
  to: string;
  total: number;
  items: CountItem[];
}

export interface DefectOverview {
  projectId: string;
  from: string;
  to: string;
  totals: DefectKpis;
  bySeverity: CountItem[];
  byClassification: CountItem[];
  recent: RecentDefect[];
}

export interface TicketOverview {
  projectId: string;
  from: string;
  to: string;
  totals: TicketKpis;
  byProvider: CountItem[];
  recent: RecentTicket[];
}

export interface DateRange {
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

export const dashboardKeys = {
  all: ['dashboard'] as const,
  summary: (projectId: string, range: DateRange) =>
    [...dashboardKeys.all, 'summary', projectId, range] as const,
  trend: (projectId: string, range: DateRange, granularity?: string) =>
    [...dashboardKeys.all, 'trend', projectId, range, granularity ?? 'auto'] as const,
  failures: (projectId: string, range: DateRange) =>
    [...dashboardKeys.all, 'failures', projectId, range] as const,
  defects: (projectId: string, range: DateRange) =>
    [...dashboardKeys.all, 'defects', projectId, range] as const,
  tickets: (projectId: string, range: DateRange) =>
    [...dashboardKeys.all, 'tickets', projectId, range] as const,
};

/** Centralized dashboard API surface — no raw fetch calls in components. */
export const dashboardEndpoints = {
  summary: (projectId: string, range: DateRange) =>
    api.get<DashboardSummary>(
      `/api/v1/projects/${projectId}/dashboard/summary${toQuery({ ...range })}`,
    ),
  trend: (projectId: string, range: DateRange, granularity?: string) =>
    api.get<ExecutionTrend>(
      `/api/v1/projects/${projectId}/dashboard/execution-trend${toQuery({ ...range, granularity })}`,
    ),
  failures: (projectId: string, range: DateRange) =>
    api.get<FailureBreakdown>(
      `/api/v1/projects/${projectId}/dashboard/failure-breakdown${toQuery({ ...range })}`,
    ),
  defects: (projectId: string, range: DateRange) =>
    api.get<DefectOverview>(
      `/api/v1/projects/${projectId}/dashboard/defects${toQuery({ ...range })}`,
    ),
  tickets: (projectId: string, range: DateRange) =>
    api.get<TicketOverview>(
      `/api/v1/projects/${projectId}/dashboard/tickets${toQuery({ ...range })}`,
    ),
};
