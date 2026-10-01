import { useEffect, useState, type FormEvent } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '../../components/ui/card';
import { Badge } from '../../components/ui/badge';
import { Button } from '../../components/ui/button';
import { Skeleton } from '../../components/ui/skeleton';
import { ErrorState } from '../../components/common/ErrorState';
import { ApiError } from '../../lib/api/client';
import {
  PROVIDERS,
  PROVIDER_EVENTS,
  ciCdEndpoints,
  ciCdErrorMessage,
  ciCdKeys,
  type CiIntegration,
} from '../../lib/api/endpoints/cicd';
import { projectsEndpoints } from '../../lib/api/endpoints/projects';
import { useProfile } from '../../lib/auth/useProfile';
import { Permissions, hasPermission } from '../../lib/auth/permissions';
import { useAppStore } from '../../stores/useAppStore';

const GUID_PATTERN = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;

function splitList(raw: string): string[] {
  return raw
    .split(',')
    .map((s) => s.trim())
    .filter((s) => s.length > 0);
}

function parseMapping(raw: string): Record<string, string> {
  const mapping: Record<string, string> = {};
  for (const part of raw.split(',').map((s) => s.trim()).filter(Boolean)) {
    const separator = part.indexOf('=');
    if (separator <= 0) continue;
    const target = part.slice(0, separator).trim();
    const source = part.slice(separator + 1).trim();
    if (target && source) mapping[target] = source;
  }
  return mapping;
}

function formatMapping(mapping: Record<string, string>): string {
  return Object.entries(mapping)
    .map(([target, source]) => `${target}=${source}`)
    .join(', ');
}

function formatTime(iso: string | null): string {
  if (!iso) return '—';
  try {
    return new Date(iso).toLocaleString();
  } catch {
    return iso;
  }
}

function deliveryTone(status: string): 'success' | 'danger' | 'warning' | 'info' | 'neutral' {
  switch (status) {
    case 'Triggered':
      return 'success';
    case 'Failed':
    case 'Rejected':
      return 'danger';
    case 'Accepted':
      return 'info';
    case 'Duplicate':
    case 'Ignored':
      return 'warning';
    default:
      return 'neutral';
  }
}

const inputClass =
  'w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900';
const labelClass = 'mb-1 block text-sm font-medium text-slate-700';

/**
 * CI/CD webhook integrations (Phase 3 Slice 3B). Project-scoped; writes and
 * delivery retry require settings.manage (backend-enforced). Secrets are
 * write-only: the UI never receives or renders secret values.
 */
