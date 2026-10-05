import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '../../components/ui/card';
import { Badge } from '../../components/ui/badge';
import { Skeleton } from '../../components/ui/skeleton';
import { Chart } from '../../components/common/Chart';
import {
  dashboardEndpoints,
  dashboardKeys,
  type DateRange,
} from '../../lib/api/endpoints/dashboard';

function pct1(value: number | null | undefined): string {
  if (value === null || value === undefined || Number.isNaN(value)) return '—';
  return `${value.toFixed(1)}%`;
}

function ratioPct(value: number | null | undefined): string {
  if (value === null || value === undefined || Number.isNaN(value)) return '—';
  return `${(value * 100).toFixed(1)}%`;
}

function formatMs(value: number | null | undefined): string {
  if (value === null || value === undefined || Number.isNaN(value)) return '—';
  if (value < 1000) return `${Math.round(value)} ms`;
  return `${(value / 1000).toFixed(1)} s`;
}

function readinessTone(status: string): 'success' | 'warning' | 'danger' | 'neutral' {
  switch (status) {
    case 'Ready':
      return 'success';
    case 'Caution':
      return 'warning';
    case 'NeedsAttention':
      return 'danger';
    default:
      return 'neutral';
  }
}

function readinessLabel(status: string): string {
  switch (status) {
    case 'NeedsAttention':
      return 'Needs attention';
    case 'InsufficientData':
      return 'Insufficient data';
    default:
      return status;
  }
}

/**
 * Executive analytics (Slice 12). Every metric is server-aggregated and
 * deterministic — no AI, no hardcoded values. Null means insufficient data
 * (rendered as "—" with an explanation), never a misleading zero.
 */
