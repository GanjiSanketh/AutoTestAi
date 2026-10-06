import { useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Pencil, Play, RefreshCw, Trash2 } from 'lucide-react';
import { Button } from '../../components/ui/button';
import { Badge } from '../../components/ui/badge';
import { Card, CardContent, CardHeader, CardTitle } from '../../components/ui/card';
import { Skeleton } from '../../components/ui/skeleton';
import { Modal } from '../../components/ui/modal';
import { ErrorState } from '../../components/common/ErrorState';
import { ApiError } from '../../lib/api/client';
import {
  testcaseKeys,
  testcasesEndpoints,
  type JiraChangeCheckResult,
  type TestCaseVersion,
} from '../../lib/api/endpoints/testcases';
import { executionEndpoints } from '../../lib/api/endpoints/executions';
import { mobileEndpoints } from '../../lib/api/endpoints/mobile';
import { useProfile } from '../../lib/auth/useProfile';
import { Permissions, hasPermission } from '../../lib/auth/permissions';
import { SourceEditor, editorLanguageFor } from './SourceEditor';
import { StepsEditor } from './StepsEditor';
import { VersionHistory, reviewTone } from './VersionHistory';

const REVIEW_OPTIONS = ['Pending', 'Approved', 'ChangesRequested', 'Rejected'];

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

const CHANGE_FIELD_LABELS: Record<string, string> = {
  title: 'Title',
  description: 'Description',
  acceptanceCriteria: 'Acceptance criteria',
  issueType: 'Issue type',
};

function changeCheckErrorMessage(error: ApiError): string {
  if (error.status === 429 || error.code === 'RATE_LIMITED')
    return 'Jira read rate limit reached. Wait a moment and retry manually.';
  if (error.status === 404)
    return error.code === 'JIRA_PROVENANCE_NOT_FOUND'
      ? 'This version has no Jira baseline to check.'
      : 'Jira issue not found in the configured project.';
  if (error.status === 409) return error.message;
  if (error.code === 'JIRA_AUTH_FAILED' || error.code === 'JIRA_FORBIDDEN' || error.code === 'JIRA_UNAVAILABLE')
    return error.message;
  return `Jira check failed: ${error.message}`;
}

/**
 * Human-gated Jira freshness check for one Jira-generated version.
 * Read-only: one server-side Jira GET, zero AI calls, nothing persisted.
 * A changed result only navigates to the existing generator (prefilled);
 * proposals, save, review, and execution stay on the existing paths.
 */
function JiraChangeCheck({
  versionId,
  issueKey,
  checking,
  result,
  error,
  onCheck,
  onFreshProposals,
}: {
  versionId: string;
  issueKey: string;
  checking: boolean;
  result: JiraChangeCheckResult | null;
  error: ApiError | null;
  onCheck: (versionId: string) => void;
  onFreshProposals: (issueKey: string) => void;
}) {
  return (
    <div className="mt-2 space-y-2 border-t border-slate-200 pt-2">
      {!result && !error && (
        <Button variant="secondary" size="sm" disabled={checking} onClick={() => onCheck(versionId)}>
          <RefreshCw className={`h-4 w-4${checking ? ' animate-spin' : ''}`} aria-hidden />
          {checking ? 'Checking…' : 'Check for Jira changes'}
        </Button>
      )}
      {error && (
        <div role="alert" className="space-y-2">
          <p className="rounded-md border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
            {changeCheckErrorMessage(error)}
          </p>
          <Button variant="secondary" size="sm" disabled={checking} onClick={() => onCheck(versionId)}>
            <RefreshCw className={`h-4 w-4${checking ? ' animate-spin' : ''}`} aria-hidden />
            Retry check
          </Button>
        </div>
      )}
      {result && result.status === 'current' && (
        <p className="text-sm text-slate-600" aria-live="polite">
          <Badge tone="success">Jira issue is up to date</Badge>{' '}
          <span className="font-mono text-xs text-slate-400">{result.checkedAt}</span>
        </p>
      )}
      {result && result.status === 'changed' && (
        <div className="space-y-2" aria-live="polite">
          <p className="text-sm text-slate-700">
            <Badge tone="warning">Jira issue has changed</Badge>
          </p>
          <p className="text-xs text-slate-500">
            Changed: {result.changedFields.map((f) => CHANGE_FIELD_LABELS[f] ?? f).join(', ')}
          </p>
          <Button variant="ai" size="sm" onClick={() => onFreshProposals(result.jiraIssueKey)}>
            <RefreshCw className="h-4 w-4" aria-hidden />
            Generate fresh proposals
          </Button>
          <p className="text-xs text-slate-400">
            Opens the story generator with {issueKey} prefilled. Nothing is generated until you submit there.
          </p>
        </div>
      )}
    </div>
  );
}

