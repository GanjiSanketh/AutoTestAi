import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { Wrench, Search, Zap } from 'lucide-react';
import { Button } from '../../components/ui/button';
import { Input } from '../../components/ui/input';
import { Badge } from '../../components/ui/badge';
import { Card, CardContent } from '../../components/ui/card';
import { Skeleton } from '../../components/ui/skeleton';
import { ErrorState } from '../../components/common/ErrorState';
import { maintenanceKeys, maintenanceEndpoints, type MaintenanceFilters } from '../../lib/api/endpoints/maintenance';
import { projectsEndpoints, projectKeys } from '../../lib/api/endpoints/projects';
import { useProfile } from '../../lib/auth/useProfile';
import { Permissions, hasPermission } from '../../lib/auth/permissions';
import { MaintenanceDetailDrawer } from './MaintenanceDetailDrawer';

const PAGE_SIZE = 25;

const STATUS_OPTIONS = ['', 'Proposed', 'Rejected', 'Applied', 'Superseded'];
const SIGNAL_OPTIONS = ['', 'healed-locator'];

function statusTone(status: string): 'success' | 'warning' | 'info' | 'neutral' | 'danger' {
  switch (status) {
    case 'Proposed':
      return 'info';
    case 'Rejected':
      return 'neutral';
    case 'Applied':
      return 'success';
    case 'Superseded':
      return 'warning';
    default:
      return 'neutral';
  }
}

function confidenceTone(confidence: number): 'success' | 'warning' | 'info' | 'neutral' | 'danger' {
  if (confidence >= 80) return 'success';
  if (confidence >= 60) return 'info';
  return 'danger';
}

function FilterSelect({
  label,
  value,
  options,
  onChange,
}: {
  label: string;
  value: string;
  options: string[];
  onChange: (value: string) => void;
}) {
  return (
    <label className="flex min-w-36 flex-1 flex-col gap-1 text-xs font-medium text-slate-500 sm:flex-none">
      {label}
      <select
        aria-label={label}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        className="rounded-md border border-slate-300 bg-white px-2 py-2 text-sm font-normal text-slate-900"
      >
        {options.map((option) => (
          <option key={option || 'all'} value={option}>
            {option || 'All'}
          </option>
        ))}
      </select>
    </label>
  );
}

