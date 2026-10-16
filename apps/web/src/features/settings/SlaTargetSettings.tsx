import { useEffect, useState, type FormEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '../../components/ui/card';
import { Button } from '../../components/ui/button';
import { Skeleton } from '../../components/ui/skeleton';
import { ErrorState } from '../../components/common/ErrorState';
import { ApiError } from '../../lib/api/client';
import {
  slaTargetEndpoints,
  slaTargetErrorMessage,
  slaTargetKeys,
} from '../../lib/api/endpoints/slaTargets';
import { projectsEndpoints } from '../../lib/api/endpoints/projects';
import { useProfile } from '../../lib/auth/useProfile';
import { Permissions, hasPermission } from '../../lib/auth/permissions';
import { useAppStore } from '../../stores/useAppStore';

const SEVERITIES = [
  { key: 'criticalDays', label: 'Critical' },
  { key: 'highDays', label: 'High' },
  { key: 'mediumDays', label: 'Medium' },
  { key: 'lowDays', label: 'Low' },
] as const;

type SeverityKey = (typeof SEVERITIES)[number]['key'];

/**
 * SLA resolve-time target configuration (Phase 4 Slice 13).
 * Project-scoped; writes require settings.manage (backend-enforced).
 * Thresholds bound the maximum age in days of currently open or
 * in-progress defects — never historical resolution times.
 */
export function SlaTargetSettings() {
  const profile = useProfile();
  const queryClient = useQueryClient();
  const currentProjectId = useAppStore((s) => s.currentProjectId);
  const setCurrentProjectId = useAppStore((s) => s.setCurrentProjectId);

  const canConfigure = hasPermission(profile.data?.permissions, Permissions.SettingsManage);
  const canRead = hasPermission(profile.data?.permissions, Permissions.ReportsRead);

  const [projectId, setProjectId] = useState<string | null>(currentProjectId);
  const [days, setDays] = useState<Record<SeverityKey, string>>({
    criticalDays: '',
    highDays: '',
    mediumDays: '',
    lowDays: '',
  });
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

  const targets = useQuery({
    queryKey: projectId ? slaTargetKeys.detail(projectId) : ['sla-targets', 'detail', 'none'],
    queryFn: () => slaTargetEndpoints.get(projectId!),
    enabled: !!projectId && canRead,
    retry: false,
  });

  useEffect(() => {
    const savedTargets = targets.data?.targets;
    if (savedTargets) {
      setDays({
        criticalDays: String(savedTargets.criticalDays),
        highDays: String(savedTargets.highDays),
        mediumDays: String(savedTargets.mediumDays),
        lowDays: String(savedTargets.lowDays),
      });
    }
  }, [targets.data]);

  const invalidate = () => {
    if (!projectId) return;
    void queryClient.invalidateQueries({ queryKey: slaTargetKeys.detail(projectId) });
  };

  const save = useMutation({
    mutationFn: () =>
      slaTargetEndpoints.upsert(projectId!, {
        criticalDays: Number(days.criticalDays),
        highDays: Number(days.highDays),
        mediumDays: Number(days.mediumDays),
        lowDays: Number(days.lowDays),
      }),
    onSuccess: () => {
      setFormError(null);
      setSaved('SLA targets saved.');
      invalidate();
    },
    onError: (error: ApiError) => {
      setSaved(null);
      setFormError(slaTargetErrorMessage(error.status, error.code));
    },
  });

  const submit = (e: FormEvent) => {
    e.preventDefault();
    setSaved(null);
    for (const { key, label } of SEVERITIES) {
      const raw = days[key].trim();
      if (raw === '') {
        setFormError(`Enter a threshold in days for ${label} severity (1–365).`);
        return;
      }
      const parsed = Number(raw);
      if (!Number.isInteger(parsed) || parsed < 1 || parsed > 365) {
        setFormError(`${label} threshold must be a whole number of days from 1 to 365.`);
        return;
      }
    }
    save.mutate();
  };

  const selectProject = (id: string) => {
    setProjectId(id || null);
    setCurrentProjectId(id || null);
    setFormError(null);
    setSaved(null);
  };

  if (!canRead && !profile.isLoading) {
    return (
      <Card>
        <CardHeader>
          <CardTitle>SLA targets</CardTitle>
        </CardHeader>
        <CardContent>
          <p className="text-sm text-slate-600">
            You do not have permission to view SLA targets for this project.
          </p>
        </CardContent>
      </Card>
    );
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>SLA targets</CardTitle>
        <CardDescription>
          Maximum age in days for currently open or in-progress defects, per severity.
          Defects older than their threshold count as breached. This measures current
          open-defect age only — never historical resolution times.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        <div>
          <label htmlFor="sla-project" className="mb-1 block text-sm font-medium text-slate-700">
            Project
          </label>
          <select
            id="sla-project"
            value={projectId ?? ''}
            onChange={(e) => selectProject(e.target.value)}
            className="w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900"
          >
            <option value="">Select a project</option>
            {(projects.data?.items ?? []).map((p) => (
              <option key={p.id} value={p.id}>
                {p.name}
              </option>
            ))}
          </select>
        </div>

        {!projectId ? (
          <p className="text-sm text-slate-500">Select a project to configure SLA targets.</p>
        ) : targets.isLoading ? (
          <div aria-label="Loading SLA targets" className="space-y-2">
            <Skeleton className="h-6 w-48" />
            <Skeleton className="h-24" />
          </div>
        ) : targets.isError ? (
          <ErrorState error={targets.error} onRetry={() => void targets.refetch()} />
        ) : (
          <>
            {!targets.data?.targets && (
              <p className="text-sm text-slate-500">
                No targets configured — breach reporting is unavailable until thresholds are saved.
              </p>
            )}
            {saved && (
              <div role="status" className="rounded-md border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-700">
                {saved}
              </div>
            )}
            {formError && (
              <div role="alert" className="rounded-md border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
                {formError}
              </div>
            )}

            {!canConfigure ? (
              <p className="text-sm text-slate-600">
                You do not have permission to change SLA targets for this project.
              </p>
            ) : (
              <form onSubmit={submit} className="space-y-4" noValidate>
                <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
                  {SEVERITIES.map(({ key, label }) => (
                    <div key={key}>
                      <label htmlFor={`sla-${key}`} className="mb-1 block text-sm font-medium text-slate-700">
                        {label} (days)
                      </label>
                      <input
                        id={`sla-${key}`}
                        value={days[key]}
                        onChange={(e) => setDays((d) => ({ ...d, [key]: e.target.value }))}
                        placeholder="e.g. 7"
                        inputMode="numeric"
                        className="w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900"
                      />
                    </div>
                  ))}
                </div>
                <div className="flex justify-end">
                  <Button type="submit" size="sm" disabled={save.isPending}>
                    {save.isPending ? 'Saving…' : 'Save targets'}
                  </Button>
                </div>
              </form>
            )}
          </>
        )}
      </CardContent>
    </Card>
  );
}
