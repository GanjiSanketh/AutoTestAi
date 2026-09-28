import { Link } from 'react-router-dom';
import { FolderKanban, Globe, Server, Smartphone } from 'lucide-react';
import { Card, CardContent, CardHeader, CardTitle } from '../../components/ui/card';
import { Badge } from '../../components/ui/badge';
import type { ProjectListItem } from '../../lib/api/endpoints/projects';

function PlatformIcon({ platform }: { platform: string | null }) {
  const normalized = (platform ?? '').toLowerCase();
  const Icon = normalized.includes('mobile')
    ? Smartphone
    : normalized.includes('api')
      ? Server
      : normalized.includes('web') || normalized === ''
        ? Globe
        : FolderKanban;
  return (
    <span className="flex h-9 w-9 items-center justify-center rounded-lg bg-brand-50 text-brand-700 ring-1 ring-brand-100">
      <Icon className="h-4 w-4" aria-hidden />
    </span>
  );
}

/** Project card per docs/02 §10: icon, status, name, description, repo, target URL. */
export function ProjectCard({ project }: { project: ProjectListItem }) {
  return (
    <Link
      to={`/projects/${project.id}`}
      className="block rounded-lg transition-shadow hover:shadow-md focus-visible:outline-2"
    >
      <Card className="h-full">
        <CardHeader>
          <div className="flex items-start justify-between gap-3">
            <div className="flex items-center gap-3">
              <PlatformIcon platform={project.platform} />
              <div>
                <CardTitle>{project.name}</CardTitle>
                <p className="mt-0.5 font-mono text-xs text-slate-500">{project.key}</p>
              </div>
            </div>
            <Badge tone={project.status === 'Active' ? 'success' : 'neutral'}>
              {project.status}
            </Badge>
          </div>
        </CardHeader>
        <CardContent className="space-y-2">
          <p className="line-clamp-2 min-h-10 text-sm text-slate-500">
            {project.description || 'No description.'}
          </p>
          {project.repositoryUrl && (
            <p className="truncate font-mono text-xs text-slate-500" title={project.repositoryUrl}>
              {project.repositoryUrl}
            </p>
          )}
          {project.targetUrl && (
            <p className="truncate font-mono text-xs text-brand-700" title={project.targetUrl}>
              {project.targetUrl}
            </p>
          )}
        </CardContent>
      </Card>
    </Link>
  );
}
