import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { FlaskConical, Play } from 'lucide-react';
import { Button } from '../../components/ui/button';
import { Input } from '../../components/ui/input';
import { Badge } from '../../components/ui/badge';
import { Skeleton } from '../../components/ui/skeleton';
import { Card, CardContent } from '../../components/ui/card';
import { ErrorState } from '../../components/common/ErrorState';
import { executionEndpoints, executionKeys } from '../../lib/api/endpoints/executions';
import { projectsEndpoints, projectKeys } from '../../lib/api/endpoints/projects';
import { useProfile } from '../../lib/auth/useProfile';
import { Permissions, hasPermission } from '../../lib/auth/permissions';

const PAGE_SIZE = 25;
const STATUS_OPTIONS = ['', 'Queued', 'Running', 'Passed', 'Failed', 'Cancelled', 'TimedOut', 'Error'];

export function executionTone(status: string): 'success' | 'danger' | 'warning' | 'info' | 'neutral' {
  switch (status) {
    case 'Passed':
      return 'success';
    case 'Failed':
    case 'Error':
      return 'danger';
    case 'Cancelled':
    case 'TimedOut':
      return 'warning';
    case 'Running':
      return 'info';
    default:
      return 'neutral';
  }
}

function formatDuration(ms: number | null): string {
  if (ms === null || ms === undefined) return '—';
  if (ms < 1000) return `${ms} ms`;
  return `${(ms / 1000).toFixed(1)} s`;
}

function formatTime(iso: string | null): string {
  if (!iso) return '—';
  try {
    return new Date(iso).toLocaleString();
  } catch {
    return iso;
  }
}