/** Project-scoped maintenance inbox: table, search, filters, pagination (docs/02 §9). */
export function MaintenanceInboxPage() {
  const { projectId = '' } = useParams();
  const profile = useProfile();
  const canManage = hasPermission(profile.data?.permissions, Permissions.TestCasesManage);

  const [search, setSearch] = useState('');
  const [filters, setFilters] = useState<MaintenanceFilters>({});
  const [committed, setCommitted] = useState<{ search: string; filters: MaintenanceFilters }>({ search: '', filters: {} });
  const [page, setPage] = useState(1);
  const [detailOpen, setDetailOpen] = useState<string | null>(null);

  const project = useQuery({
    queryKey: projectKeys.details(projectId),
    queryFn: () => projectsEndpoints.get(projectId),
    enabled: !!projectId,
    retry: false,
    staleTime: 60_000,
  });

  const proposals = useQuery({
    queryKey: maintenanceKeys.list(projectId, { ...committed.filters, search: committed.search }, page),
    queryFn: () => maintenanceEndpoints.list(projectId, { ...committed.filters, search: committed.search }, page, PAGE_SIZE),
    enabled: !!projectId,
    staleTime: 15_000,
  });

  const applyFilters = (e: React.FormEvent) => {
    e.preventDefault();
    setPage(1);
    setCommitted({ search, filters });
  };

  const setFilter = (key: keyof MaintenanceFilters) => (value: string) => {
    setFilters((f) => ({ ...f, [key]: value || undefined }));
    setPage(1);
    setCommitted((c) => ({ ...c, filters: { ...c.filters, [key]: value || undefined } }));
  };

  const totalPages = proposals.data ? Math.max(1, Math.ceil(proposals.data.totalCount / proposals.data.pageSize)) : 1;

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <Link to={`/projects/${projectId}`} className="text-sm text-brand-700 hover:text-brand-600">
            ← {project.data?.name ?? 'Back to project'}
          </Link>
          <h1 className="mt-2 text-xl font-semibold text-slate-900">Maintenance Proposals</h1>
          <p className="mt-1 text-sm text-slate-500">
            Human-gated web-locator maintenance from deterministic healing evidence. Approval creates a Pending version.
          </p>
        </div>
        <div className="flex gap-2">
          {canManage ? (
            <Button variant="secondary" onClick={() => maintenanceEndpoints.scan(projectId).then(() => proposals.refetch())}>
              <Zap className="h-4 w-4" aria-hidden />
              Scan
            </Button>
          ) : (
            <Button variant="secondary" disabled title="Scanning requires the testcases.manage permission">
              <Zap className="h-4 w-4" aria-hidden />
              Scan
            </Button>
          )}
        </div>
      </div>

      <form onSubmit={applyFilters} className="flex flex-wrap items-end gap-2">
        <label className="flex min-w-48 flex-1 flex-col gap-1 text-xs font-medium text-slate-500 sm:flex-none sm:basis-64">
          Search
          <span className="flex gap-2">
            <Input
              aria-label="Search maintenance proposals"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Test key, title…"
            />
            <Button type="submit" variant="secondary">
              <Search className="h-4 w-4" aria-hidden />
              Search
            </Button>
          </span>
        </label>
        <FilterSelect label="Status" value={filters.status ?? ''} options={STATUS_OPTIONS} onChange={setFilter('status')} />
        <FilterSelect label="Signal" value={filters.signal ?? ''} options={SIGNAL_OPTIONS} onChange={setFilter('signal')} />
      </form>

      {proposals.isLoading && (
        <div className="space-y-2" aria-label="Loading maintenance proposals">
          {[0, 1, 2, 3, 4].map((i) => (
            <Skeleton key={i} className="h-14" />
          ))}
        </div>
      )}

      {proposals.isError && (
        <ErrorState error={proposals.error} onRetry={() => void proposals.refetch()} />
      )}

      {proposals.data && proposals.data.items.length === 0 && (
        <Card>
          <CardContent className="flex flex-col items-center gap-3 py-12 text-center">
            <span className="flex h-11 w-11 items-center justify-center rounded-full bg-slate-100">
              <Wrench className="h-5 w-5 text-slate-500" aria-hidden />
            </span>
            <h2 className="text-base font-semibold text-slate-900">No maintenance proposals found</h2>
            <p className="max-w-md text-sm text-slate-500">
              {committed.search || Object.keys(committed.filters).length > 0
                ? 'No proposals match the current search or filters.'
                : 'No deterministic locator replacements have been detected yet. Run a scan to check for new proposals.'}
            </p>
            {canManage && (
              <Button variant="secondary" onClick={() => maintenanceEndpoints.scan(projectId).then(() => proposals.refetch())}>
                <Zap className="h-4 w-4" aria-hidden />
                Run scan
              </Button>
            )}
          </CardContent>
        </Card>
      )}

      {proposals.data && proposals.data.items.length > 0 && (
        <>
          <div className="overflow-x-auto rounded-lg border border-slate-200 bg-white shadow-sm">
            <table className="w-full min-w-3xl text-left text-sm">
              <thead>
                <tr className="border-b border-slate-200 bg-slate-50 text-xs uppercase tracking-wider text-slate-500">
                  <th scope="col" className="px-4 py-3 font-medium">Test</th>
                  <th scope="col" className="px-4 py-3 font-medium">Step</th>
                  <th scope="col" className="px-4 py-3 font-medium">Original → Proposed</th>
                  <th scope="col" className="px-4 py-3 font-medium">Confidence</th>
                  <th scope="col" className="px-4 py-3 font-medium">Occurrences</th>
                  <th scope="col" className="px-4 py-3 font-medium">Status</th>
                  <th scope="col" className="px-4 py-3 font-medium"><span className="sr-only">Actions</span></th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {proposals.data.items.map((proposal) => (
                  <tr key={proposal.id} className="hover:bg-slate-50 cursor-pointer" onClick={() => setDetailOpen(proposal.id)}>
                    <td className="whitespace-nowrap px-4 py-3 font-mono text-xs text-slate-700">
                      {proposal.testKey}
                    </td>
                    <td className="whitespace-nowrap px-4 py-3 text-slate-500">
                      {proposal.stepAction} · #{proposal.stepOrder}
                    </td>
                    <td className="max-w-md truncate px-4 py-3 font-mono text-xs text-slate-700">
                      {proposal.originalStrategy}={proposal.originalValue} → {proposal.proposedStrategy}={proposal.proposedValue}
                    </td>
                    <td className="whitespace-nowrap px-4 py-3">
                      <Badge tone={confidenceTone(proposal.confidence)}>{proposal.confidence}%</Badge>
                    </td>
                    <td className="whitespace-nowrap px-4 py-3 font-mono text-sm text-slate-700">
                      {proposal.occurrenceCount}
                    </td>
                    <td className="whitespace-nowrap px-4 py-3">
                      <Badge tone={statusTone(proposal.status)}>{proposal.status}</Badge>
                    </td>
                    <td className="whitespace-nowrap px-4 py-3 text-right">
                      <span className="text-xs text-slate-400">View</span>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div className="flex items-center justify-between text-sm text-slate-500">
            <p>
              Showing {proposals.data.items.length} of {proposals.data.totalCount} proposals
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

      {detailOpen && (
        <MaintenanceDetailDrawer
          projectId={projectId}
          proposalId={detailOpen}
          onClose={() => setDetailOpen(null)}
          onRefresh={() => proposals.refetch()}
        />
      )}
    </div>
  );
}