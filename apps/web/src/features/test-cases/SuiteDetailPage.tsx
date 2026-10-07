import { useMemo, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { Play, Archive, Edit, Clock, Plus } from 'lucide-react';
import { Button } from '../../components/ui/button';
import { Input } from '../../components/ui/input';
import { Badge } from '../../components/ui/badge';
import { Skeleton } from '../../components/ui/skeleton';
import { Card, CardContent, CardHeader, CardTitle } from '../../components/ui/card';
import { ErrorState } from '../../components/common/ErrorState';
import { Chart } from '../../components/common/Chart';
import {
  suiteKeys,
  suitesEndpoints,
  scheduleKeys,
  suiteSchedulesEndpoints,
  type ExecuteSuiteResult,
  type SuiteSchedule,
} from '../../lib/api/endpoints/suites';
import { useProfile } from '../../lib/auth/useProfile';
import { Permissions, hasPermission } from '../../lib/auth/permissions';

function statusTone(status: string): 'success' | 'warning' | 'info' | 'neutral' | 'danger' {
  switch (status) {
    case 'Active':
    case 'Passed':
      return 'success';
    case 'Running':
    case 'Queued':
      return 'info';
    case 'Failed':
    case 'Error':
      return 'danger';
    case 'Cancelled':
      return 'neutral';
    case 'TimedOut':
      return 'warning';
    case 'Archived':
      return 'neutral';
    default:
      return 'info';
  }
}

const TRIGGER_LABELS: Record<string, string> = {
  Manual: 'Manual',
  Ci: 'CI/CD',
  Schedule: 'Scheduled',
  Scheduled: 'Scheduled',
  Webhook: 'Webhook',
};

const TIMEZONE_SUGGESTIONS = ['UTC', 'America/New_York', 'America/Chicago', 'America/Los_Angeles', 'Europe/London', 'Europe/Berlin', 'Asia/Kolkata', 'Asia/Singapore', 'Australia/Sydney'];

function scheduleTone(status: string): 'success' | 'warning' | 'neutral' | 'info' {
  switch (status) {
    case 'Active':
      return 'success';
    case 'Disabled':
      return 'warning';
    case 'Archived':
      return 'neutral';
    default:
      return 'info';
  }
}

function SuiteSchedulesCard({
  projectId,
  suiteId,
  suiteActive,
  canManage,
  onError,
}: {
  projectId: string;
  suiteId: string;
  suiteActive: boolean;
  canManage: boolean;
  onError: (message: string) => void;
}) {
  const queryClient = useQueryClient();
  const [showForm, setShowForm] = useState(false);
  const [editing, setEditing] = useState<SuiteSchedule | null>(null);
  const [name, setName] = useState('');
  const [cron, setCron] = useState('');
  const [timeZone, setTimeZone] = useState('UTC');
  const [overlap, setOverlap] = useState('Skip');
  const [formError, setFormError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const schedules = useQuery({
    queryKey: scheduleKeys.list(suiteId),
    queryFn: () => suiteSchedulesEndpoints.list(projectId, suiteId),
    enabled: !!suiteId,
    staleTime: 15_000,
  });

  const [expandedId, setExpandedId] = useState<string | null>(null);
  const scheduleDetails = useQuery({
    queryKey: scheduleKeys.details(expandedId ?? ''),
    queryFn: () => suiteSchedulesEndpoints.get(expandedId!),
    enabled: !!expandedId,
    staleTime: 15_000,
  });

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: scheduleKeys.list(suiteId) });
    if (expandedId) {
      queryClient.invalidateQueries({ queryKey: scheduleKeys.details(expandedId) });
    }
  };

  const saveMutation = useMutation({
    mutationFn: () =>
      editing
        ? suiteSchedulesEndpoints.update(editing.id, {
            name: name.trim(),
            cronExpression: cron.trim(),
            timeZoneId: timeZone.trim() || 'UTC',
            overlapPolicy: overlap,
          })
        : suiteSchedulesEndpoints.create(projectId, suiteId, {
            name: name.trim(),
            cronExpression: cron.trim(),
            timeZoneId: timeZone.trim() || 'UTC',
            overlapPolicy: overlap,
          }),
    onSuccess: () => {
      setShowForm(false);
      setEditing(null);
      setFormError(null);
      setSaving(false);
      invalidate();
    },
    onError: (error: any) => {
      setFormError(error.message ?? 'Failed to save schedule.');
      setSaving(false);
    },
  });

  const actionMutation = useMutation({
    mutationFn: async (input: { kind: 'pause' | 'resume' | 'archive' | 'run'; id: string }) => {
      switch (input.kind) {
        case 'pause':
          await suiteSchedulesEndpoints.pause(input.id);
          return;
        case 'resume':
          await suiteSchedulesEndpoints.resume(input.id);
          return;
        case 'archive':
          await suiteSchedulesEndpoints.archive(input.id);
          return;
        case 'run':
          await suiteSchedulesEndpoints.runNow(input.id);
          return;
      }
    },
    onSuccess: (_, input) => {
      invalidate();
      if (input.kind === 'run') {
        queryClient.invalidateQueries({ queryKey: suiteKeys.all });
      }
    },
    onError: (error: any) => {
      onError(error.message ?? 'Schedule action failed.');
    },
  });

  const openCreate = () => {
    setEditing(null);
    setName('');
    setCron('');
    setTimeZone('UTC');
    setOverlap('Skip');
    setFormError(null);
    setShowForm(true);
  };

  const openEdit = (schedule: SuiteSchedule) => {
    setEditing(schedule);
    setName(schedule.name);
    setCron(schedule.cronExpression);
    setTimeZone(schedule.timeZoneId);
    setOverlap(schedule.overlapPolicy);
    setFormError(null);
    setShowForm(true);
  };

  const handleSave = (e: React.FormEvent) => {
    e.preventDefault();
    if (!name.trim()) {
      setFormError('Schedule name is required.');
      return;
    }
    if (!cron.trim()) {
      setFormError('Cron expression is required (e.g. "30 2 * * *").');
      return;
    }
    setSaving(true);
    saveMutation.mutate();
  };

  const handleAction = (kind: 'pause' | 'resume' | 'archive' | 'run', schedule: SuiteSchedule) => {
    if (kind === 'archive' && !confirm(`Delete schedule "${schedule.name}"? The remote schedule is removed; history is retained in audit.`)) {
      return;
    }
    if (kind === 'run' && !confirm(`Run schedule "${schedule.name}" now? This starts one execution without changing the cadence.`)) {
      return;
    }
    actionMutation.mutate({ kind, id: schedule.id });
  };

  return (
    <Card>
      <CardHeader>
        <div className="flex items-center justify-between">
          <CardTitle className="text-base">Schedules</CardTitle>
          {canManage && suiteActive && (
            <Button variant="secondary" size="sm" onClick={openCreate}>
              <Plus className="h-3 w-3" aria-hidden />
              New schedule
            </Button>
          )}
        </div>
      </CardHeader>
      <CardContent className="space-y-3">
        {schedules.isLoading && (
          <div className="space-y-2" aria-label="Loading schedules">
            {[0, 1].map((i) => <Skeleton key={i} className="h-12" />)}
          </div>
        )}
        {schedules.isError && (
          <ErrorState error={schedules.error} onRetry={() => void schedules.refetch()} />
        )}
        {schedules.data && schedules.data.length === 0 && (
          <p className="text-sm text-slate-500 text-center py-4">
            No schedules yet. {canManage ? 'Create one to run this suite on a cron cadence.' : ''}
          </p>
        )}
        {schedules.data && schedules.data.length > 0 && (
          <div className="space-y-2">
            {schedules.data.map((schedule) => (
              <div key={schedule.id} className="p-3 border rounded-lg bg-slate-50 space-y-2">
                <div className="flex items-center justify-between gap-2">
                  <button
                    type="button"
                    onClick={() => setExpandedId((current) => (current === schedule.id ? null : schedule.id))}
                    className="font-medium text-sm text-brand-700 hover:text-brand-600 truncate"
                    aria-expanded={expandedId === schedule.id}
                  >
                    {schedule.name}
                  </button>
                  <Badge tone={scheduleTone(schedule.status)}>{schedule.status}</Badge>
                </div>
                <div className="font-mono text-xs text-slate-600">
                  {schedule.cronExpression} · {schedule.timeZoneId} · overlap {schedule.overlapPolicy}
                </div>
                <div className="flex flex-wrap gap-x-4 gap-y-1 text-xs text-slate-500">
                  <span className="flex items-center gap-1">
                    <Clock className="h-3 w-3" aria-hidden />
                    Last: {schedule.lastTriggeredAt ? new Date(schedule.lastTriggeredAt).toLocaleString() : 'never'}
                  </span>
                  {expandedId === schedule.id && (
                    <span>
                      Next:{' '}
                      {scheduleDetails.isLoading
                        ? 'loading…'
                        : scheduleDetails.data?.nextRunAt
                          ? new Date(scheduleDetails.data.nextRunAt).toLocaleString()
                          : '—'}
                    </span>
                  )}
                </div>
                {canManage && (
                  <div className="flex flex-wrap gap-1 pt-1">
                    {schedule.status === 'Active' && (
                      <>
                        <Button variant="secondary" size="sm" onClick={() => handleAction('run', schedule)} aria-label={`Run schedule ${schedule.name} now`}>
                          <Play className="h-3 w-3" aria-hidden />
                          Run now
                        </Button>
                        <Button variant="ghost" size="sm" onClick={() => handleAction('pause', schedule)} aria-label={`Pause schedule ${schedule.name}`}>
                          Pause
                        </Button>
                      </>
                    )}
                    {schedule.status === 'Disabled' && (
                      <>
                        <Button variant="secondary" size="sm" onClick={() => handleAction('run', schedule)} aria-label={`Run schedule ${schedule.name} now`}>
                          <Play className="h-3 w-3" aria-hidden />
                          Run now
                        </Button>
                        <Button variant="ghost" size="sm" onClick={() => handleAction('resume', schedule)} aria-label={`Resume schedule ${schedule.name}`}>
                          Resume
                        </Button>
                      </>
                    )}
                    <Button variant="ghost" size="sm" onClick={() => openEdit(schedule)} aria-label={`Edit schedule ${schedule.name}`}>
                      <Edit className="h-3 w-3" aria-hidden />
                      Edit
                    </Button>
                    <Button variant="ghost" size="sm" onClick={() => handleAction('archive', schedule)} className="text-red-600 hover:text-red-700" aria-label={`Delete schedule ${schedule.name}`}>
                      <Archive className="h-3 w-3" aria-hidden />
                      Delete
                    </Button>
                  </div>
                )}
              </div>
            ))}
          </div>
        )}

        {showForm && canManage && (
          <form onSubmit={handleSave} className="space-y-3 border-t pt-3">
            <h3 className="text-sm font-medium text-slate-900">{editing ? 'Edit schedule' : 'New schedule'}</h3>
            {formError && (
              <div role="alert" className="rounded-md border border-red-200 bg-red-50 p-2 text-sm text-red-700">
                {formError}
              </div>
            )}
            <div>
              <label htmlFor="schedule-name" className="block text-xs font-medium text-slate-500">Name *</label>
              <Input id="schedule-name" value={name} onChange={(e) => setName(e.target.value)} placeholder="Nightly regression" maxLength={200} className="mt-1" />
            </div>
            <div>
              <label htmlFor="schedule-cron" className="block text-xs font-medium text-slate-500">Cron expression *</label>
              <Input
                id="schedule-cron"
                value={cron}
                onChange={(e) => setCron(e.target.value)}
                placeholder="30 2 * * *"
                className="mt-1 font-mono"
              />
              <p className="mt-1 text-xs text-slate-400">5 fields (minute hour day month weekday), or 6 with leading seconds. Server-validated.</p>
            </div>
            <div className="grid grid-cols-2 gap-2">
              <div>
                <label htmlFor="schedule-tz" className="block text-xs font-medium text-slate-500">Timezone</label>
                <Input
                  id="schedule-tz"
                  value={timeZone}
                  onChange={(e) => setTimeZone(e.target.value)}
                  placeholder="UTC"
                  list="schedule-tz-suggestions"
                  className="mt-1"
                />
                <datalist id="schedule-tz-suggestions">
                  {TIMEZONE_SUGGESTIONS.map((tz) => (
                    <option key={tz} value={tz} />
                  ))}
                </datalist>
              </div>
              <div>
                <label htmlFor="schedule-overlap" className="block text-xs font-medium text-slate-500">Overlap</label>
                <select
                  id="schedule-overlap"
                  value={overlap}
                  onChange={(e: React.ChangeEvent<HTMLSelectElement>) => setOverlap(e.target.value)}
                  className="mt-1 rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 w-full"
                >
                  <option value="Skip">Skip overlapping</option>
                  <option value="Allow">Allow overlapping</option>
                </select>
              </div>
            </div>
            <div className="flex justify-end gap-2">
              <Button type="button" variant="secondary" size="sm" onClick={() => setShowForm(false)}>
                Cancel
              </Button>
              <Button type="submit" size="sm" disabled={saving}>
                {saving ? 'Saving…' : editing ? 'Save changes' : 'Create schedule'}
              </Button>
            </div>
          </form>
        )}
      </CardContent>
    </Card>
  );
}

