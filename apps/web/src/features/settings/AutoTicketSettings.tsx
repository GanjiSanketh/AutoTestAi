import { useEffect, useState, type FormEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '../../components/ui/card';
import { Badge } from '../../components/ui/badge';
import { Button } from '../../components/ui/button';
import { Skeleton } from '../../components/ui/skeleton';
import { ErrorState } from '../../components/common/ErrorState';
import { ApiError } from '../../lib/api/client';
import {
  autoTicketEndpoints,
  autoTicketErrorMessage,
  autoTicketKeys,
} from '../../lib/api/endpoints/autoTickets';
import { projectsEndpoints } from '../../lib/api/endpoints/projects';
import { useProfile } from '../../lib/auth/useProfile';
import { Permissions, hasPermission } from '../../lib/auth/permissions';
import { useAppStore } from '../../stores/useAppStore';

const SEVERITIES = ['Critical', 'High', 'Medium', 'Low'];
const STATUSES = ['Open', 'InProgress', 'Resolved', 'Closed', 'Rejected'];
const CLASSIFICATIONS = ['ApplicationDefect', 'EnvironmentFailure', 'AutomationFailure', 'TestFailure', 'Unknown'];

function toggle(list: string[], value: string): string[] {
  return list.includes(value) ? list.filter((v) => v !== value) : [...list, value];
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
 * Automatic Jira ticket policy configuration (Phase 2 Slice 10).
 * Project-scoped; writes require settings.manage (backend-enforced).
 */
export function AutoTicketSettings() {
  const profile = useProfile();
  const queryClient = useQueryClient();
  const currentProjectId = useAppStore((s) => s.currentProjectId);
  const setCurrentProjectId = useAppStore((s) => s.setCurrentProjectId);

  const canConfigure = hasPermission(profile.data?.permissions, Permissions.SettingsManage);
  const canRead = hasPermission(profile.data?.permissions, Permissions.TicketsRead);

  const [projectId, setProjectId] = useState<string | null>(currentProjectId);
  const [enabled, setEnabled] = useState(false);
  const [severities, setSeverities] = useState<string[]>(['Critical', 'High']);
  const [statuses, setStatuses] = useState<string[]>(['Open']);
  const [classifications, setClassifications] = useState<string[]>(['ApplicationDefect']);
  const [minimumConfidence, setMinimumConfidence] = useState('');
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

  const policy = useQuery({
    queryKey: projectId ? autoTicketKeys.policy(projectId) : ['auto-tickets', 'policy', 'none'],
    queryFn: () => autoTicketEndpoints.getPolicy(projectId!),
    enabled: !!projectId && canRead,
    retry: false,
  });

  const status = useQuery({
    queryKey: projectId ? autoTicketKeys.status(projectId) : ['auto-tickets', 'status', 'none'],
    queryFn: () => autoTicketEndpoints.getStatus(projectId!),
    enabled: !!projectId && canRead,
    retry: false,
  });

  useEffect(() => {
    if (policy.data) {
      setEnabled(policy.data.enabled);
      setSeverities(policy.data.severities.length > 0 ? policy.data.severities : ['Critical', 'High']);
      setStatuses(policy.data.defectStatuses.length > 0 ? policy.data.defectStatuses : ['Open']);
      setClassifications(
        policy.data.classifications.length > 0 ? policy.data.classifications : ['ApplicationDefect'],
      );
      setMinimumConfidence(
        policy.data.minimumConfidence === null || policy.data.minimumConfidence === undefined
          ? ''
          : String(policy.data.minimumConfidence),
      );
    }
  }, [policy.data]);

  const invalidate = () => {
    if (!projectId) return;
    void queryClient.invalidateQueries({ queryKey: autoTicketKeys.policy(projectId) });
    void queryClient.invalidateQueries({ queryKey: autoTicketKeys.status(projectId) });
  };

  const save = useMutation({
    mutationFn: () => {
      const parsed = minimumConfidence.trim() === '' ? null : Number(minimumConfidence);
      return autoTicketEndpoints.upsertPolicy(projectId!, {
        enabled,
        severities,
        defectStatuses: statuses,
        classifications,
        minimumConfidence: parsed,
      });
    },
    onSuccess: () => {
      setFormError(null);
      setSaved('Automation policy saved.');
      invalidate();
    },
    onError: (error: ApiError) => {
      setSaved(null);
      setFormError(autoTicketErrorMessage(error.status, error.code));
    },
  });

  const submit = (e: FormEvent) => {
    e.preventDefault();
    setSaved(null);
    if (enabled && (severities.length === 0 || statuses.length === 0 || classifications.length === 0)) {
      setFormError(
        'When automation is enabled, select at least one severity, defect status and failure classification.',
      );
      return;
    }
    if (minimumConfidence.trim() !== '') {
      const parsed = Number(minimumConfidence);
      if (!Number.isFinite(parsed) || parsed < 0 || parsed > 1) {
        setFormError('Minimum confidence must be a number between 0 and 1, or left empty.');
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
          <CardTitle>Ticket automation</CardTitle>
        </CardHeader>
        <CardContent>
          <p className="text-sm text-slate-600">
            You do not have permission to view ticket automation for this project.
          </p>
        </CardContent>
      </Card>
    );
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Ticket automation</CardTitle>
        <CardDescription>
          Policy-controlled automatic Jira ticket creation. When enabled, eligible internal defects
          create an external Jira issue asynchronously. The defect remains the system of record.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        <div>
          <label htmlFor="autoticket-project" className="mb-1 block text-sm font-medium text-slate-700">
            Project
          </label>
          <select
            id="autoticket-project"
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
          <p className="text-sm text-slate-500">Select a project to configure ticket automation.</p>
        ) : policy.isLoading || status.isLoading ? (
          <div aria-label="Loading automation policy" className="space-y-2">
            <Skeleton className="h-6 w-48" />
            <Skeleton className="h-24" />
          </div>
        ) : policy.isError ? (
          <ErrorState error={policy.error} onRetry={() => void policy.refetch()} />
        ) : (
          <>
            <div className="flex flex-wrap items-center gap-2 text-sm">
              <Badge tone={status.data?.enabled ? 'success' : 'neutral'}>
                {status.data?.enabled ? 'Automation enabled' : 'Automation disabled'}
              </Badge>
              {status.data && status.data.configured === false && (
                <span className="text-slate-500">No policy configured — automation is off.</span>
              )}
              {status.data && (
                <span className="text-slate-500">
                  Pending {status.data.pendingCount} · Failed {status.data.failedCount} · Auto-synced{' '}
                  {status.data.syncedAutomaticCount} · Last automation{' '}
                  {formatTime(status.data.lastAutomationAt)}
                </span>
              )}
            </div>

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
                You do not have permission to change the automation policy for this project.
              </p>
            ) : (
              <form onSubmit={submit} className="space-y-4" noValidate>
                <label className="flex items-start gap-2 text-sm text-slate-700">
                  <input
                    type="checkbox"
                    checked={enabled}
                    onChange={(e) => setEnabled(e.target.checked)}
                    className="mt-1"
                  />
                  <span>
                    <span className="font-medium">Enable automatic Jira ticket creation</span>
                    <br />
                    <span className="text-slate-500">
                      Warning: automatic ticket creation creates external Jira issues for every
                      eligible defect. Eligibility is deterministic — no AI decides whether a
                      ticket is created.
                    </span>
                  </span>
                </label>

                <fieldset>
                  <legend className="mb-1 text-sm font-medium text-slate-700">Eligible severities</legend>
                  <div className="flex flex-wrap gap-3">
                    {SEVERITIES.map((s) => (
                      <label key={s} className="flex items-center gap-1 text-sm text-slate-700">
                        <input
                          type="checkbox"
                          checked={severities.includes(s)}
                          onChange={() => setSeverities(toggle(severities, s))}
                        />
                        {s}
                      </label>
                    ))}
                  </div>
                </fieldset>

                <fieldset>
                  <legend className="mb-1 text-sm font-medium text-slate-700">Eligible defect statuses</legend>
                  <div className="flex flex-wrap gap-3">
                    {STATUSES.map((s) => (
                      <label key={s} className="flex items-center gap-1 text-sm text-slate-700">
                        <input
                          type="checkbox"
                          checked={statuses.includes(s)}
                          onChange={() => setStatuses(toggle(statuses, s))}
                        />
                        {s}
                      </label>
                    ))}
                  </div>
                </fieldset>

                <fieldset>
                  <legend className="mb-1 text-sm font-medium text-slate-700">
                    Eligible failure classifications
                  </legend>
                  <div className="flex flex-wrap gap-3">
                    {CLASSIFICATIONS.map((c) => (
                      <label key={c} className="flex items-center gap-1 text-sm text-slate-700">
                        <input
                          type="checkbox"
                          checked={classifications.includes(c)}
                          onChange={() => setClassifications(toggle(classifications, c))}
                        />
                        {c}
                      </label>
                    ))}
                  </div>
                </fieldset>

                <div>
                  <label htmlFor="autoticket-confidence" className="mb-1 block text-sm font-medium text-slate-700">
                    Minimum AI confidence (optional, 0–1)
                  </label>
                  <input
                    id="autoticket-confidence"
                    value={minimumConfidence}
                    onChange={(e) => setMinimumConfidence(e.target.value)}
                    placeholder="e.g. 0.7 — empty disables the filter"
                    inputMode="decimal"
                    className="w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900"
                  />
                  <p className="mt-1 text-xs text-slate-500">
                    Advisory only: defects without a confidence value are never blocked by this
                    filter.
                  </p>
                </div>

                <div className="flex justify-end">
                  <Button type="submit" size="sm" disabled={save.isPending}>
                    {save.isPending ? 'Saving…' : 'Save policy'}
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
