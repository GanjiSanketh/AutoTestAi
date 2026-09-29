import { useState, type FormEvent } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { BrainCircuit, Pencil } from 'lucide-react';
import { Button } from '../../components/ui/button';
import { Input } from '../../components/ui/input';
import { Badge } from '../../components/ui/badge';
import { Skeleton } from '../../components/ui/skeleton';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '../../components/ui/card';
import { Modal } from '../../components/ui/modal';
import { ErrorState } from '../../components/common/ErrorState';
import { ApiError } from '../../lib/api/client';
import { defectEndpoints, defectKeys } from '../../lib/api/endpoints/defects';
import { ticketEndpoints, ticketKeys, ticketErrorMessage } from '../../lib/api/endpoints/tickets';
import { useProfile } from '../../lib/auth/useProfile';
import { Permissions, hasPermission } from '../../lib/auth/permissions';
import { defectStatusTone, severityTone } from './DefectsListPage';

const STATUSES = ['Open', 'InProgress', 'Resolved', 'Closed', 'Rejected'];
const SEVERITIES = ['Critical', 'High', 'Medium', 'Low'];

function MetaRow({ label, value, mono }: { label: string; value?: string | null; mono?: boolean }) {
  return (
    <div className="grid grid-cols-1 gap-1 py-2 sm:grid-cols-3">
      <dt className="text-sm font-medium text-slate-500">{label}</dt>
      <dd className={`text-sm text-slate-900 sm:col-span-2 ${mono ? 'font-mono break-all' : ''}`}>
        {value || <span className="text-slate-400">—</span>}
      </dd>
    </div>
  );
}

