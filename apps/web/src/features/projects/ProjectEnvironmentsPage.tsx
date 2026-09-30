import { useState, type FormEvent } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Pencil, Plus, Trash2 } from 'lucide-react';
import { Button } from '../../components/ui/button';
import { Input } from '../../components/ui/input';
import { Badge } from '../../components/ui/badge';
import { Card, CardContent, CardHeader, CardTitle } from '../../components/ui/card';
import { Skeleton } from '../../components/ui/skeleton';
import { Modal } from '../../components/ui/modal';
import { ErrorState } from '../../components/common/ErrorState';
import { ApiError } from '../../lib/api/client';
import { projectKeys, projectsEndpoints, type ProjectEnvironment } from '../../lib/api/endpoints/projects';
import { useProfile } from '../../lib/auth/useProfile';
import { Permissions, hasPermission } from '../../lib/auth/permissions';

export function ProjectEnvironmentsPage() {
  const { projectId = '' } = useParams();
  const queryClient = useQueryClient();
  const profile = useProfile();
  const canManage = hasPermission(profile.data?.permissions, Permissions.ProjectsManage);

  const [editing, setEditing] = useState<ProjectEnvironment | null>(null);
  const [creating, setCreating] = useState(false);
  const [name, setName] = useState('');
  const [baseUrl, setBaseUrl] = useState('');
  const [formError, setFormError] = useState<string | null>(null);
  const [deleting, setDeleting] = useState<ProjectEnvironment | null>(null);

  const envs = useQuery({
    queryKey: projectKeys.environments(projectId),
    queryFn: () => projectsEndpoints.environments(projectId),
    enabled: !!projectId,
    retry: false,
  });

  const invalidate = () => {
    void queryClient.invalidateQueries({ queryKey: projectKeys.environments(projectId) });
    void queryClient.invalidateQueries({ queryKey: projectKeys.details(projectId) });
  };

  const save = useMutation({
    mutationFn: () =>
      editing
        ? projectsEndpoints.updateEnvironment(editing.id, {
            name: name.trim(),
            baseUrl: baseUrl.trim() || undefined,
          })
        : projectsEndpoints.createEnvironment(projectId, {
            name: name.trim(),
            baseUrl: baseUrl.trim() || undefined,
          }),
    onSuccess: () => {
      setEditing(null);
      setCreating(false);
      setFormError(null);
      invalidate();
    },
    onError: (error: ApiError) => setFormError(error.message),
  });

  const setDefault = useMutation({
    mutationFn: (envId: string) =>
      projectsEndpoints.updateEnvironment(envId, { setAsDefault: true }),
    onSuccess: () => invalidate(),
  });

  const remove = useMutation({
    mutationFn: (envId: string) => projectsEndpoints.deleteEnvironment(envId),
    onSuccess: () => {
      setDeleting(null);
      invalidate();
    },
  });

  const openCreate = () => {
    setEditing(null);
    setName('');
    setBaseUrl('');
    setFormError(null);
    setCreating(true);
  };

  const openEdit = (env: ProjectEnvironment) => {
    setCreating(false);
    setEditing(env);
    setName(env.name);
    setBaseUrl(env.baseUrl ?? '');
    setFormError(null);
  };

  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (!name.trim()) {
      setFormError('Name is required.');
      return;
    }
    save.mutate();
  };

  return (
    <div className="mx-auto max-w-4xl space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <Link to={`/projects/${projectId}`} className="text-sm text-brand-700 hover:text-brand-600">
            ← Back to project
          </Link>
          <h1 className="mt-2 text-xl font-semibold text-slate-900">Environments</h1>
          <p className="mt-1 text-sm text-slate-500">
            Target environment metadata. Manage{' '}
            <Link to={`/projects/${projectId}/variables`} className="text-brand-700 hover:text-brand-600">
              variables &amp; secrets
            </Link>{' '}
            per environment.
          </p>
        </div>
        {canManage && (
          <Button size="sm" onClick={openCreate}>
            <Plus className="h-4 w-4" aria-hidden />
            Add environment
          </Button>
        )}
      </div>

      {envs.isLoading && (
        <div className="space-y-2" aria-label="Loading environments">
          {[0, 1].map((i) => (
            <Skeleton key={i} className="h-20" />
          ))}
        </div>
      )}
      {envs.isError && <ErrorState error={envs.error} onRetry={() => void envs.refetch()} />}
      {envs.data && envs.data.length === 0 && (
        <Card>
          <CardContent className="py-10 text-center text-sm text-slate-500">
            No environments yet. Add QA, staging or production targets.
          </CardContent>
        </Card>
      )}
      {envs.data && envs.data.length > 0 && (
        <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
          {envs.data.map((env) => (
            <Card key={env.id}>
              <CardHeader>
                <div className="flex items-center justify-between gap-2">
                  <CardTitle>{env.name}</CardTitle>
                  <div className="flex gap-1">
                    {env.isDefault && <Badge tone="brand">Default</Badge>}
                    <Badge tone={env.status === 'Active' ? 'success' : 'neutral'}>{env.status}</Badge>
                  </div>
                </div>
              </CardHeader>
              <CardContent className="space-y-3">
                <p className="truncate font-mono text-xs text-slate-500" title={env.baseUrl ?? ''}>
                  {env.baseUrl || 'No base URL.'}
                </p>
                {canManage && env.status === 'Active' && (
                  <div className="flex flex-wrap gap-2">
                    {!env.isDefault && (
                      <Button
                        variant="secondary"
                        size="sm"
                        disabled={setDefault.isPending}
                        onClick={() => setDefault.mutate(env.id)}
                      >
                        Set as default
                      </Button>
                    )}
                    <Button variant="ghost" size="sm" onClick={() => openEdit(env)} aria-label={`Edit ${env.name}`}>
                      <Pencil className="h-4 w-4" aria-hidden />
                    </Button>
                    <Button
                      variant="ghost"
                      size="sm"
                      onClick={() => setDeleting(env)}
                      aria-label={`Delete ${env.name}`}
                    >
                      <Trash2 className="h-4 w-4 text-rose-600" aria-hidden />
                    </Button>
                  </div>
                )}
              </CardContent>
            </Card>
          ))}
        </div>
      )}

      {(creating || editing) && (
        <Modal
          title={editing ? 'Edit environment' : 'Add environment'}
          onClose={() => {
            setCreating(false);
            setEditing(null);
          }}
        >
          <form onSubmit={submit} className="space-y-4">
            {formError && (
              <p role="alert" className="rounded-md border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
                {formError}
              </p>
            )}
            <div>
              <label htmlFor="env-name" className="mb-1 block text-sm font-medium text-slate-700">
                Name
              </label>
              <Input id="env-name" value={name} onChange={(e) => setName(e.target.value)} placeholder="QA" maxLength={200} />
            </div>
            <div>
              <label htmlFor="env-base-url" className="mb-1 block text-sm font-medium text-slate-700">
                Base URL
              </label>
              <Input
                id="env-base-url"
                value={baseUrl}
                onChange={(e) => setBaseUrl(e.target.value)}
                placeholder="https://qa.example.com"
                inputMode="url"
                className="font-mono"
              />
            </div>
            <div className="flex justify-end gap-2">
              <Button
                variant="secondary"
                type="button"
                onClick={() => {
                  setCreating(false);
                  setEditing(null);
                }}
              >
                Cancel
              </Button>
              <Button type="submit" disabled={save.isPending}>
                {save.isPending ? 'Saving…' : editing ? 'Save changes' : 'Add environment'}
              </Button>
            </div>
          </form>
        </Modal>
      )}

      {deleting && (
        <Modal
          title="Delete environment"
          description={deleting.isDefault ? 'This is the default environment — the default will be cleared.' : undefined}
          onClose={() => setDeleting(null)}
        >
          <p className="text-sm text-slate-600">
            Delete environment <strong className="font-semibold">{deleting.name}</strong>? Execution
            history is preserved; only the metadata is archived.
          </p>
          {remove.isError && (
            <p role="alert" className="mt-3 rounded-md border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
              Deletion failed. Please try again.
            </p>
          )}
          <div className="mt-4 flex justify-end gap-2">
            <Button variant="secondary" onClick={() => setDeleting(null)}>
              Cancel
            </Button>
            <Button variant="destructive" disabled={remove.isPending} onClick={() => remove.mutate(deleting.id)}>
              {remove.isPending ? 'Deleting…' : 'Delete'}
            </Button>
          </div>
        </Modal>
      )}
    </div>
  );
}
