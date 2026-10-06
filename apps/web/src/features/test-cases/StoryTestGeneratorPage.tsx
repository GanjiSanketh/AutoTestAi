import { useState, type FormEvent } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  AlertTriangle,
  BookOpenCheck,
  CheckCircle2,
  CloudOff,
  Loader2,
  Lock,
  RotateCcw,
  Sparkles,
} from 'lucide-react';
import { Button } from '../../components/ui/button';
import { Input } from '../../components/ui/input';
import { Badge } from '../../components/ui/badge';
import { Skeleton } from '../../components/ui/skeleton';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '../../components/ui/card';
import { ApiError } from '../../lib/api/client';
import {
  testGenerationEndpoints,
  testGenerationKeys,
  type StoryTestGenerationResult,
  type StoryTestProposal,
} from '../../lib/api/endpoints/testGeneration';
import { testcasesEndpoints, testcaseKeys } from '../../lib/api/endpoints/testcases';
import { useProfile } from '../../lib/auth/useProfile';
import { Permissions, hasPermission } from '../../lib/auth/permissions';

const PRIORITIES = ['', 'Critical', 'High', 'Medium', 'Low'];
const MAX_PROPOSALS_LIMIT = 10;
const JIRA_KEY_HINT = /^[A-Za-z][A-Za-z0-9]+-\d+$/;

/**
 * Reads the Jira issue key back from transient proposal provenance, if the
 * current result came from a Jira import. Provenance is server-built safe
 * metadata — rendered as inert text only, never as a live Jira link.
 */
export function jiraOriginOf(result: StoryTestGenerationResult | null): string | null {
  if (!result) return null;
  for (const proposal of result.proposals) {
    const key = proposal.provenance?.['jiraIssueKey'];
    if (typeof key === 'string' && key.trim().length > 0) return key.trim();
  }
  return null;
}

type StoryPhase = 'idle' | 'generating' | 'success';
type SaveState =
  | { status: 'idle' }
  | { status: 'saving' }
  | { status: 'saved'; testCaseId: string; testKey: string }
  | { status: 'error'; message: string };

function ErrorPanel({ error, onRetry }: { error: ApiError; onRetry?: () => void }) {
  const status = error.status;
  const code = error.code;

  if (status === 403 || code === 'FORBIDDEN') {
    return (
      <div role="alert" className="flex flex-col items-center gap-3 rounded-lg border border-slate-200 bg-white px-6 py-10 text-center shadow-sm">
        <span className="flex h-11 w-11 items-center justify-center rounded-full bg-slate-100">
          <Lock className="h-5 w-5 text-slate-500" aria-hidden />
        </span>
        <h2 className="text-base font-semibold text-slate-900">No access</h2>
        <p className="max-w-md text-sm text-slate-500">
          Generating story tests requires the testcases.manage permission for this project.
        </p>
      </div>
    );
  }

  const { title, message } =
    status === 429 || code === 'RATE_LIMITED'
      ? {
          title: 'Generation rate limited',
          message: 'Too many generation requests. Wait a moment and try again.',
        }
      : code === 'PROVIDER_NOT_CONFIGURED'
        ? {
            title: 'AI provider not configured',
            message: error.message,
          }
        : code === 'PROVIDER_UNAVAILABLE' || code === 'PROVIDER_TIMEOUT'
          ? {
              title: 'AI provider unavailable',
              message: error.message,
            }
          : code === 'PROVIDER_RESPONSE_ERROR'
            ? {
                title: 'Provider returned an unusable response',
                message: `${error.message} Nothing was saved.`,
              }
            : code === 'PROVIDER_NOT_SUPPORTED'
              ? {
                  title: 'AI provider not supported',
                  message: error.message,
                }
                : code === 'VALIDATION_ERROR'
                  ? { title: 'Story input invalid', message: error.message }
                  : code === 'JIRA_AUTH_FAILED' || code === 'JIRA_FORBIDDEN'
                    ? {
                        title: 'Jira rejected the request',
                        message: `${error.message} Check the project Jira integration secret and permissions.`,
                      }
                    : code === 'JIRA_UNAVAILABLE'
                      ? {
                          title: 'Jira unavailable',
                          message: `${error.message} Nothing was saved.`,
                        }
                      : code === 'NOT_FOUND'
                        ? {
                            title: 'Jira issue not found',
                            message: 'The issue key was not found in this project’s Jira project. Nothing was saved.',
                          }
                        : code === 'CONFLICT'
                          ? {
                              title: 'Jira integration not ready',
                              message: error.message,
                            }
                          : {
                              title: 'Story generation failed',
                              message: error.message,
                            };

  return (
    <div role="alert" className="rounded-lg border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">
      <p className="flex items-center gap-2 font-semibold">
        {code === 'PROVIDER_UNAVAILABLE' || code === 'PROVIDER_TIMEOUT' || code === 'PROVIDER_NOT_CONFIGURED' ? (
          <CloudOff className="h-4 w-4" aria-hidden />
        ) : (
          <AlertTriangle className="h-4 w-4" aria-hidden />
        )}
        {title}
      </p>
      <p className="mt-1">{message}</p>
      {error.details.length > 0 && (
        <ul className="mt-2 list-disc space-y-0.5 pl-5">
          {error.details.map((d, i) => (
            <li key={i}>
              {d.field ? `${d.field}: ` : ''}
              {d.message}
            </li>
          ))}
        </ul>
      )}
      {onRetry && (
        <Button variant="secondary" size="sm" className="mt-3" onClick={onRetry}>
          <RotateCcw className="h-4 w-4" aria-hidden />
          Retry generation
        </Button>
      )}
    </div>
  );
}

