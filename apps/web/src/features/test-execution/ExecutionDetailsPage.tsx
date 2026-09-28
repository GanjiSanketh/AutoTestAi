import { useEffect, useMemo, useRef, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { AlertTriangle, Ban, ImageIcon, ListChecks, Loader2, ExternalLink } from 'lucide-react';
import { Button } from '../../components/ui/button';
import { Badge } from '../../components/ui/badge';
import { Skeleton } from '../../components/ui/skeleton';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '../../components/ui/card';
import { ErrorState } from '../../components/common/ErrorState';
import { ApiError } from '../../lib/api/client';
import {
  executionEndpoints,
  executionKeys,
  type ExecutionLogEntry,
  type ExecutionStep,
} from '../../lib/api/endpoints/executions';
import {
  ExecutionEvent,
  createExecutionHubConnection,
  subscribeToExecution,
} from '../../lib/realtime/executionHub';
import { useProfile } from '../../lib/auth/useProfile';
import { Permissions, hasPermission } from '../../lib/auth/permissions';
import { executionTone } from './ExecutionListPage';
import { FailureAnalysisSection } from './FailureAnalysisSection';

const TERMINAL = new Set(['Passed', 'Failed', 'Cancelled', 'TimedOut', 'Error']);
const MAX_LOG_LINES = 500;

function formatDuration(ms: number | null | undefined): string {
  if (ms === null || ms === undefined) return '—';
  if (ms < 1000) return `${ms} ms`;
  return `${(ms / 1000).toFixed(1)} s`;
}

function formatTime(iso: string | null | undefined): string {
  if (!iso) return '—';
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? iso : date.toLocaleString();
}

function stepTone(status: string): 'success' | 'danger' | 'warning' | 'info' | 'neutral' {
  switch (status) {
    case 'Passed':
      return 'success';
    case 'Failed':
    case 'Error':
      return 'danger';
    case 'Skipped':
    case 'Cancelled':
    case 'TimedOut':
      return 'warning';
    case 'Running':
      return 'info';
    default:
      return 'neutral';
  }
}

interface LiveStep extends ExecutionStep {
  live?: boolean;
}

/**
 * Execution detail: REST is authoritative, SignalR streams live updates.
 * Subscribes only for non-terminal executions and refetches after reconnect.
 */
export function ExecutionDetailsPage() {
  const { projectId = '', executionId = '' } = useParams();
  const navigate = useNavigate();
  const profile = useProfile();
  const queryClient = useQueryClient();
  const canCancel = hasPermission(profile.data?.permissions, Permissions.ExecutionsCancel);
  const canAnalyze = hasPermission(profile.data?.permissions, Permissions.ExecutionsAnalyze);
  const canCreateDefect = hasPermission(profile.data?.permissions, Permissions.BugsManage);

  const [liveSteps, setLiveSteps] = useState<Map<number, LiveStep> | null>(null);
  const [liveLogs, setLiveLogs] = useState<ExecutionLogEntry[] | null>(null);
  const [follow, setFollow] = useState(true);
  const [connectionError, setConnectionError] = useState<string | null>(null);
  const logBoxRef = useRef<HTMLDivElement>(null);

  const detail = useQuery({
    queryKey: executionKeys.details(projectId, executionId),
    queryFn: () => executionEndpoints.get(projectId, executionId),
    enabled: !!projectId && !!executionId,
    retry: false,
    // Live updates arrive via SignalR; poll only while active as a backstop.
    refetchInterval: (query) => {
      const status = query.state.data?.status;
      return status && !TERMINAL.has(status) ? 10_000 : false;
    },
  });

  const artifacts = useQuery({
    queryKey: executionKeys.artifacts(projectId, executionId),
    queryFn: () => executionEndpoints.artifacts(projectId, executionId),
    enabled: !!projectId && !!executionId && !!detail.data,
    retry: false,
  });

  const baseLogs = useQuery({
    queryKey: executionKeys.logs(projectId, executionId),
    queryFn: () => executionEndpoints.logs(projectId, executionId, undefined, 200),
    enabled: !!projectId && !!executionId && !!detail.data,
    retry: false,
    staleTime: 5_000,
  });

  const invalidateAll = () => {
    void queryClient.invalidateQueries({ queryKey: executionKeys.details(projectId, executionId) });
    void queryClient.invalidateQueries({ queryKey: executionKeys.artifacts(projectId, executionId) });
    void queryClient.invalidateQueries({ queryKey: executionKeys.logs(projectId, executionId) });
  };

  const cancel = useMutation({
    mutationFn: () => executionEndpoints.cancel(projectId, executionId),
    onSuccess: () => invalidateAll(),
  });

  const download = useMutation({
    mutationFn: (artifactId: string) =>
      executionEndpoints.download(projectId, executionId, artifactId),
    onSuccess: (data) => {
      window.open(data.downloadUrl, '_blank', 'noopener');
    },
  });

  const isTerminal = detail.data ? TERMINAL.has(detail.data.status) : false;

  // Seed live buffers from REST state; reset when switching executions.
  useEffect(() => {
    setLiveSteps(null);
    setLiveLogs(null);
    setConnectionError(null);
  }, [projectId, executionId]);

  useEffect(() => {
    if (!detail.data || isTerminal) return;
    let disposed = false;
    const connection = createExecutionHubConnection();
    connection.onreconnected(() => {
      if (!disposed) invalidateAll();
    });
    subscribeToExecution(connection, executionId, {
      [ExecutionEvent.ExecutionStepStarted]: (payload: unknown) => {
        const event = payload as { order?: number; action?: string };
        if (typeof event.order !== 'number') return;
        const order: number = event.order;
        const action: string = event.action ?? '';
        setLiveSteps((prev) => {
          const next = new Map(prev ?? []);
          const existing = next.get(order);
          next.set(order, {
            order,
            action: existing?.action ?? action,
            target: existing?.target ?? null,
            status: 'Running',
            startedAt: new Date().toISOString(),
            completedAt: null,
            durationMs: null,
            errorMessage: null,
            live: true,
          });
          return next;
        });
      },
      [ExecutionEvent.ExecutionStepCompleted]: (payload: unknown) => {
        const event = payload as { step?: LiveStep };
        if (!event.step || typeof event.step.order !== 'number') return;
        const completed = event.step;
        setLiveSteps((prev) => {
          const next = new Map(prev ?? []);
          next.set(completed.order, { ...completed, live: true });
          return next;
        });
      },
      [ExecutionEvent.ExecutionLogReceived]: (payload: unknown) => {
        const event = payload as { logs?: Array<{ timestamp: number; level: string; message: string }> };
        if (!Array.isArray(event.logs)) return;
        const incoming = event.logs;
        setLiveLogs((prev) => {
          const base = prev ?? [];
          const appended: ExecutionLogEntry[] = incoming.map((l, i) => ({
            id: -(base.length + i + 1),
            timestamp: new Date(l.timestamp).toISOString(),
            level: l.level,
            message: l.message,
          }));
          return [...base, ...appended].slice(-MAX_LOG_LINES);
        });
      },
      [ExecutionEvent.ExecutionStatusChanged]: () => invalidateAll(),
      [ExecutionEvent.ExecutionCompleted]: () => invalidateAll(),
      [ExecutionEvent.ExecutionFailed]: () => invalidateAll(),
    })
      .then(() => {
        if (!disposed) setConnectionError(null);
      })
      .catch((error: Error) => {
        if (!disposed) setConnectionError(error.message);
      });
    return () => {
      disposed = true;
      void connection.stop();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId, executionId, detail.data?.status]);

  const steps: LiveStep[] = useMemo(() => {
    const persisted = detail.data?.test.steps ?? [];
    if (!liveSteps) return persisted;
    const merged = new Map<number, LiveStep>();
    for (const step of persisted) merged.set(step.order, step);
    for (const [order, step] of liveSteps) merged.set(order, step);
    return [...merged.values()].sort((a, b) => a.order - b.order);
  }, [detail.data, liveSteps]);

  const logs: ExecutionLogEntry[] = useMemo(() => {
    const persisted = baseLogs.data ?? [];
    if (!liveLogs) return persisted.slice(-MAX_LOG_LINES);
    // Live entries carry negative ids; merge and keep the bound.
    const merged = [...persisted, ...liveLogs];
    return merged.slice(-MAX_LOG_LINES);
  }, [baseLogs.data, liveLogs]);

  useEffect(() => {
    if (follow && logBoxRef.current) {
      logBoxRef.current.scrollTop = logBoxRef.current.scrollHeight;
    }
  }, [logs, follow]);

  if (detail.isLoading) {
    return (
      <div className="space-y-4" aria-label="Loading execution">
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-64" />
      </div>
    );
  }

  if (detail.isError || !detail.data) {
    return (
      <div className="space-y-6">
        <Link to={`/projects/${projectId}/executions`} className="text-sm text-brand-700 hover:text-brand-600">
          ← Back to executions
        </Link>
        <ErrorState
          error={detail.error}
          onRetry={() => void detail.refetch()}
          notFoundMessage="This execution does not exist."
        />
      </div>
    );
  }

  const execution = detail.data;
  const test = execution.test;
  const cancelError = cancel.error instanceof ApiError ? cancel.error : null;
  const analyzable = ['Failed', 'Error', 'TimedOut'].includes(execution.status);
  const failedSteps = test.steps.filter((s) => s.status === 'Failed' || s.status === 'Error');
  const failedStepSummary =
    failedSteps.length === 0
      ? null
      : failedSteps
          .map((s) => `step ${s.order} (${s.action})${s.errorMessage ? `: ${s.errorMessage}` : ''}`)
          .join('; ');

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <Link to={`/projects/${projectId}/executions`} className="text-sm text-brand-700 hover:text-brand-600">
            ← Back to executions
          </Link>
          <div className="mt-2 flex flex-wrap items-center gap-2">
            <Badge tone={executionTone(execution.status)}>{execution.status}</Badge>
            {test.failureClassification && test.failureClassification !== 'Unknown' && (
              <Badge tone="warning">{test.failureClassification}</Badge>
            )}
            {test.testSourceType === 'ai' && <Badge tone="ai">AI-generated</Badge>}
            <span className="font-mono text-xs text-slate-500">
              {test.testKey} · v{test.testCaseVersionNumber} · {test.reviewStatus}
            </span>
          </div>
          <h1 className="mt-1 text-xl font-semibold text-slate-900">{test.testTitle}</h1>
          <p className="mt-1 font-mono text-xs text-slate-500">
            execution {execution.id} · workflow {execution.workflowId ?? '—'}
          </p>
        </div>
        <div className="flex gap-2">
          {!isTerminal && canCancel && (
            <Button
              variant="destructive"
              size="sm"
              disabled={cancel.isPending}
              onClick={() => cancel.mutate()}
            >
              {cancel.isPending ? (
                <>
                  <Loader2 className="h-4 w-4 animate-spin" aria-hidden />
                  Cancelling…
                </>
              ) : (
                <>
                  <Ban className="h-4 w-4" aria-hidden />
                  Cancel execution
                </>
              )}
            </Button>
          )}
          {!isTerminal && !canCancel && (
            <p className="text-xs text-slate-400" title="Cancelling requires the executions.cancel permission">
              Cancellation unavailable for your role.
            </p>
          )}
        </div>
      </div>

      {cancelError && (
        <div role="alert" className="rounded-md border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
          Cancellation failed: {cancelError.message}
        </div>
      )}
      {connectionError && !isTerminal && (
        <div role="alert" className="rounded-md border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-800">
          Live updates unavailable ({connectionError}). State refreshes automatically.
        </div>
      )}

      <div className="grid grid-cols-1 gap-4 xl:grid-cols-3">
        <Card className="xl:col-span-2">
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <ListChecks className="h-4 w-4" aria-hidden />
              Step results
            </CardTitle>
            <CardDescription>
              {test.framework ?? 'playwright'} · {test.browser ?? 'chromium'} · attempt {test.attempt} ·
              duration {formatDuration(test.durationMs)}
            </CardDescription>
          </CardHeader>
          <CardContent>
            {steps.length === 0 && (
              <p className="text-sm text-slate-400">
                {isTerminal ? 'No step results recorded.' : 'Waiting for the worker to report steps…'}
              </p>
            )}
            {steps.length > 0 && (
              <ol className="divide-y divide-slate-100 rounded-md border border-slate-200">
                {steps.map((step) => (
                  <li key={step.order} className="px-3 py-2 text-sm">
                    <span className="mr-2 inline-flex h-5 w-5 items-center justify-center rounded-full bg-slate-100 font-mono text-xs text-slate-600">
                      {step.order}
                    </span>
                    <strong className="font-medium text-slate-900">{step.action}</strong>
                    {step.target && (
                      <span className="ml-2 font-mono text-xs text-slate-500">{step.target}</span>
                    )}
                    <Badge tone={stepTone(step.status)} className="ml-2">
                      {step.status}
                    </Badge>
                    <span className="ml-2 font-mono text-xs text-slate-400">
                      {formatDuration(step.durationMs)}
                    </span>
                    {step.errorMessage && (
                      <p className="mt-1 pl-7 text-xs text-rose-600">{step.errorMessage}</p>
                    )}
                  </li>
                ))}
              </ol>
            )}
            {test.errorMessage && (
              <div role="alert" className="mt-3 rounded-md border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
                <p className="flex items-center gap-1 font-semibold">
                  <AlertTriangle className="h-4 w-4" aria-hidden />
                  {test.errorType ?? 'Error'}
                </p>
                <p className="mt-1">{test.errorMessage}</p>
              </div>
            )}
          </CardContent>
        </Card>

        <div className="space-y-4">
          <Card>
            <CardHeader>
              <CardTitle>Run summary</CardTitle>
            </CardHeader>
            <CardContent className="space-y-1 text-sm">
              <div className="flex justify-between gap-2">
                <span className="text-slate-500">Status</span>
                <Badge tone={executionTone(execution.status)}>{execution.status}</Badge>
              </div>
              <div className="flex justify-between gap-2">
                <span className="text-slate-500">Started</span>
                <span className="font-mono text-xs">{formatTime(execution.startedAt)}</span>
              </div>
              <div className="flex justify-between gap-2">
                <span className="text-slate-500">Completed</span>
                <span className="font-mono text-xs">{formatTime(execution.completedAt)}</span>
              </div>
              <div className="flex justify-between gap-2">
                <span className="text-slate-500">Trigger</span>
                <span>{execution.triggerType}</span>
              </div>
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle className="flex items-center gap-2">
                <ImageIcon className="h-4 w-4" aria-hidden />
                Artifacts
              </CardTitle>
            </CardHeader>
            <CardContent>
              {artifacts.isLoading && <Skeleton className="h-10" />}
              {artifacts.data && artifacts.data.length === 0 && (
                <p className="text-sm text-slate-400">No artifacts captured.</p>
              )}
              {artifacts.data && artifacts.data.length > 0 && (
                <ul className="space-y-2 text-sm">
                  {artifacts.data.map((artifact) => (
                    <li
                      key={artifact.id}
                      className="flex items-center justify-between gap-2 rounded-md border border-slate-200 px-3 py-2"
                    >
                      <span>
                        <span className="font-medium text-slate-900">
                          {artifact.fileName ?? artifact.artifactType}
                        </span>
                        {artifact.stepOrder !== null && artifact.stepOrder !== undefined && (
                          <span className="ml-2 font-mono text-xs text-slate-400">
                            step {artifact.stepOrder}
                          </span>
                        )}
                      </span>
                      <Button
                        variant="ghost"
                        size="sm"
                        disabled={download.isPending}
                        onClick={() => download.mutate(artifact.id)}
                      >
                        Open
                        <ExternalLink className="h-3 w-3" aria-hidden />
                      </Button>
                    </li>
                  ))}
                </ul>
              )}
              {download.error && (
                <p role="alert" className="mt-2 text-xs text-rose-600">
                  Artifact unavailable. It may have expired or been removed.
                </p>
              )}
            </CardContent>
          </Card>
        </div>
      </div>

      {analyzable && (
        <FailureAnalysisSection
          projectId={projectId}
          executionId={executionId}
          executionClassification={test.failureClassification ?? 'Unknown'}
          failedStepSummary={failedStepSummary}
          errorMessage={test.errorMessage}
          testKey={test.testKey}
          canAnalyze={canAnalyze}
          canCreateDefect={canCreateDefect}
          onDefectCreated={(defectId) => navigate(`/projects/${projectId}/bugs/${defectId}`)}
        />
      )}

      <Card className="overflow-hidden border-slate-900 bg-slate-950">
        <CardHeader className="flex flex-row items-center justify-between border-b border-slate-800">
          <CardTitle className="text-slate-200">Execution terminal</CardTitle>
          <div className="flex items-center gap-2">
            <Badge tone={executionTone(execution.status)}>{execution.status}</Badge>
            <label className="flex items-center gap-1 text-xs text-slate-400">
              <input
                type="checkbox"
                checked={follow}
                onChange={(e) => setFollow(e.target.checked)}
                aria-label="Follow live output"
              />
              Follow
            </label>
          </div>
        </CardHeader>
        <CardContent>
          {baseLogs.isLoading && (
            <p className="py-4 text-xs text-slate-400">Loading logs…</p>
          )}
          <div
            ref={logBoxRef}
            aria-live="polite"
            aria-label="Execution logs"
            className="terminal max-h-96 overflow-y-auto whitespace-pre-wrap font-mono text-xs leading-6 text-slate-300"
          >
            {logs.length === 0 && !baseLogs.isLoading && (
              <span className="text-slate-500">No log entries yet.</span>
            )}
            {logs.map((entry) => (
              <div key={entry.id}>
                <span className="text-slate-500">{formatTime(entry.timestamp)} </span>
                <span
                  className={
                    entry.level === 'error' || entry.level === 'Error'
                      ? 'text-rose-400'
                      : entry.level === 'warning' || entry.level === 'Warning'
                        ? 'text-amber-300'
                        : 'text-slate-400'
                  }
                >
                  [{entry.level}]
                </span>{' '}
                {entry.message}
              </div>
            ))}
          </div>
        </CardContent>
      </Card>
    </div>
  );
}
