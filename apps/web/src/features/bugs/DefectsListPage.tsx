import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { Bug } from 'lucide-react';
import { Button } from '../../components/ui/button';
import { Input } from '../../components/ui/input';
import { Badge } from '../../components/ui/badge';
import { Skeleton } from '../../components/ui/skeleton';
import { Card, CardContent } from '../../components/ui/card';
import { ErrorState } from '../../components/common/ErrorState';
import { defectEndpoints, defectKeys } from '../../lib/api/endpoints/defects';
import { projectsEndpoints, projectKeys } from '../../lib/api/endpoints/projects';
import { useProfile } from '../../lib/auth/useProfile';
import { Permissions, hasPermission } from '../../lib/auth/permissions';

const PAGE_SIZE = 25;
const STATUS_OPTIONS = ['', 'Open', 'InProgress', 'Resolved', 'Closed', 'Rejected'];
const SEVERITY_OPTIONS = ['', 'Critical', 'High', 'Medium', 'Low'];

export function severityTone(severity: string): 'danger' | 'warning' | 'info' | 'neutral' {
  switch (severity) {
    case 'Critical':
      return 'danger';
    case 'High':
      return 'warning';
    case 'Medium':
      return 'info';
    default:
      return 'neutral';
  }
}

export function defectStatusTone(status: string): 'success' | 'danger' | 'warning' | 'info' | 'neutral' {
  switch (status) {
    case 'Open':
      return 'danger';
    case 'InProgress':
      return 'warning';
    case 'Resolved':
      return 'success';
    case 'Closed':
      return 'neutral';
    case 'Rejected':
      return 'info';
    default:
      return 'neutral';
  }
}

function formatTime(iso: string | null): string {
  if (!iso) return '—';
  try {
    return new Date(iso).toLocaleString();
  } catch {
    return iso;
  }
}