export function CiCdSettings() {
  const profile = useProfile();
  const queryClient = useQueryClient();
  const currentProjectId = useAppStore((s) => s.currentProjectId);
  const setCurrentProjectId = useAppStore((s) => s.setCurrentProjectId);

  const canConfigure = hasPermission(profile.data?.permissions, Permissions.SettingsManage);
  const canRead = hasPermission(profile.data?.permissions, Permissions.ExecutionsRead);

  const [projectId, setProjectId] = useState<string | null>(currentProjectId);
  const [provider, setProvider] = useState<string>('github');
  const [enabled, setEnabled] = useState(true);
  const [webhookSecret, setWebhookSecret] = useState('');
  const [username, setUsername] = useState('');
  const [suiteId, setSuiteId] = useState('');
  const [environmentId, setEnvironmentId] = useState('');
  const [events, setEvents] = useState('');
  const [branches, setBranches] = useState('');
  const [repositories, setRepositories] = useState('');
  const [variableMapping, setVariableMapping] = useState('');
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

  const integrations = useQuery({
    queryKey: projectId ? ciCdKeys.list(projectId) : [...ciCdKeys.all, 'list', 'none'],
    queryFn: () => ciCdEndpoints.list(projectId!),
    enabled: !!projectId && canRead,
    retry: false,
  });

  const environments = useQuery({
    queryKey: projectId ? ['projects', 'environments', projectId] : ['projects', 'environments', 'none'],
    queryFn: () => projectsEndpoints.environments(projectId!),
    enabled: !!projectId && canRead,
    retry: false,
  });

  const deliveries = useQuery({
    queryKey: projectId ? ciCdKeys.deliveries(projectId, provider, 1) : [...ciCdKeys.all, 'deliveries', 'none'],
    queryFn: () => ciCdEndpoints.deliveries(projectId!, provider, 1, 25),
    enabled: !!projectId && canRead,
    retry: false,
  });

  const current: CiIntegration | undefined = (integrations.data ?? []).find((i) => i.provider === provider);

  useEffect(() => {
    if (current) {
      setEnabled(current.enabled);
      setUsername(current.username ?? '');
      setSuiteId(current.defaultSuiteId ?? '');
      setEnvironmentId(current.defaultEnvironmentId ?? '');
      setEvents((current.eventAllowlist ?? []).join(', '));
      setBranches((current.branchAllowlist ?? []).join(', '));
      setRepositories((current.repositoryAllowlist ?? []).join(', '));
      setVariableMapping(formatMapping(current.variableMapping ?? {}));
      setWebhookSecret('');
    } else {
      setEnabled(true);
      setUsername('');
      setSuiteId('');
      setEnvironmentId('');
      setEvents('');
      setBranches('');
      setRepositories('');
      setVariableMapping('');
      setWebhookSecret('');
    }
    setFormError(null);
    setSaved(null);
  }, [current?.id, provider]); // eslint-disable-line react-hooks/exhaustive-deps

  const invalidate = () => {
    if (!projectId) return;
    void queryClient.invalidateQueries({ queryKey: ciCdKeys.list(projectId) });
    void queryClient.invalidateQueries({ queryKey: ciCdKeys.deliveries(projectId, provider, 1) });
  };

  const save = useMutation({
    mutationFn: () =>
      ciCdEndpoints.upsert(projectId!, {
        provider,
        enabled,
        defaultSuiteId: suiteId.trim() === '' ? null : suiteId.trim(),
        defaultEnvironmentId: environmentId === '' ? null : environmentId,
        eventAllowlist: splitList(events),
        branchAllowlist: splitList(branches),
        repositoryAllowlist: splitList(repositories),
        variableMapping: parseMapping(variableMapping),
        username: provider === 'azure' ? username.trim() || null : null,
        webhookSecret: webhookSecret === '' ? null : webhookSecret,
      }),
    onSuccess: () => {
      setFormError(null);
      setSaved('CI/CD integration saved.');
      setWebhookSecret('');
      invalidate();
    },
    onError: (error: ApiError) => {
      setSaved(null);
      setFormError(ciCdErrorMessage(error.status, error.code));
    },
  });

  const retry = useMutation({
    mutationFn: (deliveryId: string) => ciCdEndpoints.retry(projectId!, provider, deliveryId),
    onSuccess: () => {
      if (projectId) void queryClient.invalidateQueries({ queryKey: ciCdKeys.deliveries(projectId, provider, 1) });
    },
  });

  const submit = (e: FormEvent) => {
    e.preventDefault();
    setSaved(null);
    if (suiteId.trim() !== '' && !GUID_PATTERN.test(suiteId.trim())) {
      setFormError('Default suite must be a valid suite ID (GUID) or left empty.');
      return;
    }
    if (provider === 'azure' && enabled && username.trim() === '' && (current?.username ?? '') === '') {
      setFormError('Azure service hooks require a username for Basic authentication.');
      return;
    }
    if (enabled && !current?.hasSecret && webhookSecret === '') {
      setFormError('A webhook secret is required to enable this integration. Secrets are stored encrypted and never shown again.');
      return;
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
          <CardTitle>CI/CD integrations</CardTitle>
        </CardHeader>
        <CardContent>
          <p className="text-sm text-slate-600">
            You do not have permission to view CI/CD integrations for this project.
          </p>
        </CardContent>
      </Card>
    );
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>CI/CD integrations</CardTitle>
        <CardDescription>
          Provider-signed webhook ingress that triggers suite executions asynchronously. Each
          delivery is verified, persisted idempotently, and fanned out through the existing
          execution pipeline. Secrets are write-only and never displayed.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        <div>
          <label htmlFor="cicd-project" className={labelClass}>
            Project
          </label>
          <select
            id="cicd-project"
            value={projectId ?? ''}
            onChange={(e) => selectProject(e.target.value)}
            className={inputClass}
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
          <p className="text-sm text-slate-500">Select a project to configure CI/CD integrations.</p>
        ) : integrations.isLoading ? (
          <div aria-label="Loading CI/CD integrations" className="space-y-2">
            <Skeleton className="h-6 w-48" />
            <Skeleton className="h-24" />
          </div>
        ) : integrations.isError ? (
          <ErrorState error={integrations.error} onRetry={() => void integrations.refetch()} />
        ) : (
          <>
            <div className="flex flex-wrap gap-2" role="tablist" aria-label="CI/CD providers">
              {PROVIDERS.map((p) => {
                const configured = (integrations.data ?? []).some((i) => i.provider === p && i.configured);
                return (
                  <Button
                    key={p}
                    type="button"
                    role="tab"
                    aria-selected={provider === p}
                    size="sm"
                    variant={provider === p ? 'primary' : 'secondary'}
                    onClick={() => setProvider(p)}
                  >
                    {p}
                    {configured ? ' ●' : ''}
                  </Button>
                );
              })}
            </div>

            <div className="flex flex-wrap items-center gap-2 text-sm">
              <Badge tone={current?.enabled ? 'success' : 'neutral'}>
                {current ? (current.enabled ? 'Enabled' : 'Disabled') : 'Not configured'}
              </Badge>
              {current && (
                <Badge tone={current.hasSecret ? 'success' : 'warning'}>
                  {current.hasSecret ? 'Secret configured' : 'Secret missing'}
                </Badge>
              )}
              {current && (
                <span className="break-all text-slate-500" title="Deterministic webhook URL (IDs are non-secret identifiers)">
                  {current.webhookUrl}
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
                You do not have permission to change CI/CD integrations for this project.
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
                  <span className="font-medium">Enable {provider} integration</span>
                </label>

                <div>
                  <label htmlFor="cicd-secret" className={labelClass}>
                    Webhook secret {current?.hasSecret ? '(leave empty to keep existing)' : '(required)'}
                  </label>
                  <input
                    id="cicd-secret"
                    type="password"
                    value={webhookSecret}
                    onChange={(e) => setWebhookSecret(e.target.value)}
                    placeholder={current?.hasSecret ? '•••••••• (stored encrypted)' : 'Enter webhook secret'}
                    autoComplete="new-password"
                    className={inputClass}
                  />
                  <p className="mt-1 text-xs text-slate-500">
                    {provider === 'github' && 'HMAC signing secret for X-Hub-Signature-256 verification.'}
                    {provider === 'gitlab' && 'Compared against the X-Gitlab-Token header.'}
                    {provider === 'jenkins' && 'Bearer-style token presented by the Jenkins HTTP notification.'}
                    {provider === 'azure' && 'Password half of Basic authentication (username below).'}
                  </p>
                </div>

                {provider === 'azure' && (
                  <div>
                    <label htmlFor="cicd-username" className={labelClass}>
                      Basic-auth username
                    </label>
                    <input
                      id="cicd-username"
                      value={username}
                      onChange={(e) => setUsername(e.target.value)}
                      placeholder="hookuser"
                      className={inputClass}
                    />
                  </div>
                )}

                <div>
                  <label htmlFor="cicd-suite" className={labelClass}>
                    Default suite ID (required for execution)
                  </label>
                  <input
                    id="cicd-suite"
                    value={suiteId}
                    onChange={(e) => setSuiteId(e.target.value)}
                    placeholder="Suite GUID — deliveries fan out across its approved members"
                    className={inputClass}
                  />
                </div>

                <div>
                  <label htmlFor="cicd-environment" className={labelClass}>
                    Default environment (required for execution)
                  </label>
                  <select
                    id="cicd-environment"
                    value={environmentId}
                    onChange={(e) => setEnvironmentId(e.target.value)}
                    className={inputClass}
                  >
                    <option value="">Select an environment</option>
                    {(environments.data ?? []).map((env) => (
                      <option key={env.id} value={env.id}>
                        {env.name}
                      </option>
                    ))}
                  </select>
                </div>

                <div>
                  <label htmlFor="cicd-events" className={labelClass}>
                    Event allowlist (comma-separated, empty allows provider defaults)
                  </label>
                  <input
                    id="cicd-events"
                    value={events}
                    onChange={(e) => setEvents(e.target.value)}
                    placeholder={(PROVIDER_EVENTS[provider] ?? []).join(', ')}
                    className={inputClass}
                  />
                </div>

                <div>
                  <label htmlFor="cicd-branches" className={labelClass}>
                    Branch allowlist (comma-separated, empty allows all; prefix* supported)
                  </label>
                  <input
                    id="cicd-branches"
                    value={branches}
                    onChange={(e) => setBranches(e.target.value)}
                    placeholder="main, release/*"
                    className={inputClass}
                  />
                </div>

                <div>
                  <label htmlFor="cicd-repos" className={labelClass}>
                    Repository allowlist (comma-separated, empty allows all)
                  </label>
                  <input
                    id="cicd-repos"
                    value={repositories}
                    onChange={(e) => setRepositories(e.target.value)}
                    placeholder="octo/repo"
                    className={inputClass}
                  />
                </div>

                <div>
                  <label htmlFor="cicd-mapping" className={labelClass}>
                    Variable mapping (TARGET=SOURCE pairs; sources: BRANCH, COMMIT_SHA, REPOSITORY,
                    EVENT_TYPE, CI_RUN_ID, PULL_REQUEST_NUMBER, BUILD_NUMBER)
                  </label>
                  <input
                    id="cicd-mapping"
                    value={variableMapping}
                    onChange={(e) => setVariableMapping(e.target.value)}
                    placeholder="BRANCH=BRANCH, COMMIT_SHA=COMMIT_SHA"
                    className={inputClass}
                  />
                </div>

                <div className="flex justify-end">
                  <Button type="submit" size="sm" disabled={save.isPending}>
                    {save.isPending ? 'Saving…' : 'Save integration'}
                  </Button>
                </div>
              </form>
            )}

            <div className="space-y-2">
              <h3 className="text-sm font-medium text-slate-700">Recent deliveries</h3>
              {deliveries.isLoading ? (
                <Skeleton className="h-16" />
              ) : deliveries.isError ? (
                <ErrorState error={deliveries.error} onRetry={() => void deliveries.refetch()} />
              ) : (deliveries.data?.items.length ?? 0) === 0 ? (
                <p className="text-sm text-slate-500">No webhook deliveries recorded for {provider}.</p>
              ) : (
                <ul className="divide-y divide-slate-200 rounded-md border border-slate-200">
                  {(deliveries.data?.items ?? []).map((d) => (
                    <li key={d.id} className="flex flex-wrap items-center gap-2 px-3 py-2 text-sm">
                      <Badge tone={deliveryTone(d.processingStatus)}>{d.processingStatus}</Badge>
                      <span className="text-slate-700">{d.eventType}</span>
                      <span className="font-mono text-xs text-slate-500">{d.deliveryId.slice(0, 8)}</span>
                      <span className="text-slate-500">×{d.triggeredCount}</span>
                      {d.executionId && (
                        <span className="font-mono text-xs text-slate-500">{d.executionId.slice(0, 8)}</span>
                      )}
                      <span className="text-xs text-slate-400">{formatTime(d.receivedAt)}</span>
                      {d.failureReason && (
                        <span className="text-xs text-rose-600">{d.failureReason}</span>
                      )}
                      {d.processingStatus === 'Failed' && canConfigure && (
                        <Button
                          type="button"
                          size="sm"
                          variant="secondary"
                          disabled={retry.isPending}
                          onClick={() => retry.mutate(d.id)}
                        >
                          Retry
                        </Button>
                      )}
                    </li>
                  ))}
                </ul>
              )}
            </div>
          </>
        )}
      </CardContent>
    </Card>
  );
}
