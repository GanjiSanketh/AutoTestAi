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

export interface ReadinessComponent {
  component: string;
  value: number | null;
  weight: number;
  contribution: number | null;
  threshold: string;
  detail: string;
}

/** Scales: passRate/failRate are 0–1 ratios (Slice 8 convention); index/coverage/readiness/rates are 0–100. */
export interface ExecutiveOverview {
  projectId: string;
  from: string;
  to: string;
  terminalExecutions: number;
  totalExecutions: number;
  passRate: number | null;
  failRate: number | null;
  flakinessIndex: number | null;
  flakyTests: number;
  eligibleTests: number;
  automationCoverage: number | null;
  automatedCases: number;
  eligibleCases: number;
  releaseReadiness: number | null;
  readinessStatus: string;
  readinessComponents: ReadinessComponent[];
  openCriticalHighDefects: number;
  defectsPer100Executions: number | null;
  defectsCreated: number;
  defectsPerCase: number | null;
  averageDurationMs: number | null;
  totalDurationMs: number | null;
  durationSampleCount: number;
  healingSuccessRate: number | null;
  healingAttempts: number;
  healingApplied: number;
  unstableExecutions: number;
  cancelledExecutions: number;
}

export interface FlakinessTrendPoint {
  date: string;
  eligibleTests: number;
  flakyTests: number;
  /** Null means no data for the bucket — never render as zero. */
  index: number | null;
}

export interface FlakinessTrend {
  projectId: string;
  from: string;
  to: string;
  granularity: string;
  points: FlakinessTrendPoint[];
}

export interface HealingTrendPoint {
  date: string;
  attempts: number;
  applied: number;
}

export interface HealingAnalytics {
  projectId: string;
  from: string;
  to: string;
  attempts: number;
  applied: number;
  failed: number;
  deterministic: number;
  aiAssisted: number;
  successRate: number | null;
  testsWithHealing: number;
  executionsWithHealing: number;
  testsHealedAndFlaky: number;
  points: HealingTrendPoint[];
}

export interface DurationTrendPoint {
  date: string;
  count: number;
  averageMs: number | null;
}

export interface AgingBucket {
  name: string;
  count: number;
}

export interface DurationAnalytics {
  projectId: string;
  from: string;
  to: string;
  count: number;
  averageMs: number | null;
  minMs: number | null;
  maxMs: number | null;
  totalMs: number | null;
  p50Ms: number | null;
  p90Ms: number | null;
  slaConfigured: boolean;
  openDefectAging: AgingBucket[];
  points: DurationTrendPoint[];
}

export interface ReleaseReadiness {
  projectId: string;
  from: string;
  to: string;
  score: number | null;
  status: string;
  components: ReadinessComponent[];
  sampleSize: number;
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
  executive: (projectId: string, range: DateRange) =>
    [...dashboardKeys.all, 'executive', projectId, range] as const,
  flakinessTrend: (projectId: string, range: DateRange) =>
    [...dashboardKeys.all, 'flakiness-trend', projectId, range] as const,
  healing: (projectId: string, range: DateRange) =>
    [...dashboardKeys.all, 'healing', projectId, range] as const,
  durations: (projectId: string, range: DateRange) =>
    [...dashboardKeys.all, 'durations', projectId, range] as const,
  readiness: (projectId: string, range: DateRange) =>
    [...dashboardKeys.all, 'readiness', projectId, range] as const,
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
  executive: (projectId: string, range: DateRange) =>
    api.get<ExecutiveOverview>(
      `/api/v1/projects/${projectId}/dashboard/executive-overview${toQuery({ ...range })}`,
    ),
  flakinessTrend: (projectId: string, range: DateRange) =>
    api.get<FlakinessTrend>(
      `/api/v1/projects/${projectId}/dashboard/flakiness-trend${toQuery({ ...range })}`,
    ),
  healing: (projectId: string, range: DateRange) =>
    api.get<HealingAnalytics>(
      `/api/v1/projects/${projectId}/dashboard/healing${toQuery({ ...range })}`,
    ),
  durations: (projectId: string, range: DateRange) =>
    api.get<DurationAnalytics>(
      `/api/v1/projects/${projectId}/dashboard/durations${toQuery({ ...range })}`,
    ),
  readiness: (projectId: string, range: DateRange) =>
    api.get<ReleaseReadiness>(
      `/api/v1/projects/${projectId}/dashboard/readiness${toQuery({ ...range })}`,
    ),
};
