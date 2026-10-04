import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '../../components/ui/card';
import { Badge } from '../../components/ui/badge';
import { Button } from '../../components/ui/button';
import { Skeleton } from '../../components/ui/skeleton';
import { ErrorState } from '../../components/common/ErrorState';
import { ApiError } from '../../lib/api/client';
import {
  visualBaselineEndpoints,
  visualBaselineErrorMessage,
  visualBaselineKeys,
  type VisualBaseline,
} from '../../lib/api/endpoints/visualBaselines';
import { projectsEndpoints } from '../../lib/api/endpoints/projects';
import { useProfile } from '../../lib/auth/useProfile';
import { Permissions, hasPermission } from '../../lib/auth/permissions';
import { useAppStore } from '../../stores/useAppStore';

const inputClass =
  'w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900';
const labelClass = 'mb-1 block text-sm font-medium text-slate-700';

function statusTone(status: string): 'success' | 'danger' | 'warning' | 'neutral' {
  const normalized = status.toLowerCase();
  if (normalized === 'active') return 'success';
  if (normalized === 'candidate') return 'warning';
  if (normalized === 'superseded') return 'neutral';
  return 'neutral';
}

function formatTime(iso: string | null): string {
  if (!iso) return '—';
  try {
    return new Date(iso).toLocaleString();
  } catch {
    return iso;
  }
}

/**
 * Visual baseline review (Phase 3 Slice 3C-4D-1). Lists candidate and
 * approved reference images for explicit approval or rejection. Baselines
 * never execute anything: comparison lives in a later slice, and the
 * review image loads through a short-lived presigned URL that is never
 * logged or persisted by this UI.
 */
