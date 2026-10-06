import { useState } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { Wrench, AlertCircle, CheckCircle, XCircle, Info, ChevronDown, ChevronUp } from 'lucide-react';
import { Button } from '../../components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '../../components/ui/card';
import { Badge } from '../../components/ui/badge';
import { Skeleton } from '../../components/ui/skeleton';
import { Modal } from '../../components/ui/modal';
import { maintenanceKeys, maintenanceEndpoints } from '../../lib/api/endpoints/maintenance';
import { useProfile } from '../../lib/auth/useProfile';
import { Permissions, hasPermission } from '../../lib/auth/permissions';
import { ApiError } from '../../lib/api/client';

interface MaintenanceDetailDrawerProps {
  projectId: string;
  proposalId: string;
  onClose: () => void;
  onRefresh: () => void;
}

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

function signalTone(signal: string): 'success' | 'warning' | 'info' | 'neutral' | 'danger' {
  return signal === 'healed-locator' ? 'info' : 'neutral';
}

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

function EvidenceRow({ label, value }: { label: string; value: string | number | null | undefined }) {
  return (
    <div className="flex gap-3 py-1.5 text-sm">
      <span className="font-medium text-slate-500 w-48 shrink-0">{label}</span>
      <span className="text-slate-900 font-mono break-all">{value ?? '—'}</span>
    </div>
  );
}

function CollapsibleSection({ title, children, defaultOpen = true }: { title: string; children: React.ReactNode; defaultOpen?: boolean }) {
  const [open, setOpen] = useState(defaultOpen);
  return (
    <div className="border border-slate-200 rounded-lg">
      <button
        type="button"
        onClick={() => setOpen(!open)}
        className="w-full flex items-center justify-between p-3 bg-slate-50 hover:bg-slate-100"
        aria-expanded={open}
      >
        <span className="font-medium text-slate-900">{title}</span>
        <span className="text-slate-500">{open ? <ChevronUp className="h-4 w-4" /> : <ChevronDown className="h-4 w-4" />}</span>
      </button>
      {open && <div className="p-3">{children}</div>}
    </div>
  );
}

