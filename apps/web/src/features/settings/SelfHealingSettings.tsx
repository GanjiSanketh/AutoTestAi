import { useEffect, useState, type FormEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '../../components/ui/card';
import { Badge } from '../../components/ui/badge';
import { Button } from '../../components/ui/button';
import { Skeleton } from '../../components/ui/skeleton';
import { ErrorState } from '../../components/common/ErrorState';
import { ApiError } from '../../lib/api/client';
import {
  selfHealingEndpoints,
  selfHealingErrorMessage,
  selfHealingKeys,
} from '../../lib/api/endpoints/selfHealing';
import { projectsEndpoints } from '../../lib/api/endpoints/projects';
import { useProfile } from '../../lib/auth/useProfile';
import { Permissions, hasPermission } from '../../lib/auth/permissions';
import { useAppStore } from '../../stores/useAppStore';

const STRATEGIES = ['css', 'xpath', 'role', 'text', 'testid'];

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
 * Self-Healing Test Engine configuration (Phase 2 Slice 11).
 * Project-scoped; writes require settings.manage (backend-enforced).
 * Healing is execution-time recovery only — it never mutates stored tests.
 */
export function SelfHealingSettings() {
  const profile = useProfile();
  const queryClient = useQueryClient();
  const currentProjectId = useAppStore((s) => s.currentProjectId);
  const setCurrentProjectId = useAppStore((s) => s.setCurrentProjectId);

  const canConfigure = hasPermission(profile.data?.permissions, Permissions.SettingsManage);
  const canRead = hasPermission(profile.data?.permissions, Permissions.ExecutionsRead);

  const [projectId, setProjectId] = useState<string | null>(currentProjectId);
  const [enabled, setEnabled] = useState(false);
  const [aiFallback, setAiFallback] = useState(false);
  const [strategies, setStrategies] = useState<string[]>([...STRATEGIES]);
  const [minScore, setMinScore] = useState('');
  const [minConfidence, setMinConfidence] = useState('');
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
    queryKey: projectId ? selfHealingKeys.policy(projectId) : ['self-healing', 'policy', 'none'],
    queryFn: () => selfHealingEndpoints.getPolicy(projectId!),
    enabled: !!projectId && canRead,
    retry: false,
  });

  const status = useQuery({
    queryKey: projectId ? selfHealingKeys.status(projectId) : ['self-healing', 'status', 'none'],
    queryFn: () => selfHealingEndpoints.getStatus(projectId!),
    enabled: !!projectId && canRead,
    retry: false,
  });

  useEffect(() => {
    if (policy.data) {
      setEnabled(policy.data.enabled);
      setAiFallback(policy.data.aiFallbackEnabled);
      setStrategies(
        policy.data.allowedStrategies.length > 0 ? policy.data.allowedStrategies : [...STRATEGIES],
      );
      setMinScore(
        policy.data.minDeterministicScore === null || policy.data.minDeterministicScore === undefined
          ? ''
          : String(policy.data.minDeterministicScore),
      );
      setMinConfidence(
        policy.data.minAiConfidence === null || policy.data.minAiConfidence === undefined
          ? ''
          : String(policy.data.minAiConfidence),
      );
    }
  }, [policy.data]);

  const invalidate = () => {
    if (!projectId) return;
    void queryClient.invalidateQueries({ queryKey: selfHealingKeys.policy(projectId) });
    void queryClient.invalidateQueries({ queryKey: selfHealingKeys.status(projectId) });
  };

  const save = useMutation({
    mutationFn: () =>
      selfHealingEndpoints.upsertPolicy(projectId!, {
        enabled,
        aiFallbackEnabled: aiFallback,
        minDeterministicScore: minScore.trim() === '' ? null : Number(minScore),
        minAiConfidence: minConfidence.trim() === '' ? null : Number(minConfidence),
        allowedStrategies: strategies,
      }),
    onSuccess: () => {
      setFormError(null);
      setSaved('Self-healing policy saved.');
      invalidate();
    },
    onError: (error: ApiError) => {
      setSaved(null);
      setFormError(selfHealingErrorMessage(error.status, error.code));
    },
  });

  const submit = (e: FormEvent) => {
    e.preventDefault();
    setSaved(null);
    if (minScore.trim() !== '') {
      const parsed = Number(minScore);
      if (!Number.isFinite(parsed) || parsed < 0 || parsed > 100) {
        setFormError('Minimum deterministic score must be a number between 0 and 100, or left empty.');
        return;
      }
    }
    if (minConfidence.trim() !== '') {
      const parsed = Number(minConfidence);
      if (!Number.isFinite(parsed) || parsed < 0 || parsed > 1) {
        setFormError('Minimum AI confidence must be a number between 0 and 1, or left empty.');
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
          <CardTitle>Self-healing test engine</CardTitle>
        </CardHeader>
        <CardContent>
          <p className="text-sm text-slate-600">
            You do not have permission to view self-healing for this project.
          </p>
        </CardContent>
      </Card>
    );
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Self-healing test engine</CardTitle>
        <CardDescription>
          Execution-time recovery from locator and DOM mutations. Deterministic candidates are
          validated against the live page first; AI is an optional fallback whose output is
          validated identically. Recovered locators never mutate the stored test.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        <div>
          <label htmlFor="selfhealing-project" className="mb-1 block text-sm font-medium text-slate-700">
            Project
          </label>
          <select
            id="selfhealing-project"
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
          <p className="text-sm text-slate-500">Select a project to configure self-healing.</p>
        ) : policy.isLoading || status.isLoading ? (
          <div aria-label="Loading self-healing policy" className="space-y-2">
            <Skeleton className="h-6 w-48" />
            <Skeleton className="h-24" />
          </div>
        ) : policy.isError ? (
          <ErrorState error={policy.error} onRetry={() => void policy.refetch()} />
        ) : (
          <>
            <div className="flex flex-wrap items-center gap-2 text-sm">
              <Badge tone={status.data?.enabled ? 'success' : 'neutral'}>
                {status.data?.enabled ? 'Self-healing enabled' : 'Self-healing disabled'}
              </Badge>
              {status.data && status.data.configured === false && (
                <span className="text-slate-500">No policy configured — healing is off.</span>
              )}
              {status.data && (
                <span className="text-slate-500">
                  Attempts {status.data.attemptCount} · Recovered {status.data.appliedCount} ·
                  Last recovery {formatTime(status.data.lastHealedAt)}
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
                You do not have permission to change the self-healing policy for this project.
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
                    <span className="font-medium">Enable self-healing recovery</span>
                    <br />
                    <span className="text-slate-500">
                      When a locator fails, the worker may retry once with a validated alternate
                      locator. Ambiguous candidates are rejected; the original failure is preserved.
                    </span>
                  </span>
                </label>

                <label className="flex items-start gap-2 text-sm text-slate-700">
                  <input
                    type="checkbox"
                    checked={aiFallback}
                    onChange={(e) => setAiFallback(e.target.checked)}
                    disabled={!enabled}
                    className="mt-1"
                  />
                  <span>
                    <span className="font-medium">Enable AI fallback</span>
                    <br />
                    <span className="text-slate-500">
                      When deterministic candidates fail, request AI-suggested locators from bounded
                      redacted evidence. AI output is schema-validated and live-validated; it is never
                      executed as code.
                    </span>
                  </span>
                </label>

                <fieldset>
                  <legend className="mb-1 text-sm font-medium text-slate-700">Allowed strategies</legend>
                  <div className="flex flex-wrap gap-3">
                    {STRATEGIES.map((s) => (
                      <label key={s} className="flex items-center gap-1 text-sm text-slate-700">
                        <input
                          type="checkbox"
                          checked={strategies.includes(s)}
                          onChange={() => setStrategies(toggle(strategies, s))}
                        />
                        {s}
                      </label>
                    ))}
                  </div>
                </fieldset>

                <div className="grid gap-4 sm:grid-cols-2">
                  <div>
                    <label htmlFor="selfhealing-score" className="mb-1 block text-sm font-medium text-slate-700">
                      Minimum deterministic score (optional, 0–100)
                    </label>
                    <input
                      id="selfhealing-score"
                      value={minScore}
                      onChange={(e) => setMinScore(e.target.value)}
                      placeholder="Empty uses the conservative default"
                      inputMode="decimal"
                      className="w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900"
                    />
                  </div>
                  <div>
                    <label htmlFor="selfhealing-confidence" className="mb-1 block text-sm font-medium text-slate-700">
                      Minimum AI confidence (optional, 0–1)
                    </label>
                    <input
                      id="selfhealing-confidence"
                      value={minConfidence}
                      onChange={(e) => setMinConfidence(e.target.value)}
                      placeholder="Advisory only — never overrides validation"
                      inputMode="decimal"
                      className="w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900"
                    />
                  </div>
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
