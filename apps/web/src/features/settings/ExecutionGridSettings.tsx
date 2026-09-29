import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from '../../components/ui/card';
import { Badge } from '../../components/ui/badge';
import { Button } from '../../components/ui/button';
import { Skeleton } from '../../components/ui/skeleton';
import { ApiError } from '../../lib/api/client';
import { executionGridEndpoints, executionGridKeys } from '../../lib/api/endpoints/executionGrid';

function workerStatusTone(status: string): 'success' | 'warning' | 'danger' | 'info' | 'neutral' {
  switch (status) {
    case 'Available':
      return 'success';
    case 'Busy':
      return 'info';
    case 'Draining':
      return 'warning';
    case 'Unhealthy':
      return 'warning';
    case 'Offline':
      return 'danger';
    case 'Disabled':
      return 'neutral';
    case 'Registered':
      return 'info';
    default:
      return 'neutral';
  }
}

function formatTime(iso: string | null): string {
  if (!iso) return '—';
  try {
    return new Date(iso).toLocaleString();
  } catch {
    return iso;
  }
}

/** Execution Grid management UI (Phase 2 Slice 9). */
export function ExecutionGridSettings() {
  const queryClient = useQueryClient();

  const status = useQuery({
    queryKey: executionGridKeys.status(),
    queryFn: () => executionGridEndpoints.getStatus(),
    retry: false,
    enabled: true,
  });

  const workers = useQuery({
    queryKey: executionGridKeys.workers(),
    queryFn: () => executionGridEndpoints.getWorkers(),
    retry: false,
    enabled: true,
  });

  const invalidate = () => {
    void queryClient.invalidateQueries({ queryKey: executionGridKeys.all });
  };

  const drain = useMutation({
    mutationFn: (workerId: string) => executionGridEndpoints.drainWorker(workerId),
    onSuccess: () => invalidate(),
    onError: (error: ApiError) => console.error('Failed to drain worker:', error),
  });

  const disable = useMutation({
    mutationFn: (workerId: string) => executionGridEndpoints.disableWorker(workerId),
    onSuccess: () => invalidate(),
    onError: (error: ApiError) => console.error('Failed to disable worker:', error),
  });

  const enable = useMutation({
    mutationFn: (workerId: string) => executionGridEndpoints.enableWorker(workerId),
    onSuccess: () => invalidate(),
    onError: (error: ApiError) => console.error('Failed to enable worker:', error),
  });

  const handleDrain = (workerId: string) => {
    if (confirm('Drain this worker? It will finish current assignments but accept no new work.')) {
      drain.mutate(workerId);
    }
  };

  const handleDisable = (workerId: string) => {
    if (confirm('Disable this worker? It will immediately stop all work and cannot receive new assignments.')) {
      disable.mutate(workerId);
    }
  };

  const handleEnable = (workerId: string) => {
    enable.mutate(workerId);
  };

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-xl font-semibold text-slate-900">Execution Grid</h1>
        <p className="mt-1 text-sm text-slate-500">
          Monitor and manage the distributed Playwright execution grid (Phase 2 Slice 9).
        </p>
      </div>

      {status.isLoading && workers.isLoading ? (
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-4">
          {[0, 1, 2, 3].map((i) => <Skeleton key={i} className="h-28" />)}
        </div>
      ) : (
        <>
          <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-4">
            <Card>
              <CardHeader>
                <CardTitle>Total Workers</CardTitle>
              </CardHeader>
              <CardContent>
                <p className="text-3xl font-semibold text-slate-900">{status.data?.totalWorkers ?? 0}</p>
                <p className="mt-1 text-xs text-slate-500">
                  {status.data?.availableWorkers ?? 0} available · {status.data?.busyWorkers ?? 0} busy
                </p>
              </CardContent>
            </Card>
            <Card>
              <CardHeader>
                <CardTitle>Total Capacity</CardTitle>
              </CardHeader>
              <CardContent>
                <p className="text-3xl font-semibold text-slate-900">{status.data?.totalCapacity ?? 0}</p>
                <p className="mt-1 text-xs text-slate-500">
                  {status.data?.activeAssignments ?? 0} active · {status.data?.availableSlots ?? 0} slots free
                </p>
              </CardContent>
            </Card>
            <Card>
              <CardHeader>
                <CardTitle>Active Leases</CardTitle>
              </CardHeader>
              <CardContent>
                <p className="text-3xl font-semibold text-slate-900">{status.data?.activeLeases ?? 0}</p>
                <p className="mt-1 text-xs text-slate-500">
                  {status.data?.queuedExecutions ?? 0} queued
                </p>
              </CardContent>
            </Card>
            <Card>
              <CardHeader>
                <CardTitle>Queue Wait</CardTitle>
              </CardHeader>
              <CardContent>
                <p className="text-3xl font-semibold text-slate-900">
                  {status.data?.queuedExecutions ?? 0}
                </p>
                <p className="mt-1 text-xs text-slate-500">Executions waiting for capacity</p>
              </CardContent>
            </Card>
          </div>

          <Card>
            <CardHeader>
              <CardTitle>Workers</CardTitle>
              <CardDescription>Registered Playwright workers with effective status (derived from heartbeats)</CardDescription>
            </CardHeader>
            <CardContent>
              {workers.isLoading ? (
                <div className="space-y-3">
                  {[0, 1, 2].map((i) => <Skeleton key={i} className="h-16" />)}
                </div>
              ) : workers.data && workers.data.length === 0 ? (
                <p className="text-sm text-slate-500">
                  No workers registered. Start a Playwright worker with GRID_PROVISIONING_TOKEN set to register it.
                </p>
              ) : (
                <div className="overflow-x-auto">
                  <table className="w-full text-left text-sm">
                    <thead>
                      <tr className="border-b border-slate-200 text-xs uppercase text-slate-500">
                        <th scope="col" className="py-2 pr-3">Worker</th>
                        <th scope="col" className="py-2 pr-3">Type</th>
                        <th scope="col" className="py-2 pr-3">Status</th>
                        <th scope="col" className="py-2 pr-3">Capacity</th>
                        <th scope="col" className="py-2 pr-3">Last Heartbeat</th>
                        <th scope="col" className="py-2 pr-3">Actions</th>
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-slate-100">
                      {workers.data?.map((w) => (
                        <tr key={w.id}>
                          <td className="py-2 pr-3">
                            <div>
                              <Link
                                to={`/settings/execution-grid/workers/${w.id}`}
                                className="font-mono text-sm font-medium text-brand-700 hover:text-brand-600"
                              >
                                {w.workerKey}
                              </Link>
                              <p className="truncate text-xs text-slate-500">
                                {w.displayName} · {w.framework}
                              </p>
                            </div>
                          </td>
                          <td className="py-2 pr-3 font-mono text-xs text-slate-700">{w.workerType}</td>
                          <td className="py-2 pr-3">
                            <Badge tone={workerStatusTone(w.effectiveStatus)}>{w.effectiveStatus}</Badge>
                          </td>
                          <td className="py-2 pr-3 text-sm text-slate-700">
                            {w.activeAssignmentCount} / {w.capacity}
                          </td>
                          <td className="py-2 pr-3 text-xs text-slate-500 font-mono">{formatTime(w.lastHeartbeatAt)}</td>
                          <td className="py-2 pr-3">
                            <div className="flex items-center gap-2">
                              {w.effectiveStatus !== 'Disabled' && w.effectiveStatus !== 'Draining' && (
                                <Button
                                  variant="secondary"
                                  size="sm"
                                  disabled={drain.isPending}
                                  onClick={() => handleDrain(w.id)}
                                >
                                  Drain
                                </Button>
                              )}
                              {w.effectiveStatus === 'Draining' && (
                                <Button
                                  variant="secondary"
                                  size="sm"
                                  disabled={enable.isPending}
                                  onClick={() => handleEnable(w.id)}
                                >
                                  Enable
                                </Button>
                              )}
                              {w.effectiveStatus === 'Disabled' && (
                                <Button
                                  variant="secondary"
                                  size="sm"
                                  disabled={enable.isPending}
                                  onClick={() => handleEnable(w.id)}
                                >
                                  Enable
                                </Button>
                              )}
                              {w.effectiveStatus !== 'Disabled' && w.effectiveStatus !== 'Draining' && (
                                <Button
                                  variant="secondary"
                                  size="sm"
                                  disabled={disable.isPending}
                                  onClick={() => handleDisable(w.id)}
                                >
                                  Disable
                                </Button>
                              )}
                            </div>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </CardContent>
          </Card>
        </>
      )}
    </div>
  );
}