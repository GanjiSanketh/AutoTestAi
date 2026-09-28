import { useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Card, CardContent, CardHeader, CardTitle } from '../../components/ui/card';
import { Skeleton } from '../../components/ui/skeleton';
import { ErrorState } from '../../components/common/ErrorState';
import { ApiError } from '../../lib/api/client';
import { projectKeys, projectsEndpoints, type UpdateProjectInput } from '../../lib/api/endpoints/projects';
import { ProjectForm } from './ProjectForm';

export function ProjectEditPage() {
  const { projectId = '' } = useParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [serverError, setServerError] = useState<ApiError | null>(null);

  const project = useQuery({
    queryKey: projectKeys.details(projectId),
    queryFn: () => projectsEndpoints.get(projectId),
    enabled: !!projectId,
    retry: false,
  });

  const update = useMutation({
    mutationFn: (input: UpdateProjectInput) => projectsEndpoints.update(projectId, input),
    onSuccess: (updated) => {
      void queryClient.invalidateQueries({ queryKey: projectKeys.all });
      navigate(`/projects/${updated.id}`);
    },
    onError: (error: ApiError) => setServerError(error),
  });

  if (project.isLoading) {
    return (
      <div className="mx-auto max-w-3xl space-y-4" aria-label="Loading project">
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-96" />
      </div>
    );
  }

  if (project.isError || !project.data) {
    return (
      <div className="mx-auto max-w-3xl space-y-6">
        <Link to="/projects" className="text-sm text-brand-700 hover:text-brand-600">
          ← Back to projects
        </Link>
        <ErrorState error={project.error} onRetry={() => void project.refetch()} />
      </div>
    );
  }

  return (
    <div className="mx-auto max-w-3xl space-y-6">
      <div>
        <Link to={`/projects/${projectId}`} className="text-sm text-brand-700 hover:text-brand-600">
          ← Back to project
        </Link>
        <h1 className="mt-2 text-xl font-semibold text-slate-900">Edit project</h1>
        <p className="mt-1 font-mono text-xs text-slate-500">{project.data.key} · key cannot be changed</p>
      </div>
      <Card>
        <CardHeader>
          <CardTitle>Project details</CardTitle>
        </CardHeader>
        <CardContent>
          <ProjectForm
            initial={project.data}
            submitLabel="Save changes"
            submitting={update.isPending}
            serverError={serverError}
            onSubmit={(values) => {
              setServerError(null);
              update.mutate({
                name: values.name,
                description: values.description,
                repositoryUrl: values.repositoryUrl,
                targetUrl: values.targetUrl,
                framework: values.framework,
                platform: values.platform,
                status: values.status,
                defaultEnvironmentId: project.data!.defaultEnvironmentId,
              });
            }}
          />
        </CardContent>
      </Card>
    </div>
  );
}
