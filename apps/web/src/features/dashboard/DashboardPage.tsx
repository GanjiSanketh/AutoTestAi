import { useQuery } from '@tanstack/react-query';
import { Card, CardContent, CardHeader, CardTitle } from '../../components/ui/card';
import { Badge } from '../../components/ui/badge';
import { healthEndpoints } from '../../lib/api/endpoints/health';

const KPI_CARDS = [
  { label: 'Total Test Cases', key: 'total' },
  { label: 'Passed Tests', key: 'passed' },
  { label: 'Failed / Bugs', key: 'failed' },
  { label: 'Tickets Raised', key: 'tickets' },
] as const;

/**
 * System Overview (docs/02 §8). Phase 0 shows the shell with empty states —
 * real KPI values arrive from the dashboard API in Phase 1 (no demo data).
 */
export function DashboardPage() {
  const health = useQuery({ queryKey: ['api-health'], queryFn: healthEndpoints.getApiHealth });

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-xl font-semibold text-slate-900">System Overview</h1>
        <p className="mt-1 text-sm text-slate-500">
          Operational quality at a glance.{' '}
          {health.data ? (
            <>
              API <span className="font-mono">{health.data.version}</span> is{' '}
              <Badge tone="success">{health.data.status}</Badge>
            </>
          ) : (
            'Connecting to the API…'
          )}
        </p>
      </div>
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-4">
        {KPI_CARDS.map((card) => (
          <Card key={card.key}>
            <CardHeader>
              <CardTitle>{card.label}</CardTitle>
            </CardHeader>
            <CardContent>
              <p className="text-3xl font-semibold text-slate-300">—</p>
              <p className="mt-1 text-xs text-slate-400">Phase-1: API-backed metric</p>
            </CardContent>
          </Card>
        ))}
      </div>
      <div className="grid grid-cols-1 gap-4 xl:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle>Infrastructure dependencies</CardTitle>
          </CardHeader>
          <CardContent>
            {health.data ? (
              <ul className="space-y-2">
                {health.data.dependencies.map((d) => (
                  <li key={d.name} className="flex items-center justify-between text-sm">
                    <span className="font-mono text-slate-700">{d.name}</span>
                    <Badge
                      tone={d.status === 'up' || d.status === 'enabled' ? 'success' : 'neutral'}
                    >
                      {d.status}
                    </Badge>
                  </li>
                ))}
              </ul>
            ) : (
              <p className="text-sm text-slate-400">
                {health.isError ? 'API unreachable — start the backend (see README).' : 'Loading…'}
              </p>
            )}
          </CardContent>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle>Bug summary by severity</CardTitle>
          </CardHeader>
          <CardContent>
            <p className="text-sm text-slate-400">Phase-1: ECharts donut from persisted data.</p>
          </CardContent>
        </Card>
      </div>
    </div>
  );
}
