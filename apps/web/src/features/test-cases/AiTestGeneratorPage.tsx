import { useState, type FormEvent } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  AlertTriangle,
  CheckCircle2,
  CloudOff,
  ListChecks,
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
  type GenerateTestResult,
} from '../../lib/api/endpoints/testGeneration';
import { testcaseKeys } from '../../lib/api/endpoints/testcases';
import { useProfile } from '../../lib/auth/useProfile';
import { Permissions, hasPermission } from '../../lib/auth/permissions';

const PRIORITIES = ['', 'Critical', 'High', 'Medium', 'Low'];

type GeneratorPhase = 'idle' | 'generating' | 'success';

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
          Generating tests requires the testcases.manage permission for this project.
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
                ? { title: 'Generation input invalid', message: error.message }
                : {
                    title: 'Generation failed',
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
 * AI test generator (Slice 4): structured requirements in, validated AI test
 * version out. This is a testing tool, not a chatbot: output is previewed with
 * provenance metadata and lands in the repository as Pending for human review.
 */
export function AiTestGeneratorPage() {
  const { projectId = '' } = useParams();
  const profile = useProfile();
  const queryClient = useQueryClient();
  const canManage = hasPermission(profile.data?.permissions, Permissions.TestCasesManage);

  const [title, setTitle] = useState('');
  const [description, setDescription] = useState('');
  const [requirementsText, setRequirementsText] = useState('');
  const [targetUrl, setTargetUrl] = useState('');
  const [framework, setFramework] = useState('playwright');
  const [platform, setPlatform] = useState('web');
  const [moduleName, setModuleName] = useState('');
  const [priority, setPriority] = useState('');
  const [additionalContext, setAdditionalContext] = useState('');
  const [clientErrors, setClientErrors] = useState<Record<string, string>>({});
  const [result, setResult] = useState<GenerateTestResult | null>(null);

  const providerStatus = useQuery({
    queryKey: testGenerationKeys.status(projectId),
    queryFn: () => testGenerationEndpoints.status(projectId),
    enabled: !!projectId,
    retry: false,
    staleTime: 60_000,
  });

  const generate = useMutation({
    mutationFn: () =>
      testGenerationEndpoints.generate(projectId, {
        title: title.trim(),
        description: description.trim() || undefined,
        requirements: requirementsText
          .split('\n')
          .map((r) => r.trim())
          .filter((r) => r.length > 0),
        targetUrl: targetUrl.trim() || undefined,
        framework: framework.trim(),
        platform: platform.trim(),
        module: moduleName.trim() || undefined,
        priority: priority || undefined,
        additionalContext: additionalContext.trim() || undefined,
      }),
    onSuccess: (data) => {
      setResult(data);
      void queryClient.invalidateQueries({ queryKey: testcaseKeys.all });
    },
  });

  const phase: GeneratorPhase = generate.isPending ? 'generating' : result ? 'success' : 'idle';
  const serverError = generate.error instanceof ApiError ? generate.error : null;

  const handleSubmit = (e: FormEvent) => {
    e.preventDefault();
    const errors: Record<string, string> = {};
    if (!title.trim()) errors.title = 'Title is required.';
    if (!framework.trim()) errors.framework = 'Framework is required.';
    if (!platform.trim()) errors.platform = 'Platform is required.';
    const requirements = requirementsText
      .split('\n')
      .map((r) => r.trim())
      .filter((r) => r.length > 0);
    if (requirements.length === 0) errors.requirements = 'At least one requirement is required.';
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
    generate.mutate();
  };

  const handleReset = () => {
    setResult(null);
    generate.reset();
  };

  return (
    <div className="mx-auto max-w-4xl space-y-6">
      <div>
        <Link to={`/projects/${projectId}/test-cases`} className="text-sm text-brand-700 hover:text-brand-600">
          ← Back to test cases
        </Link>
        <h1 className="mt-2 flex items-center gap-2 text-xl font-semibold text-slate-900">
          <span className="flex h-8 w-8 items-center justify-center rounded-lg bg-gradient-to-r from-brand-600 to-purple-600">
            <Sparkles className="h-4 w-4 text-white" aria-hidden />
          </span>
          AI Test Generator
        </h1>
        <p className="mt-1 text-sm text-slate-500">
          Generate a complete automated test from structured requirements. Output is
          AI-generated, stored for human review, and never executed automatically.
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
          You can inspect this generator, but saving generated tests requires the
          testcases.manage permission. The backend enforces this independently.
        </div>
      )}

      <Card>
        <CardHeader>
          <CardTitle>Generation input</CardTitle>
          <CardDescription>
            Describe intent, not implementation. Use placeholders like {'{{username}}'} instead of real credentials.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <form onSubmit={handleSubmit} className="space-y-4" noValidate>
            <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
              <Field label="Title" error={clientErrors.title}>
                <Input
                  aria-label="Title"
                  value={title}
                  onChange={(e) => setTitle(e.target.value)}
                  placeholder="Successful user login"
                  maxLength={200}
                />
              </Field>
              <Field label="Target URL" error={clientErrors.targetUrl} hint="Optional absolute http(s) URL.">
                <Input
                  aria-label="Target URL"
                  value={targetUrl}
                  onChange={(e) => setTargetUrl(e.target.value)}
                  placeholder="https://example.test/login"
                />
              </Field>
            </div>
            <Field label="Description" error={clientErrors.description}>
              <textarea
                aria-label="Description"
                value={description}
                onChange={(e) => setDescription(e.target.value)}
                placeholder="Verify that a registered user can log in."
                rows={2}
                className="w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 placeholder:text-slate-400 focus:border-brand-500"
              />
            </Field>
            <Field
              label="Requirements"
              error={clientErrors.requirements}
              hint="One requirement per line. Never paste passwords, tokens, or API keys."
            >
              <textarea
                aria-label="Requirements"
                value={requirementsText}
                onChange={(e) => setRequirementsText(e.target.value)}
                placeholder={'Validate successful login\nValidate dashboard navigation'}
                rows={4}
                className="w-full rounded-md border border-slate-300 bg-white px-3 py-2 font-mono text-sm text-slate-900 placeholder:text-slate-400 focus:border-brand-500"
              />
            </Field>
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
                  placeholder="Authentication"
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
              <Button type="submit" variant="ai" disabled={generate.isPending || !canManage}>
                {generate.isPending ? (
                  <>
                    <Loader2 className="h-4 w-4 animate-spin" aria-hidden />
                    Generating…
                  </>
                ) : (
                  <>
                    <Sparkles className="h-4 w-4" aria-hidden />
                    Generate test
                  </>
                )}
              </Button>
            </div>
          </form>
        </CardContent>
      </Card>

      {phase === 'generating' && (
        <Card aria-label="Generation in progress">
          <CardContent className="flex items-center gap-3 py-6 text-sm text-slate-600">
            <Loader2 className="h-5 w-5 animate-spin text-purple-600" aria-hidden />
            <p>
              Generating with <strong className="font-mono">{providerStatus.data?.provider ?? '…'}</strong>
              {providerStatus.data?.model && (
                <> (<span className="font-mono">{providerStatus.data.model}</span>)</>
              )}
              … this can take a minute. Do not close this page.
            </p>
          </CardContent>
        </Card>
      )}

      {serverError && (
        <ErrorPanel error={serverError} onRetry={() => generate.mutate()} />
      )}

      {phase === 'success' && result && (
        <div className="space-y-4" aria-label="Generation result">
          <Card className="border-purple-200">
            <CardHeader>
              <CardTitle className="flex flex-wrap items-center gap-2">
                <Sparkles className="h-4 w-4 text-purple-600" aria-hidden />
                Generated test preview
                <Badge tone="ai">AI-generated</Badge>
                <Badge tone="warning">{result.reviewStatus} review</Badge>
              </CardTitle>
              <CardDescription>
                Saved to the repository as <strong className="font-mono">{result.testKey}</strong> (version{' '}
                {result.versionNumber}). AI output is untrusted until reviewed — it has not been executed.
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-4">
              <dl className="grid grid-cols-2 gap-2 rounded-md bg-slate-50 px-3 py-2 font-mono text-xs text-slate-600 md:grid-cols-4">
                <div>
                  <dt className="text-slate-400">Provider</dt>
                  <dd>{result.provider}</dd>
                </div>
                <div>
                  <dt className="text-slate-400">Model</dt>
                  <dd>{result.model ?? '—'}</dd>
                </div>
                <div>
                  <dt className="text-slate-400">Prompt</dt>
                  <dd>{result.promptVersion}</dd>
                </div>
                <div>
                  <dt className="text-slate-400">Latency</dt>
                  <dd>{result.latencyMs} ms</dd>
                </div>
              </dl>

              <div>
                <h3 className="text-sm font-semibold text-slate-900">{result.title}</h3>
                {result.description && <p className="mt-1 text-sm text-slate-500">{result.description}</p>}
              </div>

              <div>
                <h3 className="mb-1 flex items-center gap-1 text-sm font-semibold text-slate-900">
                  <ListChecks className="h-4 w-4" aria-hidden />
                  Structured steps
                </h3>
                <ol className="divide-y divide-slate-100 rounded-md border border-slate-200">
                  {result.structuredSteps.map((s) => (
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
              </div>

              <div>
                <h3 className="mb-1 text-sm font-semibold text-slate-900">Source code</h3>
                <pre className="max-h-96 overflow-auto rounded-md bg-slate-950 p-4 font-mono text-xs text-slate-100">
                  {result.sourceCode}
                </pre>
              </div>

              {result.assumptions.length > 0 && (
                <div>
                  <h3 className="mb-1 text-sm font-semibold text-slate-900">Assumptions</h3>
                  <ul className="list-disc space-y-0.5 pl-5 text-sm text-slate-600">
                    {result.assumptions.map((a, i) => (
                      <li key={i}>{a}</li>
                    ))}
                  </ul>
                </div>
              )}

              {result.warnings.length > 0 && (
                <div className="rounded-md border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-800">
                  <p className="flex items-center gap-1 font-semibold">
                    <AlertTriangle className="h-4 w-4" aria-hidden />
                    Warnings
                  </p>
                  <ul className="mt-1 list-disc space-y-0.5 pl-5">
                    {result.warnings.map((w, i) => (
                      <li key={i}>{w}</li>
                    ))}
                  </ul>
                </div>
              )}

              <div className="flex flex-wrap gap-2">
                <Link to={`/projects/${projectId}/test-cases/${result.testCaseId}`}>
                  <Button variant="secondary" size="sm">
                    <CheckCircle2 className="h-4 w-4" aria-hidden />
                    View in repository
                  </Button>
                </Link>
                <Link to={`/projects/${projectId}/test-cases/${result.testCaseId}/edit`}>
                  <Button variant="secondary" size="sm">
                    Review & edit
                  </Button>
                </Link>
                <Button variant="ghost" size="sm" onClick={handleReset}>
                  <RotateCcw className="h-4 w-4" aria-hidden />
                  Generate another
                </Button>
              </div>
            </CardContent>
          </Card>
        </div>
      )}
    </div>
  );
}