export function VisualBaselines() {
  const profile = useProfile();
  const queryClient = useQueryClient();
  const currentProjectId = useAppStore((s) => s.currentProjectId);

  const canConfigure = hasPermission(profile.data?.permissions, Permissions.SettingsManage);
  const canRead = hasPermission(profile.data?.permissions, Permissions.ExecutionsRead);

  const [projectId, setProjectId] = useState<string | null>(currentProjectId);
  const [statusFilter, setStatusFilter] = useState('');
  const [previewUrl, setPreviewUrl] = useState<string | null>(null);
  const [formError, setFormError] = useState<string | null>(null);
  const [saved, setSaved] = useState<string | null>(null);

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

  const baselines = useQuery({
    queryKey: projectId ? visualBaselineKeys.list(projectId, undefined, statusFilter || undefined) : [...visualBaselineKeys.all, 'none'],
    queryFn: () => visualBaselineEndpoints.list(projectId!, undefined, statusFilter || undefined),
    enabled: !!projectId && canRead,
    retry: false,
  });

  const invalidate = () => {
    if (projectId) void queryClient.invalidateQueries({ queryKey: visualBaselineKeys.list(projectId) });
  };

  const approve = useMutation({
    mutationFn: (baseline: VisualBaseline) => visualBaselineEndpoints.approve(projectId!, baseline.id),
    onSuccess: () => {
      setSaved('Baseline approved.');
      setFormError(null);
      invalidate();
    },
    onError: (error: unknown) => {
      setSaved(null);
      setFormError(
        error instanceof ApiError
          ? visualBaselineErrorMessage(error.status, error.code)
          : 'Approving the baseline failed. Please try again.',
      );
    },
  });

  const reject = useMutation({
    mutationFn: (baseline: VisualBaseline) => visualBaselineEndpoints.reject(projectId!, baseline.id),
    onSuccess: () => {
      setSaved('Candidate rejected.');
      setFormError(null);
      invalidate();
    },
    onError: (error: unknown) => {
      setSaved(null);
      setFormError(
        error instanceof ApiError
          ? visualBaselineErrorMessage(error.status, error.code)
          : 'Rejecting the baseline failed. Please try again.',
      );
    },
  });

  const preview = async (baseline: VisualBaseline) => {
    setFormError(null);
    try {
      const download = await visualBaselineEndpoints.download(projectId!, baseline.id);
      setPreviewUrl(download.downloadUrl);
    } catch (error: unknown) {
      setPreviewUrl(null);
      setFormError(
        error instanceof ApiError
          ? visualBaselineErrorMessage(error.status, error.code)
          : 'Loading the baseline image failed. Please try again.',
      );
    }
  };

  if (!canRead) {
    return (
      <Card>
        <CardHeader>
          <CardTitle>Visual baselines</CardTitle>
          <CardDescription>Reference screenshots for visual checks.</CardDescription>
        </CardHeader>
        <CardContent>
          <p className="text-sm text-slate-500">
            You do not have permission to view visual baselines for this project.
          </p>
        </CardContent>
      </Card>
    );
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Visual baselines</CardTitle>
        <CardDescription>
          Candidates await explicit approval; approving a candidate supersedes the previous active baseline.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        <div className="grid gap-3 sm:grid-cols-2">
          <div>
            <label className={labelClass} htmlFor="visual-baseline-project">Project</label>
            <select
              id="visual-baseline-project"
              className={inputClass}
              value={projectId ?? ''}
              onChange={(e) => {
                setProjectId(e.target.value || null);
                setPreviewUrl(null);
              }}
            >
              {(projects.data?.items ?? []).map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name}
                </option>
              ))}
            </select>
          </div>
          <div>
            <label className={labelClass} htmlFor="visual-baseline-status">Status</label>
            <select
              id="visual-baseline-status"
              className={inputClass}
              value={statusFilter}
              onChange={(e) => setStatusFilter(e.target.value)}
            >
              <option value="">All statuses</option>
              <option value="Candidate">Candidate</option>
              <option value="Active">Active</option>
              <option value="Superseded">Superseded</option>
            </select>
          </div>
        </div>

        {formError && (
          <p role="alert" className="text-sm text-red-600">
            {formError}
          </p>
        )}
        {saved && <p className="text-sm text-green-700">{saved}</p>}

        {baselines.isLoading && <Skeleton className="h-10" />}
        {baselines.error && <ErrorState error={baselines.error} onRetry={() => baselines.refetch()} />}
        {baselines.data && baselines.data.length === 0 && (
          <p className="text-sm text-slate-400">No visual baselines found.</p>
        )}
        {baselines.data && baselines.data.length > 0 && (
          <ul className="divide-y divide-slate-200 rounded-md border border-slate-200">
            {baselines.data.map((baseline) => (
              <li key={baseline.id} className="flex flex-wrap items-center gap-3 px-4 py-3">
                <div className="min-w-0 flex-1">
                  <p className="truncate text-sm font-medium text-slate-900">
                    Step {baseline.stepOrder} <Badge tone={statusTone(baseline.status)}>{baseline.status}</Badge>
                  </p>
                  <p className="truncate text-xs text-slate-500">
                    {baseline.width}×{baseline.height} · approved{' '}
                    {formatTime(baseline.approvedAt)} · updated {formatTime(baseline.updatedAt)}
                  </p>
                </div>
                <Button variant="secondary" size="sm" onClick={() => preview(baseline)}>
                  Review
                </Button>
                {canConfigure && baseline.status === 'Candidate' && (
                  <>
                    <Button size="sm" onClick={() => approve.mutate(baseline)} disabled={approve.isPending}>
                      Approve
                    </Button>
                    <Button
                      variant="secondary"
                      size="sm"
                      onClick={() => reject.mutate(baseline)}
                      disabled={reject.isPending}
                    >
                      Reject
                    </Button>
                  </>
                )}
              </li>
            ))}
          </ul>
        )}

        {previewUrl && (
          <div className="rounded-md border border-slate-200 p-3">
            <p className="mb-2 text-xs text-slate-500">
              Reference image preview. The image loads through a short-lived review URL.
            </p>
            <img src={previewUrl} alt="Visual baseline reference" className="max-h-96 rounded border" />
            <p className="mt-2">
              <Button variant="secondary" size="sm" onClick={() => setPreviewUrl(null)}>
                Close preview
              </Button>
            </p>
          </div>
        )}
      </CardContent>
    </Card>
  );
}
