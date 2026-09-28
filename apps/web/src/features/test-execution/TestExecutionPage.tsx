import { useState } from 'react';
import { useMutation } from '@tanstack/react-query';
import { Card, CardContent, CardHeader, CardTitle } from '../../components/ui/card';
import { Button } from '../../components/ui/button';
import { Badge } from '../../components/ui/badge';
import { useAppStore } from '../../stores/useAppStore';
import { executionEndpoints } from '../../lib/api/endpoints/executions';

/**
 * Dark terminal-style execution console shell (docs/02 §11).
 * Live log streaming via SignalR arrives in Phase 1 (FR-3.3).
 */
export function TestExecutionPage() {
  const currentProjectId = useAppStore((s) => s.currentProjectId);
  const [lines, setLines] = useState<string[]>([
    '$ autotestai worker --status',
    'Phase-0: worker environment not yet connected. See workers/playwright README.',
  ]);

  const start = useMutation({
    mutationFn: () =>
      executionEndpoints.start(currentProjectId ?? '00000000-0000-0000-0000-000000000000', {}),
    onSuccess: (data) => {
      setLines((prev) => [
        ...prev,
        `$ execution started: ${data.executionId}`,
        `workflow: ${data.workflowId} (${data.status})`,
      ]);
    },
    onError: (error: Error) => {
      setLines((prev) => [...prev, `error: ${error.message}`]);
    },
  });

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="text-xl font-semibold text-slate-900">Test Execution</h1>
          <p className="mt-1 text-sm text-slate-500">
            Live execution console. Real-time events arrive via SignalR in Phase 1.
          </p>
        </div>
        <div className="flex gap-2">
          <Button variant="success" size="sm" onClick={() => start.mutate()} disabled={start.isPending}>
            Start pipeline
          </Button>
          <Button variant="destructive" size="sm">
            Stop
          </Button>
        </div>
      </div>
      <Card className="overflow-hidden border-slate-900 bg-slate-950">
        <CardHeader className="flex flex-row items-center justify-between border-b border-slate-800">
          <CardTitle className="text-slate-200">Execution terminal</CardTitle>
          <Badge tone="warning">idle</Badge>
        </CardHeader>
        <CardContent>
          <pre
            className="terminal max-h-96 overflow-y-auto whitespace-pre-wrap text-xs leading-6 text-slate-300"
            aria-live="polite"
          >
            {lines.join('\n')}
          </pre>
        </CardContent>
      </Card>
    </div>
  );
}