export function SuiteDetailPage() {
  const { projectId = '', suiteId } = useParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const profile = useProfile();
  const canManage = hasPermission(profile.data?.permissions, Permissions.TestCasesManage);
  const canReadSchedules = hasPermission(profile.data?.permissions, Permissions.TestCasesRead);
  const canReadExecutions = hasPermission(profile.data?.permissions, Permissions.ExecutionsRead);
  const canReadReports = hasPermission(profile.data?.permissions, Permissions.ReportsRead);

  const [execPage, setExecPage] = useState(1);
  const [reportFrom, setReportFrom] = useState('');
  const [reportTo, setReportTo] = useState('');
  const [executing, setExecuting] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);

  const suite = useQuery({
    queryKey: suiteKeys.details(suiteId!),
    queryFn: () => suitesEndpoints.get(suiteId!),
    enabled: !!suiteId,
    staleTime: 0,
  });

  const executions = useQuery({
    queryKey: suiteKeys.executions(suiteId!, {}, execPage),
    queryFn: () => suitesEndpoints.getExecutions(suiteId!, {}, execPage, 25),
    enabled: !!suiteId && canReadExecutions,
    staleTime: 15_000,
  });

  const report = useQuery({
    queryKey: suiteKeys.report(suiteId!, { from: reportFrom || undefined, to: reportTo || undefined, groupBy: 'day' }),
    queryFn: () => suitesEndpoints.getReport(suiteId!, { from: reportFrom || undefined, to: reportTo || undefined, groupBy: 'day' }),
    enabled: !!suiteId && canReadReports,
    staleTime: 30_000,
  });

  const trendOption = useMemo(() => {
    const points = report.data?.trend ?? [];
    return {
      tooltip: { trigger: 'axis' as const },
      legend: { data: ['Passed', 'Failed'] },
      xAxis: { type: 'category' as const, data: points.map((p) => p.date) },
      yAxis: { type: 'value' as const },
      series: [
        { name: 'Passed', type: 'bar' as const, data: points.map((p) => p.passed), itemStyle: { color: '#16a34a' } },
        { name: 'Failed', type: 'bar' as const, data: points.map((p) => p.failed), itemStyle: { color: '#dc2626' } },
      ],
    };
  }, [report.data]);

  const executeMutation = useMutation({
    mutationFn: (input: { projectId: string; suiteId: string; idempotencyKey?: string }) =>
      suitesEndpoints.execute(input),
    onSuccess: (result: ExecuteSuiteResult) => {
      queryClient.invalidateQueries({ queryKey: suiteKeys.executions(suiteId!, {}, 1) });
      queryClient.invalidateQueries({ queryKey: [...suiteKeys.all, 'report', suiteId!] });
      navigate(`/projects/${projectId}/executions/${result.executionId}`);
    },
    onError: (error: any) => {
      setActionError(error.message ?? 'Failed to execute suite');
    },
    onSettled: () => setExecuting(false),
  });

  const archiveMutation = useMutation({
    mutationFn: () => suitesEndpoints.archive(suiteId!),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: suiteKeys.all });
      navigate(`/projects/${projectId}/test-suites`);
    },
    onError: (error: any) => {
      setActionError(error.message ?? 'Failed to archive suite');
    },
  });

  const handleExecute = () => {
    if (suite.data?.members.length === 0) {
      setActionError('Cannot execute an empty suite. Add test cases first.');
      return;
    }
    if (!confirm(`Run suite "${suite.data?.name}"? This will create ${suite.data?.members.length} execution(s).`)) {
      return;
    }
    setExecuting(true);
    executeMutation.mutate({ projectId, suiteId: suiteId! });
  };

  const handleArchive = () => {
    if (!confirm(`Archive suite "${suite.data?.name}"? This action cannot be undone.`)) {
      return;
    }
    archiveMutation.mutate();
  };

  const handleReportFilter = (e: React.FormEvent) => {
    e.preventDefault();
    report.refetch();
  };

  if (suite.isLoading) {
    return (
      <div className="space-y-6" aria-label="Loading suite details">
        <Skeleton className="h-8 w-1/4" />
        <Skeleton className="h-4 w-1/2" />
        <div className="space-y-2">
          {[0, 1, 2].map((i) => <Skeleton key={i} className="h-14" />)}
        </div>
      </div>
    );
  }

  if (suite.isError) {
    return <ErrorState error={suite.error} onRetry={() => void suite.refetch()} />;
  }

  if (!suite.data) {
    return (
      <Card>
        <CardContent className="flex flex-col items-center gap-3 py-12 text-center">
          <h2 className="text-base font-semibold text-slate-900">Suite not found</h2>
          <p className="max-w-md text-sm text-slate-500">This suite may have been deleted or you may not have access.</p>
          <Link to={`/projects/${projectId}/test-suites`}>
            <Button size="sm">Back to suites</Button>
          </Link>
        </CardContent>
      </Card>
    );
  }

  const s = suite.data;
  const isActive = s.status === 'Active';

  return (
    <div className="space-y-6">
      {actionError && (
        <div role="alert" className="rounded-md border border-red-200 bg-red-50 p-3 text-sm text-red-700">
          {actionError}
        </div>
      )}
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <Link to={`/projects/${projectId}/test-suites`} className="text-sm text-brand-700 hover:text-brand-600">
            ← Back to suites
          </Link>
          <div className="flex items-baseline gap-2 mt-1">
            <h1 className="text-xl font-semibold text-slate-900">{s.name}</h1>
            <Badge tone={statusTone(s.status)}>{s.status}</Badge>
          </div>
          <p className="mt-1 text-sm text-slate-500">
            {s.description ?? 'No description'}
          </p>
        </div>
        <div className="flex gap-2">
          {canManage && isActive && (
            <Button onClick={handleExecute} disabled={executing || s.members.length === 0}>
              <Play className="h-4 w-4" aria-hidden />
              {executing ? 'Running…' : `Run now (${s.members.length})`}
            </Button>
          )}
          {canManage && (
            <Link to={`/projects/${projectId}/test-suites/${suiteId}/edit`}>
              <Button variant="secondary">
                <Edit className="h-4 w-4" aria-hidden />
                Edit
              </Button>
            </Link>
          )}
          {canManage && isActive && (
            <Button variant="ghost" onClick={handleArchive} className="text-red-600 hover:text-red-700">
              <Archive className="h-4 w-4" aria-hidden />
              Archive
            </Button>
          )}
        </div>
      </div>

      <div className="grid gap-6 lg:grid-cols-3">
        <div className="lg:col-span-2 space-y-6">
          <Card>
            <CardHeader>
              <CardTitle className="text-base">Members ({s.members.length})</CardTitle>
            </CardHeader>
            <CardContent>
              {s.members.length === 0 ? (
                <p className="text-sm text-slate-500 text-center py-4">No test cases in this suite.</p>
              ) : (
                <div className="space-y-2">
                  {s.members.map((m, index) => (
                    <div key={m.testCaseId} className="flex items-center gap-3 p-3 border rounded-lg bg-slate-50">
                      <Badge tone="info">{index + 1}</Badge>
                      <div className="flex-1 min-w-0">
                        <div className="flex items-center gap-2">
                          <span className="font-mono text-sm text-slate-700">{m.testKey}</span>
                          {m.jiraIssueKey && (
                            <Badge className="text-xs bg-slate-100 text-slate-600">
                              {m.jiraIssueKey}
                            </Badge>
                          )}
                        </div>
                        <p className="text-sm text-slate-500 truncate">{m.title}</p>
                      </div>
                    </div>
                  ))}
                </div>
              )}
            </CardContent>
          </Card>

          {canReadSchedules && (
            <SuiteSchedulesCard
              projectId={projectId}
              suiteId={suiteId!}
              suiteActive={isActive}
              canManage={canManage}
              onError={setActionError}
            />
          )}

          {canReadExecutions && (
            <Card>
              <CardHeader>
                <CardTitle className="text-base">Execution history</CardTitle>
              </CardHeader>
              <CardContent>
                {executions.isLoading && (
                  <div className="space-y-2">
                    {[0, 1, 2].map((i) => <Skeleton key={i} className="h-10" />)}
                  </div>
                )}

                {executions.isError && <ErrorState error={executions.error} onRetry={() => void executions.refetch()} />}

                {executions.data && executions.data.items.length === 0 && (
                  <p className="text-sm text-slate-500 text-center py-4">No executions yet.</p>
                )}

                {executions.data && executions.data.items.length > 0 && (
                  <div className="overflow-x-auto">
                    <table className="w-full min-w-3xl text-left text-sm">
                      <thead>
                        <tr className="border-b border-slate-200 bg-slate-50 text-xs uppercase tracking-wider text-slate-500">
                          <th scope="col" className="px-4 py-3 font-medium">Execution</th>
                          <th scope="col" className="px-4 py-3 font-medium">Trigger</th>
                          <th scope="col" className="px-4 py-3 font-medium">Status</th>
                          <th scope="col" className="px-4 py-3 font-medium">Tests</th>
                          <th scope="col" className="px-4 py-3 font-medium">Passed</th>
                          <th scope="col" className="px-4 py-3 font-medium">Failed</th>
                          <th scope="col" className="px-4 py-3 font-medium">Started</th>
                          <th scope="col" className="px-4 py-3 font-medium">Completed</th>
                        </tr>
                      </thead>
                      <tbody className="divide-y divide-slate-100">
                        {executions.data.items.map((exec) => (
                          <tr key={exec.executionId} className="hover:bg-slate-50">
                            <td className="whitespace-nowrap px-4 py-3 font-mono text-xs text-slate-700">
                              <Link to={`/projects/${projectId}/executions/${exec.executionId}`} className="text-brand-700 hover:text-brand-600">
                                {exec.executionId.slice(0, 8)}…
                              </Link>
                            </td>
                            <td className="whitespace-nowrap px-4 py-3 text-slate-500">
                              {TRIGGER_LABELS[exec.triggerType] ?? exec.triggerType}
                            </td>
                            <td className="whitespace-nowrap px-4 py-3">
                              <Badge tone={statusTone(exec.status)}>{exec.status}</Badge>
                            </td>
                            <td className="whitespace-nowrap px-4 py-3 text-right font-mono text-sm text-slate-700">
                              {exec.testCount}
                            </td>
                            <td className="whitespace-nowrap px-4 py-3 text-right font-mono text-sm text-green-700">
                              {exec.passedCount}
                            </td>
                            <td className="whitespace-nowrap px-4 py-3 text-right font-mono text-sm text-red-700">
                              {exec.failedCount}
                            </td>
                            <td className="whitespace-nowrap px-4 py-3 text-slate-500">
                              {exec.startedAt ? new Date(exec.startedAt).toLocaleString() : '—'}
                            </td>
                            <td className="whitespace-nowrap px-4 py-3 text-slate-500">
                              {exec.completedAt ? new Date(exec.completedAt).toLocaleString() : '—'}
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                )}

                {executions.data && (
                  <div className="mt-4 flex items-center justify-between text-sm text-slate-500">
                    <p>Showing {executions.data.items.length} of {executions.data.totalCount} executions</p>
                    <div className="flex gap-2">
                      <Button variant="secondary" size="sm" disabled={execPage <= 1} onClick={() => setExecPage(p => Math.max(1, p - 1))}>
                        Previous
                      </Button>
                      <span className="px-2 py-1.5 font-mono text-xs">{execPage}</span>
                      <Button variant="secondary" size="sm" disabled={execPage * 25 >= executions.data.totalCount} onClick={() => setExecPage(p => p + 1)}>
                        Next
                      </Button>
                    </div>
                  </div>
                )}
              </CardContent>
            </Card>
          )}
        </div>

        <div className="space-y-6">
          <Card>
            <CardHeader>
              <CardTitle className="text-base">Suite info</CardTitle>
            </CardHeader>
            <CardContent className="space-y-3 text-sm">
              <div className="flex justify-between">
                <span className="text-slate-500">Suite ID</span>
                <span className="font-mono text-slate-900">{s.id.slice(0, 8)}…</span>
              </div>
              <div className="flex justify-between">
                <span className="text-slate-500">Project</span>
                <span className="font-mono text-slate-900">{s.projectId.slice(0, 8)}…</span>
              </div>
              <div className="flex justify-between">
                <span className="text-slate-500">Test cases</span>
                <span className="font-mono text-slate-900">{s.members.length}</span>
              </div>
              <div className="flex justify-between">
                <span className="text-slate-500">Created</span>
                <span className="text-slate-900">{new Date(s.createdAt).toLocaleString()}</span>
              </div>
              <div className="flex justify-between">
                <span className="text-slate-500">Updated</span>
                <span className="text-slate-900">{new Date(s.updatedAt).toLocaleString()}</span>
              </div>
            </CardContent>
          </Card>

          {canReadReports && s.members.length > 0 && (
            <Card>
              <CardHeader>
                <CardTitle className="text-base">Execution report</CardTitle>
              </CardHeader>
              <CardContent className="space-y-4">
                <form onSubmit={handleReportFilter} className="space-y-2">
                  <div className="grid grid-cols-2 gap-2">
                    <label className="text-xs font-medium text-slate-500">
                      From
                      <Input type="date" value={reportFrom} onChange={(e: React.ChangeEvent<HTMLInputElement>) => setReportFrom(e.target.value)} className="mt-1" />
                    </label>
                    <label className="text-xs font-medium text-slate-500">
                      To
                      <Input type="date" value={reportTo} onChange={(e: React.ChangeEvent<HTMLInputElement>) => setReportTo(e.target.value)} className="mt-1" />
                    </label>
                  </div>
                  <Button type="submit" size="sm" variant="secondary" className="w-full">
                    Filter
                  </Button>
                </form>

                {report.isLoading && (
                  <div className="space-y-2">
                    {[0, 1].map((i) => <Skeleton key={i} className="h-10" />)}
                  </div>
                )}

                {report.isError && <ErrorState error={report.error} onRetry={() => void report.refetch()} />}

                {report.data && (
                  <div className="space-y-3">
                    <div className="grid grid-cols-2 gap-3 text-center">
                      <div className="p-3 rounded-lg bg-green-50">
                        <p className="text-2xl font-bold text-green-700">{report.data.passedCount}</p>
                        <p className="text-xs text-green-600">Passed</p>
                      </div>
                      <div className="p-3 rounded-lg bg-red-50">
                        <p className="text-2xl font-bold text-red-700">{report.data.failedCount}</p>
                        <p className="text-xs text-red-600">Failed</p>
                      </div>
                      <div className="p-3 rounded-lg bg-yellow-50">
                        <p className="text-2xl font-bold text-yellow-700">{report.data.cancelledCount}</p>
                        <p className="text-xs text-yellow-600">Cancelled</p>
                      </div>
                      <div className="p-3 rounded-lg bg-slate-50">
                        <p className="text-2xl font-bold text-slate-700">{report.data.timedOutCount + report.data.errorCount}</p>
                        <p className="text-xs text-slate-600">Timeout/Error</p>
                      </div>
                    </div>
                    {report.data.triggerBreakdown.length > 0 && (
                      <div className="pt-2 border-t space-y-1">
                        <p className="text-xs font-medium text-slate-500">By trigger</p>
                        {report.data.triggerBreakdown.map((row) => (
                          <div key={row.trigger} className="flex justify-between text-sm">
                            <span className="text-slate-500">{TRIGGER_LABELS[row.trigger] ?? row.trigger}</span>
                            <span className="font-mono text-slate-900">
                              {row.total} exec · {row.passed} passed · {row.failed} failed
                            </span>
                          </div>
                        ))}
                      </div>
                    )}
                    {report.data.trend.length > 0 && (
                      <div className="pt-2 border-t space-y-1">
                        <p className="text-xs font-medium text-slate-500">Daily trend (UTC)</p>
                        <Chart
                          label="Daily suite execution trend"
                          option={trendOption}
                          fallback={(
                            <ul className="text-sm text-slate-600 space-y-1">
                              {report.data.trend.map((point) => (
                                <li key={point.date}>
                                  {point.date}: {point.total} executions, {point.passed} passed, {point.failed} failed
                                </li>
                              ))}
                            </ul>
                          )}
                        />
                      </div>
                    )}
                    <div className="pt-2 border-t">
                      <div className="flex justify-between text-sm">
                        <span className="text-slate-500">Total executions</span>
                        <span className="font-medium">{report.data.totalExecutions}</span>
                      </div>
                      <div className="flex justify-between text-sm">
                        <span className="text-slate-500">Pass rate</span>
                        <span className="font-medium">{report.data.passRate?.toFixed(1) ?? '—'}%</span>
                      </div>
                      <div className="flex justify-between text-sm">
                        <span className="text-slate-500">Total duration</span>
                        <span className="font-medium">{(report.data.totalDurationMs / 1000).toFixed(1)}s</span>
                      </div>
                      <div className="flex justify-between text-sm">
                        <span className="text-slate-500">Avg duration</span>
                        <span className="font-medium">{(report.data.averageDurationMs / 1000).toFixed(1)}s</span>
                      </div>
                      <div className="flex justify-between text-sm">
                        <span className="text-slate-500">Latest run</span>
                        <span className="font-medium">{report.data.latestExecutionAt ? new Date(report.data.latestExecutionAt).toLocaleString() : '—'}</span>
                      </div>
                    </div>
                  </div>
                )}
              </CardContent>
            </Card>
          )}
        </div>
      </div>
    </div>
  );
}