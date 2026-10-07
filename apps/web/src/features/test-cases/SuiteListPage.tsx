import { useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Plus, Play, Archive, Search } from 'lucide-react';
import { Button } from '../../components/ui/button';
import { Input } from '../../components/ui/input';
import { Badge } from '../../components/ui/badge';
import { Skeleton } from '../../components/ui/skeleton';
import { Card, CardContent } from '../../components/ui/card';
import { ErrorState } from '../../components/common/ErrorState';
import { suiteKeys, suitesEndpoints, type SuiteFilters } from '../../lib/api/endpoints/suites';
import { projectsEndpoints, projectKeys } from '../../lib/api/endpoints/projects';
import { useProfile } from '../../lib/auth/useProfile';
import { Permissions, hasPermission } from '../../lib/auth/permissions';

const PAGE_SIZE = 25;

function statusTone(status: string): 'success' | 'warning' | 'info' | 'neutral' {
  switch (status) {
    case 'Active':
      return 'success';
    case 'Archived':
      return 'neutral';
    default:
      return 'info';
  }
}

const STATUS_OPTIONS = ['', 'Active', 'Archived'];

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

export function SuiteListPage() {
  const { projectId = '' } = useParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const profile = useProfile();
  const canManage = hasPermission(profile.data?.permissions, Permissions.TestCasesManage);

  const [search, setSearch] = useState('');
  const [filters, setFilters] = useState<SuiteFilters>({});
  const [committed, setCommitted] = useState<{ search: string; filters: SuiteFilters }>({ search: '', filters: {} });
  const [page, setPage] = useState(1);

  const project = useQuery({
    queryKey: projectKeys.details(projectId),
    queryFn: () => projectsEndpoints.get(projectId),
    enabled: !!projectId,
    retry: false,
    staleTime: 60_000,
  });

  const suites = useQuery({
    queryKey: suiteKeys.list(projectId, { ...committed.filters, search: committed.search }, page),
    queryFn: () => suitesEndpoints.list(projectId, { ...committed.filters, search: committed.search }, page, PAGE_SIZE),
    enabled: !!projectId,
    staleTime: 15_000,
  });

  const applyFilters = (e: React.FormEvent) => {
    e.preventDefault();
    setPage(1);
    setCommitted({ search, filters });
  };

  const setFilter = (key: keyof SuiteFilters) => (value: string) => {
    setFilters((f) => ({ ...f, [key]: value || undefined }));
    setPage(1);
    setCommitted((c) => ({ ...c, filters: { ...c.filters, [key]: value || undefined } }));
  };

  const totalPages = suites.data ? Math.max(1, Math.ceil(suites.data.totalCount / suites.data.pageSize)) : 1;

  const [actionError, setActionError] = useState<string | null>(null);

  const executeMutation = useMutation({
    mutationFn: (suiteId: string) => suitesEndpoints.execute({ projectId, suiteId }),
    onSuccess: (result) => {
      setActionError(null);
      queryClient.invalidateQueries({ queryKey: suiteKeys.all });
      navigate(`/projects/${projectId}/executions/${result.executionId}`);
    },
    onError: (error: Error) => setActionError(error.message),
  });

  const archiveMutation = useMutation({
    mutationFn: (suiteId: string) => suitesEndpoints.archive(suiteId),
    onSuccess: () => {
      setActionError(null);
      queryClient.invalidateQueries({ queryKey: suiteKeys.all });
    },
    onError: (error: Error) => setActionError(error.message),
  });

  const handleRun = (suiteId: string, testCount: number) => {
    if (testCount === 0) {
      setActionError('Cannot execute an empty suite. Add test cases first.');
      return;
    }
    if (!confirm(`Run this suite now? This will start ${testCount} execution(s).`)) return;
    executeMutation.mutate(suiteId);
  };

  const handleArchive = (suiteId: string, name: string) => {
    if (!confirm(`Archive suite "${name}"? Archived suites become read-only.`)) return;
    archiveMutation.mutate(suiteId);
  };

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <Link to={`/projects/${projectId}`} className="text-sm text-brand-700 hover:text-brand-600">
            ← {project.data?.name ?? 'Back to project'}
          </Link>
          <h1 className="mt-2 text-xl font-semibold text-slate-900">Test Suites</h1>
          <p className="mt-1 text-sm text-slate-500">
            Organize test cases into executable suites. Manual run fans out to individual executions.
          </p>
        </div>
        <div className="flex gap-2">
          {canManage && (
            <Link to={`/projects/${projectId}/test-suites/new`}>
              <Button>
                <Plus className="h-4 w-4" aria-hidden />
                New test suite
              </Button>
            </Link>
          )}
        </div>
      </div>

      <form onSubmit={applyFilters} className="flex flex-wrap items-end gap-2">
        <label className="flex min-w-48 flex-1 flex-col gap-1 text-xs font-medium text-slate-500 sm:flex-none sm:basis-64">
          Search
          <span className="flex gap-2">
            <Input
              aria-label="Search test suites"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Suite name or description…"
            />
            <Button type="submit" variant="secondary">
              <Search className="h-4 w-4" aria-hidden />
            </Button>
          </span>
        </label>
        <FilterSelect label="Status" value={filters.status ?? ''} options={STATUS_OPTIONS} onChange={setFilter('status')} />
      </form>

      {actionError && (
        <div role="alert" className="rounded-md border border-red-200 bg-red-50 p-3 text-sm text-red-700">
          {actionError}
        </div>
      )}

      {suites.isLoading && (
        <div className="space-y-2" aria-label="Loading test suites">
          {[0, 1, 2, 3, 4].map((i) => (
            <Skeleton key={i} className="h-14" />
          ))}
        </div>
      )}

      {suites.isError && (
        <ErrorState error={suites.error} onRetry={() => void suites.refetch()} />
      )}

      {suites.data && suites.data.items.length === 0 && (
        <Card>
          <CardContent className="flex flex-col items-center gap-3 py-12 text-center">
            <span className="flex h-11 w-11 items-center justify-center rounded-full bg-slate-100">
              <Play className="h-5 w-5 text-slate-500" aria-hidden />
            </span>
            <h2 className="text-base font-semibold text-slate-900">No test suites found</h2>
            <p className="max-w-md text-sm text-slate-500">
              {committed.search || Object.keys(committed.filters).length > 0
                ? 'No test suites match the current search or filters.'
                : 'This project has no test suites yet. Create the first one to start organizing tests.'}
            </p>
            {canManage && (
              <Link to={`/projects/${projectId}/test-suites/new`}>
                <Button size="sm">
                  <Plus className="h-4 w-4" aria-hidden />
                  New test suite
                </Button>
              </Link>
            )}
          </CardContent>
        </Card>
      )}

      {suites.data && suites.data.items.length > 0 && (
        <>
          <div className="overflow-x-auto rounded-lg border border-slate-200 bg-white shadow-sm">
            <table className="w-full min-w-3xl text-left text-sm">
              <thead>
                <tr className="border-b border-slate-200 bg-slate-50 text-xs uppercase tracking-wider text-slate-500">
                  <th scope="col" className="px-4 py-3 font-medium">Suite</th>
                  <th scope="col" className="px-4 py-3 font-medium">Description</th>
                  <th scope="col" className="px-4 py-3 font-medium">Status</th>
                  <th scope="col" className="px-4 py-3 font-medium">Tests</th>
                  <th scope="col" className="px-4 py-3 font-medium">Updated</th>
                  <th scope="col" className="px-4 py-3 font-medium"><span className="sr-only">Actions</span></th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {suites.data.items.map((suite) => (
                  <tr key={suite.id} className="hover:bg-slate-50">
                    <td className="whitespace-nowrap px-4 py-3 font-mono text-xs text-slate-700">
                      {suite.name}
                    </td>
                    <td className="max-w-xs truncate px-4 py-3 text-slate-500">
                      {suite.description ?? '—'}
                    </td>
                    <td className="whitespace-nowrap px-4 py-3">
                      <Badge tone={statusTone(suite.status)}>{suite.status}</Badge>
                    </td>
                    <td className="whitespace-nowrap px-4 py-3 text-right font-mono text-sm text-slate-700">
                      {suite.testCount}
                    </td>
                    <td className="whitespace-nowrap px-4 py-3 text-slate-500">
                      {new Date(suite.updatedAt).toLocaleDateString()}
                    </td>
                    <td className="whitespace-nowrap px-4 py-3 text-right">
                      <div className="flex items-center justify-end gap-1">
                        <Link
                          to={`/projects/${projectId}/test-suites/${suite.id}`}
                          className="text-sm font-medium text-brand-700 hover:text-brand-600"
                        >
                          View
                        </Link>
                        {canManage && suite.status === 'Active' && (
                          <Button
                            variant="secondary"
                            size="sm"
                            className="ml-1"
                            disabled={executeMutation.isPending}
                            onClick={() => handleRun(suite.id, suite.testCount)}
                            aria-label={`Run suite ${suite.name}`}
                          >
                            <Play className="h-3 w-3" aria-hidden />
                            Run
                          </Button>
                        )}
                        {canManage && suite.status === 'Active' && (
                          <Button
                            variant="ghost"
                            size="sm"
                            className="ml-1"
                            disabled={archiveMutation.isPending}
                            onClick={() => handleArchive(suite.id, suite.name)}
                            aria-label={`Archive suite ${suite.name}`}
                          >
                            <Archive className="h-3 w-3" aria-hidden />
                          </Button>
                        )}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div className="flex items-center justify-between text-sm text-slate-500">
            <p>
              Showing {suites.data.items.length} of {suites.data.totalCount} test suites
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