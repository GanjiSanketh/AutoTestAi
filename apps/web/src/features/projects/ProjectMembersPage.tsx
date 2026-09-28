import { useState, type FormEvent } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Plus, Trash2 } from 'lucide-react';
import { Button } from '../../components/ui/button';
import { Input } from '../../components/ui/input';
import { Badge } from '../../components/ui/badge';
import { Card, CardContent } from '../../components/ui/card';
import { Skeleton } from '../../components/ui/skeleton';
import { Modal } from '../../components/ui/modal';
import { ErrorState } from '../../components/common/ErrorState';
import { ApiError } from '../../lib/api/client';
import { projectKeys, projectsEndpoints } from '../../lib/api/endpoints/projects';
import { useProfile } from '../../lib/auth/useProfile';
import { Permissions, hasPermission } from '../../lib/auth/permissions';

export function ProjectMembersPage() {
  const { projectId = '' } = useParams();
  const queryClient = useQueryClient();
  const profile = useProfile();
  const canManage = hasPermission(profile.data?.permissions, Permissions.ProjectsManage);

  const [showAdd, setShowAdd] = useState(false);
  const [email, setEmail] = useState('');
  const [roleId, setRoleId] = useState('');
  const [formError, setFormError] = useState<string | null>(null);
  const [removing, setRemoving] = useState<string | null>(null);

  const members = useQuery({
    queryKey: projectKeys.members(projectId),
    queryFn: () => projectsEndpoints.members(projectId),
    enabled: !!projectId,
    retry: false,
  });
  const roles = useQuery({
    queryKey: projectKeys.roles,
    queryFn: projectsEndpoints.roles,
    enabled: canManage && showAdd,
    retry: false,
    staleTime: 300_000,
  });

  const invalidate = () =>
    void queryClient.invalidateQueries({ queryKey: projectKeys.members(projectId) });

  const addMember = useMutation({
    mutationFn: () => projectsEndpoints.addMember(projectId, { email: email.trim(), roleId }),
    onSuccess: () => {
      setShowAdd(false);
      setEmail('');
      setRoleId('');
      setFormError(null);
      invalidate();
    },
    onError: (error: ApiError) => {
      setFormError(
        error.code === 'CONFLICT'
          ? 'This user is already a member of the project.'
          : error.code === 'NOT_FOUND'
            ? 'No user found with that email.'
            : error.message,
      );
    },
  });

  const changeRole = useMutation({
    mutationFn: ({ userId, nextRoleId }: { userId: string; nextRoleId: string }) =>
      projectsEndpoints.updateMemberRole(projectId, userId, nextRoleId),
    onSuccess: () => invalidate(),
  });

  const removeMember = useMutation({
    mutationFn: (userId: string) => projectsEndpoints.removeMember(projectId, userId),
    onSuccess: () => {
      setRemoving(null);
      invalidate();
    },
  });

  const openAdd = (e: FormEvent) => {
    e.preventDefault();
    setFormError(null);
    if (!email.trim()) {
      setFormError('Email is required.');
      return;
    }
    if (!roleId) {
      setFormError('Select a role.');
      return;
    }
    addMember.mutate();
  };

  return (
    <div className="mx-auto max-w-4xl space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <Link to={`/projects/${projectId}`} className="text-sm text-brand-700 hover:text-brand-600">
            ← Back to project
          </Link>
          <h1 className="mt-2 text-xl font-semibold text-slate-900">Members</h1>
          <p className="mt-1 text-sm text-slate-500">
            Who can access this project and with which role.
          </p>
        </div>
        {canManage && (
          <Button size="sm" onClick={() => setShowAdd(true)}>
            <Plus className="h-4 w-4" aria-hidden />
            Add member
          </Button>
        )}
      </div>

      {members.isLoading && (
        <div className="space-y-2" aria-label="Loading members">
          {[0, 1, 2].map((i) => (
            <Skeleton key={i} className="h-14" />
          ))}
        </div>
      )}
      {members.isError && (
        <ErrorState error={members.error} onRetry={() => void members.refetch()} />
      )}
      {members.data && members.data.length === 0 && (
        <Card>
          <CardContent className="py-10 text-center text-sm text-slate-500">
            No members yet.
          </CardContent>
        </Card>
      )}
      {members.data && members.data.length > 0 && (
        <Card>
          <CardContent className="divide-y divide-slate-100 !px-5 !py-2">
            {members.data.map((m) => (
              <div key={m.userId} className="flex flex-wrap items-center gap-3 py-3">
                <div className="min-w-0 flex-1">
                  <p className="truncate text-sm font-medium text-slate-900">{m.displayName}</p>
                  <p className="truncate font-mono text-xs text-slate-500">{m.email}</p>
                </div>
                {canManage ? (
                  <select
                    aria-label={`Role for ${m.displayName}`}
                    value={m.roleId}
                    disabled={changeRole.isPending}
                    onChange={(e) =>
                      e.target.value !== m.roleId &&
                      changeRole.mutate({ userId: m.userId, nextRoleId: e.target.value })
                    }
                    className="rounded-md border border-slate-300 bg-white px-2 py-1.5 text-sm text-slate-700"
                  >
                    {(roles.data ?? [{ id: m.roleId, name: m.roleName, description: null }]).map((r) => (
                      <option key={r.id} value={r.id}>
                        {r.name}
                      </option>
                    ))}
                  </select>
                ) : (
                  <Badge tone="brand">{m.roleName}</Badge>
                )}
                <span className="hidden font-mono text-xs text-slate-400 sm:inline" title={m.createdAt}>
                  {new Date(m.createdAt).toLocaleDateString()}
                </span>
                {canManage && (
                  <Button
                    variant="ghost"
                    size="sm"
                    aria-label={`Remove ${m.displayName}`}
                    onClick={() => setRemoving(m.userId)}
                  >
                    <Trash2 className="h-4 w-4 text-rose-600" aria-hidden />
                  </Button>
                )}
              </div>
            ))}
          </CardContent>
        </Card>
      )}

      {showAdd && (
        <Modal title="Add member" description="Find the user by email and assign a project role." onClose={() => setShowAdd(false)}>
          <form onSubmit={openAdd} className="space-y-4">
            {formError && (
              <p role="alert" className="rounded-md border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
                {formError}
              </p>
            )}
            <div>
              <label htmlFor="member-email" className="mb-1 block text-sm font-medium text-slate-700">
                User email
              </label>
              <Input
                id="member-email"
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                placeholder="colleague@example.com"
                inputMode="email"
              />
            </div>
            <div>
              <label htmlFor="member-role" className="mb-1 block text-sm font-medium text-slate-700">
                Role
              </label>
              <select
                id="member-role"
                value={roleId}
                onChange={(e) => setRoleId(e.target.value)}
                className="w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900"
              >
                <option value="">Select a role…</option>
                {(roles.data ?? []).map((r) => (
                  <option key={r.id} value={r.id}>
                    {r.name}
                  </option>
                ))}
              </select>
            </div>
            <div className="flex justify-end gap-2">
              <Button variant="secondary" type="button" onClick={() => setShowAdd(false)}>
                Cancel
              </Button>
              <Button type="submit" disabled={addMember.isPending}>
                {addMember.isPending ? 'Adding…' : 'Add member'}
              </Button>
            </div>
          </form>
        </Modal>
      )}

      {removing && (
        <Modal title="Remove member" onClose={() => setRemoving(null)}>
          <p className="text-sm text-slate-600">
            Remove this member from the project? They will immediately lose access.
          </p>
          {removeMember.isError && (
            <p role="alert" className="mt-3 rounded-md border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
              Removal failed. Please try again.
            </p>
          )}
          <div className="mt-4 flex justify-end gap-2">
            <Button variant="secondary" onClick={() => setRemoving(null)}>
              Cancel
            </Button>
            <Button
              variant="destructive"
              disabled={removeMember.isPending}
              onClick={() => removeMember.mutate(removing)}
            >
              {removeMember.isPending ? 'Removing…' : 'Remove'}
            </Button>
          </div>
        </Modal>
      )}
    </div>
  );
}