/** Defect detail: human decision record with advisory AI context (Slice 6). */
export function DefectDetailsPage() {
  const { projectId = '', defectId = '' } = useParams();
  const profile = useProfile();
  const queryClient = useQueryClient();
  const canManage = hasPermission(profile.data?.permissions, Permissions.BugsManage);
  const canCreateTicket = hasPermission(profile.data?.permissions, Permissions.TicketsCreate);
  const canReadTicket = hasPermission(profile.data?.permissions, Permissions.TicketsRead);
  const canConfigureJira = hasPermission(profile.data?.permissions, Permissions.SettingsManage);

  const [editOpen, setEditOpen] = useState(false);
  const [statusOpen, setStatusOpen] = useState(false);
  const [ticketConfirmOpen, setTicketConfirmOpen] = useState(false);
  const [ticketError, setTicketError] = useState<string | null>(null);
  const [ticketSuccess, setTicketSuccess] = useState<string | null>(null);
  const [title, setTitle] = useState('');
  const [description, setDescription] = useState('');
  const [severity, setSeverity] = useState('Medium');
  const [targetStatus, setTargetStatus] = useState('InProgress');
  const [formError, setFormError] = useState<ApiError | null>(null);

  const defect = useQuery({
    queryKey: defectKeys.details(projectId, defectId),
    queryFn: () => defectEndpoints.get(projectId, defectId),
    enabled: !!projectId && !!defectId,
    retry: false,
  });

  const jiraStatus = useQuery({
    queryKey: ticketKeys.jiraStatus(projectId),
    queryFn: () => ticketEndpoints.jiraStatus(projectId),
    enabled: !!projectId && !!defectId && canReadTicket,
    retry: false,
  });

  const defectTicket = useQuery({
    queryKey: ticketKeys.defectTicket(projectId, defectId),
    queryFn: () => ticketEndpoints.getForDefect(projectId, defectId),
    enabled: !!projectId && !!defectId && canReadTicket,
    retry: false,
  });

  const invalidate = () => {
    void queryClient.invalidateQueries({ queryKey: defectKeys.details(projectId, defectId) });
    void queryClient.invalidateQueries({ queryKey: defectKeys.all });
    void queryClient.invalidateQueries({ queryKey: ticketKeys.defectTicket(projectId, defectId) });
  };

  const createTicket = useMutation({
    mutationFn: () => ticketEndpoints.createForDefect(projectId, defectId),
    onSuccess: (ticket) => {
      setTicketConfirmOpen(false);
      setTicketError(null);
      setTicketSuccess(
        ticket.alreadyExisted
          ? `A Jira ticket already exists for this defect (${ticket.externalKey ?? 'see Jira'}).`
          : `Jira ticket ${ticket.externalKey ?? ''} created.`.trim(),
      );
      invalidate();
    },
    onError: (error: ApiError) => {
      setTicketError(ticketErrorMessage(error.status, error.code));
    },
  });

  const update = useMutation({
    mutationFn: () =>
      defectEndpoints.update(projectId, defectId, {
        title: title.trim(),
        description: description.trim() || undefined,
        severity,
      }),
    onSuccess: () => {
      setEditOpen(false);
      setFormError(null);
      invalidate();
    },
    onError: (error: ApiError) => setFormError(error),
  });

  const changeStatus = useMutation({
    mutationFn: () => defectEndpoints.changeStatus(projectId, defectId, targetStatus),
    onSuccess: () => {
      setStatusOpen(false);
      setFormError(null);
      invalidate();
    },
    onError: (error: ApiError) => setFormError(error),
  });

  const openEdit = () => {
    if (!defect.data) return;
    setTitle(defect.data.title);
    setDescription(defect.data.description ?? '');
    setSeverity(defect.data.severity);
    setFormError(null);
    setEditOpen(true);
  };

  const submitEdit = (e: FormEvent) => {
    e.preventDefault();
    if (!title.trim()) {
      setFormError(new ApiError(400, 'VALIDATION_ERROR', 'Title is required.'));
      return;
    }
    update.mutate();
  };

  if (defect.isLoading) {
    return (
      <div className="space-y-4" aria-label="Loading defect">
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-64" />
      </div>
    );
  }

  if (defect.isError || !defect.data) {
    return (
      <div className="space-y-6">
        <Link to={`/projects/${projectId}/bugs`} className="text-sm text-brand-700 hover:text-brand-600">
          ← Back to bugs
        </Link>
        <ErrorState
          error={defect.error}
          onRetry={() => void defect.refetch()}
          notFoundMessage="This defect does not exist."
        />
      </div>
    );
  }

  const item = defect.data;

  return (
    <div className="space-y-6">
      <div>
        <Link to={`/projects/${projectId}/bugs`} className="text-sm text-brand-700 hover:text-brand-600">
          ← Back to bugs
        </Link>
        <div className="mt-2 flex flex-wrap items-center gap-2">
          <Badge tone={severityTone(item.severity)}>{item.severity}</Badge>
          <Badge tone={defectStatusTone(item.status)}>{item.status}</Badge>
          {item.failureClassification && <Badge tone="warning">{item.failureClassification}</Badge>}
        </div>
        <h1 className="mt-1 text-xl font-semibold text-slate-900">{item.title}</h1>
      </div>

      {formError && (
        <div role="alert" className="rounded-md border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
          {formError.message}
        </div>
      )}

      <div className="grid grid-cols-1 gap-4 xl:grid-cols-3">
        <div className="space-y-4 xl:col-span-2">
          <Card>
            <CardHeader>
              <CardTitle>Description</CardTitle>
            </CardHeader>
            <CardContent>
              <p className="whitespace-pre-wrap text-sm text-slate-700">
                {item.description ?? <span className="text-slate-400">No description.</span>}
              </p>
              {canManage && (
                <div className="mt-3 flex gap-2">
                  <Button variant="secondary" size="sm" onClick={openEdit}>
                    <Pencil className="h-4 w-4" aria-hidden />
                    Edit
                  </Button>
                  <Button variant="secondary" size="sm" onClick={() => { setTargetStatus('InProgress'); setFormError(null); setStatusOpen(true); }}>
                    Change status
                  </Button>
                </div>
              )}
            </CardContent>
          </Card>

          {item.analysis && (
            <Card className="border-purple-200">
              <CardHeader>
                <CardTitle className="flex items-center gap-2">
                  <BrainCircuit className="h-4 w-4 text-purple-600" aria-hidden />
                  Linked AI analysis
                  <Badge tone="ai">Advisory</Badge>
                </CardTitle>
                <CardDescription>
                  Attempt {item.analysis.attempt} · {item.analysis.status}. This informed the report —
                  the defect itself is a human decision.
                </CardDescription>
              </CardHeader>
              <CardContent className="space-y-2 text-sm">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="text-slate-500">Suggested classification</span>
                  <Badge tone="info">{item.analysis.classification}</Badge>
                  <span className="text-slate-500">Confidence</span>
                  <span className="font-mono text-xs">
                    {item.analysis.confidence ?? 'unknown'}
                  </span>
                </div>
                {item.analysis.summary && <p className="text-slate-700">{item.analysis.summary}</p>}
                <p className="font-mono text-xs text-slate-400">
                  {item.analysis.provider ?? '—'}
                  {item.analysis.model ? `/${item.analysis.model}` : ''}
                </p>
              </CardContent>
            </Card>
          )}
        </div>

        <Card>
          <CardHeader>
            <CardTitle>Traceability</CardTitle>
          </CardHeader>
          <CardContent>
            <dl className="divide-y divide-slate-100">
              <MetaRow label="Severity" value={item.severity} />
              <MetaRow label="Status" value={item.status} />
              <MetaRow label="Classification" value={item.failureClassification} mono />
              <MetaRow label="Test case" value={item.testKey} mono />
              <MetaRow label="Test title" value={item.testTitle} />
              <MetaRow label="Version" value={item.testCaseVersionNumber !== null && item.testCaseVersionNumber !== undefined ? `v${item.testCaseVersionNumber}` : null} mono />
              <MetaRow label="Execution" value={item.executionId} mono />
              <MetaRow label="Analysis" value={item.failureAnalysisId} mono />
            </dl>
            {item.executionId && (
              <Link
                to={`/projects/${projectId}/executions/${item.executionId}`}
                className="mt-3 inline-block text-sm font-medium text-brand-700 hover:text-brand-600"
              >
                Open execution →
              </Link>
            )}
          </CardContent>
        </Card>

        {canReadTicket && (
          <Card>
            <CardHeader>
              <CardTitle>Jira ticket</CardTitle>
              <CardDescription>Manual external ticket. The defect remains the system of record.</CardDescription>
            </CardHeader>
            <CardContent className="space-y-3 text-sm">
              {ticketSuccess && (
                <div role="status" className="rounded-md border border-emerald-200 bg-emerald-50 px-3 py-2 text-emerald-700">
                  {ticketSuccess}
                </div>
              )}
              {ticketError && (
                <div role="alert" className="rounded-md border border-rose-200 bg-rose-50 px-3 py-2 text-rose-700">
                  {ticketError}
                </div>
              )}
              {defectTicket.isLoading || jiraStatus.isLoading ? (
                <Skeleton className="h-16" />
              ) : defectTicket.data?.externalKey ? (
                <div className="space-y-2">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="font-mono text-sm font-semibold text-slate-900">{defectTicket.data.externalKey}</span>
                    <Badge tone="success">{defectTicket.data.syncStatus}</Badge>
                  </div>
                  <MetaRow label="Provider" value={defectTicket.data.provider} />
                  <MetaRow label="Created" value={new Date(defectTicket.data.createdAt).toLocaleString()} />
                  {defectTicket.data.externalUrl && /^https?:\/\//i.test(defectTicket.data.externalUrl) && (
                    <a
                      href={defectTicket.data.externalUrl}
                      target="_blank"
                      rel="noopener noreferrer"
                      className="inline-block font-medium text-brand-700 hover:text-brand-600"
                    >
                      Open in Jira →
                    </a>
                  )}
                </div>
              ) : jiraStatus.data && !jiraStatus.data.configured ? (
                <div className="space-y-2">
                  <p className="text-slate-600">Jira is not configured for this project.</p>
                  {canConfigureJira && (
                    <Link to="/settings" className="font-medium text-brand-700 hover:text-brand-600">
                      Open integration settings →
                    </Link>
                  )}
                </div>
              ) : jiraStatus.data && !jiraStatus.data.enabled ? (
                <p className="text-slate-600">The Jira integration is disabled for this project.</p>
              ) : canCreateTicket ? (
                <div className="space-y-2">
                  <p className="text-slate-600">
                    {jiraStatus.data?.projectKey
                      ? `Create an external issue in Jira project ${jiraStatus.data.projectKey}.`
                      : 'Create an external Jira issue from this defect.'}
                  </p>
                  <Button
                    variant="secondary"
                    size="sm"
                    disabled={createTicket.isPending}
                    onClick={() => { setTicketError(null); setTicketSuccess(null); setTicketConfirmOpen(true); }}
                  >
                    {createTicket.isPending ? 'Creating…' : 'Create Jira Ticket'}
                  </Button>
                </div>
              ) : (
                <p className="text-slate-600">You do not have permission to create Jira tickets for this project.</p>
              )}
            </CardContent>
          </Card>
        )}
      </div>

      {editOpen && (
        <Modal title="Edit defect" description="Relationships (execution, version, analysis) are immutable." onClose={() => setEditOpen(false)}>
          <form onSubmit={submitEdit} className="space-y-4" noValidate>
            <div>
              <label htmlFor="defect-edit-title" className="mb-1 block text-sm font-medium text-slate-700">
                Title
              </label>
              <Input id="defect-edit-title" value={title} onChange={(e) => setTitle(e.target.value)} maxLength={200} />
            </div>
            <div>
              <label htmlFor="defect-edit-description" className="mb-1 block text-sm font-medium text-slate-700">
                Description
              </label>
              <textarea
                id="defect-edit-description"
                value={description}
                onChange={(e) => setDescription(e.target.value)}
                rows={4}
                className="w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900"
              />
            </div>
            <div>
              <label htmlFor="defect-edit-severity" className="mb-1 block text-sm font-medium text-slate-700">
                Severity
              </label>
              <select
                id="defect-edit-severity"
                value={severity}
                onChange={(e) => setSeverity(e.target.value)}
                className="w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900"
              >
                {SEVERITIES.map((s) => (
                  <option key={s} value={s}>{s}</option>
                ))}
              </select>
            </div>
            <div className="flex justify-end gap-2">
              <Button type="button" variant="secondary" size="sm" onClick={() => setEditOpen(false)}>
                Cancel
              </Button>
              <Button type="submit" size="sm" disabled={update.isPending}>
                {update.isPending ? 'Saving…' : 'Save'}
              </Button>
            </div>
          </form>
        </Modal>
      )}

      {statusOpen && (
        <Modal title="Change defect status" description="Transitions are validated and audited." onClose={() => setStatusOpen(false)}>
          <form
            className="space-y-4"
            onSubmit={(e) => {
              e.preventDefault();
              changeStatus.mutate();
            }}
          >
            <div>
              <label htmlFor="defect-status" className="mb-1 block text-sm font-medium text-slate-700">
                Status
              </label>
              <select
                id="defect-status"
                value={targetStatus}
                onChange={(e) => setTargetStatus(e.target.value)}
                className="w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900"
              >
                {STATUSES.map((s) => (
                  <option key={s} value={s}>{s}</option>
                ))}
              </select>
            </div>
            <div className="flex justify-end gap-2">
              <Button type="button" variant="secondary" size="sm" onClick={() => setStatusOpen(false)}>
                Cancel
              </Button>
              <Button type="submit" size="sm" disabled={changeStatus.isPending}>
                {changeStatus.isPending ? 'Saving…' : 'Save status'}
              </Button>
            </div>
          </form>
        </Modal>
      )}

      {ticketConfirmOpen && (
        <Modal
          title="Create Jira ticket"
          description="This creates an external Jira issue. This is a manual action — nothing is created automatically."
          onClose={() => { if (!createTicket.isPending) setTicketConfirmOpen(false); }}
        >
          <div className="space-y-3 text-sm text-slate-700">
            <p>
              Create a Jira issue from defect <span className="font-medium text-slate-900">{item.title}</span>
              {jiraStatus.data?.projectKey ? (
                <> in Jira project <span className="font-mono">{jiraStatus.data.projectKey}</span></>
              ) : null}
              ?
            </p>
            <p className="text-slate-500">
              The internal defect remains the system of record. Only safe defect details are sent to Jira —
              no credentials or secrets leave this server.
            </p>
            <div className="flex justify-end gap-2">
              <Button type="button" variant="secondary" size="sm" onClick={() => setTicketConfirmOpen(false)} disabled={createTicket.isPending}>
                Cancel
              </Button>
              <Button type="button" size="sm" onClick={() => createTicket.mutate()} disabled={createTicket.isPending}>
                {createTicket.isPending ? 'Creating…' : 'Confirm creation'}
              </Button>
            </div>
          </div>
        </Modal>
      )}
    </div>
  );
}
