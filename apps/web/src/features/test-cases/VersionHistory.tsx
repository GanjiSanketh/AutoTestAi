import { History } from 'lucide-react';
import { Badge } from '../../components/ui/badge';
import { cn } from '../../components/ui/cn';
import type { TestCaseVersion } from '../../lib/api/endpoints/testcases';

export function reviewTone(status: string): 'success' | 'warning' | 'info' | 'danger' | 'neutral' {
  switch (status) {
    case 'Approved':
      return 'success';
    case 'Pending':
      return 'warning';
    case 'ChangesRequested':
      return 'info';
    case 'Rejected':
      return 'danger';
    default:
      return 'neutral';
  }
}

/** Read-only version history, newest first. Selecting a version shows it read-only. */
export function VersionHistory({
  versions,
  selectedId,
  onSelect,
}: {
  versions: TestCaseVersion[];
  selectedId: string | null;
  onSelect: (versionId: string | null) => void;
}) {
  if (versions.length === 0) {
    return <p className="text-sm text-slate-400">No versions yet.</p>;
  }
  return (
    <ol className="space-y-2">
      {versions.map((version, index) => {
        const selected =
          (selectedId === null && index === 0) || selectedId === version.id;
        return (
          <li key={version.id}>
            <button
              type="button"
              onClick={() => onSelect(index === 0 ? null : version.id)}
              aria-pressed={selected}
              className={cn(
                'flex w-full items-center gap-3 rounded-md border px-3 py-2 text-left text-sm transition-colors',
                selected
                  ? 'border-brand-300 bg-brand-50'
                  : 'border-slate-200 bg-white hover:border-slate-300 hover:bg-slate-50',
              )}
            >
              <span className="flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-slate-100">
                <History className="h-4 w-4 text-slate-500" aria-hidden />
              </span>
              <span className="min-w-0 flex-1">
                <span className="flex items-center gap-2 font-medium text-slate-900">
                  <span className="font-mono">v{version.versionNumber}</span>
                  {index === 0 && (
                    <Badge tone="brand">Current</Badge>
                  )}
                  <Badge tone={reviewTone(version.reviewStatus)}>{version.reviewStatus}</Badge>
                  {version.jiraProvenance && (
                    <Badge tone="neutral">From {version.jiraProvenance.jiraIssueKey}</Badge>
                  )}
                </span>
                <span className="block truncate text-xs text-slate-500">
                  {new Date(version.createdAt).toLocaleString()}
                  {version.generationProvider
                    ? ` · ${version.generationProvider}${version.generationModel ? `/${version.generationModel}` : ''}`
                    : ' · manual'}
                </span>
              </span>
            </button>
          </li>
        );
      })}
    </ol>
  );
}
