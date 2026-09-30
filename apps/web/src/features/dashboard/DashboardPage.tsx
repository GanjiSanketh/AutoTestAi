import { useEffect, useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '../../components/ui/card';
import { Badge } from '../../components/ui/badge';
import { Button } from '../../components/ui/button';
import { Skeleton } from '../../components/ui/skeleton';
import { ErrorState } from '../../components/common/ErrorState';
import { Chart } from '../../components/common/Chart';
import { dashboardEndpoints, dashboardKeys, type DateRange } from '../../lib/api/endpoints/dashboard';
import { projectsEndpoints } from '../../lib/api/endpoints/projects';
import { useProfile } from '../../lib/auth/useProfile';
import { Permissions, hasPermission } from '../../lib/auth/permissions';
import { useAppStore } from '../../stores/useAppStore';
import { defectStatusTone, severityTone } from '../bugs/DefectsListPage';
import { ExecutiveSection } from './ExecutiveSection';

type Preset = '7' | '30' | '90' | 'custom';

function toIsoDaysAgo(days: number): string {
  return new Date(Date.now() - days * 24 * 60 * 60 * 1000).toISOString();
}

function formatTime(iso: string | null): string {
  if (!iso) return '—';
  try {
    return new Date(iso).toLocaleString();
  } catch {
    return iso;
  }
}

function formatPassRate(rate: number | null): string {
  if (rate === null || rate === undefined || Number.isNaN(rate)) return '—';
  return `${(rate * 100).toFixed(1)}%`;
}

function executionTone(status: string): 'success' | 'danger' | 'warning' | 'info' | 'neutral' {
  switch (status) {
    case 'Passed':
      return 'success';
    case 'Failed':
    case 'Error':
      return 'danger';
    case 'TimedOut':
    case 'Cancelled':
      return 'warning';
    case 'Running':
    case 'Queued':
      return 'info';
    default:
      return 'neutral';
  }
}

const ACTIVITY_LABELS: Record<string, string> = {
  'testcase.created': 'Test case created',
  'test-generation.completed': 'AI test generated',
  'failure-analysis.completed': 'Failure analysis completed',
  'defect.created': 'Defect filed',
  'defect.created_from_analysis': 'Defect filed from analysis',
  'defect.status_changed': 'Defect status changed',
  'ticket.created': 'Jira ticket created',
  'ticket.creation_failed': 'Jira ticket failed',
};

/**
 * Project quality overview (Slice 8, docs/02 §8). Every metric is
 * database-backed; empty histories render intentional empty states and the
 * pass rate shows "—" (never a misleading 0%) when nothing is terminal.
 */
export function DashboardPage() {
  const profile = useProfile();
  const canRead = hasPermission(profile.data?.permissions, Permissions.DashboardRead);
  const currentProjectId = useAppStore((s) => s.currentProjectId);
  const setCurrentProjectId = useAppStore((s) => s.setCurrentProjectId);

  const [projectId, setProjectId] = useState<string | null>(currentProjectId);
  const [preset, setPreset] = useState<Preset>('30');
  const [customFrom, setCustomFrom] = useState('');
  const [customTo, setCustomTo] = useState('');

  const projects = useQuery({
    queryKey: ['projects', 'list', '', 1],
    queryFn: () => projectsEndpoints.list('', 1, 100),
    retry: false,
    staleTime: 60_000,
  });

  useEffect(() => {
    if (!projectId && currentProjectId) setProjectId(currentProjectId);
  }, [projectId, currentProjectId]);

  useEffect(() => {
    if (!projectId && !currentProjectId && (projects.data?.items.length ?? 0) > 0) {
      setProjectId(projects.data!.items[0].id);
    }
  }, [projectId, currentProjectId, projects.data]);

  const range: DateRange = useMemo(() => {
    if (preset === 'custom') {
      return {
        ...(customFrom ? { from: customFrom } : {}),
        ...(customTo ? { to: customTo } : {}),
      };
    }
    return { from: toIsoDaysAgo(Number(preset)) };
  }, [preset, customFrom, customTo]);

  const enabled = !!projectId && canRead;

  const summary = useQuery({
    queryKey: projectId ? dashboardKeys.summary(projectId, range) : ['dashboard', 'summary', 'none'],
    queryFn: () => dashboardEndpoints.summary(projectId!, range),
    enabled,
    retry: false,
  });
  const trend = useQuery({
    queryKey: projectId ? dashboardKeys.trend(projectId, range) : ['dashboard', 'trend', 'none'],
    queryFn: () => dashboardEndpoints.trend(projectId!, range),
    enabled,
    retry: false,
  });
  const failures = useQuery({
    queryKey: projectId ? dashboardKeys.failures(projectId, range) : ['dashboard', 'failures', 'none'],
    queryFn: () => dashboardEndpoints.failures(projectId!, range),
    enabled,
    retry: false,
  });
  const defectOverview = useQuery({
    queryKey: projectId ? dashboardKeys.defects(projectId, range) : ['dashboard', 'defects', 'none'],
    queryFn: () => dashboardEndpoints.defects(projectId!, range),
    enabled,
    retry: false,
  });

  const selectProject = (id: string) => {
    setProjectId(id || null);
    setCurrentProjectId(id || null);
  };

  const trendOption = useMemo(() => {
    const points = trend.data?.points ?? [];
    return {
      tooltip: { trigger: 'axis' },
      legend: { data: ['Passed', 'Failed'] },
      grid: { left: 8, right: 8, bottom: 8, top: 32, containLabel: true },
      xAxis: { type: 'category', data: points.map((p) => p.date) },
      yAxis: { type: 'value', minInterval: 1 },
      series: [
        { name: 'Passed', type: 'line', smooth: true, data: points.map((p) => p.passed), color: '#059669' },
        { name: 'Failed', type: 'line', smooth: true, data: points.map((p) => p.failed), color: '#e11d48' },
      ],
    };
  }, [trend.data]);

  const failureOption = useMemo(() => {
    const items = failures.data?.items ?? [];
    return {
      tooltip: { trigger: 'item' },
      series: [
        {
          type: 'pie',
          radius: ['45%', '70%'],
          data: items.map((i) => ({ name: i.name, value: i.count })),
        },
      ],
    };
  }, [failures.data]);

  const severityOption = useMemo(() => {
    const items = defectOverview.data?.bySeverity ?? [];
    return {
      tooltip: { trigger: 'axis' },
      grid: { left: 8, right: 8, bottom: 8, top: 8, containLabel: true },
      xAxis: { type: 'category', data: items.map((i) => i.name) },
      yAxis: { type: 'value', minInterval: 1 },
      series: [{ type: 'bar', data: items.map((i) => i.count), color: '#4f46e5' }],
    };
  }, [defectOverview.data]);

  if (!canRead && !profile.isLoading) {
    return (
      <div className="space-y-6">
        <h1 className="text-xl font-semibold text-slate-900">System Overview</h1>
        <ErrorState error={null} notFoundMessage="You do not have access to the dashboard." />
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h1 className="text-xl font-semibold text-slate-900">System Overview</h1>
          <p className="mt-1 text-sm text-slate-500">
            Operational quality at a glance — descriptive metrics from persisted project data.
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <label htmlFor="dashboard-project" className="text-sm font-medium text-slate-700">
            Project
          </label>
          <select
            id="dashboard-project"
            value={projectId ?? ''}
            onChange={(e) => selectProject(e.target.value)}
            className="rounded-md border border-slate-300 bg-white px-3 py-1.5 text-sm text-slate-900"
          >
            <option value="">Select a project…</option>
            {(projects.data?.items ?? []).map((p) => (
              <option key={p.id} value={p.id}>
                {p.name} ({p.key})
              </option>
            ))}
          </select>
          <label htmlFor="dashboard-range" className="text-sm font-medium text-slate-700">
            Range
          </label>
          <select
            id="dashboard-range"
            value={preset}
            onChange={(e) => setPreset(e.target.value as Preset)}
            className="rounded-md border border-slate-300 bg-white px-3 py-1.5 text-sm text-slate-900"
          >
            <option value="7">Last 7 days</option>
            <option value="30">Last 30 days</option>
            <option value="90">Last 90 days</option>
            <option value="custom">Custom…</option>
          </select>
        </div>
      </div>

      {preset === 'custom' && (
        <Card>
          <CardContent className="flex flex-wrap items-end gap-3">
            <div>
              <label htmlFor="dashboard-from" className="mb-1 block text-sm font-medium text-slate-700">
                From (UTC)
              </label>
              <input
                id="dashboard-from"
                type="date"
                value={customFrom}
                onChange={(e) => setCustomFrom(e.target.value)}
                className="rounded-md border border-slate-300 bg-white px-3 py-1.5 text-sm text-slate-900"
              />
            </div>
            <div>
              <label htmlFor="dashboard-to" className="mb-1 block text-sm font-medium text-slate-700">
                To (UTC)
              </label>
              <input
                id="dashboard-to"
                type="date"
                value={customTo}
                onChange={(e) => setCustomTo(e.target.value)}
                className="rounded-md border border-slate-300 bg-white px-3 py-1.5 text-sm text-slate-900"
              />
            </div>
            <p className="w-full text-xs text-slate-500">
              Dates are UTC calendar days. The range is limited to 365 days.
            </p>
          </CardContent>
        </Card>
      )}

      {!projectId ? (
        <Card>
          <CardContent>
            <p className="text-sm text-slate-500">
              Select a project to view its quality dashboard.
            </p>
          </CardContent>
        </Card>
      ) : summary.isLoading ? (
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-5" aria-label="Loading dashboard">
          {[0, 1, 2, 3, 4].map((i) => (
            <Skeleton key={i} className="h-28" />
          ))}
        </div>
      ) : summary.isError || !summary.data ? (
        <ErrorState error={summary.error} onRetry={() => void summary.refetch()} />
      ) : (
        <>
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-5">
            <Card>
              <CardHeader>
                <CardTitle>Test Cases</CardTitle>
              </CardHeader>
              <CardContent>
                <p className="text-3xl font-semibold text-slate-900">{summary.data.testCases.total}</p>
                <p className="mt-1 text-xs text-slate-500">{summary.data.testCases.approved} approved</p>
              </CardContent>
            </Card>
            <Card>
              <CardHeader>
                <CardTitle>Executions</CardTitle>
              </CardHeader>
              <CardContent>
                <p className="text-3xl font-semibold text-slate-900">{summary.data.executions.total}</p>
                <p className="mt-1 text-xs text-slate-500">
                  {summary.data.executions.passed} passed · {summary.data.executions.failed} failed
                </p>
              </CardContent>
            </Card>
            <Card>
              <CardHeader>
                <CardTitle>Pass Rate</CardTitle>
              </CardHeader>
              <CardContent>
                <p className="text-3xl font-semibold text-slate-900">
                  {formatPassRate(summary.data.executions.passRate)}
                </p>
                <p className="mt-1 text-xs text-slate-500">
                  {summary.data.executions.passRate === null
                    ? 'No terminal executions yet'
                    : 'Passed ÷ terminal executions'}
                </p>
              </CardContent>
            </Card>
            <Card>
              <CardHeader>
                <CardTitle>Open Bugs</CardTitle>
              </CardHeader>
              <CardContent>
                <p className="text-3xl font-semibold text-slate-900">{summary.data.defects.open}</p>
                <p className="mt-1 text-xs text-slate-500">
                  {summary.data.defects.highSeverity} high/critical · {summary.data.defects.total} total
                </p>
              </CardContent>
            </Card>
            <Card>
              <CardHeader>
                <CardTitle>Tickets Synced</CardTitle>
              </CardHeader>
              <CardContent>
                <p className="text-3xl font-semibold text-slate-900">{summary.data.tickets.synced}</p>
                <p className="mt-1 text-xs text-slate-500">
                  {summary.data.tickets.total} total · {summary.data.tickets.failed} failed
                </p>
              </CardContent>
            </Card>
          </div>

          <div className="grid grid-cols-1 gap-4 xl:grid-cols-2">
            <Card>
              <CardHeader>
                <CardTitle>Execution Trend</CardTitle>
                <CardDescription>
                  {trend.data ? `${trend.data.granularity === 'week' ? 'Weekly' : 'Daily'} buckets (UTC)` : 'Per-period execution outcomes'}
                </CardDescription>
              </CardHeader>
              <CardContent>
                {trend.isLoading ? (
                  <Skeleton className="h-64" />
                ) : trend.isError ? (
                  <p className="text-sm text-slate-500">Trend unavailable — summary data above is unaffected.</p>
                ) : (trend.data?.points.length ?? 0) === 0 ? (
                  <p className="text-sm text-slate-500">
                    No executions yet. Run an approved test case to start building your quality history.
                  </p>
                ) : (
                  <Chart
                    option={trendOption}
                    label={`Execution trend: ${trend.data!.points.length} buckets`}
                    fallback={
                      <ul className="space-y-1 text-sm text-slate-600">
                        {trend.data!.points.slice(-7).map((p) => (
                          <li key={p.date}>
                            {p.date}: {p.passed} passed, {p.failed} failed ({p.total} total)
                          </li>
                        ))}
                      </ul>
                    }
                  />
                )}
              </CardContent>
            </Card>
            <Card>
              <CardHeader>
                <CardTitle>Failure Classification</CardTitle>
                <CardDescription>Deterministic execution classification (authoritative)</CardDescription>
              </CardHeader>
              <CardContent>
                {failures.isLoading ? (
                  <Skeleton className="h-64" />
                ) : failures.isError ? (
                  <p className="text-sm text-slate-500">Breakdown unavailable — summary data above is unaffected.</p>
                ) : (failures.data?.items.length ?? 0) === 0 ? (
                  <p className="text-sm text-slate-500">No terminal test results in this period.</p>
                ) : (
                  <Chart
                    option={failureOption}
                    label={`Failure classification across ${failures.data!.total} terminal results`}
                    fallback={
                      <ul className="space-y-1 text-sm text-slate-600">
                        {failures.data!.items.map((i) => (
                          <li key={i.name}>
                            {i.name}: {i.count}
                          </li>
                        ))}
                      </ul>
                    }
                  />
                )}
              </CardContent>
            </Card>
          </div>

          <ExecutiveSection projectId={projectId} range={range} enabled={enabled} />

          <div className="grid grid-cols-1 gap-4 xl:grid-cols-2">
            <Card>
              <CardHeader>
                <CardTitle>Recent Executions</CardTitle>
              </CardHeader>
              <CardContent>
                {summary.data.recentExecutions.length === 0 ? (
                  <p className="text-sm text-slate-500">No executions yet.</p>
                ) : (
                  <ul className="divide-y divide-slate-100">
                    {summary.data.recentExecutions.map((e) => (
                      <li key={e.id} className="flex items-center justify-between gap-3 py-2">
                        <div className="min-w-0">
                          <Link
                            to={`/projects/${projectId}/executions/${e.id}`}
                            className="truncate text-sm font-medium text-brand-700 hover:text-brand-600"
                          >
                            {e.testKey ?? e.id.slice(0, 8)}
                          </Link>
                          <p className="truncate text-xs text-slate-500">
                            {e.testTitle ?? '—'} · {formatTime(e.createdAt)}
                          </p>
                        </div>
                        <Badge tone={executionTone(e.status)}>{e.status}</Badge>
                      </li>
                    ))}
                  </ul>
                )}
                <Link
                  to={`/projects/${projectId}/executions`}
                  className="mt-3 inline-block text-sm font-medium text-brand-700 hover:text-brand-600"
                >
                  View execution history →
                </Link>
              </CardContent>
            </Card>
            <Card>
              <CardHeader>
                <CardTitle>Recent Defects</CardTitle>
              </CardHeader>
              <CardContent>
                {summary.data.recentDefects.length === 0 ? (
                  <p className="text-sm text-slate-500">No defects filed.</p>
                ) : (
                  <ul className="divide-y divide-slate-100">
                    {summary.data.recentDefects.map((d) => (
                      <li key={d.id} className="flex items-center justify-between gap-3 py-2">
                        <div className="min-w-0">
                          <Link
                            to={`/projects/${projectId}/bugs/${d.id}`}
                            className="truncate text-sm font-medium text-brand-700 hover:text-brand-600"
                          >
                            {d.title}
                          </Link>
                          <p className="truncate text-xs text-slate-500">
                            {d.severity} · {d.status}
                            {d.jiraKey ? ` · ${d.jiraKey}` : ''}
                          </p>
                        </div>
                        <Badge tone={defectStatusTone(d.status)}>{d.status}</Badge>
                      </li>
                    ))}
                  </ul>
                )}
                <Link
                  to={`/projects/${projectId}/bugs`}
                  className="mt-3 inline-block text-sm font-medium text-brand-700 hover:text-brand-600"
                >
                  View bugs →
                </Link>
              </CardContent>
            </Card>
          </div>

          <div className="grid grid-cols-1 gap-4 xl:grid-cols-2">
            <Card>
              <CardHeader>
                <CardTitle>Ticket / Jira Overview</CardTitle>
                <CardDescription>From internal ticket records — never contacts Jira</CardDescription>
              </CardHeader>
              <CardContent>
                {summary.data.recentTickets.length === 0 ? (
                  <p className="text-sm text-slate-500">
                    No tickets yet. File a defect, then create a Jira ticket from it.
                  </p>
                ) : (
                  <ul className="divide-y divide-slate-100">
                    {summary.data.recentTickets.map((t) => (
                      <li key={t.id} className="flex items-center justify-between gap-3 py-2">
                        <span className="truncate font-mono text-sm text-slate-900">
                          {t.externalKey ?? t.id.slice(0, 8)}
                        </span>
                        <Badge tone={t.syncStatus === 'Synced' ? 'success' : t.syncStatus === 'Failed' ? 'danger' : 'warning'}>
                          {t.syncStatus}
                        </Badge>
                      </li>
                    ))}
                  </ul>
                )}
                {defectOverview.data && defectOverview.data.bySeverity.length > 0 && (
                  <div className="mt-2">
                    <Chart
                      option={severityOption}
                      label="Defect severity distribution"
                      fallback={
                        <ul className="space-y-1 text-sm text-slate-600">
                          {defectOverview.data.bySeverity.map((i) => (
                            <li key={i.name}>
                              <Badge tone={severityTone(i.name)}>{i.name}</Badge> {i.count}
                            </li>
                          ))}
                        </ul>
                      }
                    />
                  </div>
                )}
              </CardContent>
            </Card>
            <Card>
              <CardHeader>
                <CardTitle>Recent Activity</CardTitle>
                <CardDescription>Project audit events (safe actions only)</CardDescription>
              </CardHeader>
              <CardContent>
                {summary.data.recentActivity.length === 0 ? (
                  <p className="text-sm text-slate-500">No recent activity in this period.</p>
                ) : (
                  <ul className="divide-y divide-slate-100">
                    {summary.data.recentActivity.map((a, i) => (
                      <li key={`${a.action}-${a.entityId ?? i}-${a.createdAt}`} className="py-2">
                        <p className="text-sm text-slate-900">{ACTIVITY_LABELS[a.action] ?? a.action}</p>
                        <p className="font-mono text-xs text-slate-400">
                          {a.entityType} · {formatTime(a.createdAt)}
                        </p>
                      </li>
                    ))}
                  </ul>
                )}
                <Link
                  to="/reports"
                  className="mt-3 inline-block text-sm font-medium text-brand-700 hover:text-brand-600"
                >
                  Open reports →
                </Link>
              </CardContent>
            </Card>
          </div>

          <div className="flex justify-start">
            <Button variant="secondary" size="sm" onClick={() => void summary.refetch()}>
              Refresh dashboard
            </Button>
          </div>
        </>
      )}
    </div>
  );
}
