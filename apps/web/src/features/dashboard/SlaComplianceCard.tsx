import { useQuery } from '@tanstack/react-query';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '../../components/ui/card';
import { Badge } from '../../components/ui/badge';
import { Skeleton } from '../../components/ui/skeleton';
import { ErrorState } from '../../components/common/ErrorState';
import { slaTargetEndpoints, slaTargetKeys } from '../../lib/api/endpoints/slaTargets';
import { useProfile } from '../../lib/auth/useProfile';
import { Permissions, hasPermission } from '../../lib/auth/permissions';

function pct1(value: number | null | undefined): string {
  if (value === null || value === undefined || Number.isNaN(value)) return '—';
  return `${value.toFixed(1)}%`;
}

/**
 * Current open-defect SLA compliance (Phase 4 Slice 13). Reads the
 * project targets endpoint (reports.read): thresholds plus the
 * server-evaluated breach result. Current open-defect age only — never
 * historical resolution times. Renders nothing actionable when no
 * targets are configured.
 */
export function SlaComplianceCard({ projectId }: { projectId: string }) {
  const profile = useProfile();
  const canRead = hasPermission(profile.data?.permissions, Permissions.ReportsRead);

  const targets = useQuery({
    queryKey: slaTargetKeys.detail(projectId),
    queryFn: () => slaTargetEndpoints.get(projectId),
    enabled: !!projectId && canRead,
    retry: false,
    staleTime: 30_000,
  });

  if (!canRead && !profile.isLoading) return null;

  const compliance = targets.data?.compliance ?? null;
  const configured = targets.data?.targets ?? null;

  return (
    <Card>
      <CardHeader>
        <CardTitle>SLA Compliance</CardTitle>
        <CardDescription>
          Open and in-progress defects older than their severity threshold count as breached
        </CardDescription>
      </CardHeader>
      <CardContent>
        {targets.isLoading ? (
          <Skeleton className="h-24" aria-label="Loading SLA compliance" />
        ) : targets.isError ? (
          <ErrorState error={targets.error} onRetry={() => void targets.refetch()} />
        ) : !configured || !compliance ? (
          <p className="text-sm text-slate-500">
            No SLA targets configured — breach reporting is unavailable until thresholds are saved in Settings.
          </p>
        ) : compliance.eligible === 0 ? (
          <p className="text-sm text-slate-500">
            No eligible open defects — compliance is unavailable, not 100%.
          </p>
        ) : (
          <div className="space-y-2">
            <div className="flex flex-wrap items-center gap-2">
              <Badge tone={compliance.breached === 0 ? 'success' : 'warning'}>
                {pct1(compliance.compliancePercent)} compliant
              </Badge>
              <span className="text-sm text-slate-500">
                {compliance.compliant} of {compliance.eligible} eligible · {compliance.breached} breached
              </span>
            </div>
            <ul className="divide-y divide-slate-100">
              {compliance.bySeverity.map((s) => (
                <li key={s.severity} className="flex items-start justify-between gap-3 py-1.5">
                  <p className="text-sm text-slate-700">
                    {s.severity} <span className="text-slate-400">· {s.thresholdDays}d</span>
                  </p>
                  <p className="shrink-0 font-mono text-sm text-slate-700">
                    {s.breached} breached of {s.eligible}
                  </p>
                </li>
              ))}
            </ul>
            {compliance.unconfiguredSeverityCount > 0 && (
              <p className="text-xs text-slate-500">
                {compliance.unconfiguredSeverityCount} severit
                {compliance.unconfiguredSeverityCount === 1 ? 'y' : 'ies'} without a configured target
                excluded from the denominator.
              </p>
            )}
          </div>
        )}
      </CardContent>
    </Card>
  );
}