export function ExecutiveSection({
  projectId,
  range,
  enabled,
}: {
  projectId: string;
  range: DateRange;
  enabled: boolean;
}) {
  const executive = useQuery({
    queryKey: dashboardKeys.executive(projectId, range),
    queryFn: () => dashboardEndpoints.executive(projectId, range),
    enabled,
    retry: false,
  });
  const flakinessTrend = useQuery({
    queryKey: dashboardKeys.flakinessTrend(projectId, range),
    queryFn: () => dashboardEndpoints.flakinessTrend(projectId, range),
    enabled,
    retry: false,
  });
  const healing = useQuery({
    queryKey: dashboardKeys.healing(projectId, range),
    queryFn: () => dashboardEndpoints.healing(projectId, range),
    enabled,
    retry: false,
  });
  const durations = useQuery({
    queryKey: dashboardKeys.durations(projectId, range),
    queryFn: () => dashboardEndpoints.durations(projectId, range),
    enabled,
    retry: false,
  });

  const flakinessOption = useMemo(() => {
    const points = flakinessTrend.data?.points ?? [];
    return {
      tooltip: { trigger: 'axis' },
      grid: { left: 8, right: 8, bottom: 8, top: 32, containLabel: true },
      xAxis: { type: 'category', data: points.map((p) => p.date) },
      yAxis: { type: 'value', min: 0, max: 100, axisLabel: { formatter: '{value}%' } },
      series: [
        {
          name: 'Flakiness index',
          type: 'line',
          smooth: true,
          connectNulls: false,
          data: points.map((p) => (p.index === null ? null : Number(p.index.toFixed(1)))),
          color: '#7c3aed',
        },
      ],
    };
  }, [flakinessTrend.data]);

  const healingOption = useMemo(() => {
    const points = healing.data?.points ?? [];
    return {
      tooltip: { trigger: 'axis' },
      legend: { data: ['Attempts', 'Applied'] },
      grid: { left: 8, right: 8, bottom: 8, top: 32, containLabel: true },
      xAxis: { type: 'category', data: points.map((p) => p.date) },
      yAxis: { type: 'value', minInterval: 1 },
      series: [
        { name: 'Attempts', type: 'bar', data: points.map((p) => p.attempts), color: '#94a3b8' },
        { name: 'Applied', type: 'bar', data: points.map((p) => p.applied), color: '#059669' },
      ],
    };
  }, [healing.data]);

  if (executive.isLoading) {
    return (
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-5" aria-label="Loading executive analytics">
        {[0, 1, 2, 3, 4].map((i) => (
          <Skeleton key={i} className="h-28" />
        ))}
      </div>
    );
  }

  if (executive.isError || !executive.data) {
    return (
      <Card>
        <CardHeader>
          <CardTitle>Executive analytics</CardTitle>
        </CardHeader>
        <CardContent>
          <p className="text-sm text-slate-500">
            Executive analytics are unavailable right now.{' '}
            <button
              type="button"
              className="font-medium text-brand-700 hover:text-brand-600"
              onClick={() => void executive.refetch()}
            >
              Retry
            </button>
          </p>
        </CardContent>
      </Card>
    );
  }

  const overview = executive.data;

  return (
    <div className="space-y-4">
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-5">
        <Card>
          <CardHeader>
            <CardTitle>Flakiness Index</CardTitle>
          </CardHeader>
          <CardContent>
            <p className="text-3xl font-semibold text-slate-900">{pct1(overview.flakinessIndex)}</p>
            <p className="mt-1 text-xs text-slate-500">
              {overview.flakinessIndex === null
                ? 'Not enough repeated executions yet (needs 2+ verdicts per test)'
                : `${overview.flakyTests} flaky of ${overview.eligibleTests} eligible tests`}
            </p>
            <p className="mt-1 text-xs text-slate-500">
              Advisory risk: {overview.highRiskTests} high · {overview.mediumRiskTests} medium ·{' '}
              {overview.lowRiskTests} low · {overview.insufficientHistoryTests} insufficient history
            </p>
          </CardContent>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle>Automation Coverage</CardTitle>
          </CardHeader>
          <CardContent>
            <p className="text-3xl font-semibold text-slate-900">{pct1(overview.automationCoverage)}</p>
            <p className="mt-1 text-xs text-slate-500">
              {overview.automationCoverage === null
                ? 'No eligible test cases'
                : `${overview.automatedCases} approved of ${overview.eligibleCases} eligible cases`}
            </p>
          </CardContent>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle>Release Readiness</CardTitle>
          </CardHeader>
          <CardContent>
            <p className="text-3xl font-semibold text-slate-900">
              {overview.releaseReadiness === null ? '—' : `${overview.releaseReadiness.toFixed(0)}`}
              <span className="ml-2 align-middle">
                <Badge tone={readinessTone(overview.readinessStatus)}>
                  {readinessLabel(overview.readinessStatus)}
                </Badge>
              </span>
            </p>
            <p className="mt-1 text-xs text-slate-500">
              Deterministic quality indicator — never an AI release decision
            </p>
          </CardContent>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle>Healing Success</CardTitle>
          </CardHeader>
          <CardContent>
            <p className="text-3xl font-semibold text-slate-900">{pct1(overview.healingSuccessRate)}</p>
            <p className="mt-1 text-xs text-slate-500">
              {overview.healingSuccessRate === null
                ? 'No healing attempts recorded yet'
                : `${overview.healingApplied} recovered of ${overview.healingAttempts} attempts`}
            </p>
          </CardContent>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle>Avg Duration</CardTitle>
          </CardHeader>
          <CardContent>
            <p className="text-3xl font-semibold text-slate-900">{formatMs(overview.averageDurationMs)}</p>
            <p className="mt-1 text-xs text-slate-500">
              {overview.averageDurationMs === null
                ? 'No completed durations yet'
                : `${overview.durationSampleCount} sampled executions · ${overview.unstableExecutions} unstable`}
            </p>
          </CardContent>
        </Card>
      </div>

      <div className="grid grid-cols-1 gap-4 xl:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle>Flakiness Trend</CardTitle>
            <CardDescription>
              Share of eligible tests with mixed pass/fail outcomes (UTC). Gaps mean no data, not zero.
            </CardDescription>
          </CardHeader>
          <CardContent>
            {flakinessTrend.isLoading ? (
              <Skeleton className="h-64" />
            ) : flakinessTrend.isError ? (
              <p className="text-sm text-slate-500">Flakiness trend unavailable.</p>
            ) : (flakinessTrend.data?.points.length ?? 0) === 0 ? (
              <p className="text-sm text-slate-500">No data for the selected period.</p>
            ) : (
              <Chart
                option={flakinessOption}
                label={`Flakiness trend: ${flakinessTrend.data!.points.length} buckets`}
                fallback={
                  <ul className="space-y-1 text-sm text-slate-600">
                    {flakinessTrend.data!.points.slice(-7).map((p) => (
                      <li key={p.date}>
                        {p.date}: {p.index === null ? 'no data' : `${p.index.toFixed(1)}%`} (
                        {p.flakyTests}/{p.eligibleTests} tests)
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
            <CardTitle>Release Readiness Breakdown</CardTitle>
            <CardDescription>
              Deterministic score from measurable signals — every input is shown with its rule
            </CardDescription>
          </CardHeader>
          <CardContent>
            {overview.readinessComponents.length === 0 ? (
              <p className="text-sm text-slate-500">
                Not enough data to score readiness in this period.
              </p>
            ) : (
              <ul className="divide-y divide-slate-100">
                {overview.readinessComponents.map((c) => (
                  <li key={c.component} className="flex items-start justify-between gap-3 py-2">
                    <div className="min-w-0">
                      <p className="text-sm font-medium text-slate-900">{c.component}</p>
                      <p className="text-xs text-slate-500">
                        {c.threshold} · {c.detail}
                      </p>
                    </div>
                    <p className="shrink-0 font-mono text-sm text-slate-700">
                      {pct1(c.value)}{' '}
                      <span className="text-xs text-slate-400">× {c.weight}%</span>
                    </p>
                  </li>
                ))}
              </ul>
            )}
            <p className="mt-2 text-xs text-slate-500">
              Pass rate {ratioPct(overview.passRate)} · {overview.openCriticalHighDefects} open
              Critical/High defects · {overview.defectsPer100Executions === null ? '—' : `${overview.defectsPer100Executions.toFixed(1)} defects per 100 executions`}
            </p>
          </CardContent>
        </Card>
      </div>

      <div className="grid grid-cols-1 gap-4 xl:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle>Self-Healing Activity</CardTitle>
            <CardDescription>
              Execution-time locator recovery (descriptive — healing alongside flakiness is
              correlation, not causation)
            </CardDescription>
          </CardHeader>
          <CardContent>
            {healing.isLoading ? (
              <Skeleton className="h-48" />
            ) : healing.isError ? (
              <p className="text-sm text-slate-500">Healing analytics unavailable.</p>
            ) : (healing.data?.attempts ?? 0) === 0 && (healing.data?.points.length ?? 0) === 0 ? (
              <p className="text-sm text-slate-500">No healing attempts in this period.</p>
            ) : (
              <>
                <p className="text-sm text-slate-700">
                  {healing.data!.applied} recovered of {healing.data!.attempts} attempts (
                  {pct1(healing.data!.successRate)}) · {healing.data!.deterministic} deterministic ·{' '}
                  {healing.data!.aiAssisted} AI-assisted · {healing.data!.testsWithHealing} tests
                </p>
                <div className="mt-3">
                  <Chart
                    option={healingOption}
                    label={`Healing attempts: ${healing.data!.attempts} total`}
                    fallback={
                      <ul className="space-y-1 text-sm text-slate-600">
                        {healing.data!.points.slice(-7).map((p) => (
                          <li key={p.date}>
                            {p.date}: {p.applied} applied of {p.attempts} attempts
                          </li>
                        ))}
                      </ul>
                    }
                  />
                </div>
              </>
            )}
          </CardContent>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle>Duration &amp; Defect Aging</CardTitle>
            <CardDescription>
              Terminal execution durations plus open-defect age (no SLA targets are configured)
            </CardDescription>
          </CardHeader>
          <CardContent>
            {durations.isLoading ? (
              <Skeleton className="h-48" />
            ) : durations.isError ? (
              <p className="text-sm text-slate-500">Duration analytics unavailable.</p>
            ) : durations.data?.count === 0 ? (
              <p className="text-sm text-slate-500">No completed durations in this period.</p>
            ) : (
              <dl className="grid grid-cols-2 gap-2 text-sm">
                <div>
                  <dt className="text-slate-500">Average</dt>
                  <dd className="font-mono font-medium text-slate-900">{formatMs(durations.data!.averageMs)}</dd>
                </div>
                <div>
                  <dt className="text-slate-500">P50 / P90</dt>
                  <dd className="font-mono font-medium text-slate-900">
                    {formatMs(durations.data!.p50Ms)} / {formatMs(durations.data!.p90Ms)}
                  </dd>
                </div>
                <div>
                  <dt className="text-slate-500">Min / Max</dt>
                  <dd className="font-mono font-medium text-slate-900">
                    {formatMs(durations.data!.minMs)} / {formatMs(durations.data!.maxMs)}
                  </dd>
                </div>
                <div>
                  <dt className="text-slate-500">Open defects by age</dt>
                  <dd className="font-mono font-medium text-slate-900">
                    {durations.data!.openDefectAging.length === 0
                      ? 'None open'
                      : durations.data!.openDefectAging.map((b) => `${b.name}: ${b.count}`).join(' · ')}
                  </dd>
                </div>
              </dl>
            )}
          </CardContent>
        </Card>
      </div>
    </div>
  );
}
