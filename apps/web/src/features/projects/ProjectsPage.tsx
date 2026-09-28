import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { FolderSearch, Plus } from 'lucide-react';
import { Button } from '../../components/ui/button';
import { Input } from '../../components/ui/input';
import { Skeleton } from '../../components/ui/skeleton';
import { Card, CardContent } from '../../components/ui/card';
import { ErrorState } from '../../components/common/ErrorState';
import { ProjectCard } from './ProjectCard';
import { projectKeys, projectsEndpoints } from '../../lib/api/endpoints/projects';
import { useProfile } from '../../lib/auth/useProfile';
import { Permissions, hasPermission } from '../../lib/auth/permissions';

const PAGE_SIZE = 12;

/** Project list: cards, search, pagination, permission-gated creation. */
export function ProjectsPage() {
  const [search, setSearch] = useState('');
  const [committedSearch, setCommittedSearch] = useState('');
  const [page, setPage] = useState(1);
  const profile = useProfile();
  const canManage = hasPermission(profile.data?.permissions, Permissions.ProjectsManage);

  const projects = useQuery({
    queryKey: projectKeys.list(committedSearch, page),
    queryFn: () => projectsEndpoints.list(committedSearch, page, PAGE_SIZE),
    staleTime: 15_000,
  });

  const totalPages = projects.data ? Math.max(1, Math.ceil(projects.data.totalCount / projects.data.pageSize)) : 1;

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="text-xl font-semibold text-slate-900">Projects</h1>
          <p className="mt-1 text-sm text-slate-500">
            Applications and systems under test. Only projects you can access are shown.
          </p>
        </div>
        {canManage && (
          <Link to="/projects/new">
            <Button>
              <Plus className="h-4 w-4" aria-hidden />
              Add Project
            </Button>
          </Link>
        )}
      </div>

      <form
        className="flex max-w-md gap-2"
        onSubmit={(e) => {
          e.preventDefault();
          setPage(1);
          setCommittedSearch(search);
        }}
      >
        <Input
          aria-label="Search projects"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          placeholder="Search by name or key…"
        />
        <Button type="submit" variant="secondary">
          Search
        </Button>
      </form>

      {projects.isLoading && (
        <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3" aria-label="Loading projects">
          {[0, 1, 2, 3, 4, 5].map((i) => (
            <Skeleton key={i} className="h-44" />
          ))}
        </div>
      )}

      {projects.isError && (
        <ErrorState error={projects.error} onRetry={() => void projects.refetch()} />
      )}

      {projects.data && projects.data.items.length === 0 && (
        <Card>
          <CardContent className="flex flex-col items-center gap-3 py-12 text-center">
            <span className="flex h-11 w-11 items-center justify-center rounded-full bg-slate-100">
              <FolderSearch className="h-5 w-5 text-slate-500" aria-hidden />
            </span>
            <h2 className="text-base font-semibold text-slate-900">No projects found</h2>
            <p className="max-w-md text-sm text-slate-500">
              {committedSearch
                ? 'No projects match your search.'
                : 'You are not a member of any project yet, or no projects exist.'}
            </p>
            {canManage && !committedSearch && (
              <Link to="/projects/new">
                <Button size="sm">
                  <Plus className="h-4 w-4" aria-hidden />
                  Add Project
                </Button>
              </Link>
            )}
          </CardContent>
        </Card>
      )}

      {projects.data && projects.data.items.length > 0 && (
        <>
          <div className="grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3">
            {projects.data.items.map((project) => (
              <ProjectCard key={project.id} project={project} />
            ))}
          </div>
          <div className="flex items-center justify-between text-sm text-slate-500">
            <p>
              Showing {projects.data.items.length} of {projects.data.totalCount} projects
            </p>
            <div className="flex gap-2">
              <Button
                variant="secondary"
                size="sm"
                disabled={page <= 1}
                onClick={() => setPage((p) => Math.max(1, p - 1))}
              >
                Previous
              </Button>
              <span className="px-2 py-1.5 font-mono text-xs">
                {page} / {totalPages}
              </span>
              <Button
                variant="secondary"
                size="sm"
                disabled={page >= totalPages}
                onClick={() => setPage((p) => p + 1)}
              >
                Next
              </Button>
            </div>
          </div>
        </>
      )}
    </div>
  );
}