/** Project-scoped defect list (docs/02 §12). */
export function DefectsListPage() {
  const { projectId = '' } = useParams();
  const profile = useProfile();
  const canManage = hasPermission(profile.data?.permissions, Permissions.BugsManage);

  const [status, setStatus] = useState('');
  const [severity, setSeverity] = useState('');
  const [search, setSearch] = useState('');
  const [committed, setCommitted] = useState({ status: '', severity: '', search: '' });
  const [page, setPage] = useState(1);

  const project = useQuery({
    queryKey: projectKeys.details(projectId),
    queryFn: () => projectsEndpoints.get(projectId),
    enabled: !!projectId,
    retry: false,
    staleTime: 60_000,
  });

  const defects = useQuery({
    queryKey: defectKeys.list(projectId, committed, page),
    queryFn: () => defectEndpoints.list(projectId, committed, page, PAGE_SIZE),
    enabled: !!projectId,
    staleTime: 10_000,
  });

  const totalPages = defects.data
    ? Math.max(1, Math.ceil(defects.data.totalCount / defects.data.pageSize))
    : 1;

  const applySelect = (setter: (value: string) => void) => (value: string) => {
    setter(value);
    setPage(1);
  };

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <Link to={`/projects/${projectId}`} className="text-sm text-brand-700 hover:text-brand-600">
            ← {project.data?.name ?? 'Back to project'}
          </Link>
          <h1 className="mt-2 text-xl font-semibold text-slate-900">Bugs</h1>
          <p className="mt-1 text-sm text-slate-500">
            Human-filed defects from failed executions. AI analysis is advisory only.
          </p>
        </div>
      </div>

      <form
        className="flex flex-wrap items-end gap-2"
        onSubmit={(e) => {
          e.preventDefault();
          setPage(1);
          setCommitted({ status, severity, search });
        }}
      >
        <label className="flex min-w-36 flex-col gap-1 text-xs font-medium text-slate-500">
          Status
          <select
            aria-label="Status filter"
            value={status}
            onChange={(e) => {
              applySelect(setStatus)(e.target.value);
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
        <label className="flex min-w-36 flex-col gap-1 text-xs font-medium text-slate-500">
          Severity
          <select
            aria-label="Severity filter"
            value={severity}
            onChange={(e) => {
              applySelect(setSeverity)(e.target.value);
              setCommitted((c) => ({ ...c, severity: e.target.value }));
            }}
            className="rounded-md border border-slate-300 bg-white px-2 py-2 text-sm font-normal text-slate-900"
          >
            {SEVERITY_OPTIONS.map((option) => (
              <option key={option || 'all'} value={option}>
                {option || 'All'}
              </option>
            ))}
          </select>
        </label>
        <label className="flex min-w-48 flex-1 flex-col gap-1 text-xs font-medium text-slate-500 sm:flex-none sm:basis-64">
          Search
          <span className="flex gap-2">
            <Input
              aria-label="Search defects"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Title or description…"
            />
            <Button type="submit" variant="secondary">
              Search
            </Button>
          </span>
        </label>
      </form>

      {defects.isLoading && (
        <div className="space-y-2" aria-label="Loading defects">
          {[0, 1, 2, 3].map((i) => (
            <Skeleton key={i} className="h-14" />
          ))}
        </div>
      )}

      {defects.isError && <ErrorState error={defects.error} onRetry={() => void defects.refetch()} />}

      {defects.data && defects.data.items.length === 0 && (
        <Card>
          <CardContent className="flex flex-col items-center gap-3 py-12 text-center">
            <span className="flex h-11 w-11 items-center justify-center rounded-full bg-slate-100">
              <Bug className="h-5 w-5 text-slate-500" aria-hidden />
            </span>
            <h2 className="text-base font-semibold text-slate-900">No defects found</h2>
            <p className="max-w-md text-sm text-slate-500">
              {committed.status || committed.severity || committed.search
                ? 'No defects match the current filters.'
                : 'File a defect from a failed execution to track it here.'}
            </p>
            {!canManage && (
              <p className="text-xs text-amber-700">
                Filing defects requires the bugs.manage permission.
              </p>
            )}
          </CardContent>
        </Card>
      )}

      {defects.data && defects.data.items.length > 0 && (
        <>
          <div className="overflow-x-auto rounded-lg border border-slate-200 bg-white shadow-sm">
            <table className="w-full min-w-3xl text-left text-sm">
              <thead>
                <tr className="border-b border-slate-200 bg-slate-50 text-xs uppercase tracking-wider text-slate-500">
                  <th scope="col" className="px-4 py-3 font-medium">Severity</th>
                  <th scope="col" className="px-4 py-3 font-medium">Title</th>
                  <th scope="col" className="px-4 py-3 font-medium">Status</th>
                  <th scope="col" className="px-4 py-3 font-medium">Classification</th>
                  <th scope="col" className="px-4 py-3 font-medium">Test</th>
                  <th scope="col" className="px-4 py-3 font-medium">Created</th>
                  <th scope="col" className="px-4 py-3 font-medium"><span className="sr-only">Actions</span></th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {defects.data.items.map((defect) => (
                  <tr key={defect.id} className="hover:bg-slate-50">
                    <td className="whitespace-nowrap px-4 py-3">
                      <Badge tone={severityTone(defect.severity)}>{defect.severity}</Badge>
                    </td>
                    <td className="max-w-xs truncate px-4 py-3 font-medium text-slate-900">
                      {defect.title}
                    </td>
                    <td className="whitespace-nowrap px-4 py-3">
                      <Badge tone={defectStatusTone(defect.status)}>{defect.status}</Badge>
                    </td>
                    <td className="whitespace-nowrap px-4 py-3 font-mono text-xs text-slate-500">
                      {defect.failureClassification ?? '—'}
                    </td>
                    <td className="whitespace-nowrap px-4 py-3 font-mono text-xs text-slate-500">
                      {defect.testKey ?? '—'}
                    </td>
                    <td className="whitespace-nowrap px-4 py-3 text-xs text-slate-500">
                      {formatTime(defect.createdAt)}
                    </td>
                    <td className="whitespace-nowrap px-4 py-3 text-right">
                      <Link
                        to={`/projects/${projectId}/bugs/${defect.id}`}
                        className="text-sm font-medium text-brand-700 hover:text-brand-600"
                      >
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
              Showing {defects.data.items.length} of {defects.data.totalCount} defects
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
