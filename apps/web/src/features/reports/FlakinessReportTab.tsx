import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '../../components/ui/card';
import { Badge } from '../../components/ui/badge';
import { Button } from '../../components/ui/button';
import { Input } from '../../components/ui/input';
import { Skeleton } from '../../components/ui/skeleton';
import { ErrorState } from '../../components/common/ErrorState';
import { ApiError, api } from '../../lib/api/client';
import {
  reportEndpoints,
  reportKeys,
  type FlakinessReportFilters,
} from '../../lib/api/endpoints/reports';
import type { DateRange } from '../../lib/api/endpoints/dashboard';

const PAGE_SIZE = 25;
const SORTS = ['flakinessRate', 'testKey', 'title', 'executions', 'lastRun'] as const;

function formatRate(value: number | null | undefined): string {
  if (value === null || value === undefined || Number.isNaN(value)) return '—';
  return `${value.toFixed(1)}%`;
}

function formatTime(iso: string | null | undefined): string {
  if (!iso) return '—';
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? iso : date.toLocaleString();
}

/**
 * Test-level flakiness report (Slice 12). All filtering, sorting, and
 * pagination happen server-side; the CSV export reuses the same filters
 * with a deterministic TestKey order (max 5000 rows).
 */
export function FlakinessReportTab({
  projectId,
  range,
  enabled,
}: {
  projectId: string;
  range: DateRange;
  enabled: boolean;
}) {
  const queryClient = useQueryClient();
  const [page, setPage] = useState(1);
  const [search, setSearch] = useState('');
  const [flakyOnly, setFlakyOnly] = useState(false);
  const [healedOnly, setHealedOnly] = useState(false);
  const [minExecutions, setMinExecutions] = useState('');
  const [module, setModule] = useState('');
  const [priority, setPriority] = useState('');
  const [framework, setFramework] = useState('');
  const [sort, setSort] = useState<string>('flakinessRate');
  const [descending, setDescending] = useState(true);
  const [exportError, setExportError] = useState<string | null>(null);

  const parsedMin = minExecutions.trim() === '' ? undefined : Number(minExecutions);
  const filters: FlakinessReportFilters = {
    ...range,
    search: search.trim() || undefined,
    flakyOnly: flakyOnly || undefined,
    minExecutions:
      parsedMin === undefined || !Number.isFinite(parsedMin) ? undefined : Math.max(0, Math.floor(parsedMin)),
    module: module.trim() || undefined,
    priority: priority || undefined,
    framework: framework.trim() || undefined,
    healedOnly: healedOnly || undefined,
    sort,
    descending: descending || undefined,
  };

  const report = useQuery({
    queryKey: reportKeys.flakiness(projectId, filters, page),
    queryFn: () => reportEndpoints.flakiness(projectId, filters, page, PAGE_SIZE),
    enabled,
    retry: false,
  });

  const download = useMutation({
    mutationFn: async () => {
      const { blob, fileName } = await api.download(
        reportEndpoints.flakinessExportUrl(projectId, { ...filters, sort: 'testKey', descending: false }),
      );
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement('a');
      anchor.href = url;
      anchor.download = fileName;
      document.body.appendChild(anchor);
      anchor.click();
      anchor.remove();
      URL.revokeObjectURL(url);
    },
    onSuccess: () => setExportError(null),
    onError: (error: ApiError) => {
      setExportError(
        error.status === 403
          ? 'You do not have permission to export this report.'
          : 'Export failed. Please try again.',
      );
    },
  });

  const resetPage = () => setPage(1);
  const invalidate = () =>
    void queryClient.invalidateQueries({ queryKey: reportKeys.flakiness(projectId, filters, page) });

  return (
    <Card>
      <CardHeader>
        <CardTitle>Test flakiness</CardTitle>
        <CardDescription>
          Tests with mixed pass/fail outcomes are flaky; consistently failing tests are
          failure-prone, not flaky. Healing activity alongside flakiness is correlation, not causation.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 xl:grid-cols-6">
          <div className="col-span-2">
            <label htmlFor="flaky-search" className="mb-1 block text-sm font-medium text-slate-700">
              Search
            </label>
            <Input
              id="flaky-search"
              value={search}
              onChange={(e) => {
                setSearch(e.target.value);
                resetPage();
              }}
              placeholder="Key or title"
            />
          </div>
          <div>
            <label htmlFor="flaky-min" className="mb-1 block text-sm font-medium text-slate-700">
              Min executions
            </label>
            <Input
              id="flaky-min"
              value={minExecutions}
              onChange={(e) => {
                setMinExecutions(e.target.value);
                resetPage();
              }}
              placeholder="0"
              inputMode="numeric"
            />
          </div>
          <div>
            <label htmlFor="flaky-module" className="mb-1 block text-sm font-medium text-slate-700">
              Module
            </label>
            <Input
              id="flaky-module"
              value={module}
              onChange={(e) => {
                setModule(e.target.value);
                resetPage();
              }}
              placeholder="Any"
            />
          </div>
          <div>
            <label htmlFor="flaky-priority" className="mb-1 block text-sm font-medium text-slate-700">
              Priority
            </label>
            <select
              id="flaky-priority"
              value={priority}
              onChange={(e) => {
                setPriority(e.target.value);
                resetPage();
              }}
              className="w-full rounded-md border border-slate-300 bg-white px-3 py-1.5 text-sm text-slate-900"
            >
              {['', 'Critical', 'High', 'Medium', 'Low'].map((o) => (
                <option key={o} value={o}>
                  {o === '' ? 'All' : o}
                </option>
              ))}
            </select>
          </div>
          <div>
            <label htmlFor="flaky-sort" className="mb-1 block text-sm font-medium text-slate-700">
              Sort
            </label>
            <select
              id="flaky-sort"
              value={sort}
              onChange={(e) => {
                setSort(e.target.value);
                resetPage();
              }}
              className="w-full rounded-md border border-slate-300 bg-white px-3 py-1.5 text-sm text-slate-900"
            >
              {SORTS.map((o) => (
                <option key={o} value={o}>
                  {o}
                </option>
              ))}
            </select>
          </div>
        </div>

        <div className="flex flex-wrap items-center gap-4 text-sm text-slate-700">
          <label className="flex items-center gap-1">
            <input
              type="checkbox"
              checked={flakyOnly}
              onChange={(e) => {
                setFlakyOnly(e.target.checked);
                resetPage();
              }}
            />
            Flaky only
          </label>
          <label className="flex items-center gap-1">
            <input
              type="checkbox"
              checked={healedOnly}
              onChange={(e) => {
                setHealedOnly(e.target.checked);
                resetPage();
              }}
            />
            With healing activity
          </label>
          <label className="flex items-center gap-1">
            <input
              type="checkbox"
              checked={descending}
              onChange={(e) => {
                setDescending(e.target.checked);
                resetPage();
              }}
            />
            Descending
          </label>
          <span className="ml-auto flex items-center gap-2">
            <Input
              aria-label="Framework filter"
              value={framework}
              onChange={(e) => {
                setFramework(e.target.value);
                resetPage();
              }}
              placeholder="Framework"
              className="w-32"
            />
            <Button
              variant="secondary"
              size="sm"
              disabled={download.isPending}
              onClick={() => download.mutate()}
            >
              {download.isPending ? 'Exporting…' : 'Export CSV'}
            </Button>
          </span>
        </div>

        {exportError && (
          <p role="alert" className="text-xs text-rose-600">
            {exportError}
          </p>
        )}

        {report.isLoading ? (
          <div aria-label="Loading flakiness report" className="space-y-2">
            <Skeleton className="h-10" />
            <Skeleton className="h-10" />
            <Skeleton className="h-10" />
          </div>
        ) : report.isError || !report.data ? (
          <ErrorState error={report.error} onRetry={() => invalidate()} />
        ) : report.data.totalCount === 0 ? (
          <p className="text-sm text-slate-500">No data for the selected period and filters.</p>
        ) : (
          <>
            <div className="overflow-x-auto">
              <table className="w-full text-left text-sm">
                <thead>
                  <tr className="border-b border-slate-200 text-xs text-slate-500">
                    <th scope="col" className="py-2 pr-3 font-medium">Test</th>
                    <th scope="col" className="py-2 pr-3 font-medium">Runs</th>
                    <th scope="col" className="py-2 pr-3 font-medium">Pass/Fail</th>
                    <th scope="col" className="py-2 pr-3 font-medium">State</th>
                    <th scope="col" className="py-2 pr-3 font-medium">Flakiness</th>
                    <th scope="col" className="py-2 pr-3 font-medium">Last outcome</th>
                    <th scope="col" className="py-2 font-medium">Healing</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-slate-100">
                  {report.data.items.map((row) => (
                    <tr key={row.testCaseId}>
                      <td className="py-2 pr-3">
                        <p className="font-mono text-xs text-slate-500">{row.testKey}</p>
                        <p className="font-medium text-slate-900">{row.title}</p>
                      </td>
                      <td className="py-2 pr-3 font-mono">{row.totalExecutions}</td>
                      <td className="py-2 pr-3 font-mono">
                        {row.passed}/{row.failed}
                      </td>
                      <td className="py-2 pr-3">
                        <Badge tone={row.isFlaky ? 'warning' : 'neutral'}>
                          {row.isFlaky ? 'Flaky' : 'Stable'}
                        </Badge>
                      </td>
                      <td className="py-2 pr-3 font-mono">{formatRate(row.flakinessRate)}</td>
                      <td className="py-2 pr-3 text-xs text-slate-600">
                        {row.lastOutcome ?? '—'}
                        <span className="block font-mono text-slate-400">{formatTime(row.lastRunAt)}</span>
                      </td>
                      <td className="py-2 font-mono text-xs text-slate-600">
                        {row.healingAttempts === 0 ? '—' : `${row.healedRuns}/${row.healingAttempts} recovered`}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <div className="mt-3 flex items-center justify-between gap-2">
              <p className="text-xs text-slate-500" aria-live="polite">
                Page {report.data.page} of {Math.max(1, Math.ceil(report.data.totalCount / report.data.pageSize))} ·{' '}
                {report.data.totalCount} total
              </p>
              <div className="flex gap-2">
                <Button
                  variant="secondary"
                  size="sm"
                  disabled={page <= 1}
                  onClick={() => setPage(page - 1)}
                >
                  Previous
                </Button>
                <Button
                  variant="secondary"
                  size="sm"
                  disabled={page >= Math.ceil(report.data.totalCount / report.data.pageSize)}
                  onClick={() => setPage(page + 1)}
                >
                  Next
                </Button>
              </div>
            </div>
          </>
        )}
      </CardContent>
    </Card>
  );
}
