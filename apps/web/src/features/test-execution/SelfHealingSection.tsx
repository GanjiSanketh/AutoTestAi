import { useQuery } from '@tanstack/react-query';
import { Wrench } from 'lucide-react';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '../../components/ui/card';
import { Badge } from '../../components/ui/badge';
import { Skeleton } from '../../components/ui/skeleton';
import {
  selfHealingEndpoints,
  selfHealingKeys,
  type HealingAttempt,
} from '../../lib/api/endpoints/selfHealing';

function statusTone(status: string): 'success' | 'danger' | 'neutral' {
  if (status === 'Applied') return 'success';
  if (status === 'Failed') return 'danger';
  return 'neutral';
}

function statusText(attempt: HealingAttempt): string {
  if (attempt.status === 'Applied') return 'Step recovered using self-healing';
  if (attempt.status === 'Failed') return 'Self-healing attempted but could not safely recover the locator';
  return 'Self-healing not applicable';
}

function formatLocator(strategy: string | null, value: string | null): string {
  if (!strategy || !value) return '—';
  return `${strategy}=${value}`;
}

/**
 * Self-healing activity for one execution (Phase 2 Slice 11).
 * Supplementary to the step list and log stream: each row shows the original
 * locator, the recovered locator (when one qualified), and whether AI
 * assisted. The stored test is never mutated by healing.
 */
export function SelfHealingSection({
  projectId,
  executionId,
  active,
}: {
  projectId: string;
  executionId: string;
  active: boolean;
}) {
  const attempts = useQuery({
    queryKey: selfHealingKeys.attempts(projectId, executionId),
    queryFn: () => selfHealingEndpoints.listAttempts(projectId, executionId),
    enabled: !!projectId && !!executionId,
    retry: false,
    refetchInterval: active ? 10_000 : false,
  });

  if (attempts.isLoading) {
    return (
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Wrench className="h-4 w-4" aria-hidden />
            Self-healing activity
          </CardTitle>
        </CardHeader>
        <CardContent>
          <Skeleton className="h-10" />
        </CardContent>
      </Card>
    );
  }

  // Healing runs only for eligible locator failures: stay silent when nothing
  // was attempted rather than adding noise to every execution.
  if (attempts.isError || !attempts.data || attempts.data.length === 0) return null;

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <Wrench className="h-4 w-4" aria-hidden />
          Self-healing activity
        </CardTitle>
        <CardDescription>
          Execution-time locator recovery. Original steps are unchanged; recovered locators
          apply to this run only.
        </CardDescription>
      </CardHeader>
      <CardContent>
        <ul className="space-y-2 text-sm">
          {attempts.data.map((attempt) => (
            <li
              key={attempt.id}
              className="rounded-md border border-slate-200 px-3 py-2"
            >
              <div className="flex flex-wrap items-center gap-2">
                <span className="font-mono text-xs text-slate-500">
                  step {attempt.stepOrder} · {attempt.stepAction}
                </span>
                <Badge tone={statusTone(attempt.status)}>{attempt.status}</Badge>
                {attempt.isAiAssisted && <Badge tone="ai">AI-assisted</Badge>}
                <Badge tone="neutral">{attempt.healingStrategy}</Badge>
              </div>
              <p className="mt-1 text-xs text-slate-600">{statusText(attempt)}</p>
              <p className="mt-1 font-mono text-xs text-slate-500">
                Original locator: {formatLocator(attempt.originalStrategy, attempt.originalValue)}
              </p>
              {attempt.wasApplied && (
                <p className="mt-1 font-mono text-xs text-emerald-700">
                  Recovered locator: {formatLocator(attempt.recoveredStrategy, attempt.recoveredValue)}
                </p>
              )}
            </li>
          ))}
        </ul>
      </CardContent>
    </Card>
  );
}