export function TestCaseDetailsPage() {
  const { projectId = '', testCaseId = '' } = useParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const profile = useProfile();
  const canManage = hasPermission(profile.data?.permissions, Permissions.TestCasesManage);
  const canExecute = hasPermission(profile.data?.permissions, Permissions.ExecutionsExecute);

  const [selectedVersionId, setSelectedVersionId] = useState<string | null>(null);
  const [reviewOpen, setReviewOpen] = useState(false);
  const [reviewStatus, setReviewStatus] = useState('Approved');
  const [reviewError, setReviewError] = useState<string | null>(null);
  const [confirmArchive, setConfirmArchive] = useState(false);
  const [checkingVersionId, setCheckingVersionId] = useState<string | null>(null);
  const [checking, setChecking] = useState(false);
  const [checkResult, setCheckResult] = useState<JiraChangeCheckResult | null>(null);
  const [checkError, setCheckError] = useState<ApiError | null>(null);

  const testCase = useQuery({
    queryKey: testcaseKeys.details(testCaseId),
    queryFn: () => testcasesEndpoints.get(testCaseId),
    enabled: !!testCaseId,
    retry: false,
  });

  const versions = useQuery({
    queryKey: testcaseKeys.versions(testCaseId),
    queryFn: () => testcasesEndpoints.versions(testCaseId),
    enabled: !!testCaseId,
    retry: false,
  });

  const invalidate = () => {
    void queryClient.invalidateQueries({ queryKey: testcaseKeys.details(testCaseId) });
    void queryClient.invalidateQueries({ queryKey: testcaseKeys.versions(testCaseId) });
    void queryClient.invalidateQueries({ queryKey: testcaseKeys.all });
  };

  const runChangeCheck = async (versionId: string) => {
    if (checking) return;
    setChecking(true);
    setCheckError(null);
    setCheckingVersionId(versionId);
    try {
      setCheckResult(await testcasesEndpoints.checkJiraChanges(testCaseId, versionId));
    } catch (error) {
      setCheckResult(null);
      setCheckError(error instanceof ApiError ? error : new ApiError(0, 'UNKNOWN', 'Check failed.'));
    } finally {
      setChecking(false);
    }
  };

  const review = useMutation({
    mutationFn: (status: string) => testcasesEndpoints.review(testCaseId, selectedVersion?.id ?? '', status),
    onSuccess: () => {
      setReviewOpen(false);
      setReviewError(null);
      invalidate();
    },
    onError: (error: ApiError) => setReviewError(error.message),
  });

  const archive = useMutation({
    mutationFn: () => testcasesEndpoints.remove(testCaseId),
    onSuccess: () => {
      invalidate();
      navigate(`/projects/${projectId}/test-cases`);
    },
  });

  const [runError, setRunError] = useState<ApiError | null>(null);
  const [mobilePoolId, setMobilePoolId] = useState('');
  const [mobileAppId, setMobileAppId] = useState('');
  const [mobileTargetError, setMobileTargetError] = useState<string | null>(null);
  const isMobileRun = (testCase.data?.framework ?? '').trim().toLowerCase() === 'appium';
  const mobilePools = useQuery({
    queryKey: ['mobile', 'pools', projectId],
    queryFn: () => mobileEndpoints.listPools(projectId),
    enabled: isMobileRun,
    retry: false,
  });
  const mobileApps = useQuery({
    queryKey: ['mobile', 'apps', projectId],
    queryFn: () => mobileEndpoints.listApps(projectId),
    enabled: isMobileRun,
    retry: false,
  });
  const run = useMutation({
    mutationFn: (versionId: string) =>
      executionEndpoints.start(projectId, {
        testCaseVersionId: versionId,
        idempotencyKey:
          typeof crypto !== 'undefined' && 'randomUUID' in crypto
            ? crypto.randomUUID()
            : `${Date.now()}-${Math.random().toString(36).slice(2)}`,
        ...(isMobileRun
          ? {
              mobileDevicePoolId: mobilePoolId || undefined,
              mobileAppId: mobileAppId || undefined,
            }
          : {}),
      }),
    onSuccess: (result) => {
      setRunError(null);
      navigate(`/projects/${projectId}/executions/${result.executionId}`);
    },
    onError: (error: ApiError) => setRunError(error),
  });

  if (testCase.isLoading) {
    return (
      <div className="space-y-4" aria-label="Loading test case">
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-64" />
      </div>
    );
  }

  if (testCase.isError || !testCase.data) {
    return (
      <div className="space-y-6">
        <Link to={`/projects/${projectId}/test-cases`} className="text-sm text-brand-700 hover:text-brand-600">
          ← Back to test cases
        </Link>
        <ErrorState
          error={testCase.error}
          onRetry={() => void testCase.refetch()}
          notFoundMessage="This test case does not exist."
        />
      </div>
    );
  }

  const tc = testCase.data;
  const ordered = [...(versions.data ?? [])].sort((a, b) => b.versionNumber - a.versionNumber);
  const selectedVersion: TestCaseVersion | null =
    ordered.find((v) => v.id === selectedVersionId) ?? ordered[0] ?? null;
  const isHistorical =
    selectedVersion !== null &&
    ordered.length > 0 &&
    selectedVersion.id !== ordered[0].id;

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <Link to={`/projects/${projectId}/test-cases`} className="text-sm text-brand-700 hover:text-brand-600">
            ← Back to test cases
          </Link>
          <div className="mt-2 flex flex-wrap items-center gap-2">
            <span className="font-mono text-sm text-slate-500">{tc.testKey}</span>
            <Badge tone={statusTone(tc.status)}>{tc.status}</Badge>
            <Badge tone={priorityTone(tc.priority)}>{tc.priority}</Badge>
            <Badge tone={reviewTone(tc.latestReviewStatus)}>{tc.latestReviewStatus}</Badge>
          </div>
          <h1 className="mt-1 text-xl font-semibold text-slate-900">{tc.title}</h1>
        </div>
        {canManage && tc.status !== 'Archived' && (
          <div className="flex flex-wrap gap-2">
            <Link to={`/projects/${projectId}/test-cases/${tc.id}/edit`}>
              <Button variant="secondary" size="sm">
                <Pencil className="h-4 w-4" aria-hidden />
                Edit
              </Button>
            </Link>
            <Button variant="destructive" size="sm" onClick={() => setConfirmArchive(true)}>
              <Trash2 className="h-4 w-4" aria-hidden />
              Archive
            </Button>
          </div>
        )}
      </div>

      <div className="grid grid-cols-1 gap-4 xl:grid-cols-3">
        <Card className="xl:col-span-2">
          <CardHeader>
            <CardTitle>
              {isHistorical && selectedVersion
                ? `Historical version v${selectedVersion.versionNumber} (read-only)`
                : `Current version v${tc.latestVersionNumber}`}
            </CardTitle>
          </CardHeader>
          <CardContent className="space-y-4">
            {!selectedVersion && (
              <p className="text-sm text-slate-400">No versions recorded for this test case.</p>
            )}
            {selectedVersion && (
              <>
                <SourceEditor
                  id="version-source-code"
                  label="Source code"
                  value={selectedVersion.sourceCode ?? ''}
                  language={editorLanguageFor(tc.framework)}
                  readOnly
                />
                <StepsEditor
                  idPrefix="version-steps"
                  steps={selectedVersion.structuredSteps.map((s) => ({
                    action: s.action,
                    target: s.target ?? '',
                    value: s.value ?? '',
                  }))}
                  onChange={() => {}}
                  readOnly
                />
                {selectedVersion.generationProvider && (
                  <p className="text-xs text-slate-400">
                    Generated by {selectedVersion.generationProvider}
                    {selectedVersion.generationModel ? `/${selectedVersion.generationModel}` : ''}
                    {selectedVersion.generationLatencyMs != null ? ` in ${selectedVersion.generationLatencyMs}ms` : ''}.
                    Generated content requires human review before use.
                  </p>
                )}
                {selectedVersion.jiraProvenance && (
                  <div
                    className="rounded-md border border-slate-200 bg-slate-50 px-3 py-2"
                    aria-label={`Generated from ${selectedVersion.jiraProvenance.jiraIssueKey}`}
                  >
                    <p className="flex flex-wrap items-center gap-2 text-sm text-slate-700">
                      <Badge tone="neutral">Generated from {selectedVersion.jiraProvenance.jiraIssueKey}</Badge>
                    </p>
                    <dl className="mt-1 space-y-0.5 text-xs text-slate-500">
                      {selectedVersion.jiraProvenance.jiraIssueType && (
                        <div className="flex gap-1">
                          <dt className="font-medium">Issue type:</dt>
                          <dd>{selectedVersion.jiraProvenance.jiraIssueType}</dd>
                        </div>
                      )}
                      {selectedVersion.jiraProvenance.jiraBaseUrlHost && (
                        <div className="flex gap-1">
                          <dt className="font-medium">Jira host:</dt>
                          <dd className="font-mono">{selectedVersion.jiraProvenance.jiraBaseUrlHost}</dd>
                        </div>
                      )}
                      {selectedVersion.jiraProvenance.jiraFetchedAt && (
                        <div className="flex gap-1">
                          <dt className="font-medium">Imported:</dt>
                          <dd className="font-mono">{selectedVersion.jiraProvenance.jiraFetchedAt}</dd>
                        </div>
                      )}
                    </dl>
                    <p className="mt-1 text-xs text-slate-400">
                      Historical import record only — Jira is not queried to display this.
                    </p>
                    {canManage && (
                      <JiraChangeCheck
                        versionId={selectedVersion.id}
                        issueKey={selectedVersion.jiraProvenance.jiraIssueKey}
                        checking={checking && checkingVersionId === selectedVersion.id}
                        result={checkingVersionId === selectedVersion.id ? checkResult : null}
                        error={checkingVersionId === selectedVersion.id ? checkError : null}
                        onCheck={() => void runChangeCheck(selectedVersion.id)}
                        onFreshProposals={(key) =>
                          navigate(
                            `/projects/${projectId}/test-cases/generate-story?issueKey=${encodeURIComponent(key)}`,
                          )
                        }
                      />
                    )}
                  </div>
                )}
              </>
            )}
          </CardContent>
        </Card>

        <div className="space-y-4">
          <Card>
            <CardHeader>
              <CardTitle>Details</CardTitle>
            </CardHeader>
            <CardContent>
              <dl className="divide-y divide-slate-100">
                <MetaRow label="Module" value={tc.module} />
                <MetaRow label="Framework" value={tc.framework} mono />
                <MetaRow label="Platform" value={tc.platform} />
                <MetaRow label="Source type" value={tc.sourceType} />
                <MetaRow label="Description" value={tc.description} />
              </dl>
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>Review</CardTitle>
            </CardHeader>
            <CardContent className="space-y-3">
              <p className="text-sm text-slate-600">
                Current version status:{' '}
                <Badge tone={reviewTone(tc.latestReviewStatus)}>{tc.latestReviewStatus}</Badge>
              </p>
              {canManage && selectedVersion && (
                <Button
                  variant="secondary"
                  size="sm"
                  onClick={() => {
                    setReviewStatus(selectedVersion.reviewStatus);
                    setReviewError(null);
                    setReviewOpen(true);
                  }}
                >
                  Review v{selectedVersion.versionNumber}
                </Button>
              )}
              {!canManage && (
                <p className="text-xs text-slate-400">Review requires the test-case manage permission.</p>
              )}
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>Execution</CardTitle>
            </CardHeader>
            <CardContent className="space-y-3">
              <p className="text-sm text-slate-600">
                {selectedVersion
                  ? (
                    <>
                      Version v{selectedVersion.versionNumber} ·{' '}
                      <Badge tone={reviewTone(selectedVersion.reviewStatus)}>
                        {selectedVersion.reviewStatus}
                      </Badge>
                    </>
                  )
                  : 'No version selected.'}
              </p>
              {runError && (
                <p role="alert" className="rounded-md border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
                  {runError.code === 'CONFLICT'
                    ? 'Only approved versions can be executed.'
                    : `Run failed: ${runError.message}`}
                </p>
              )}
              {canExecute && selectedVersion && selectedVersion.reviewStatus === 'Approved' && (
                <>
                  {isMobileRun && (
                    <div className="space-y-2">
                      <label className="block text-sm font-medium text-slate-700" htmlFor="run-mobile-pool">
                        Device pool
                      </label>
                      <select
                        id="run-mobile-pool"
                        className="w-full rounded-md border border-slate-300 px-2 py-1.5 text-sm"
                        value={mobilePoolId}
                        onChange={(e) => setMobilePoolId(e.target.value)}
                      >
                        <option value="">Select a device pool</option>
                        {(mobilePools.data ?? []).map((pool) => (
                          <option key={pool.id} value={pool.id}>
                            {pool.name} ({pool.platform})
                          </option>
                        ))}
                      </select>
                      <label className="block text-sm font-medium text-slate-700" htmlFor="run-mobile-app">
                        Application
                      </label>
                      <select
                        id="run-mobile-app"
                        className="w-full rounded-md border border-slate-300 px-2 py-1.5 text-sm"
                        value={mobileAppId}
                        onChange={(e) => setMobileAppId(e.target.value)}
                      >
                        <option value="">Select an application</option>
                        {(mobileApps.data ?? []).map((app) => (
                          <option key={app.id} value={app.id}>
                            {app.name} ({app.platform})
                          </option>
                        ))}
                      </select>
                      {mobileTargetError && (
                        <p role="alert" className="rounded-md border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
                          {mobileTargetError}
                        </p>
                      )}
                    </div>
                  )}
                  <Button
                    variant="success"
                    size="sm"
                    disabled={run.isPending}
                    onClick={() => {
                      if (isMobileRun && (!mobilePoolId || !mobileAppId)) {
                        setMobileTargetError('Select a device pool and an application to run this mobile test.');
                        return;
                      }
                      setMobileTargetError(null);
                      run.mutate(selectedVersion.id);
                    }}
                  >
                    <Play className="h-4 w-4" aria-hidden />
                    {run.isPending ? 'Starting…' : `Run v${selectedVersion.versionNumber}`}
                  </Button>
                </>
              )}
              {canExecute && selectedVersion && selectedVersion.reviewStatus !== 'Approved' && (
                <p className="text-xs text-slate-400" title="Only approved versions can be executed">
                  Execution unlocks once this version is approved. The backend enforces this independently.
                </p>
              )}
              {!canExecute && (
                <p className="text-xs text-slate-400">Running tests requires the executions.execute permission.</p>
              )}
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>Version history</CardTitle>
            </CardHeader>
            <CardContent>
              {versions.isLoading ? (
                <div className="space-y-2" aria-label="Loading versions">
                  {[0, 1].map((i) => (
                    <Skeleton key={i} className="h-12" />
                  ))}
                </div>
              ) : (
                <VersionHistory
                  versions={ordered}
                  selectedId={selectedVersionId}
                  onSelect={setSelectedVersionId}
                />
              )}
            </CardContent>
          </Card>
        </div>
      </div>

      {reviewOpen && selectedVersion && (
        <Modal
          title={`Review version v${selectedVersion.versionNumber}`}
          description="Generated content is never trusted implicitly — record an explicit human decision."
          onClose={() => setReviewOpen(false)}
        >
          <form
            className="space-y-4"
            onSubmit={(e) => {
              e.preventDefault();
              review.mutate(reviewStatus);
            }}
          >
            {reviewError && (
              <p role="alert" className="rounded-md border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
                {reviewError}
              </p>
            )}
            <div>
              <label htmlFor="review-status" className="mb-1 block text-sm font-medium text-slate-700">
                Review status
              </label>
              <select
                id="review-status"
                value={reviewStatus}
                onChange={(e) => setReviewStatus(e.target.value)}
                className="w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900"
              >
                {REVIEW_OPTIONS.map((s) => (
                  <option key={s} value={s}>{s}</option>
                ))}
              </select>
            </div>
            <div className="flex justify-end gap-2">
              <Button variant="secondary" type="button" onClick={() => setReviewOpen(false)}>
                Cancel
              </Button>
              <Button type="submit" disabled={review.isPending}>
                {review.isPending ? 'Saving…' : 'Save review'}
              </Button>
            </div>
          </form>
        </Modal>
      )}

      {confirmArchive && (
        <Modal
          title="Archive test case"
          description="Archiving preserves all versions and future execution references stay reproducible."
          onClose={() => setConfirmArchive(false)}
        >
          <p className="text-sm text-slate-600">
            Archive <strong className="font-mono">{tc.testKey}</strong>?
          </p>
          {archive.isError && (
            <p role="alert" className="mt-3 rounded-md border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
              Archiving failed. Please try again.
            </p>
          )}
          <div className="mt-4 flex justify-end gap-2">
            <Button variant="secondary" onClick={() => setConfirmArchive(false)}>
              Cancel
            </Button>
            <Button variant="destructive" disabled={archive.isPending} onClick={() => archive.mutate()}>
              {archive.isPending ? 'Archiving…' : 'Archive test case'}
            </Button>
          </div>
        </Modal>
      )}
    </div>
  );
}
