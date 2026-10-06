import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { FlaskConical, Plus, Sparkles } from 'lucide-react';
import { Button } from '../../components/ui/button';
import { Input } from '../../components/ui/input';
import { Badge } from '../../components/ui/badge';
import { Skeleton } from '../../components/ui/skeleton';
import { Card, CardContent } from '../../components/ui/card';
import { ErrorState } from '../../components/common/ErrorState';
import { testcaseKeys, testcasesEndpoints, type TestCaseFilters } from '../../lib/api/endpoints/testcases';
import { projectsEndpoints, projectKeys } from '../../lib/api/endpoints/projects';
import { useProfile } from '../../lib/auth/useProfile';
import { Permissions, hasPermission } from '../../lib/auth/permissions';
import { reviewTone } from './VersionHistory';

const PAGE_SIZE = 25;

function statusTone(status: string): 'success' | 'warning' | 'info' | 'neutral' {
  switch (status) {
    case 'Active':
      return 'success';
    case 'Draft':
      return 'info';
    case 'Deprecated':
      return 'warning';
    default:
      return 'neutral';
  }
}

function priorityTone(priority: string): 'danger' | 'warning' | 'info' | 'neutral' {
  switch (priority) {
    case 'Critical':
      return 'danger';
    case 'High':
      return 'warning';
    case 'Medium':
      return 'info';
    default:
      return 'neutral';
  }
}

const STATUS_OPTIONS = ['', 'Draft', 'Active', 'Deprecated', 'Archived'];
const PRIORITY_OPTIONS = ['', 'Critical', 'High', 'Medium', 'Low'];
const REVIEW_OPTIONS = ['', 'Pending', 'Approved', 'ChangesRequested', 'Rejected'];

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

