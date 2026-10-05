import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Pencil, Trash2, Users, Server, KeyRound, ScrollText } from 'lucide-react';
import { Button } from '../../components/ui/button';
import { Badge } from '../../components/ui/badge';
import { Card, CardContent, CardHeader, CardTitle } from '../../components/ui/card';
import { Skeleton } from '../../components/ui/skeleton';
import { Modal } from '../../components/ui/modal';
import { ErrorState } from '../../components/common/ErrorState';
import { useAppStore } from '../../stores/useAppStore';
import { projectKeys, projectsEndpoints } from '../../lib/api/endpoints/projects';
import { useProfile } from '../../lib/auth/useProfile';
import { Permissions, hasPermission } from '../../lib/auth/permissions';

function DetailRow({ label, value, mono }: { label: string; value?: string | null; mono?: boolean }) {
  return (
    <div className="grid grid-cols-1 gap-1 py-2 sm:grid-cols-3">
      <dt className="text-sm font-medium text-slate-500">{label}</dt>
      <dd className={`text-sm text-slate-900 sm:col-span-2 ${mono ? 'font-mono break-all' : ''}`}>
        {value || <span className="text-slate-400">—</span>}
      </dd>
    </div>
  );
}

export function ProjectDetailsPage() {
  const { projectId = '' } = useParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const setCurrentProjectId = useAppStore((s) => s.setCurrentProjectId);
  const profile = useProfile();
  const canManage = hasPermission(profile.data?.permissions, Permissions.ProjectsManage);
  const canReadReports = hasPermission(profile.data?.permissions, Permissions.ReportsRead);
  const [confirmArchive, setConfirmArchive] = useState(false);

  const project = useQuery({
    queryKey: projectKeys.details(projectId),
    queryFn: () => projectsEndpoints.get(projectId),
    enabled: !!projectId,
    retry: false,
  });

  useEffect(() => {
    if (project.data) setCurrentProjectId(project.data.id);
  }, [project.data, setCurrentProjectId]);

  const archive = useMutation({
    mutationFn: () => projectsEndpoints.remove(projectId),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: projectKeys.all });
      navigate('/projects');
    },
  });

  if (project.isLoading) {
    return (
      <div className="space-y-4" aria-label="Loading project">
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-64" />
      </div>
    );
  }

  if (project.isError || !project.data) {
    return (
      <div className="space-y-6">
        <Link to="/projects" className="text-sm text-brand-700 hover:text-brand-600">
          ← Back to projects
        </Link>
        <ErrorState
          error={project.error}
          onRetry={() => void project.refetch()}
          notFoundMessage="This project does not exist."
        />
      </div>
    );
  }

  const p = project.data;

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <Link to="/projects" className="text-sm text-brand-700 hover:text-brand-600">
            ← Back to projects
          </Link>
          <div className="mt-2 flex items-center gap-3">
            <h1 className="text-xl font-semibold text-slate-900">{p.name}</h1>
            <Badge tone={p.status === 'Active' ? 'success' : 'neutral'}>{p.status}</Badge>
          </div>
          <p className="mt-1 font-mono text-xs text-slate-500">{p.key}</p>
        </div>
        {(canManage || canReadReports) && (
          <div className="flex flex-wrap gap-2">
            {canReadReports && (
              <Link to={`/projects/${p.id}/audit`}>
                <Button variant="secondary" size="sm">
                  <ScrollText className="h-4 w-4" aria-hidden />
                  Audit
                </Button>
              </Link>
            )}
            {canManage && (
              <>
            <Link to={`/projects/${p.id}/edit`}>
              <Button variant="secondary" size="sm">
                <Pencil className="h-4 w-4" aria-hidden />
                Edit
              </Button>
            </Link>
            <Link to={`/projects/${p.id}/members`}>
              <Button variant="secondary" size="sm">
                <Users className="h-4 w-4" aria-hidden />
                Members
              </Button>
            </Link>
            <Link to={`/projects/${p.id}/environments`}>
              <Button variant="secondary" size="sm">
                <Server className="h-4 w-4" aria-hidden />
                Environments
              </Button>
            </Link>
            <Link to={`/projects/${p.id}/variables`}>
              <Button variant="secondary" size="sm">
                <KeyRound className="h-4 w-4" aria-hidden />
                Variables
              </Button>
            </Link>
            {p.status === 'Active' && (
              <Button variant="destructive" size="sm" onClick={() => setConfirmArchive(true)}>
                <Trash2 className="h-4 w-4" aria-hidden />
                Archive
              </Button>
            )}
              </>
            )}
          </div>
        )}
      </div>

      <div className="grid grid-cols-1 gap-4 xl:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle>Details</CardTitle>
          </CardHeader>
          <CardContent>
            <dl className="divide-y divide-slate-100">
              <DetailRow label="Description" value={p.description} />
              <DetailRow label="Repository" value={p.repositoryUrl} mono />
              <DetailRow label="Target URL" value={p.targetUrl} mono />
              <DetailRow label="Platform" value={p.platform} />
              <DetailRow label="Framework" value={p.framework} />
              <DetailRow label="Members" value={String(p.memberCount)} />
            </dl>
          </CardContent>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle>Default environment</CardTitle>
          </CardHeader>
          <CardContent>
            {p.defaultEnvironment ? (
              <dl className="divide-y divide-slate-100">
                <DetailRow label="Name" value={p.defaultEnvironment.name} />
                <DetailRow label="Base URL" value={p.defaultEnvironment.baseUrl} mono />
                <DetailRow label="Status" value={p.defaultEnvironment.status} />
              </dl>
            ) : (
              <p className="text-sm text-slate-400">
                No default environment.{' '}
                {canManage && (
                  <Link to={`/projects/${p.id}/environments`} className="text-brand-700 hover:text-brand-600">
                    Manage environments
                  </Link>
                )}
              </p>
            )}
          </CardContent>
        </Card>
      </div>

      {confirmArchive && (
        <Modal
          title="Archive project"
          description="Archiving preserves execution history, defects and audit data. The project becomes read-only context."
          onClose={() => setConfirmArchive(false)}
        >
          <p className="text-sm text-slate-600">
            Archive <strong className="font-semibold">{p.name}</strong> ({p.key})?
          </p>
          {archive.isError && (
            <p role="alert" className="mt-3 rounded-md border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
              Archiving failed. Please try again.
            </p>
          )}
          <div className="mt-4 flex justify-end gap-2">
            <Button variant="secondary" onClick={() => setConfirmArchive(false)}>
              Cancel
            </Button>
            <Button variant="destructive" disabled={archive.isPending} onClick={() => archive.mutate()}>
              {archive.isPending ? 'Archiving…' : 'Archive project'}
            </Button>
          </div>
        </Modal>
      )}
    </div>
  );
}