function Field({
  label,
  error,
  hint,
  children,
}: {
  label: string;
  error?: string;
  hint?: string;
  children: React.ReactNode;
}) {
  return (
    <div>
      <span className="mb-1 block text-sm font-medium text-slate-700">{label}</span>
      {children}
      {hint && !error && <p className="mt-1 text-xs text-slate-400">{hint}</p>}
      {error && (
        <p role="alert" className="mt-1 text-xs text-rose-600">
          {error}
        </p>
      )}
    </div>
  );
}

/**
 * Derives a repository test key from a proposal title, mirroring the
 * server-side key shape (AI- prefix, letter start, ≤32 chars, random
 * suffix). Collisions surface as 409 and are retried with a fresh suffix.
 */
export function buildStoryTestKey(title: string): string {
  let upper = title
    .toUpperCase()
    .replace(/[^A-Z0-9]/g, '-')
    .replace(/-+/g, '-')
    .replace(/^-+|-+$/g, '');
  if (!upper || !/[A-Z]/.test(upper[0])) upper = 'AI-TEST';
  else if (!upper.startsWith('AI-')) upper = `AI-${upper}`;
  upper = upper.slice(0, 20).replace(/-+$/g, '');
  const suffix = Array.from(
    { length: 6 },
    () => '0123456789ABCDEF'[Math.floor(Math.random() * 16)],
  ).join('');
  const key = `${upper}-${suffix}`;
  return key.length <= 32 ? key : `AI-TEST-${suffix}`;
}

/**
 * Manual user-story to test proposals (Phase 4 Slice 3). Proposals are
 * previews only — saving stores them through existing TestCase creation
 * as Pending for human review. Nothing here executes tests or approves them.
 */
