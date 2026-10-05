import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '../../components/ui/card';
import { Button } from '../../components/ui/button';
import { Input } from '../../components/ui/input';
import { Skeleton } from '../../components/ui/skeleton';
import { ErrorState } from '../../components/common/ErrorState';
import { ApiError, api } from '../../lib/api/client';
import {
  reportEndpoints,
  reportKeys,
  type AuditEventFilters,
} from '../../lib/api/endpoints/reports';
import { useProfile } from '../../lib/auth/useProfile';
import { Permissions, hasPermission } from '../../lib/auth/permissions';

const PAGE_SIZE = 25;

function formatTime(iso: string | null | undefined): string {
  if (!iso) return '—';
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? iso : date.toLocaleString();
}

/**
 * Project-scoped Audit Explorer (Phase 4 Slice 2). All filtering and
 * pagination happen server-side; the CSV export reuses the same filters
 * with a deterministic CreatedAt/Id order (max 5000 rows, safe fields only).
 * Action strings render as returned by the backend — unknown future actions
 * display safely without a frontend taxonomy.
 */
export function AuditExplorerPage() {
  const { projectId = '' } = useParams();
  const profile = useProfile();
  const queryClient = useQueryClient();
  const canRead = hasPermission(profile.data?.permissions, Permissions.ReportsRead);

  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [action, setAction] = useState('');
  const [actor, setActor] = useState('');
  const [entityType, setEntityType] = useState('');
  const [page, setPage] = useState(1);
  const [exportError, setExportError] = useState<string | null>(null);

  const filters: AuditEventFilters = {
    ...(from ? { from } : {}),
    ...(to ? { to } : {}),
    ...(action.trim() ? { action: action.trim() } : {}),
    ...(actor.trim() ? { actor: actor.trim() } : {}),
    ...(entityType.trim() ? { entityType: entityType.trim() } : {}),
  };

  const enabled = !!projectId && canRead;
  const events = useQuery({
    queryKey: projectId ? reportKeys.audit(projectId, filters, page) : ['reports', 'audit', 'none'],
    queryFn: () => reportEndpoints.audit(projectId, filters, page, PAGE_SIZE),
    enabled,
    retry: false,
  });

  const download = useMutation({
    mutationFn: async () => {
      const { blob, fileName } = await api.download(reportEndpoints.auditExportUrl(projectId, filters));
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = fileName;
      document.body.appendChild(anchor);
      anchor.click();
      anchor.remove();
      URL.revokeObjectURL(url);
    },
    onSuccess: () => setExportError(null),
    onError: (error: ApiError) => {
      setExportError(
        error.status === 403
          ? 'You do not have permission to export audit events.'
          : 'Export failed. Please try again.',
      );
    },
  });

  const resetPage = () => setPage(1);
  const invalidate = () =>
    void queryClient.invalidateQueries({ queryKey: reportKeys.audit(projectId, filters, page) });

  if (!canRead && !profile.isLoading) {
    return (
      <div className="space-y-6">
        <h1 className="text-xl font-semibold text-slate-900">Audit Explorer</h1>
        <ErrorState error={new ApiError(403, 'FORBIDDEN', 'You do not have access to reports.')} />
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <div>
        <Link to={`/projects/${projectId}`} className="text-sm text-brand-700 hover:text-brand-600">
          ← Back to project
        </Link>
        <h1 className="mt-2 text-xl font-semibold text-slate-900">Audit Explorer</h1>
        <p className="mt-1 text-sm text-slate-500">
          Review project audit activity and administrative changes.
        </p>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Project audit events</CardTitle>
          <CardDescription>
            Filtered administrative history for this project. Dates are UTC; ranges are limited
            to 365 days. Exports contain timestamps, actions, entity references, and actors only.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 xl:grid-cols-5">
            <div>
              <label htmlFor="audit-from" className="mb-1 block text-sm font-medium text-slate-700">
                From
              </label>
              <Input
                id="audit-from"
                type="date"
                value={from}
                onChange={(e) => {
                  setFrom(e.target.value);
                  resetPage();
                }}
              />
            </div>
            <div>
              <label htmlFor="audit-to" className="mb-1 block text-sm font-medium text-slate-700">
                To
              </label>
              <Input
                id="audit-to"
                type="date"
                value={to}
                onChange={(e) => {
                  setTo(e.target.value);
                  resetPage();
                }}
              />
            </div>
            <div>
              <label htmlFor="audit-action" className="mb-1 block text-sm font-medium text-slate-700">
                Action
              </label>
              <Input
                id="audit-action"
                value={action}
                onChange={(e) => {
                  setAction(e.target.value);
                  resetPage();
                }}
                placeholder="e.g. defect.created"
              />
            </div>
            <div>
              <label htmlFor="audit-entity" className="mb-1 block text-sm font-medium text-slate-700">
                Entity type
              </label>
              <Input
                id="audit-entity"
                value={entityType}
                onChange={(e) => {
                  setEntityType(e.target.value);
                  resetPage();
                }}
                placeholder="e.g. defect"
              />
            </div>
            <div>
              <label htmlFor="audit-actor" className="mb-1 block text-sm font-medium text-slate-700">
                Actor ID
              </label>
              <Input
                id="audit-actor"
                value={actor}
                onChange={(e) => {
                  setActor(e.target.value);
                  resetPage();
                }}
                placeholder="User ID"
              />
            </div>
          </div>

          <div className="flex items-center justify-end">
            <Button
              variant="secondary"
              size="sm"
              disabled={download.isPending}
              onClick={() => download.mutate()}
            >
              {download.isPending ? 'Exporting…' : 'Export CSV'}
            </Button>
          </div>

          {exportError && (
            <p role="alert" className="text-xs text-rose-600">
              {exportError}
            </p>
          )}

          {events.isLoading ? (
            <div aria-label="Loading audit events" className="space-y-2">
              <Skeleton className="h-10" />
              <Skeleton className="h-10" />
              <Skeleton className="h-10" />
            </div>
          ) : events.isError || !events.data ? (
            <ErrorState error={events.error} onRetry={() => invalidate()} />
          ) : events.data.totalCount === 0 ? (
            <p className="text-sm text-slate-500">No audit events found for the selected filters.</p>
          ) : (
            <>
              <div className="overflow-x-auto">
                <table className="w-full text-left text-sm">
                  <thead>
                    <tr className="border-b border-slate-200 text-xs text-slate-500">
                      <th scope="col" className="py-2 pr-3 font-medium">Timestamp</th>
                      <th scope="col" className="py-2 pr-3 font-medium">Action</th>
                      <th scope="col" className="py-2 pr-3 font-medium">Entity type</th>
                      <th scope="col" className="py-2 pr-3 font-medium">Entity ID</th>
                      <th scope="col" className="py-2 font-medium">Actor</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-slate-100">
                    {events.data.items.map((row) => (
                      <tr key={row.id}>
                        <td className="py-2 pr-3 font-mono text-xs text-slate-600">
                          {formatTime(row.timestamp)}
                        </td>
                        <td className="py-2 pr-3 font-mono text-xs text-slate-900">{row.action}</td>
                        <td className="py-2 pr-3 text-xs text-slate-600">{row.entityType}</td>
                        <td className="py-2 pr-3 font-mono text-xs text-slate-500">
                          {row.entityId ?? '—'}
                        </td>
                        <td className="py-2 font-mono text-xs text-slate-500">
                          {row.actorUserId ?? '—'}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              <div className="mt-3 flex items-center justify-between gap-2">
                <p className="text-xs text-slate-500" aria-live="polite">
                  Page {events.data.page} of{' '}
                  {Math.max(1, Math.ceil(events.data.totalCount / events.data.pageSize))} ·{' '}
                  {events.data.totalCount} total
                </p>
                <div className="flex gap-2">
                  <Button
                    variant="secondary"
                    size="sm"
                    disabled={page <= 1}
                    onClick={() => setPage(page - 1)}
                  >
                    Previous
                  </Button>
                  <Button
                    variant="secondary"
                    size="sm"
                    disabled={page >= Math.ceil(events.data.totalCount / events.data.pageSize)}
                    onClick={() => setPage(page + 1)}
                  >
                    Next
                  </Button>
                </div>
              </div>
            </>
          )}
        </CardContent>
      </Card>
    </div>
  );
}