/** Project-scoped execution history (docs/02 §11). */
export function ExecutionListPage() {
  const { projectId = '' } = useParams();
  const profile = useProfile();
  const canExecute = hasPermission(profile.data?.permissions, Permissions.ExecutionsExecute);

  const [status, setStatus] = useState('');
  const [testCaseId, setTestCaseId] = useState('');
  const [committed, setCommitted] = useState({ status: '', testCaseId: '' });
  const [page, setPage] = useState(1);

  const project = useQuery({
    queryKey: projectKeys.details(projectId),
    queryFn: () => projectsEndpoints.get(projectId),
    enabled: !!projectId,
    retry: false,
    staleTime: 60_000,
  });

  const executions = useQuery({
    queryKey: executionKeys.list(projectId, committed, page),
    queryFn: () => executionEndpoints.list(projectId, committed, page, PAGE_SIZE),
    enabled: !!projectId,
    staleTime: 10_000,
  });

  const totalPages = executions.data
    ? Math.max(1, Math.ceil(executions.data.totalCount / executions.data.pageSize))
    : 1;

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <Link to={`/projects/${projectId}`} className="text-sm text-brand-700 hover:text-brand-600">
            ← {project.data?.name ?? 'Back to project'}
          </Link>
          <h1 className="mt-2 text-xl font-semibold text-slate-900">Test Executions</h1>
          <p className="mt-1 text-sm text-slate-500">
            History of Playwright runs. Each execution pins one exact approved test version.
          </p>
        </div>
      </div>

      <form
        className="flex flex-wrap items-end gap-2"
        onSubmit={(e) => {
          e.preventDefault();
          setPage(1);
          setCommitted({ status, testCaseId });
        }}
      >
        <label className="flex min-w-36 flex-col gap-1 text-xs font-medium text-slate-500">
          Status
          <select
            aria-label="Status filter"
            value={status}
            onChange={(e) => {
              setStatus(e.target.value);
              setPage(1);
              setCommitted((c) => ({ ...c, status: e.target.value }));
            }}
            className="rounded-md border border-slate-300 bg-white px-2 py-2 text-sm font-normal text-slate-900"
          >
            {STATUS_OPTIONS.map((option) => (
              <option key={option || 'all'} value={option}>
                {option || 'All'}
              </option>
            ))}
          </select>
        </label>
        <label className="flex min-w-48 flex-col gap-1 text-xs font-medium text-slate-500">
          Test case ID
          <Input
            aria-label="Test case filter"
            value={testCaseId}
            onChange={(e) => setTestCaseId(e.target.value)}
            placeholder="Filter by test case…"
          />
        </label>
        <Button type="submit" variant="secondary">
          Search
        </Button>
      </form>

      {executions.isLoading && (
        <div className="space-y-2" aria-label="Loading executions">
          {[0, 1, 2, 3].map((i) => (
            <Skeleton key={i} className="h-14" />
          ))}
        </div>
      )}

      {executions.isError && <ErrorState error={executions.error} onRetry={() => void executions.refetch()} />}

      {executions.data && executions.data.items.length === 0 && (
        <Card>
          <CardContent className="flex flex-col items-center gap-3 py-12 text-center">
            <span className="flex h-11 w-11 items-center justify-center rounded-full bg-slate-100">
              <FlaskConical className="h-5 w-5 text-slate-500" aria-hidden />
            </span>
            <h2 className="text-base font-semibold text-slate-900">No executions yet</h2>
            <p className="max-w-md text-sm text-slate-500">
              {committed.status || committed.testCaseId
                ? 'No executions match the current filters.'
                : 'Start a run from an approved test version to see execution history here.'}
            </p>
            {!canExecute && (
              <p className="text-xs text-amber-700">
                Starting executions requires the executions.execute permission.
              </p>
            )}
          </CardContent>
        </Card>
      )}

      {executions.data && executions.data.items.length > 0 && (
        <>
          <div className="overflow-x-auto rounded-lg border border-slate-200 bg-white shadow-sm">
            <table className="w-full min-w-3xl text-left text-sm">
              <thead>
                <tr className="border-b border-slate-200 bg-slate-50 text-xs uppercase tracking-wider text-slate-500">
                  <th scope="col" className="px-4 py-3 font-medium">Status</th>
                  <th scope="col" className="px-4 py-3 font-medium">Test</th>
                  <th scope="col" className="px-4 py-3 font-medium">Version</th>
                  <th scope="col" className="px-4 py-3 font-medium">Browser</th>
                  <th scope="col" className="px-4 py-3 font-medium">Duration</th>
                  <th scope="col" className="px-4 py-3 font-medium">Started</th>
                  <th scope="col" className="px-4 py-3 font-medium"><span className="sr-only">Actions</span></th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {executions.data.items.map((execution) => (
                  <tr key={execution.id} className="hover:bg-slate-50">
                    <td className="whitespace-nowrap px-4 py-3">
                      <Badge tone={executionTone(execution.status)}>{execution.status}</Badge>
                    </td>
                    <td className="max-w-xs px-4 py-3">
                      <span className="mr-2 font-mono text-xs text-slate-500">{execution.testKey}</span>
                      <span className="font-medium text-slate-900">{execution.testTitle}</span>
                    </td>
                    <td className="whitespace-nowrap px-4 py-3 font-mono text-xs text-slate-500">
                      v{execution.testCaseVersionNumber}
                    </td>
                    <td className="whitespace-nowrap px-4 py-3 text-slate-500">
                      {execution.browser ?? '—'}
                    </td>
                    <td className="whitespace-nowrap px-4 py-3 font-mono text-xs text-slate-500">
                      {formatDuration(execution.durationMs)}
                    </td>
                    <td className="whitespace-nowrap px-4 py-3 text-xs text-slate-500">
                      {formatTime(execution.startedAt)}
                    </td>
                    <td className="whitespace-nowrap px-4 py-3 text-right">
                      <Link
                        to={`/projects/${projectId}/executions/${execution.id}`}
                        className="inline-flex items-center gap-1 text-sm font-medium text-brand-700 hover:text-brand-600"
                      >
                        <Play className="h-3 w-3" aria-hidden />
                        View
                      </Link>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div className="flex items-center justify-between text-sm text-slate-500">
            <p>
              Showing {executions.data.items.length} of {executions.data.totalCount} executions
            </p>
            <div className="flex gap-2">
              <Button
                variant="secondary"
                size="sm"
                disabled={page <= 1}
                onClick={() => setPage((p) => Math.max(1, p - 1))}
              >
                Previous
              </Button>
              <span className="px-2 py-1.5 font-mono text-xs">
                {page} / {totalPages}
              </span>
              <Button
                variant="secondary"
                size="sm"
                disabled={page >= totalPages}
                onClick={() => setPage((p) => p + 1)}
              >
                Next
              </Button>
            </div>
          </div>
        </>
      )}
    </div>
  );
}