export function StoryTestGeneratorPage() {
  const { projectId = '' } = useParams();
  const profile = useProfile();
  const queryClient = useQueryClient();
  const canManage = hasPermission(profile.data?.permissions, Permissions.TestCasesManage);

  const [storyTitle, setStoryTitle] = useState('');
  const [storyDescription, setStoryDescription] = useState('');
  const [criteriaText, setCriteriaText] = useState('');
  const [targetUrl, setTargetUrl] = useState('');
  const [framework, setFramework] = useState('playwright');
  const [platform, setPlatform] = useState('web');
  const [moduleName, setModuleName] = useState('');
  const [priority, setPriority] = useState('');
  const [additionalContext, setAdditionalContext] = useState('');
  const [maxProposals, setMaxProposals] = useState('10');
  const [issueKey, setIssueKey] = useState('');
  const [jiraClientError, setJiraClientError] = useState<string | undefined>(undefined);
  const [clientErrors, setClientErrors] = useState<Record<string, string>>({});
  const [result, setResult] = useState<StoryTestGenerationResult | null>(null);
  const [selected, setSelected] = useState<Record<string, boolean>>({});
  const [saves, setSaves] = useState<Record<string, SaveState>>({});
  const [saving, setSaving] = useState(false);

  const providerStatus = useQuery({
    queryKey: testGenerationKeys.status(projectId),
    queryFn: () => testGenerationEndpoints.status(projectId),
    enabled: !!projectId,
    retry: false,
    staleTime: 60_000,
  });

  const generate = useMutation({
    mutationFn: () =>
      testGenerationEndpoints.generateFromStory(projectId, {
        storyTitle: storyTitle.trim(),
        storyDescription: storyDescription.trim() || undefined,
        acceptanceCriteria: criteriaText
          .split('\n')
          .map((r) => r.trim())
          .filter((r) => r.length > 0),
        targetUrl: targetUrl.trim() || undefined,
        framework: framework.trim(),
        platform: platform.trim(),
        module: moduleName.trim() || undefined,
        priority: priority || undefined,
        additionalContext: additionalContext.trim() || undefined,
        maxProposals: Number.parseInt(maxProposals, 10),
      }),
    onSuccess: (data) => {
      setResult(data);
      setSelected(Object.fromEntries(data.proposals.filter((p) => p.status === 'Succeeded').map((p) => [p.proposalId, true])));
      setSaves({});
    },
  });

  const generateFromJira = useMutation({
    mutationFn: () =>
      testGenerationEndpoints.generateFromJira(projectId, {
        issueKey: issueKey.trim(),
        framework: framework.trim(),
        platform: platform.trim(),
        targetUrl: targetUrl.trim() || undefined,
        module: moduleName.trim() || undefined,
        priority: priority || undefined,
        additionalContext: additionalContext.trim() || undefined,
        maxProposals: Number.parseInt(maxProposals, 10),
      }),
    onSuccess: (data) => {
      setResult(data);
      setSelected(Object.fromEntries(data.proposals.filter((p) => p.status === 'Succeeded').map((p) => [p.proposalId, true])));
      setSaves({});
    },
  });

  const phase: StoryPhase = generate.isPending || generateFromJira.isPending ? 'generating' : result ? 'success' : 'idle';
  const serverError =
    generate.error instanceof ApiError
      ? generate.error
      : generateFromJira.error instanceof ApiError
        ? generateFromJira.error
        : null;
  const jiraOrigin = jiraOriginOf(result);

  const handleJiraSubmit = (e: FormEvent) => {
    e.preventDefault();
    const trimmed = issueKey.trim();
    if (!JIRA_KEY_HINT.test(trimmed) || trimmed.length > 30) {
      setJiraClientError('Enter a Jira issue key like PROJ-123 (at most 30 characters).');
      return;
    }
    const parsedMax = Number.parseInt(maxProposals, 10);
    if (!Number.isInteger(parsedMax) || parsedMax < 1 || parsedMax > MAX_PROPOSALS_LIMIT) {
      setJiraClientError(
        `Max proposals must be an integer between 1 and ${MAX_PROPOSALS_LIMIT}. Adjust it in the story form below.`,
      );
      return;
    }
    if (!framework.trim() || !platform.trim()) {
      setJiraClientError('Framework and platform are required. Set them in the story form below.');
      return;
    }
    setJiraClientError(undefined);
    setResult(null);
    setSelected({});
    setSaves({});
    generate.reset();
    generateFromJira.mutate();
  };

  const handleSubmit = (e: FormEvent) => {
    e.preventDefault();
    const errors: Record<string, string> = {};
    if (!storyTitle.trim()) errors.storyTitle = 'Story title is required.';
    if (!framework.trim()) errors.framework = 'Framework is required.';
    if (!platform.trim()) errors.platform = 'Platform is required.';
    const criteria = criteriaText
      .split('\n')
      .map((r) => r.trim())
      .filter((r) => r.length > 0);
    if (criteria.length === 0) errors.acceptanceCriteria = 'At least one acceptance criterion is required.';
    const parsedMax = Number.parseInt(maxProposals, 10);
    if (!Number.isInteger(parsedMax) || parsedMax < 1 || parsedMax > MAX_PROPOSALS_LIMIT)
      errors.maxProposals = `Max proposals must be an integer between 1 and ${MAX_PROPOSALS_LIMIT}.`;
    if (targetUrl.trim()) {
      try {
        const url = new URL(targetUrl.trim());
        if (url.protocol !== 'http:' && url.protocol !== 'https:')
          errors.targetUrl = 'Target URL must be an absolute http(s) URL.';
      } catch {
        errors.targetUrl = 'Target URL must be an absolute http(s) URL.';
      }
    }
    setClientErrors(errors);
    if (Object.keys(errors).length > 0) return;
    setResult(null);
    setSelected({});
    setSaves({});
    generate.mutate();
  };

  const handleReset = () => {
    setResult(null);
    setSelected({});
    setSaves({});
    setJiraClientError(undefined);
    generate.reset();
    generateFromJira.reset();
  };

  const saveOne = async (proposal: StoryTestProposal) => {
    // Key-collision retry mirrors the server allocator; anything else fails fast.
    for (let attempt = 0; attempt < 3; attempt++) {
      try {
        return await testcasesEndpoints.create(projectId, {
          testKey: buildStoryTestKey(proposal.title ?? 'AI story test'),
          title: proposal.title ?? 'AI story test',
          description: proposal.description ?? undefined,
          module: proposal.module ?? undefined,
          framework: proposal.framework ?? undefined,
          platform: proposal.platform ?? undefined,
          priority: proposal.priority ?? undefined,
          sourceType: 'ai',
          sourceCode: proposal.sourceCode ?? undefined,
          structuredSteps: proposal.structuredSteps,
          generationProvider: proposal.provider ?? undefined,
          generationModel: proposal.model ?? undefined,
          generationLatencyMs: proposal.latencyMs,
          generationRequest: proposal.provenance ?? undefined,
        });
      } catch (error) {
        const conflict = error instanceof ApiError && (error.status === 409 || error.code === 'CONFLICT');
        if (!conflict || attempt === 2) throw error;
      }
    }
    throw new Error('Could not allocate a unique test key.');
  };

  const handleSaveSelected = async () => {
    if (!result || saving) return;
    const targets = result.proposals.filter(
      (p) => p.status === 'Succeeded' && selected[p.proposalId] && saves[p.proposalId]?.status !== 'saved',
    );
    if (targets.length === 0) return;
    setSaving(true);
    try {
      for (const proposal of targets) {
        // Skip proposals saved by an earlier click; each success is preserved
        // even if a later save fails (no duplicate resubmission).
        setSaves((prev) => ({ ...prev, [proposal.proposalId]: { status: 'saving' } }));
        try {
          const created = await saveOne(proposal);
          setSaves((prev) => ({
            ...prev,
            [proposal.proposalId]: { status: 'saved', testCaseId: created.id, testKey: created.testKey },
          }));
          void queryClient.invalidateQueries({ queryKey: testcaseKeys.all });
        } catch (error) {
          setSaves((prev) => ({
            ...prev,
            [proposal.proposalId]: {
              status: 'error',
              message: error instanceof ApiError ? error.message : 'Save failed. Please try again.',
            },
          }));
        }
      }
    } finally {
      setSaving(false);
    }
  };

  const selectableCount =
    result?.proposals.filter((p) => p.status === 'Succeeded' && saves[p.proposalId]?.status !== 'saved').length ?? 0;
  const checkedCount =
    result?.proposals.filter(
      (p) => p.status === 'Succeeded' && selected[p.proposalId] && saves[p.proposalId]?.status !== 'saved',
    ).length ?? 0;

  return (
    <div className="mx-auto max-w-4xl space-y-6">
      <div>
        <Link to={`/projects/${projectId}/test-cases`} className="text-sm text-brand-700 hover:text-brand-600">
          ← Back to test cases
        </Link>
        <h1 className="mt-2 flex items-center gap-2 text-xl font-semibold text-slate-900">
          <span className="flex h-8 w-8 items-center justify-center rounded-lg bg-gradient-to-r from-brand-600 to-purple-600">
            <BookOpenCheck className="h-4 w-4 text-white" aria-hidden />
          </span>
          AI Story Test Generator
        </h1>
        <p className="mt-1 text-sm text-slate-500">
          Generate test proposals from a user story and acceptance criteria. Proposals are
          previews — saving stores them as Pending for human review, and nothing executes automatically.
        </p>
      </div>

      <Card>
        <CardContent className="flex flex-wrap items-center gap-2 pt-4 text-sm">
          <Badge tone="ai">
            <Sparkles className="mr-1 h-3 w-3" aria-hidden />
            AI-assisted
          </Badge>
          {providerStatus.isLoading && <Skeleton className="h-5 w-48" />}
          {providerStatus.data && (
            <span className="text-slate-500" aria-label="AI provider status">
              Provider <strong className="font-mono text-slate-700">{providerStatus.data.provider}</strong>
              {providerStatus.data.model && (
                <>
                  {' '}· model <strong className="font-mono text-slate-700">{providerStatus.data.model}</strong>
                </>
              )}
              {!providerStatus.data.configured && (
                <span className="text-amber-700"> · not configured</span>
              )}
            </span>
          )}
          {providerStatus.isError && (
            <span className="text-slate-400">Provider status unavailable.</span>
          )}
        </CardContent>
      </Card>

      {!canManage && (
        <div role="note" className="rounded-lg border border-amber-200 bg-amber-50 px-4 py-3 text-sm text-amber-800">
          You can inspect this generator, but generating and saving story tests requires the
          testcases.manage permission. The backend enforces this independently.
        </div>
      )}

      <Card>
        <CardHeader>
          <CardTitle>Import from Jira</CardTitle>
          <CardDescription>
            Fetch one issue server-side through this project’s Jira integration and generate
            proposals from it. Only the issue summary, description, and issue type are used —
            comments, attachments, links, and custom fields are ignored. Uses the framework,
            platform, and other options from the story form below.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <form onSubmit={handleJiraSubmit} className="space-y-3" noValidate>
            <Field
              label="Jira issue key"
              error={jiraClientError}
              hint="Format PROJ-123. Fetch happens server-side; the browser never contacts Jira."
            >
              <Input
                aria-label="Jira issue key"
                value={issueKey}
                onChange={(e) => setIssueKey(e.target.value)}
                placeholder="PROJ-123"
                maxLength={30}
              />
            </Field>
            <div>
              <Button
                type="submit"
                variant="ai"
                disabled={generateFromJira.isPending || generate.isPending || !canManage}
              >
                {generateFromJira.isPending ? (
                  <>
                    <Loader2 className="h-4 w-4 animate-spin" aria-hidden />
                    Importing…
                  </>
                ) : (
                  <>
                    <Sparkles className="h-4 w-4" aria-hidden />
                    Generate from Jira
                  </>
                )}
              </Button>
            </div>
          </form>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>User story input</CardTitle>
          <CardDescription>
            Describe intent, not implementation. Use placeholders like {'{{username}}'} instead of real credentials.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <form onSubmit={handleSubmit} className="space-y-4" noValidate>
            <Field label="Story title" error={clientErrors.storyTitle}>
              <Input
                aria-label="Story title"
                value={storyTitle}
                onChange={(e) => setStoryTitle(e.target.value)}
                placeholder="Guest checkout"
                maxLength={200}
              />
            </Field>
            <Field label="Story description">
              <textarea
                aria-label="Story description"
                value={storyDescription}
                onChange={(e) => setStoryDescription(e.target.value)}
                placeholder="As a guest shopper I want to check out without creating an account."
                rows={3}
                className="w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 placeholder:text-slate-400 focus:border-brand-500"
              />
            </Field>
            <Field
              label="Acceptance criteria"
              error={clientErrors.acceptanceCriteria}
              hint="One criterion per line. Never paste passwords, tokens, or API keys."
            >
              <textarea
                aria-label="Acceptance criteria"
                value={criteriaText}
                onChange={(e) => setCriteriaText(e.target.value)}
                placeholder={'Guest can place an order\nOrder confirmation is shown'}
                rows={4}
                className="w-full rounded-md border border-slate-300 bg-white px-3 py-2 font-mono text-sm text-slate-900 placeholder:text-slate-400 focus:border-brand-500"
              />
            </Field>
            <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
              <Field label="Target URL" error={clientErrors.targetUrl} hint="Optional absolute http(s) URL.">
                <Input
                  aria-label="Target URL"
                  value={targetUrl}
                  onChange={(e) => setTargetUrl(e.target.value)}
                  placeholder="https://example.test/checkout"
                />
              </Field>
              <Field label="Max proposals" error={clientErrors.maxProposals} hint="1–10 sequential AI calls.">
                <Input
                  aria-label="Max proposals"
                  value={maxProposals}
                  onChange={(e) => setMaxProposals(e.target.value)}
                  inputMode="numeric"
                />
              </Field>
            </div>
            <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
              <Field label="Framework" error={clientErrors.framework}>
                <Input aria-label="Framework" value={framework} onChange={(e) => setFramework(e.target.value)} maxLength={100} />
              </Field>
              <Field label="Platform" error={clientErrors.platform}>
                <Input aria-label="Platform" value={platform} onChange={(e) => setPlatform(e.target.value)} maxLength={100} />
              </Field>
              <Field label="Module">
                <Input
                  aria-label="Module"
                  value={moduleName}
                  onChange={(e) => setModuleName(e.target.value)}
                  placeholder="Checkout"
                  maxLength={100}
                />
              </Field>
            </div>
            <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
              <Field label="Priority">
                <select
                  aria-label="Priority"
                  value={priority}
                  onChange={(e) => setPriority(e.target.value)}
                  className="w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 focus:border-brand-500"
                >
                  {PRIORITIES.map((p) => (
                    <option key={p || 'none'} value={p}>
                      {p || 'Default'}
                    </option>
                  ))}
                </select>
              </Field>
              <Field label="Additional context">
                <Input
                  aria-label="Additional context"
                  value={additionalContext}
                  onChange={(e) => setAdditionalContext(e.target.value)}
                  placeholder="Edge cases, data constraints…"
                  maxLength={4000}
                />
              </Field>
            </div>
            <div>
              <Button type="submit" variant="ai" disabled={generate.isPending || generateFromJira.isPending || !canManage}>
                {generate.isPending ? (
                  <>
                    <Loader2 className="h-4 w-4 animate-spin" aria-hidden />
                    Generating…
                  </>
                ) : (
                  <>
                    <Sparkles className="h-4 w-4" aria-hidden />
                    Generate proposals
                  </>
                )}
              </Button>
            </div>
          </form>
        </CardContent>
      </Card>

      {phase === 'generating' && (
        <Card aria-label="Story generation in progress">
          <CardContent className="flex items-center gap-3 py-6 text-sm text-slate-600">
            <Loader2 className="h-5 w-5 animate-spin text-purple-600" aria-hidden />
            <p>
              Generating proposals sequentially with{' '}
              <strong className="font-mono">{providerStatus.data?.provider ?? '…'}</strong>
              … this can take a few minutes. Do not close this page.
            </p>
          </CardContent>
        </Card>
      )}

      {serverError && (
        <ErrorPanel
          error={serverError}
          onRetry={() => {
            if (generateFromJira.error) generateFromJira.mutate();
            else generate.mutate();
          }}
        />
      )}

      {phase === 'success' && result && (
        <div className="space-y-4" aria-label="Story generation result">
          <Card className="border-purple-200">
            <CardHeader>
              <CardTitle className="flex flex-wrap items-center gap-2">
                <Sparkles className="h-4 w-4 text-purple-600" aria-hidden />
                Generated proposals
                <Badge tone="ai">AI-generated previews</Badge>
                {jiraOrigin && <Badge tone="neutral">From {jiraOrigin}</Badge>}
              </CardTitle>
              <CardDescription>
                {result.successCount} of {result.proposalCount} proposals succeeded
                {result.failureCount > 0 && ` (${result.failureCount} failed)`} · prompt{' '}
                <span className="font-mono">{result.promptVersion}</span>. Proposals are not
                saved until you save them — saved tests land as Pending for human review.
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-4">
              {result.proposals.length === 0 && (
                <p className="text-sm text-slate-500">No proposals were generated for this story.</p>
              )}
              {result.proposals.map((proposal) => {
                const save = saves[proposal.proposalId] ?? { status: 'idle' };
                const saved = save.status === 'saved';
                return (
                  <div
                    key={proposal.proposalId}
                    className="rounded-md border border-slate-200 px-3 py-3"
                    aria-label={`Proposal ${proposal.index}`}
                  >
                    <div className="flex flex-wrap items-center gap-2">
                      {proposal.status === 'Succeeded' && !saved && (
                        <input
                          type="checkbox"
                          aria-label={`Select proposal ${proposal.index}`}
                          checked={!!selected[proposal.proposalId]}
                          onChange={(e) =>
                            setSelected((prev) => ({ ...prev, [proposal.proposalId]: e.target.checked }))
                          }
                        />
                      )}
                      <span className="font-mono text-xs text-slate-500">#{proposal.index}</span>
                      {proposal.status === 'Succeeded' ? (
                        <Badge tone="ai">Generated proposal</Badge>
                      ) : (
                        <Badge tone="danger">Failed</Badge>
                      )}
                      {saved && <Badge tone="warning">Saved · Pending review</Badge>}
                      {proposal.focusCriterion && (
                        <span className="text-xs text-slate-500">
                          Focus: <span className="font-medium text-slate-700">{proposal.focusCriterion}</span>
                        </span>
                      )}
                    </div>

                    {proposal.status === 'Failed' ? (
                      <p role="alert" className="mt-2 text-sm text-rose-600">
                        {proposal.errorMessage ?? 'Proposal generation failed.'}
                        {proposal.errorCode && (
                          <span className="ml-2 font-mono text-xs">({proposal.errorCode})</span>
                        )}
                      </p>
                    ) : (
                      <div className="mt-2 space-y-2">
                        <h3 className="text-sm font-semibold text-slate-900">{proposal.title}</h3>
                        {proposal.description && (
                          <p className="text-sm text-slate-500">{proposal.description}</p>
                        )}
                        <ol className="divide-y divide-slate-100 rounded-md border border-slate-200">
                          {proposal.structuredSteps.map((s) => (
                            <li key={s.order} className="px-3 py-2 text-sm">
                              <span className="mr-2 inline-flex h-5 w-5 items-center justify-center rounded-full bg-purple-100 font-mono text-xs text-purple-700">
                                {s.order}
                              </span>
                              <strong className="font-medium text-slate-900">{s.action}</strong>
                              {s.target && <span className="ml-2 font-mono text-xs text-slate-500">{s.target}</span>}
                              {s.value && <span className="ml-2 text-xs text-slate-500">→ {s.value}</span>}
                            </li>
                          ))}
                        </ol>
                        {proposal.assumptions.length > 0 && (
                          <p className="text-xs text-slate-500">
                            Assumptions: {proposal.assumptions.join('; ')}
                          </p>
                        )}
                        {proposal.warnings.length > 0 && (
                          <p className="text-xs text-amber-700">
                            Warnings: {proposal.warnings.join('; ')}
                          </p>
                        )}
                      </div>
                    )}

                    {save.status === 'saving' && (
                      <p className="mt-2 text-xs text-slate-500" aria-live="polite">
                        Saving…
                      </p>
                    )}
                    {save.status === 'saved' && (
                      <p className="mt-2 text-xs">
                        <Link
                          to={`/projects/${projectId}/test-cases/${save.testCaseId}`}
                          className="font-medium text-brand-700 hover:text-brand-600"
                        >
                          View {save.testKey} in repository
                        </Link>
                        <span className="ml-2 text-slate-500">Pending review — approval required before execution.</span>
                      </p>
                    )}
                    {save.status === 'error' && (
                      <p role="alert" className="mt-2 text-xs text-rose-600">
                        Save failed: {save.message}
                      </p>
                    )}
                  </div>
                );
              })}

              {selectableCount > 0 && (
                <div className="flex flex-wrap items-center gap-3">
                  <Button
                    variant="ai"
                    size="sm"
                    disabled={saving || checkedCount === 0 || !canManage}
                    onClick={() => void handleSaveSelected()}
                  >
                    {saving ? (
                      <>
                        <Loader2 className="h-4 w-4 animate-spin" aria-hidden />
                        Saving…
                      </>
                    ) : (
                      <>
                        <CheckCircle2 className="h-4 w-4" aria-hidden />
                        Save selected ({checkedCount})
                      </>
                    )}
                  </Button>
                  <span className="text-xs text-slate-500">
                    Saved tests are Pending and require review/approval before execution.
                  </span>
                </div>
              )}

              <div className="flex flex-wrap gap-2">
                <Button variant="ghost" size="sm" onClick={handleReset}>
                  <RotateCcw className="h-4 w-4" aria-hidden />
                  Generate another story
                </Button>
              </div>
            </CardContent>
          </Card>
        </div>
      )}
    </div>
  );
}
