import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { AlertTriangle, BrainCircuit, Loader2, Plus } from 'lucide-react';
import { Button } from '../../components/ui/button';
import { Badge } from '../../components/ui/badge';
import { Skeleton } from '../../components/ui/skeleton';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '../../components/ui/card';
import { Modal } from '../../components/ui/modal';
import { Input } from '../../components/ui/input';
import { ApiError } from '../../lib/api/client';
import {
  failureAnalysisEndpoints,
  failureAnalysisKeys,
  type FailureAnalysis,
} from '../../lib/api/endpoints/failureAnalysis';
import { defectEndpoints } from '../../lib/api/endpoints/defects';

function Confidence({ value }: { value: number | null }) {
  if (value === null || value === undefined) {
    return <span className="text-xs text-slate-400">confidence unknown</span>;
  }
  const percent = Math.round(Math.max(0, Math.min(1, value)) * 100);
  const tone = percent >= 70 ? 'bg-emerald-500' : percent >= 40 ? 'bg-amber-500' : 'bg-slate-400';
  return (
    <span className="inline-flex items-center gap-2" aria-label={`AI confidence ${percent} percent`}>
      <span className="h-1.5 w-24 overflow-hidden rounded-full bg-slate-200">
        <span className={`block h-full ${tone}`} style={{ width: `${percent}%` }} />
      </span>
      <span className="font-mono text-xs text-slate-600">{percent}%</span>
    </span>
  );
}

function CreateDefectModal({
  projectId,
  executionId,
  analysis,
  defaultTitle,
  defaultDescription,
  onClose,
  onCreated,
}: {
  projectId: string;
  executionId: string;
  analysis: FailureAnalysis | null;
  defaultTitle: string;
  defaultDescription: string;
  onClose: () => void;
  onCreated: (defectId: string) => void;
}) {
  const [title, setTitle] = useState(defaultTitle);
  const [description, setDescription] = useState(defaultDescription);
  const [severity, setSeverity] = useState('Medium');
  const [clientError, setClientError] = useState<string | null>(null);

  const create = useMutation({
    mutationFn: () =>
      defectEndpoints.create(projectId, {
        executionId,
        title: title.trim(),
        description: description.trim() || undefined,
        severity,
        failureAnalysisId: analysis?.id,
      }),
    onSuccess: (defect) => onCreated(defect.id),
  });
  const serverError = create.error instanceof ApiError ? create.error : null;

  const submit = (e: React.FormEvent) => {
    e.preventDefault();
    if (!title.trim()) {
      setClientError('Title is required.');
      return;
    }
    setClientError(null);
    create.mutate();
  };

  return (
    <Modal title="Create defect" description="Filed by you from this failed execution. AI analysis is advisory context only." onClose={onClose} wide>
      <form onSubmit={submit} className="space-y-4" noValidate>
        {(clientError || serverError) && (
          <div role="alert" className="rounded-md border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
            {clientError ?? `Could not create the defect: ${serverError?.message}`}
          </div>
        )}
        <div>
          <label htmlFor="defect-title" className="mb-1 block text-sm font-medium text-slate-700">
            Title
          </label>
          <Input id="defect-title" value={title} onChange={(e) => setTitle(e.target.value)} maxLength={200} />
        </div>
        <div>
          <label htmlFor="defect-description" className="mb-1 block text-sm font-medium text-slate-700">
            Description
          </label>
          <textarea
            id="defect-description"
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            rows={5}
            className="w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 placeholder:text-slate-400 focus:border-brand-500"
          />
        </div>
        <div>
          <label htmlFor="defect-severity" className="mb-1 block text-sm font-medium text-slate-700">
            Severity
          </label>
          <select
            id="defect-severity"
            value={severity}
            onChange={(e) => setSeverity(e.target.value)}
            className="w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900"
          >
            {['Critical', 'High', 'Medium', 'Low'].map((s) => (
              <option key={s} value={s}>{s}</option>
            ))}
          </select>
        </div>
        {analysis && (
          <p className="text-xs text-slate-500">
            Linked to AI analysis attempt {analysis.attempt} ({analysis.classification},
            confidence {analysis.confidence ?? 'unknown'}). The defect itself is your decision.
          </p>
        )}
        <div className="flex justify-end gap-2">
          <Button type="button" variant="secondary" size="sm" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" size="sm" disabled={create.isPending}>
            {create.isPending ? 'Creating…' : 'File defect'}
          </Button>
        </div>
      </form>
    </Modal>
  );
}