export function MaintenanceDetailDrawer({
  projectId,
  proposalId,
  onClose,
  onRefresh,
}: MaintenanceDetailDrawerProps) {
  const [rejectReason, setRejectReason] = useState('');
  const [rejectOpen, setRejectOpen] = useState(false);
  const [rejectError, setRejectError] = useState<string | null>(null);

  const proposal = useQuery({
    queryKey: maintenanceKeys.detail(projectId, proposalId),
    queryFn: () => maintenanceEndpoints.detail(projectId, proposalId),
    enabled: !!proposalId,
    retry: false,
  });

  const approve = useMutation({
    mutationFn: () => maintenanceEndpoints.approve(projectId, proposalId),
    onSuccess: () => {
      onRefresh();
      onClose();
    },
    onError: (error: ApiError) => {
      if (error.code === 'CONFLICT') {
        alert(error.message);
      } else {
        setRejectError(error.message);
      }
    },
  });

  const reject = useMutation({
    mutationFn: (reason: string) => maintenanceEndpoints.reject(projectId, proposalId, reason),
    onSuccess: () => {
      setRejectOpen(false);
      setRejectReason('');
      onRefresh();
      onClose();
    },
    onError: (error: ApiError) => setRejectError(error.message),
  });

  const profile = useProfile();
  const canManage = hasPermission(profile.data?.permissions, Permissions.TestCasesManage);

  if (proposal.isLoading) {
    return (
      <Modal
        title="Loading proposal…"
        description=""
        onClose={onClose}
        wide
      >
        <div className="space-y-4" aria-label="Loading proposal detail">
          {[0, 1, 2, 3, 4].map((i) => (
            <Skeleton key={i} className="h-12" />
          ))}
        </div>
      </Modal>
    );
  }

  if (proposal.isError || !proposal.data) {
    return (
      <Modal
        title="Proposal not found"
        description="This proposal may have been removed or you don't have access."
        onClose={onClose}
      >
        <p className="text-sm text-slate-600">Unable to load proposal detail.</p>
      </Modal>
    );
  }

  const { proposal: p, evidence } = proposal.data;
  const isProposed = p.status === 'Proposed';
  const isStale = p.status === 'Superseded';
  const isApplied = p.status === 'Applied';
  const isRejected = p.status === 'Rejected';

  return (
    <Modal
      title={`Maintenance Proposal · {p.testKey} · Step {p.stepOrder}`}
      description="Review deterministic healing evidence and decide whether to create a Pending test version."
      onClose={onClose}
      wide
    >
      <div className="space-y-4">
        <div className="flex items-center gap-3 flex-wrap">
          <Badge tone={statusTone(p.status)} className="text-sm px-3 py-1">{p.status}</Badge>
          <Badge tone={signalTone(p.signalType)} className="text-xs px-2 py-1">{p.signalType}</Badge>
          <Badge tone={confidenceTone(p.confidence)} className="text-sm px-3 py-1">{p.confidence}% confidence</Badge>
          <span className="text-xs text-slate-500 font-mono">{p.occurrenceCount} occurrences</span>
        </div>

        <Card>
          <CardHeader>
            <CardTitle className="text-base">Locator Replacement</CardTitle>
          </CardHeader>
          <CardContent className="space-y-3">
            <div className="flex items-center gap-3 flex-wrap text-sm font-mono text-slate-700">
              <span className="px-2 py-1 rounded bg-slate-100">{p.originalStrategy}={p.originalValue}</span>
              <Wrench className="h-4 w-4 text-slate-400" aria-hidden />
              <span className="px-2 py-1 rounded bg-brand-50 text-brand-700">{p.proposedStrategy}={p.proposedValue}</span>
            </div>
            <div className="text-xs text-slate-500">Healing strategy: {p.healingStrategy}</div>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle className="text-base">Test Context</CardTitle>
          </CardHeader>
          <CardContent className="space-y-2">
            <MetaRow label="Test case" value={p.testKey} />
            <MetaRow label="Title" value={p.title} />
            <MetaRow label="Step" value={`${p.stepAction} · #{p.stepOrder}`} />
            <MetaRow label="Source version" value={`v${p.testCaseVersionNumber}`} mono />
            {p.createdVersionNumber && (
              <MetaRow label="Created version" value={`v${p.createdVersionNumber} (Pending)`} mono />
            )}
            <MetaRow label="Healing strategy" value={p.healingStrategy} />
          </CardContent>
        </Card>

        <CollapsibleSection title="Evidence">
          <div className="space-y-3">
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
              <EvidenceRow label="Qualifying occurrences" value={evidence.occurrenceCount} />
              <EvidenceRow label="Failed corroboration" value={evidence.failedCorroborationCount} />
              <EvidenceRow label="Healing success ratio" value={`${Math.round(evidence.healingSuccessRatio * 100)}%`} />
              <EvidenceRow label="Flakiness forecast" value={evidence.forecastBand ?? '—'} />
            </div>
            <div className="pt-2 border-t border-slate-100">
              <p className="text-xs font-medium text-slate-500 mb-2">Confidence factors</p>
              <ul className="space-y-1 text-sm text-slate-700">
                {evidence.confidenceFactors.map((factor, i) => (
                  <li key={i} className="flex items-start gap-2">
                    <Info className="h-3.5 w-3.5 text-brand-600 flex-shrink-0 mt-0.5" aria-hidden />
                    {factor}
                  </li>
                ))}
              </ul>
            </div>
            <div className="pt-2 border-t border-slate-100">
              <p className="text-xs font-medium text-slate-500 mb-2">Execution IDs</p>
              <div className="flex flex-wrap gap-1 text-xs font-mono text-slate-600">
                {evidence.executionIds.map((id, i) => (
                  <span key={i} className="px-1.5 py-0.5 rounded bg-slate-100">{id.slice(0, 8)}…</span>
                ))}
              </div>
            </div>
            <div className="pt-2 border-t border-slate-100">
              <p className="text-xs font-medium text-slate-500 mb-2">Healing attempt IDs</p>
              <div className="flex flex-wrap gap-1 text-xs font-mono text-slate-600">
                {evidence.healingAttemptIds.map((id, i) => (
                  <span key={i} className="px-1.5 py-0.5 rounded bg-slate-100">{id.slice(0, 8)}…</span>
                ))}
              </div>
            </div>
            <div className="pt-2 border-t border-slate-100 text-xs text-slate-500">
              <EvidenceRow label="First seen" value={evidence.firstSeen} />
              <EvidenceRow label="Last seen" value={evidence.lastSeen} />
            </div>
          </div>
        </CollapsibleSection>

        <CollapsibleSection title="Stale Protection">
          <p className="text-sm text-slate-600">
            Approval revalidates the pinned test case version, step order, step action, and current locator.
            If any have changed, the proposal becomes <strong>Superseded</strong> and no version is created.
          </p>
          {isStale && (
            <p className="mt-2 text-sm text-amber-700" role="alert">
              <AlertCircle className="h-4 w-4 inline mr-1" aria-hidden />
              This proposal is stale — the test definition changed after the proposal was created.
              Run a new scan to detect current maintenance needs.
            </p>
          )}
          {isApplied && (
            <p className="mt-2 text-sm text-emerald-700">
              <CheckCircle className="h-4 w-4 inline mr-1" aria-hidden />
              Approved and applied — version v{p.createdVersionNumber} was created as Pending.
              The existing review workflow remains the final execution gate.
            </p>
          )}
          {isRejected && (
            <p className="mt-2 text-sm text-slate-700">
              <XCircle className="h-4 w-4 inline mr-1" aria-hidden />
              Rejected: {p.rejectionReason}
            </p>
          )}
        </CollapsibleSection>

        {canManage && isProposed && (
          <div className="flex flex-wrap gap-2 pt-2 border-t border-slate-200">
            <Button
              variant="success"
              size="sm"
              disabled={approve.isPending}
              onClick={() => approve.mutate()}
            >
              <CheckCircle className="h-4 w-4" aria-hidden />
              {approve.isPending ? 'Applying…' : 'Approve & Create Pending Version'}
            </Button>
            <Button
              variant="secondary"
              size="sm"
              onClick={() => { setRejectError(null); setRejectReason(''); setRejectOpen(true); }}
            >
              <XCircle className="h-4 w-4" aria-hidden />
              Reject
            </Button>
          </div>
        )}

        {!canManage && (
          <p className="text-xs text-slate-400">
            Approve/reject requires the testcases.manage permission.
          </p>
        )}
      </div>

      {rejectOpen && (
        <Modal
          title="Reject maintenance proposal"
          description="Provide a reason for rejecting this proposal. No version will be created."
          onClose={() => { setRejectOpen(false); setRejectReason(''); setRejectError(null); }}
        >
          <form
            className="space-y-4"
            onSubmit={(e) => {
              e.preventDefault();
              reject.mutate(rejectReason.trim());
            }}
          >
            {rejectError && (
              <p role="alert" className="rounded-md border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
                {rejectError}
              </p>
            )}
            <div>
              <label htmlFor="reject-reason" className="mb-1 block text-sm font-medium text-slate-700">
                Rejection reason (required, max 500 characters)
              </label>
              <textarea
                id="reject-reason"
                value={rejectReason}
                onChange={(e) => setRejectReason(e.target.value)}
                maxLength={500}
                rows={4}
                className="w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 focus:border-brand-500 focus:ring-1 focus:ring-brand-500"
                placeholder="e.g., Locator still valid in staging; evidence reflects a transient issue."
              />
            </div>
            <div className="flex justify-end gap-2">
              <Button variant="secondary" type="button" onClick={() => { setRejectOpen(false); setRejectReason(''); setRejectError(null); }}>
                Cancel
              </Button>
              <Button type="submit" disabled={reject.isPending || rejectReason.trim().length === 0}>
                {reject.isPending ? 'Rejecting…' : 'Reject proposal'}
              </Button>
            </div>
          </form>
        </Modal>
      )}
    </Modal>
  );
}