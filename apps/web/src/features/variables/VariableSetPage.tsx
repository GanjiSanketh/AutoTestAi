import { useMemo, useState, type FormEvent } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { KeyRound, Plus, Trash2 } from 'lucide-react';
import { Button } from '../../components/ui/button';
import { Input } from '../../components/ui/input';
import { Badge } from '../../components/ui/badge';
import { Card, CardContent, CardHeader, CardTitle } from '../../components/ui/card';
import { Skeleton } from '../../components/ui/skeleton';
import { Modal } from '../../components/ui/modal';
import { ErrorState } from '../../components/common/ErrorState';
import { ApiError } from '../../lib/api/client';
import { projectKeys, projectsEndpoints } from '../../lib/api/endpoints/projects';
import {
  isValidVariableKey,
  variableKeys,
  variablesEndpoints,
  type VariableSet,
} from '../../lib/api/endpoints/variables';
import { secretKeys, secretsEndpoints, type SecretMetadata } from '../../lib/api/endpoints/secrets';
import { useProfile } from '../../lib/auth/useProfile';
import { Permissions, hasPermission } from '../../lib/auth/permissions';

interface EntryDraft {
  key: string;
  kind: 'value' | 'secretRef';
  text: string;
  secretId: string;
}

function entriesFromSet(set: VariableSet | undefined): EntryDraft[] {
  if (!set) return [{ key: '', kind: 'value', text: '', secretId: '' }];
  try {
    const raw = JSON.parse(set.variablesJson) as Record<string, { value?: string; secretRef?: string }>;
    const entries = Object.entries(raw).map(([key, entry]) =>
      entry.secretRef
        ? { key, kind: 'secretRef' as const, text: '', secretId: '' }
        : { key, kind: 'value' as const, text: entry.value ?? '', secretId: '' },
    );
    return entries.length > 0 ? entries : [{ key: '', kind: 'value', text: '', secretId: '' }];
  } catch {
    return [{ key: '', kind: 'value', text: '', secretId: '' }];
  }
}