/** Project-scoped test repository: table, search, filters, pagination (docs/02 §9). */
export function TestCaseListPage() {
  const { projectId = '' } = useParams();
  const profile = useProfile();
  const canManage = hasPermission(profile.data?.permissions, Permissions.TestCasesManage);

  const [search, setSearch] = useState('');
  const [filters, setFilters] = useState<TestCaseFilters>({});
  const [committed, setCommitted] = useState<{ search: string; filters: TestCaseFilters }>({ search: '', filters: {} });
  const [page, setPage] = useState(1);

  const project = useQuery({
    queryKey: projectKeys.details(projectId),
    queryFn: () => projectsEndpoints.get(projectId),
    enabled: !!projectId,
    retry: false,
    staleTime: 60_000,
  });

  const cases = useQuery({
    queryKey: testcaseKeys.list(projectId, { ...committed.filters, search: committed.search }, page),
    queryFn: () => testcasesEndpoints.list(projectId, { ...committed.filters, search: committed.search }, page, PAGE_SIZE),
    enabled: !!projectId,
    staleTime: 15_000,
  });

  const applyFilters = (e: React.FormEvent) => {
    e.preventDefault();
    setPage(1);
    setCommitted({ search, filters });
  };

  const setFilter = (key: keyof TestCaseFilters) => (value: string) => {
    setFilters((f) => ({ ...f, [key]: value || undefined }));
    setPage(1);
    setCommitted((c) => ({ ...c, filters: { ...c.filters, [key]: value || undefined } }));
  };

  const totalPages = cases.data ? Math.max(1, Math.ceil(cases.data.totalCount / cases.data.pageSize)) : 1;

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <Link to={`/projects/${projectId}`} className="text-sm text-brand-700 hover:text-brand-600">
            ← {project.data?.name ?? 'Back to project'}
          </Link>
          <h1 className="mt-2 text-xl font-semibold text-slate-900">Test Cases</h1>
          <p className="mt-1 text-sm text-slate-500">
            Project-scoped source of truth for automated tests. Content edits create new versions.
          </p>
        </div>
        <div className="flex gap-2">
          {canManage ? (
            <Link to={`/projects/${projectId}/test-cases/generate`}>
              <Button variant="ai">
                <Sparkles className="h-4 w-4" aria-hidden />
                AI Case Generator
              </Button>
            </Link>
          ) : (
            <Button variant="ai" disabled title="Generating tests requires the testcases.manage permission">
              <Sparkles className="h-4 w-4" aria-hidden />
              AI Case Generator
            </Button>
          )}
          {canManage && (
            <Link to={`/projects/${projectId}/test-cases/new`}>
              <Button>
                <Plus className="h-4 w-4" aria-hidden />
                New test case
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
              aria-label="Search test cases"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Key, title or module…"
            />
            <Button type="submit" variant="secondary">
              Search
            </Button>
          </span>
        </label>
        <FilterSelect label="Status" value={filters.status ?? ''} options={STATUS_OPTIONS} onChange={setFilter('status')} />
        <FilterSelect label="Priority" value={filters.priority ?? ''} options={PRIORITY_OPTIONS} onChange={setFilter('priority')} />
        <FilterSelect label="Review" value={filters.reviewStatus ?? ''} options={REVIEW_OPTIONS} onChange={setFilter('reviewStatus')} />
        <label className="flex min-w-36 flex-1 flex-col gap-1 text-xs font-medium text-slate-500 sm:flex-none">
          Framework
          <Input
            aria-label="Framework filter"
            value={filters.framework ?? ''}
            onChange={(e) => setFilter('framework')(e.target.value)}
            placeholder="e.g. playwright"
          />
        </label>
        <label className="flex min-w-36 flex-1 flex-col gap-1 text-xs font-medium text-slate-500 sm:flex-none">
          Platform
          <Input
            aria-label="Platform filter"
            value={filters.platform ?? ''}
            onChange={(e) => setFilter('platform')(e.target.value)}
            placeholder="e.g. web"
          />
        </label>
        <label className="flex min-w-36 flex-1 flex-col gap-1 text-xs font-medium text-slate-500 sm:flex-none">
          Jira issue key
          <Input
            aria-label="Jira issue key filter"
            value={filters.jiraIssueKey ?? ''}
            onChange={(e) => setFilter('jiraIssueKey')(e.target.value)}
            placeholder="PROJ-123"
            maxLength={30}
          />
        </label>
      </form>

      {committed.filters.jiraIssueKey && (
        <p className="text-sm text-slate-600" aria-label="Active Jira filter">
          Showing tests from <strong className="font-mono">{committed.filters.jiraIssueKey}</strong> — historical
          Jira traceability, not a live Jira query.
        </p>
      )}

      {cases.isLoading && (
        <div className="space-y-2" aria-label="Loading test cases">
          {[0, 1, 2, 3, 4].map((i) => (
            <Skeleton key={i} className="h-14" />
          ))}
        </div>
      )}

      {cases.isError && (
        <ErrorState error={cases.error} onRetry={() => void cases.refetch()} />
      )}

      {cases.data && cases.data.items.length === 0 && (
        <Card>
          <CardContent className="flex flex-col items-center gap-3 py-12 text-center">
            <span className="flex h-11 w-11 items-center justify-center rounded-full bg-slate-100">
              <FlaskConical className="h-5 w-5 text-slate-500" aria-hidden />
            </span>
            <h2 className="text-base font-semibold text-slate-900">No test cases found</h2>
            <p className="max-w-md text-sm text-slate-500">
              {committed.filters.jiraIssueKey
                ? `No tests were generated from ${committed.filters.jiraIssueKey}.`
                : committed.search || Object.keys(committed.filters).length > 0
                  ? 'No test cases match the current search or filters.'
                  : 'This project has no test cases yet. Create the first one to start the repository.'}
            </p>
            {canManage && (
              <Link to={`/projects/${projectId}/test-cases/new`}>
                <Button size="sm">
                  <Plus className="h-4 w-4" aria-hidden />
                  New test case
                </Button>
              </Link>
            )}
          </CardContent>
        </Card>
      )}

      {cases.data && cases.data.items.length > 0 && (
        <>
          <div className="overflow-x-auto rounded-lg border border-slate-200 bg-white shadow-sm">
            <table className="w-full min-w-3xl text-left text-sm">
              <thead>
                <tr className="border-b border-slate-200 bg-slate-50 text-xs uppercase tracking-wider text-slate-500">
                  <th scope="col" className="px-4 py-3 font-medium">Test ID</th>
                  <th scope="col" className="px-4 py-3 font-medium">Title</th>
                  <th scope="col" className="px-4 py-3 font-medium">Module</th>
                  <th scope="col" className="px-4 py-3 font-medium">Framework</th>
                  <th scope="col" className="px-4 py-3 font-medium">Priority</th>
                  <th scope="col" className="px-4 py-3 font-medium">Status</th>
                  <th scope="col" className="px-4 py-3 font-medium">Review</th>
                  <th scope="col" className="px-4 py-3 font-medium"><span className="sr-only">Actions</span></th>
                </tr>
              </thead>
              <tbody className="divide-y divide-slate-100">
                {cases.data.items.map((testCase) => (
                  <tr key={testCase.id} className="hover:bg-slate-50">
                    <td className="whitespace-nowrap px-4 py-3 font-mono text-xs text-slate-700">
                      {testCase.testKey}
                    </td>
                    <td className="max-w-xs truncate px-4 py-3 font-medium text-slate-900">
                      {testCase.title}
                    </td>
                    <td className="whitespace-nowrap px-4 py-3 text-slate-500">
                      {testCase.module ?? '—'}
                    </td>
                    <td className="whitespace-nowrap px-4 py-3 font-mono text-xs text-slate-500">
                      {testCase.framework ?? '—'}
                    </td>
                    <td className="whitespace-nowrap px-4 py-3">
                      <Badge tone={priorityTone(testCase.priority)}>{testCase.priority}</Badge>
                    </td>
                    <td className="whitespace-nowrap px-4 py-3">
                      <Badge tone={statusTone(testCase.status)}>{testCase.status}</Badge>
                    </td>
                    <td className="whitespace-nowrap px-4 py-3">
                      <Badge tone={reviewTone(testCase.latestReviewStatus)}>
                        {testCase.latestReviewStatus} · v{testCase.latestVersionNumber}
                      </Badge>
                    </td>
                    <td className="whitespace-nowrap px-4 py-3 text-right">
                      <Link
                        to={`/projects/${projectId}/test-cases/${testCase.id}`}
                        className="text-sm font-medium text-brand-700 hover:text-brand-600"
                      >
                        View
                      </Link>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div className="flex items-center justify-between text-sm text-slate-500">
            <p>
              Showing {cases.data.items.length} of {cases.data.totalCount} test cases
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