/**
 * Failure analysis section for failed executions (Slice 6). The deterministic
 * execution classification stays primary; AI output is labeled advisory and
 * can never file defects by itself.
 */
export function FailureAnalysisSection({
  projectId,
  executionId,
  executionClassification,
  failedStepSummary,
  errorMessage,
  testKey,
  canAnalyze,
  canCreateDefect,
  onDefectCreated,
}: {
  projectId: string;
  executionId: string;
  executionClassification: string;
  failedStepSummary: string | null;
  errorMessage: string | null;
  testKey: string;
  canAnalyze: boolean;
  canCreateDefect: boolean;
  onDefectCreated: (defectId: string) => void;
}) {
  const queryClient = useQueryClient();
  const [defectOpen, setDefectOpen] = useState(false);

  const latest = useQuery({
    queryKey: failureAnalysisKeys.latest(projectId, executionId),
    queryFn: () => failureAnalysisEndpoints.latest(projectId, executionId),
    retry: false,
  });

  const analyze = useMutation({
    mutationFn: () => failureAnalysisEndpoints.analyze(projectId, executionId),
    onSuccess: () => {
      void queryClient.invalidateQueries({
        queryKey: failureAnalysisKeys.latest(projectId, executionId),
      });
      void queryClient.invalidateQueries({
        queryKey: failureAnalysisKeys.attempts(projectId, executionId),
      });
    },
  });
  const serverError = analyze.error instanceof ApiError ? analyze.error : null;
  const analysis = latest.data ?? null;
  // 404 means "never analyzed" — a normal state, not an error panel.
  const neverAnalyzed = latest.error instanceof ApiError && latest.error.status === 404;
  const loadError =
    latest.error instanceof ApiError && latest.error.status !== 404 ? latest.error : null;

  const defaultTitle = analysis?.summary?.slice(0, 200) ?? `${testKey} failed: ${executionClassification}`;
  const defaultDescription = [
    failedStepSummary ? `Failed step: ${failedStepSummary}` : null,
    errorMessage ? `Error: ${errorMessage}` : null,
    analysis ? `AI analysis (attempt ${analysis.attempt}, advisory): ${analysis.summary ?? ''}` : null,
  ]
    .filter(Boolean)
    .join('\n');

  return (
    <Card className="border-purple-200">
      <CardHeader>
        <CardTitle className="flex flex-wrap items-center gap-2">
          <BrainCircuit className="h-4 w-4 text-purple-600" aria-hidden />
          Failure analysis
          <Badge tone="ai">AI advisory</Badge>
        </CardTitle>
        <CardDescription>
          Deterministic classification <strong className="font-mono">{executionClassification}</strong> is
          authoritative. AI suggestions never change execution history.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        {latest.isLoading && <Skeleton className="h-24" />}
        {loadError && (
          <div role="alert" className="rounded-md border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
            Could not load analysis: {loadError.message}
          </div>
        )}
        {neverAnalyzed && !analyze.isPending && (
          <p className="text-sm text-slate-500">
            No analysis yet. Run one to get an AI-assisted explanation of this failure.
          </p>
        )}
        {serverError && (
          <div role="alert" className="rounded-md border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
            {serverError.code === 'CONFLICT'
              ? 'An analysis is already running for this execution.'
              : `Analysis failed: ${serverError.message}`}
            {serverError.code !== 'CONFLICT' && (
              <div className="mt-2">
                <Button variant="secondary" size="sm" onClick={() => analyze.mutate()} disabled={analyze.isPending}>
                  Retry analysis
                </Button>
              </div>
            )}
          </div>
        )}
        {analyze.isPending && (
          <p className="flex items-center gap-2 text-sm text-slate-600" aria-label="Analysis in progress">
            <Loader2 className="h-4 w-4 animate-spin text-purple-600" aria-hidden />
            Analyzing bounded failure evidence…
          </p>
        )}
        {analysis && (
          <div className="space-y-3" aria-label="AI analysis result">
            <div className="flex flex-wrap items-center gap-2 text-sm">
              <span className="text-slate-500">Suggested classification</span>
              <Badge tone={analysis.classification === executionClassification ? 'info' : 'warning'}>
                {analysis.classification}
              </Badge>
              {analysis.classification !== executionClassification && (
                <span className="text-xs text-amber-700">
                  Differs from the deterministic {executionClassification} — execution history unchanged.
                </span>
              )}
              <Confidence value={analysis.confidence} />
            </div>
            {analysis.summary && <p className="text-sm text-slate-900">{analysis.summary}</p>}
            {analysis.probableCause && (
              <div>
                <h4 className="text-xs font-semibold uppercase tracking-wide text-slate-500">Probable cause</h4>
                <p className="mt-1 text-sm text-slate-700">{analysis.probableCause}</p>
              </div>
            )}
            {analysis.evidence.length > 0 && (
              <div>
                <h4 className="text-xs font-semibold uppercase tracking-wide text-slate-500">Quoted evidence</h4>
                <ul className="mt-1 list-disc space-y-0.5 pl-5 font-mono text-xs text-slate-600">
                  {analysis.evidence.map((item, i) => (
                    <li key={i}>{item}</li>
                  ))}
                </ul>
              </div>
            )}
            {analysis.assumptions.length > 0 && (
              <div>
                <h4 className="text-xs font-semibold uppercase tracking-wide text-slate-500">Assumptions</h4>
                <ul className="mt-1 list-disc space-y-0.5 pl-5 text-sm text-slate-600">
                  {analysis.assumptions.map((item, i) => (
                    <li key={i}>{item}</li>
                  ))}
                </ul>
              </div>
            )}
            {analysis.warnings.length > 0 && (
              <div className="rounded-md border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-800">
                <p className="flex items-center gap-1 font-semibold">
                  <AlertTriangle className="h-4 w-4" aria-hidden />
                  Warnings
                </p>
                <ul className="mt-1 list-disc space-y-0.5 pl-5">
                  {analysis.warnings.map((item, i) => (
                    <li key={i}>{item}</li>
                  ))}
                </ul>
              </div>
            )}
            {analysis.recommendedAction && (
              <p className="text-sm text-slate-700">
                <strong className="font-semibold">Recommended next step:</strong> {analysis.recommendedAction}
              </p>
            )}
            <p className="font-mono text-xs text-slate-400">
              {analysis.provider ?? '—'}
              {analysis.model ? `/${analysis.model}` : ''} · {analysis.promptVersion ?? '—'} ·{' '}
              {analysis.latencyMs ?? '—'} ms · attempt {analysis.attempt}
            </p>
          </div>
        )}
        <div className="flex flex-wrap gap-2">
          {canAnalyze && (
            <Button variant="ai" size="sm" disabled={analyze.isPending} onClick={() => analyze.mutate()}>
              {analyze.isPending ? (
                <>
                  <Loader2 className="h-4 w-4 animate-spin" aria-hidden />
                  Analyzing…
                </>
              ) : (
                <>
                  <BrainCircuit className="h-4 w-4" aria-hidden />
                  {analysis ? 'Retry analysis' : 'Analyze failure'}
                </>
              )}
            </Button>
          )}
          {!canAnalyze && (
            <p className="text-xs text-slate-400" title="Analysis requires the executions.analyze permission">
              Analysis unavailable for your role.
            </p>
          )}
          {canCreateDefect && (
            <Button variant="secondary" size="sm" onClick={() => setDefectOpen(true)}>
              <Plus className="h-4 w-4" aria-hidden />
              Create defect
            </Button>
          )}
        </div>
        {defectOpen && (
          <CreateDefectModal
            projectId={projectId}
            executionId={executionId}
            analysis={analysis}
            defaultTitle={defaultTitle}
            defaultDescription={defaultDescription}
            onClose={() => setDefectOpen(false)}
            onCreated={onDefectCreated}
          />
        )}
      </CardContent>
    </Card>
  );
}