export function VariableSetPage() {
  const { projectId = '' } = useParams();
  const queryClient = useQueryClient();
  const profile = useProfile();
  const canManageVariables = hasPermission(profile.data?.permissions, Permissions.VariablesManage);
  const canManageSecrets = hasPermission(profile.data?.permissions, Permissions.SecretsManage);

  const [scope, setScope] = useState<'Project' | 'Environment'>('Project');
  const [scopeEnvId, setScopeEnvId] = useState<string>('');
  const [entries, setEntries] = useState<EntryDraft[]>([
    { key: '', kind: 'value', text: '', secretId: '' },
  ]);
  const [formError, setFormError] = useState<string | null>(null);

  const [secretName, setSecretName] = useState('');
  const [secretValue, setSecretValue] = useState('');
  const [secretEnvId, setSecretEnvId] = useState('');
  const [secretError, setSecretError] = useState<string | null>(null);
  const [replacing, setReplacing] = useState<SecretMetadata | null>(null);
  const [replaceValue, setReplaceValue] = useState('');
  const [deletingSecret, setDeletingSecret] = useState<SecretMetadata | null>(null);

  const envs = useQuery({
    queryKey: projectKeys.environments(projectId),
    queryFn: () => projectsEndpoints.environments(projectId),
    enabled: !!projectId,
    retry: false,
  });

  const sets = useQuery({
    queryKey: variableKeys.list(projectId),
    queryFn: () => variablesEndpoints.list(projectId),
    enabled: !!projectId,
    retry: false,
  });

  const secrets = useQuery({
    queryKey: secretKeys.list(projectId),
    queryFn: () => secretsEndpoints.list(projectId),
    enabled: !!projectId,
    retry: false,
  });

  const activeScopeId = scope === 'Project' ? null : scopeEnvId || null;
  const activeSet = useMemo(
    () =>
      sets.data?.find(
        (s) => s.scopeType.toLowerCase() === scope.toLowerCase() && (s.scopeId ?? null) === activeScopeId,
      ),
    [sets.data, scope, activeScopeId],
  );

  const openScope = (nextScope: 'Project' | 'Environment', envId = '') => {
    setScope(nextScope);
    setScopeEnvId(envId);
    setFormError(null);
  };

  const loadActive = () => {
    setEntries(entriesFromSet(activeSet));
    setFormError(null);
  };

  const invalidate = () => {
    void queryClient.invalidateQueries({ queryKey: variableKeys.list(projectId) });
    void queryClient.invalidateQueries({ queryKey: secretKeys.list(projectId) });
  };

  const toVariablesPayload = () => {
    const payload: Record<string, unknown> = {};
    for (const entry of entries) {
      const key = entry.key.trim();
      if (!key) continue;
      if (!isValidVariableKey(key)) throw new Error(`Key "${entry.key}" must match ^[A-Z0-9_]{1,64}$ (use uppercase).`);
      if (payload[key]) throw new Error(`Duplicate key "${key}".`);
      if (entry.kind === 'value') {
        payload[key] = { value: entry.text };
      } else {
        const secret = secrets.data?.find((s) => s.id === entry.secretId);
        if (!secret) throw new Error(`Select a stored secret for "${key}". Raw secret values are never accepted.`);
        payload[key] = { secretRef: secret.secretReference };
      }
    }
    return payload;
  };

  const save = useMutation({
    mutationFn: () => {
      const variables = toVariablesPayload();
      if (activeSet) {
        return variablesEndpoints.update(activeSet.id, {
          name: activeSet.name,
          variables,
          rowVersion: activeSet.rowVersion,
        });
      }
      return variablesEndpoints.create(projectId, {
        scopeType: scope,
        scopeId: activeScopeId,
        name: scope === 'Project' ? 'defaults' : `env-${activeScopeId?.slice(0, 8)}`,
        variables,
      });
    },
    onSuccess: () => {
      setFormError(null);
      invalidate();
    },
    onError: (error: ApiError) => {
      if (error.status === 409) {
        setFormError('This variable set was modified by another user. Reload and retry.');
      } else {
        setFormError(error.message);
      }
    },
  });

  const removeSet = useMutation({
    mutationFn: () => variablesEndpoints.remove(activeSet!.id),
    onSuccess: () => {
      setEntries([{ key: '', kind: 'value', text: '', secretId: '' }]);
      invalidate();
    },
  });

  const submitSet = (e: FormEvent) => {
    e.preventDefault();
    if (scope === 'Environment' && !scopeEnvId) {
      setFormError('Select an environment for the environment scope.');
      return;
    }
    try {
      toVariablesPayload();
    } catch (err) {
      setFormError(err instanceof Error ? err.message : 'Invalid entries.');
      return;
    }
    save.mutate();
  };

  const createSecret = useMutation({
    mutationFn: () =>
      secretsEndpoints.create(projectId, {
        environmentId: secretEnvId,
        name: secretName.trim().toUpperCase(),
        value: secretValue,
      }),
    onSuccess: () => {
      setSecretName('');
      setSecretValue('');
      setSecretError(null);
      invalidate();
    },
    onError: (error: ApiError) => setSecretError(error.message),
  });

  const submitSecret = (e: FormEvent) => {
    e.preventDefault();
    if (!secretEnvId) {
      setSecretError('Select an environment for the secret.');
      return;
    }
    if (!isValidVariableKey(secretName.trim().toUpperCase())) {
      setSecretError('Secret name must match ^[A-Z0-9_]{1,64}$.');
      return;
    }
    if (!secretValue) {
      setSecretError('Secret value is required.');
      return;
    }
    createSecret.mutate();
  };

  const replaceSecret = useMutation({
    mutationFn: () => secretsEndpoints.update(replacing!.id, { value: replaceValue }),
    onSuccess: () => {
      setReplacing(null);
      setReplaceValue('');
      invalidate();
    },
  });

  const deleteSecret = useMutation({
    mutationFn: () => secretsEndpoints.remove(deletingSecret!.id),
    onSuccess: () => {
      setDeletingSecret(null);
      invalidate();
    },
  });

  return (
    <div className="mx-auto max-w-4xl space-y-6">
      <div>
        <Link to={`/projects/${projectId}`} className="text-sm text-brand-700 hover:text-brand-600">
          ← Back to project
        </Link>
        <h1 className="mt-2 text-xl font-semibold text-slate-900">Variables &amp; Secrets</h1>
        <p className="mt-1 text-sm text-slate-500">
          Precedence: System → Project → Environment → Suite → Execution override. Secrets are
          stored encrypted and referenced — values are never displayed.
        </p>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Variable set scope</CardTitle>
        </CardHeader>
        <CardContent className="flex flex-wrap gap-2">
          <Button variant={scope === 'Project' ? 'primary' : 'secondary'} size="sm" onClick={() => openScope('Project')}>
            Project
          </Button>
          <select
            aria-label="Environment scope"
            className="rounded-md border border-slate-300 px-2 py-1 text-sm"
            value={scope === 'Environment' ? scopeEnvId : ''}
            onChange={(e) => openScope('Environment', e.target.value)}
          >
            <option value="">Environment…</option>
            {(envs.data ?? []).map((env) => (
              <option key={env.id} value={env.id}>
                {env.name}
              </option>
            ))}
          </select>
          {canManageVariables && (
            <Button variant="ghost" size="sm" onClick={loadActive}>
              Reload scope
            </Button>
          )}
        </CardContent>
      </Card>

      {sets.isLoading ? (
        <Skeleton className="h-40" />
      ) : sets.isError ? (
        <ErrorState error={sets.error} onRetry={() => void sets.refetch()} />
      ) : (
        <Card>
          <CardHeader>
            <div className="flex items-center justify-between gap-2">
              <CardTitle>
                {scope} variables{' '}
                {activeSet ? (
                  <Badge tone="brand">{activeSet.keys.length + activeSet.secretKeys.length} keys</Badge>
                ) : (
                  <Badge tone="neutral">No set yet</Badge>
                )}
              </CardTitle>
              {canManageVariables && activeSet && (
                <Button
                  variant="ghost"
                  size="sm"
                  disabled={removeSet.isPending}
                  onClick={() => removeSet.mutate()}
                  aria-label="Delete variable set"
                >
                  <Trash2 className="h-4 w-4 text-rose-600" aria-hidden />
                </Button>
              )}
            </div>
          </CardHeader>
          <CardContent>
            {formError && (
              <p role="alert" className="mb-3 rounded-md border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
                {formError}
              </p>
            )}
            {canManageVariables ? (
              <form onSubmit={submitSet} className="space-y-3">
                {entries.map((entry, index) => (
                  <div key={index} className="flex flex-wrap items-center gap-2">
                    <Input
                      aria-label={`Variable key ${index + 1}`}
                      value={entry.key}
                      onChange={(e) =>
                        setEntries((prev) => prev.map((p, i) => (i === index ? { ...p, key: e.target.value } : p)))
                      }
                      placeholder="BASE_URL"
                      className="w-44 font-mono"
                      maxLength={64}
                    />
                    <select
                      aria-label={`Variable kind ${index + 1}`}
                      className="rounded-md border border-slate-300 px-2 py-1 text-sm"
                      value={entry.kind}
                      onChange={(e) =>
                        setEntries((prev) =>
                          prev.map((p, i) => (i === index ? { ...p, kind: e.target.value as 'value' | 'secretRef' } : p)),
                        )
                      }
                    >
                      <option value="value">value</option>
                      <option value="secretRef">secretRef</option>
                    </select>
                    {entry.kind === 'value' ? (
                      <Input
                        aria-label={`Variable value ${index + 1}`}
                        value={entry.text}
                        onChange={(e) =>
                          setEntries((prev) => prev.map((p, i) => (i === index ? { ...p, text: e.target.value } : p)))
                        }
                        placeholder="https://qa.example.com"
                        className="min-w-52 flex-1 font-mono"
                      />
                    ) : (
                      <select
                        aria-label={`Secret reference ${index + 1}`}
                        className="min-w-52 flex-1 rounded-md border border-slate-300 px-2 py-1 font-mono text-sm"
                        value={entry.secretId}
                        onChange={(e) =>
                          setEntries((prev) => prev.map((p, i) => (i === index ? { ...p, secretId: e.target.value } : p)))
                        }
                      >
                        <option value="">Select a stored secret…</option>
                        {(secrets.data ?? []).map((secret) => (
                          <option key={secret.id} value={secret.id}>
                            {secret.name} ({secret.secretReference})
                          </option>
                        ))}
                      </select>
                    )}
                    <Button
                      variant="ghost"
                      size="sm"
                      type="button"
                      aria-label={`Remove entry ${index + 1}`}
                      onClick={() => setEntries((prev) => prev.filter((_, i) => i !== index))}
                    >
                      <Trash2 className="h-4 w-4 text-rose-600" aria-hidden />
                    </Button>
                  </div>
                ))}
                <div className="flex flex-wrap gap-2">
                  <Button
                    variant="secondary"
                    size="sm"
                    type="button"
                    onClick={() => setEntries((prev) => [...prev, { key: '', kind: 'value', text: '', secretId: '' }])}
                  >
                    <Plus className="h-4 w-4" aria-hidden /> Add entry
                  </Button>
                  <Button type="submit" size="sm" disabled={save.isPending}>
                    {save.isPending ? 'Saving…' : activeSet ? 'Save changes' : 'Create set'}
                  </Button>
                </div>
                <p className="text-xs text-slate-500">
                  Use <code className="font-mono">{'${{ KEY }}'}</code> in test steps. Missing variables fail
                  executions deterministically.
                </p>
              </form>
            ) : (
              <div className="space-y-1 text-sm">
                {(activeSet?.keys ?? []).map((key) => (
                  <p key={key} className="font-mono text-slate-700">
                    {key} <Badge tone="neutral">value</Badge>
                  </p>
                ))}
                {(activeSet?.secretKeys ?? []).map((key) => (
                  <p key={key} className="font-mono text-slate-700">
                    {key} <Badge tone="brand">secretRef</Badge>
                  </p>
                ))}
                {!activeSet && <p className="text-slate-500">No variable set for this scope.</p>}
              </div>
            )}
          </CardContent>
        </Card>
      )}

      <Card>
        <CardHeader>
          <CardTitle>
            <span className="inline-flex items-center gap-2">
              <KeyRound className="h-4 w-4" aria-hidden /> Secrets
            </span>
          </CardTitle>
        </CardHeader>
        <CardContent className="space-y-4">
          {secrets.isLoading ? (
            <Skeleton className="h-20" />
          ) : (
            <div className="space-y-2">
              {(secrets.data ?? []).map((secret) => (
                <div key={secret.id} className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-slate-200 px-3 py-2">
                  <div>
                    <p className="font-mono text-sm text-slate-800">{secret.name}</p>
                    <p className="font-mono text-xs text-slate-500">{secret.secretReference}</p>
                  </div>
                  <div className="flex items-center gap-2">
                    {secret.hasValue ? <Badge tone="success">stored</Badge> : <Badge tone="neutral">missing</Badge>}
                    {canManageSecrets && (
                      <>
                        <Button variant="ghost" size="sm" onClick={() => setReplacing(secret)}>
                          Replace
                        </Button>
                        <Button variant="ghost" size="sm" onClick={() => setDeletingSecret(secret)} aria-label={`Delete ${secret.name}`}>
                          <Trash2 className="h-4 w-4 text-rose-600" aria-hidden />
                        </Button>
                      </>
                    )}
                  </div>
                </div>
              ))}
              {(secrets.data ?? []).length === 0 && (
                <p className="text-sm text-slate-500">No secrets stored. Values are never displayed once saved.</p>
              )}
            </div>
          )}
          {canManageSecrets && (
            <form onSubmit={submitSecret} className="space-y-3 border-t border-slate-200 pt-4">
              <h3 className="text-sm font-medium text-slate-700">Store a secret</h3>
              {secretError && (
                <p role="alert" className="rounded-md border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
                  {secretError}
                </p>
              )}
              <div className="flex flex-wrap gap-2">
                <select
                  aria-label="Secret environment"
                  className="rounded-md border border-slate-300 px-2 py-1 text-sm"
                  value={secretEnvId}
                  onChange={(e) => setSecretEnvId(e.target.value)}
                >
                  <option value="">Environment…</option>
                  {(envs.data ?? []).map((env) => (
                    <option key={env.id} value={env.id}>
                      {env.name}
                    </option>
                  ))}
                </select>
                <Input
                  aria-label="Secret name"
                  value={secretName}
                  onChange={(e) => setSecretName(e.target.value)}
                  placeholder="API_TOKEN"
                  className="w-44 font-mono"
                  maxLength={64}
                />
                <Input
                  aria-label="Secret value"
                  type="password"
                  value={secretValue}
                  onChange={(e) => setSecretValue(e.target.value)}
                  placeholder="Value (never displayed again)"
                  className="min-w-52 flex-1 font-mono"
                />
                <Button type="submit" size="sm" disabled={createSecret.isPending}>
                  {createSecret.isPending ? 'Storing…' : 'Store'}
                </Button>
              </div>
            </form>
          )}
        </CardContent>
      </Card>

      {replacing && (
        <Modal title={`Replace ${replacing.name}`} onClose={() => setReplacing(null)}>
          <form
            onSubmit={(e) => {
              e.preventDefault();
              replaceSecret.mutate();
            }}
            className="space-y-4"
          >
            <Input
              aria-label="Replacement secret value"
              type="password"
              value={replaceValue}
              onChange={(e) => setReplaceValue(e.target.value)}
              placeholder="New value"
              className="font-mono"
            />
            <div className="flex justify-end gap-2">
              <Button variant="secondary" type="button" onClick={() => setReplacing(null)}>
                Cancel
              </Button>
              <Button type="submit" disabled={replaceSecret.isPending || !replaceValue}>
                {replaceSecret.isPending ? 'Replacing…' : 'Replace'}
              </Button>
            </div>
          </form>
        </Modal>
      )}

      {deletingSecret && (
        <Modal title="Delete secret" onClose={() => setDeletingSecret(null)}>
          <p className="text-sm text-slate-600">
            Delete secret <strong className="font-mono">{deletingSecret.name}</strong>? Referencing variable
            sets will fail resolution until updated.
          </p>
          <div className="mt-4 flex justify-end gap-2">
            <Button variant="secondary" onClick={() => setDeletingSecret(null)}>
              Cancel
            </Button>
            <Button variant="destructive" disabled={deleteSecret.isPending} onClick={() => deleteSecret.mutate()}>
              {deleteSecret.isPending ? 'Deleting…' : 'Delete'}
            </Button>
          </div>
        </Modal>
      )}
    </div>
  );
}
