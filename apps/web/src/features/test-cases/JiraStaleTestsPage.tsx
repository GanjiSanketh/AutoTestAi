import { useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { RefreshCw, ChevronLeft, ChevronRight } from 'lucide-react';
import { Button } from '../../components/ui/button';
import { Badge } from '../../components/ui/badge';
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from '../../components/ui/card';
import { Input } from '../../components/ui/input';
import { Skeleton } from '../../components/ui/skeleton';
import { ErrorState } from '../../components/common/ErrorState';
import {
  testcasesEndpoints,
} from '../../lib/api/endpoints/testcases';

const STATE_TONES: Record<string, 'success' | 'warning' | 'danger' | 'neutral'> = {
  changed: 'warning',
  stale: 'danger',
  neverChecked: 'neutral',
  current: 'success',
};

const STATE_LABELS: Record<string, string> = {
  changed: 'Changed',
  stale: 'Stale',
  neverChecked: 'Never checked',
  current: 'Current',
};

function formatTime(iso: string | null): string {
  if (!iso) return '—';
  try {
    return new Date(iso).toLocaleString();
  } catch {
    return iso;
  }
}

export function JiraStaleTestsPage() {
  const { projectId = '' } = useParams();
  const navigate = useNavigate();

  const [filters, setFilters] = useState<{ freshnessState?: string; search?: string }>({});
  const [page, setPage] = useState(1);
  const pageSize = 25;

  const staleTests = useQuery({
    queryKey: ['stale-jira-tests', projectId, filters, page],
    queryFn: () => testcasesEndpoints.getStaleJiraTests(projectId, filters, page),
    enabled: !!projectId,
    retry: false,
  });

  const handleFilterChange = (key: keyof typeof filters, value: string) => {
    setFilters((prev) => ({ ...prev, [key]: value || undefined }));
    setPage(1);
  };

  const handleSearch = (e: React.FormEvent) => {
    e.preventDefault();
    setPage(1);
  };

  if (staleTests.isLoading && page === 1) {
    return (
      <div className="space-y-4" aria-label="Loading stale Jira tests">
        <Skeleton className="h-8 w-64" />
        <div className="grid grid-cols-1 gap-4">
          {[0, 1, 2, 3].map((i) => (
            <Skeleton key={i} className="h-16" />
          ))}
        </div>
      </div>
    );
  }

  if (staleTests.isError || !staleTests.data) {
    return (
      <div className="space-y-6">
        <ErrorState
          error={staleTests.error}
          onRetry={() => void staleTests.refetch()}
          notFoundMessage="Unable to load stale Jira tests."
        />
      </div>
    );
  }

  const data = staleTests.data!;
  const totalPages = Math.ceil(data.totalCount / pageSize);

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <Link to={`/projects/${projectId}/dashboard`} className="text-sm text-brand-700 hover:text-brand-600">
            ← Back to dashboard
          </Link>
          <h1 className="mt-2 text-xl font-semibold text-slate-900">Jira-Origin Tests</h1>
          <p className="mt-1 text-sm text-slate-500">
            Tests generated from Jira issues with their freshness status. Check for changes to get fresh proposals.
          </p>
        </div>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Filters</CardTitle>
        </CardHeader>
        <CardContent>
          <form onSubmit={handleSearch} className="flex flex-wrap items-end gap-4">
            <div className="flex-1 min-w-[200px]">
              <label htmlFor="stale-search" className="mb-1 block text-sm font-medium text-slate-700">
                Search
              </label>
              <Input
                id="stale-search"
                type="text"
                placeholder="Test key, title, or Jira issue key"
                value={filters.search ?? ''}
                onChange={(e) => handleFilterChange('search', e.target.value)}
              />
            </div>
            <div>
              <label htmlFor="stale-freshness" className="mb-1 block text-sm font-medium text-slate-700">
                Freshness state
              </label>
              <select
                id="stale-freshness"
                value={filters.freshnessState ?? ''}
                onChange={(e) => handleFilterChange('freshnessState', e.target.value)}
                className="w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 focus:border-brand-500"
              >
                <option value="">All states</option>
                <option value="changed">Changed</option>
                <option value="neverChecked">Never checked</option>
                <option value="stale">Stale</option>
                <option value="current">Current</option>
              </select>
            </div>
          </form>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Jira-Origin Tests</CardTitle>
          <CardDescription>
            {data.totalCount} test case(s) with Jira provenance on their latest version.
          </CardDescription>
        </CardHeader>
        <CardContent>
          {data.items.length === 0 ? (
            <p className="text-sm text-slate-500">No Jira-origin tests found for the current filters.</p>
          ) : (
            <div className="space-y-4">
              <div className="overflow-x-auto">
                <table className="w-full" role="table">
                  <thead>
                    <tr className="border-b border-slate-200">
                      <th className="text-left py-2 px-3 text-sm font-medium text-slate-500">Test Case</th>
                      <th className="text-left py-2 px-3 text-sm font-medium text-slate-500">Version</th>
                      <th className="text-left py-2 px-3 text-sm font-medium text-slate-500">Jira Issue</th>
                      <th className="text-left py-2 px-3 text-sm font-medium text-slate-500">Freshness</th>
                      <th className="text-left py-2 px-3 text-sm font-medium text-slate-500">Last Checked</th>
                      <th className="text-left py-2 px-3 text-sm font-medium text-slate-500">Changed Fields</th>
                      <th className="text-right py-2 px-3 text-sm font-medium text-slate-500">Actions</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-slate-100">
                    {data.items.map((item) => (
                      <tr key={item.testCaseId} className="hover:bg-slate-50">
                        <td className="py-3 px-3">
                          <Link
                            to={`/projects/${projectId}/test-cases/${item.testCaseId}`}
                            className="font-medium text-brand-700 hover:text-brand-600"
                          >
                            {item.testKey}
                          </Link>
                          <p className="text-sm text-slate-500 truncate max-w-xs">{item.title}</p>
                        </td>
                        <td className="py-3 px-3 text-sm text-slate-500">
                          v{item.versionNumber}
                        </td>
                        <td className="py-3 px-3">
                          <Link
                            to={`/projects/${projectId}/test-cases/generate-story?issueKey=${encodeURIComponent(item.jiraIssueKey)}`}
                            className="font-mono text-sm text-brand-700 hover:text-brand-600"
                          >
                            {item.jiraIssueKey}
                          </Link>
                        </td>
                        <td className="py-3 px-3">
                          <Badge tone={STATE_TONES[item.freshnessState]}>
                            {STATE_LABELS[item.freshnessState] ?? item.freshnessState}
                          </Badge>
                        </td>
                        <td className="py-3 px-3 text-sm text-slate-500 font-mono">
                          {formatTime(item.lastCheckedAt)}
                        </td>
                        <td className="py-3 px-3 text-sm text-slate-500">
                          {item.changedFieldCount !== null && item.changedFieldCount > 0 ? (
                            <span className="font-mono">{item.changedFieldCount}</span>
                          ) : (
                            '—'
                          )}
                        </td>
                        <td className="py-3 px-3 text-right">
                          {item.freshnessState === 'changed' && (
                            <Link
                              to={`/projects/${projectId}/test-cases/generate-story?issueKey=${encodeURIComponent(item.jiraIssueKey)}`}
                              className="inline-flex items-center gap-1 text-sm font-medium text-brand-700 hover:text-brand-600"
                            >
                              <RefreshCw className="h-4 w-4" aria-hidden />
                              Fresh proposals
                            </Link>
                          )}
                          {item.freshnessState === 'stale' && (
                            <Button
                              variant="secondary"
                              size="sm"
                              onClick={() => navigate(`/projects/${projectId}/test-cases/${item.testCaseId}`)}
                            >
                              View test case
                            </Button>
                          )}
                          {item.freshnessState === 'neverChecked' && (
                            <Button
                              variant="secondary"
                              size="sm"
                              onClick={() => navigate(`/projects/${projectId}/test-cases/${item.testCaseId}`)}
                            >
                              Check freshness
                            </Button>
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>

              {totalPages > 1 && (
                <div className="mt-4 flex items-center justify-between">
                  <p className="text-sm text-slate-500">
                    Page {page} of {totalPages} — {data.totalCount} total
                  </p>
                  <div className="flex items-center gap-2">
                    <Button
                      variant="secondary"
                      size="sm"
                      disabled={page === 1}
                      onClick={() => setPage((p) => p - 1)}
                    >
                      <ChevronLeft className="h-4 w-4" aria-hidden />
                      Previous
                    </Button>
                    <Button
                      variant="secondary"
                      size="sm"
                      disabled={page === totalPages}
                      onClick={() => setPage((p) => p + 1)}
                    >
                      Next
                      <ChevronRight className="h-4 w-4" aria-hidden />
                    </Button>
                  </div>
                </div>
              )}

            </div>
          )}
        </CardContent>
      </Card>
    </div>
  );
}