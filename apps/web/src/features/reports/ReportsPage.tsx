import { useEffect, useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { Card, CardContent, CardHeader, CardTitle } from '../../components/ui/card';
import { Badge } from '../../components/ui/badge';
import { Button } from '../../components/ui/button';
import { Input } from '../../components/ui/input';
import { Skeleton } from '../../components/ui/skeleton';
import { ErrorState } from '../../components/common/ErrorState';
import { reportEndpoints, reportKeys } from '../../lib/api/endpoints/reports';
import type { DateRange } from '../../lib/api/endpoints/dashboard';
import { projectsEndpoints } from '../../lib/api/endpoints/projects';
import { useProfile } from '../../lib/auth/useProfile';
import { Permissions, hasPermission } from '../../lib/auth/permissions';
import { useAppStore } from '../../stores/useAppStore';
import { defectStatusTone, severityTone } from '../bugs/DefectsListPage';

const PAGE_SIZE = 25;
const EXECUTION_STATUSES = ['', 'Passed', 'Failed', 'Cancelled', 'TimedOut', 'Error', 'Queued', 'Running'];
const CLASSIFICATIONS = ['', 'ApplicationDefect', 'EnvironmentFailure', 'AutomationFailure', 'TestFailure', 'Unknown'];
const DEFECT_STATUSES = ['', 'Open', 'InProgress', 'Resolved', 'Closed', 'Rejected'];
const SEVERITIES = ['', 'Critical', 'High', 'Medium', 'Low'];
const SYNC_STATUSES = ['', 'Pending', 'Synced', 'Failed'];

type Tab = 'executions' | 'defects' | 'tickets';

function formatTime(iso: string | null): string {
  if (!iso) return '—';
  try {
    return new Date(iso).toLocaleString();
  } catch {
    return iso;
  }
}

function FilterSelect({
  id,
  label,
  value,
  options,
  onChange,
}: {
  id: string;
  label: string;
  value: string;
  options: readonly string[];
  onChange: (value: string) => void;
}) {
  return (
    <div>
      <label htmlFor={id} className="mb-1 block text-sm font-medium text-slate-700">
        {label}
      </label>
      <select
        id={id}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        className="rounded-md border border-slate-300 bg-white px-3 py-1.5 text-sm text-slate-900"
      >
        {options.map((o) => (
          <option key={o} value={o}>
            {o === '' ? 'All' : o}
          </option>
        ))}
      </select>
    </div>
  );
}

function Pager({
  page,
  totalCount,
  pageSize,
  onPage,
}: {
  page: number;
  totalCount: number;
  pageSize: number;
  onPage: (page: number) => void;
}) {
  const totalPages = Math.max(1, Math.ceil(totalCount / pageSize));
  return (
    <div className="mt-3 flex items-center justify-between gap-2">
      <p className="text-xs text-slate-500" aria-live="polite">
        Page {page} of {totalPages} · {totalCount} total
      </p>
      <div className="flex gap-2">
        <Button variant="secondary" size="sm" disabled={page <= 1} onClick={() => onPage(page - 1)}>
          Previous
        </Button>
        <Button
          variant="secondary"
          size="sm"
          disabled={page >= totalPages}
          onClick={() => onPage(page + 1)}
        >
          Next
        </Button>
      </div>
    </div>
  );
}

/**
 * Operational reports (Slice 8). Server-side filtering and pagination over
 * persisted data; ticket data comes from internal records only.
 */
export function ReportsPage() {
  const profile = useProfile();
  const canRead = hasPermission(profile.data?.permissions, Permissions.ReportsRead);
  const currentProjectId = useAppStore((s) => s.currentProjectId);
  const setCurrentProjectId = useAppStore((s) => s.setCurrentProjectId);

  const [projectId, setProjectId] = useState<string | null>(currentProjectId);
  const [tab, setTab] = useState<Tab>('executions');
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [page, setPage] = useState(1);

  const [execStatus, setExecStatus] = useState('');
  const [execClassification, setExecClassification] = useState('');
  const [defectStatus, setDefectStatus] = useState('');
  const [defectSeverity, setDefectSeverity] = useState('');
  const [defectClassification, setDefectClassification] = useState('');
  const [defectSearch, setDefectSearch] = useState('');
  const [ticketProvider, setTicketProvider] = useState('');
  const [ticketSync, setTicketSync] = useState('');

  const projects = useQuery({
    queryKey: ['projects', 'list', '', 1],
    queryFn: () => projectsEndpoints.list('', 1, 100),
    retry: false,
    staleTime: 60_000,
  });

  useEffect(() => {
    if (!projectId && currentProjectId) setProjectId(currentProjectId);
  }, [projectId, currentProjectId]);

  useEffect(() => {
    if (!projectId && !currentProjectId && (projects.data?.items.length ?? 0) > 0) {
      setProjectId(projects.data!.items[0].id);
    }
  }, [projectId, currentProjectId, projects.data]);

  const range: DateRange = useMemo(
    () => ({
      ...(from ? { from } : {}),
      ...(to ? { to } : {}),
    }),
    [from, to],
  );

  const enabled = !!projectId && canRead;
  const resetPage = () => setPage(1);

  const selectProject = (id: string) => {
    setProjectId(id || null);
    setCurrentProjectId(id || null);
    resetPage();
  };

  const execFilters = useMemo(
    () => ({ ...range, status: execStatus || undefined, classification: execClassification || undefined }),
    [range, execStatus, execClassification],
  );
  const defectFilters = useMemo(
    () => ({
      ...range,
      status: defectStatus || undefined,
      severity: defectSeverity || undefined,
      classification: defectClassification || undefined,
      search: defectSearch.trim() || undefined,
    }),
    [range, defectStatus, defectSeverity, defectClassification, defectSearch],
  );
  const ticketFilters = useMemo(
    () => ({ ...range, provider: ticketProvider.trim() || undefined, syncStatus: ticketSync || undefined }),
    [range, ticketProvider, ticketSync],
  );

  const executions = useQuery({
    queryKey: projectId ? reportKeys.executions(projectId, execFilters, page) : ['reports', 'executions', 'none'],
    queryFn: () => reportEndpoints.executions(projectId!, execFilters, page, PAGE_SIZE),
    enabled: enabled && tab === 'executions',
    retry: false,
  });
  const defects = useQuery({
    queryKey: projectId ? reportKeys.defects(projectId, defectFilters, page) : ['reports', 'defects', 'none'],
    queryFn: () => reportEndpoints.defects(projectId!, defectFilters, page, PAGE_SIZE),
    enabled: enabled && tab === 'defects',
    retry: false,
  });
  const tickets = useQuery({
    queryKey: projectId ? reportKeys.tickets(projectId, ticketFilters, page) : ['reports', 'tickets', 'none'],
    queryFn: () => reportEndpoints.tickets(projectId!, ticketFilters, page, PAGE_SIZE),
    enabled: enabled && tab === 'tickets',
    retry: false,
  });

  const tabs: { id: Tab; label: string }[] = [
    { id: 'executions', label: 'Executions' },
    { id: 'defects', label: 'Defects' },
    { id: 'tickets', label: 'Tickets' },
  ];

  if (!canRead && !profile.isLoading) {
    return (
      <div className="space-y-6">
        <h1 className="text-xl font-semibold text-slate-900">Reports</h1>
        <ErrorState error={null} notFoundMessage="You do not have access to reports." />
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h1 className="text-xl font-semibold text-slate-900">Reports</h1>
          <p className="mt-1 text-sm text-slate-500">
            Filtered operational history. Dates are UTC; ranges are limited to 365 days.
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <label htmlFor="reports-project" className="text-sm font-medium text-slate-700">
            Project
          </label>
          <select
            id="reports-project"
            value={projectId ?? ''}
            onChange={(e) => selectProject(e.target.value)}
            className="rounded-md border border-slate-300 bg-white px-3 py-1.5 text-sm text-slate-900"
          >
            <option value="">Select a project…</option>
            {(projects.data?.items ?? []).map((p) => (
              <option key={p.id} value={p.id}>
                {p.name} ({p.key})
              </option>
            ))}
          </select>
        </div>
      </div>

      <div className="flex gap-1 border-b border-slate-200" role="tablist" aria-label="Report types">
        {tabs.map((t) => (
          <button
            key={t.id}
            type="button"
            role="tab"
            aria-selected={tab === t.id}
            onClick={() => {
              setTab(t.id);
              resetPage();
            }}
            className={`-mb-px border-b-2 px-4 py-2 text-sm font-medium ${
              tab === t.id
                ? 'border-brand-600 text-brand-700'
                : 'border-transparent text-slate-500 hover:text-slate-700'
            }`}
          >
            {t.label}
          </button>
        ))}
      </div>

      {!projectId ? (
        <Card>
          <CardContent>
            <p className="text-sm text-slate-500">Select a project to view its reports.</p>
          </CardContent>
        </Card>
      ) : (
        <Card>
          <CardHeader>
            <CardTitle>
              {tab === 'executions' ? 'Execution report' : tab === 'defects' ? 'Defect report' : 'Ticket report'}
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-4">
            <div className="flex flex-wrap items-end gap-3">
              <div>
                <label htmlFor="reports-from" className="mb-1 block text-sm font-medium text-slate-700">
                  From
                </label>
                <input
                  id="reports-from"
                  type="date"
                  value={from}
                  onChange={(e) => {
                    setFrom(e.target.value);
                    resetPage();
                  }}
                  className="rounded-md border border-slate-300 bg-white px-3 py-1.5 text-sm text-slate-900"
                />
              </div>
              <div>
                <label htmlFor="reports-to" className="mb-1 block text-sm font-medium text-slate-700">
                  To
                </label>
                <input
                  id="reports-to"
                  type="date"
                  value={to}
                  onChange={(e) => {
                    setTo(e.target.value);
                    resetPage();
                  }}
                  className="rounded-md border border-slate-300 bg-white px-3 py-1.5 text-sm text-slate-900"
                />
              </div>
              {tab === 'executions' && (
                <>
                  <FilterSelect id="reports-exec-status" label="Status" value={execStatus} options={EXECUTION_STATUSES} onChange={(v) => { setExecStatus(v); resetPage(); }} />
                  <FilterSelect id="reports-exec-class" label="Classification" value={execClassification} options={CLASSIFICATIONS} onChange={(v) => { setExecClassification(v); resetPage(); }} />
                </>
              )}
              {tab === 'defects' && (
                <>
                  <FilterSelect id="reports-defect-status" label="Status" value={defectStatus} options={DEFECT_STATUSES} onChange={(v) => { setDefectStatus(v); resetPage(); }} />
                  <FilterSelect id="reports-defect-severity" label="Severity" value={defectSeverity} options={SEVERITIES} onChange={(v) => { setDefectSeverity(v); resetPage(); }} />
                  <FilterSelect id="reports-defect-class" label="Classification" value={defectClassification} options={CLASSIFICATIONS} onChange={(v) => { setDefectClassification(v); resetPage(); }} />
                  <div>
                    <label htmlFor="reports-defect-search" className="mb-1 block text-sm font-medium text-slate-700">
                      Search
                    </label>
                    <Input
                      id="reports-defect-search"
                      value={defectSearch}
                      onChange={(e) => {
                        setDefectSearch(e.target.value);
                        resetPage();
                      }}
                      placeholder="Title or description…"
                    />
                  </div>
                </>
              )}
              {tab === 'tickets' && (
                <>
                  <div>
                    <label htmlFor="reports-ticket-provider" className="mb-1 block text-sm font-medium text-slate-700">
                      Provider
                    </label>
                    <Input
                      id="reports-ticket-provider"
                      value={ticketProvider}
                      onChange={(e) => {
                        setTicketProvider(e.target.value);
                        resetPage();
                      }}
                      placeholder="jira"
                    />
                  </div>
                  <FilterSelect id="reports-ticket-sync" label="Sync status" value={ticketSync} options={SYNC_STATUSES} onChange={(v) => { setTicketSync(v); resetPage(); }} />
                </>
              )}
            </div>

            {tab === 'executions' && (
              executions.isLoading ? <Skeleton className="h-64" />
              : executions.isError || !executions.data ? <ErrorState error={executions.error} onRetry={() => void executions.refetch()} />
              : executions.data.items.length === 0 ? <p className="text-sm text-slate-500">No executions match these filters.</p>
              : (
                <>
                  <div className="overflow-x-auto">
                    <table className="w-full text-left text-sm">
                      <thead>
                        <tr className="border-b border-slate-200 text-xs uppercase text-slate-500">
                          <th scope="col" className="py-2 pr-3">Execution</th>
                          <th scope="col" className="py-2 pr-3">Test</th>
                          <th scope="col" className="py-2 pr-3">Status</th>
                          <th scope="col" className="py-2 pr-3">Classification</th>
                          <th scope="col" className="py-2 pr-3">Started</th>
                        </tr>
                      </thead>
                      <tbody className="divide-y divide-slate-100">
                        {executions.data.items.map((e) => (
                          <tr key={e.id}>
                            <td className="py-2 pr-3 font-mono text-xs">
                              <Link to={`/projects/${projectId}/executions/${e.id}`} className="text-brand-700 hover:text-brand-600">
                                {e.id.slice(0, 8)}
                              </Link>
                            </td>
                            <td className="py-2 pr-3">{e.testKey ?? '—'}</td>
                            <td className="py-2 pr-3">{e.status}</td>
                            <td className="py-2 pr-3">{e.failureClassification ?? '—'}</td>
                            <td className="py-2 pr-3 text-slate-500">{formatTime(e.startedAt)}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                  <Pager page={executions.data.page} totalCount={executions.data.totalCount} pageSize={executions.data.pageSize} onPage={setPage} />
                </>
              )
            )}

            {tab === 'defects' && (
              defects.isLoading ? <Skeleton className="h-64" />
              : defects.isError || !defects.data ? <ErrorState error={defects.error} onRetry={() => void defects.refetch()} />
              : defects.data.items.length === 0 ? <p className="text-sm text-slate-500">No defects match these filters.</p>
              : (
                <>
                  <div className="overflow-x-auto">
                    <table className="w-full text-left text-sm">
                      <thead>
                        <tr className="border-b border-slate-200 text-xs uppercase text-slate-500">
                          <th scope="col" className="py-2 pr-3">Defect</th>
                          <th scope="col" className="py-2 pr-3">Severity</th>
                          <th scope="col" className="py-2 pr-3">Status</th>
                          <th scope="col" className="py-2 pr-3">Jira</th>
                          <th scope="col" className="py-2 pr-3">Created</th>
                        </tr>
                      </thead>
                      <tbody className="divide-y divide-slate-100">
                        {defects.data.items.map((d) => (
                          <tr key={d.id}>
                            <td className="py-2 pr-3">
                              <Link to={`/projects/${projectId}/bugs/${d.id}`} className="font-medium text-brand-700 hover:text-brand-600">
                                {d.title}
                              </Link>
                            </td>
                            <td className="py-2 pr-3"><Badge tone={severityTone(d.severity)}>{d.severity}</Badge></td>
                            <td className="py-2 pr-3"><Badge tone={defectStatusTone(d.status)}>{d.status}</Badge></td>
                            <td className="py-2 pr-3 font-mono text-xs">{d.jiraKey ?? '—'}</td>
                            <td className="py-2 pr-3 text-slate-500">{formatTime(d.createdAt)}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                  <Pager page={defects.data.page} totalCount={defects.data.totalCount} pageSize={defects.data.pageSize} onPage={setPage} />
                </>
              )
            )}

            {tab === 'tickets' && (
              tickets.isLoading ? <Skeleton className="h-64" />
              : tickets.isError || !tickets.data ? <ErrorState error={tickets.error} onRetry={() => void tickets.refetch()} />
              : tickets.data.items.length === 0 ? <p className="text-sm text-slate-500">No tickets match these filters.</p>
              : (
                <>
                  <div className="overflow-x-auto">
                    <table className="w-full text-left text-sm">
                      <thead>
                        <tr className="border-b border-slate-200 text-xs uppercase text-slate-500">
                          <th scope="col" className="py-2 pr-3">Ticket</th>
                          <th scope="col" className="py-2 pr-3">Provider</th>
                          <th scope="col" className="py-2 pr-3">Sync</th>
                          <th scope="col" className="py-2 pr-3">Defect</th>
                          <th scope="col" className="py-2 pr-3">Created</th>
                        </tr>
                      </thead>
                      <tbody className="divide-y divide-slate-100">
                        {tickets.data.items.map((t) => (
                          <tr key={t.id}>
                            <td className="py-2 pr-3 font-mono text-xs">{t.externalKey ?? t.id.slice(0, 8)}</td>
                            <td className="py-2 pr-3">{t.provider}</td>
                            <td className="py-2 pr-3">
                              <Badge tone={t.syncStatus === 'Synced' ? 'success' : t.syncStatus === 'Failed' ? 'danger' : 'warning'}>
                                {t.syncStatus}
                              </Badge>
                            </td>
                            <td className="py-2 pr-3">
                              {t.defectId ? (
                                <Link to={`/projects/${projectId}/bugs/${t.defectId}`} className="text-brand-700 hover:text-brand-600">
                                  {t.defectTitle ?? t.defectId.slice(0, 8)}
                                </Link>
                              ) : '—'}
                            </td>
                            <td className="py-2 pr-3 text-slate-500">{formatTime(t.createdAt)}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                  <Pager page={tickets.data.page} totalCount={tickets.data.totalCount} pageSize={tickets.data.pageSize} onPage={setPage} />
                </>
              )
            )}
          </CardContent>
        </Card>
      )}
    </div>
  );
}
