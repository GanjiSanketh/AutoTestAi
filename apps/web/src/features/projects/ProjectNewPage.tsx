import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Card, CardContent, CardHeader, CardTitle } from '../../components/ui/card';
import { ApiError } from '../../lib/api/client';
import { projectKeys, projectsEndpoints, type CreateProjectInput } from '../../lib/api/endpoints/projects';
import { ProjectForm } from './ProjectForm';

export function ProjectNewPage() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [serverError, setServerError] = useState<ApiError | null>(null);

  const create = useMutation({
    mutationFn: (input: CreateProjectInput) => projectsEndpoints.create(input),
    onSuccess: (project) => {
      void queryClient.invalidateQueries({ queryKey: projectKeys.all });
      navigate(`/projects/${project.id}`);
    },
    onError: (error: ApiError) => setServerError(error),
  });

  return (
    <div className="mx-auto max-w-3xl space-y-6">
      <div>
        <Link to="/projects" className="text-sm text-brand-700 hover:text-brand-600">
          ← Back to projects
        </Link>
        <h1 className="mt-2 text-xl font-semibold text-slate-900">New project</h1>
        <p className="mt-1 text-sm text-slate-500">
          Register an application or system under test. You will become its first member.
        </p>
      </div>
      <Card>
        <CardHeader>
          <CardTitle>Project details</CardTitle>
        </CardHeader>
        <CardContent>
          <ProjectForm
            submitLabel="Create project"
            submitting={create.isPending}
            serverError={serverError}
            onSubmit={(values) => {
              setServerError(null);
              create.mutate(values);
            }}
          />
        </CardContent>
      </Card>
    </div>
  );
}
