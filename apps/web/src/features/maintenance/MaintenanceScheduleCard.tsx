import { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { CalendarClock } from 'lucide-react';
import { Button } from '../../components/ui/button';
import { Input } from '../../components/ui/input';
import { Badge } from '../../components/ui/badge';
import { Card, CardContent, CardHeader, CardTitle } from '../../components/ui/card';
import { Skeleton } from '../../components/ui/skeleton';
import { ErrorState } from '../../components/common/ErrorState';
import { maintenanceKeys, maintenanceEndpoints } from '../../lib/api/endpoints/maintenance';

const TIMEZONE_SUGGESTIONS = ['UTC', 'America/New_York', 'America/Chicago', 'America/Los_Angeles', 'Europe/London', 'Europe/Berlin', 'Asia/Kolkata', 'Asia/Singapore', 'Australia/Sydney'];

function scheduleTone(status: string): 'success' | 'warning' | 'neutral' | 'info' {
  switch (status) {
    case 'Active':
      return 'success';
    case 'Disabled':
      return 'warning';
    case 'Archived':
      return 'neutral';
    default:
      return 'info';
  }
}

/**
 * Compact one-per-project maintenance scan schedule card (Phase 4 Slice 10).
 * The schedule only controls WHEN the deterministic scan runs; every scan
 * result stays human-gated in the inbox below. Next-run is fetched only on
 * explicit request (no polling, no per-render describe).
 */
export function MaintenanceScheduleCard({
  projectId,
  canManage,
  onError,
}: {
  projectId: string;
  canManage: boolean;
  onError: (message: string) => void;
}) {
  const queryClient = useQueryClient();
  const [showForm, setShowForm] = useState(false);
  const [editing, setEditing] = useState(false);
  const [cron, setCron] = useState('');
  const [timeZone, setTimeZone] = useState('UTC');
  const [formError, setFormError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const schedule = useQuery({
    queryKey: maintenanceKeys.schedule(projectId),
    queryFn: () => maintenanceEndpoints.getSchedule(projectId),
    enabled: !!projectId,
    retry: false,
    staleTime: 15_000,
  });

  // Explicit next-run retrieval only: never fetched automatically.
  const [nextRunRequested, setNextRunRequested] = useState(false);
  const nextRun = useQuery({
    queryKey: [...maintenanceKeys.schedule(projectId), 'next-run'],
    queryFn: () => maintenanceEndpoints.nextScheduleRun(projectId),
    enabled: false,
    retry: false,
    staleTime: 15_000,
  });

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: maintenanceKeys.schedule(projectId) });
  };

  const saveMutation = useMutation({
    mutationFn: () =>
      editing
        ? maintenanceEndpoints.updateSchedule(projectId, {
            cronExpression: cron.trim(),
            timeZoneId: timeZone.trim() || 'UTC',
          })
        : maintenanceEndpoints.createSchedule(projectId, {
            cronExpression: cron.trim(),
            timeZoneId: timeZone.trim() || 'UTC',
          }),
    onSuccess: () => {
      setShowForm(false);
      setEditing(false);
      setFormError(null);
      setSaving(false);
      invalidate();
    },
    onError: (error: any) => {
      setFormError(error.message ?? 'Failed to save schedule.');
      setSaving(false);
    },
  });

  const actionMutation = useMutation({
    mutationFn: async (kind: 'pause' | 'resume' | 'delete') => {
      switch (kind) {
        case 'pause':
          await maintenanceEndpoints.pauseSchedule(projectId);
          return;
        case 'resume':
          await maintenanceEndpoints.resumeSchedule(projectId);
          return;
        case 'delete':
          await maintenanceEndpoints.deleteSchedule(projectId);
          return;
      }
    },
    onSuccess: () => {
      setNextRunRequested(false);
      invalidate();
    },
    onError: (error: any) => {
      onError(error.message ?? 'Schedule action failed.');
    },
  });

  const openCreate = () => {
    setEditing(false);
    setCron('');
    setTimeZone('UTC');
    setFormError(null);
    setShowForm(true);
  };

  const openEdit = () => {
    if (!schedule.data) return;
    setEditing(true);
    setCron(schedule.data.cronExpression);
    setTimeZone(schedule.data.timeZoneId);
    setFormError(null);
    setShowForm(true);
  };

  const handleSave = (e: React.FormEvent) => {
    e.preventDefault();
    if (!cron.trim()) {
      setFormError('Cron expression is required (at most once per hour, for example "0 * * * *").');
      return;
    }
    setSaving(true);
    saveMutation.mutate();
  };

  const handleAction = (kind: 'pause' | 'resume' | 'delete') => {
    if (kind === 'delete' && !confirm('Delete the maintenance scan schedule? The remote schedule is removed; history is retained in audit. Manual scans stay available.')) {
      return;
    }
    actionMutation.mutate(kind);
  };

  const handleNextRun = () => {
    setNextRunRequested(true);
    void nextRun.refetch();
  };

  // A missing schedule is a 404: the normal empty state, not an error.
  const isMissing = (schedule.error as any)?.status === 404
    || /not found/i.test((schedule.error as Error)?.message ?? '');

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-sm">
          <CalendarClock className="h-4 w-4" aria-hidden />
          Scheduled scans
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-3">
        {schedule.isLoading && (
          <div className="space-y-2" aria-label="Loading maintenance schedule">
            <Skeleton className="h-6" />
          </div>
        )}

        {schedule.isError && !isMissing && (
          <ErrorState error={schedule.error} onRetry={() => void schedule.refetch()} />
        )}

        {(!schedule.data && (schedule.isSuccess || isMissing)) && (
          <div className="space-y-2">
            <p className="text-sm text-slate-500">
              No scheduled scans. {canManage ? 'Configure one to run the deterministic scan on a cron cadence (at most hourly).' : ''}
            </p>
            {canManage && !showForm && (
              <Button variant="secondary" size="sm" onClick={openCreate}>
                Configure schedule
              </Button>
            )}
          </div>
        )}

        {schedule.data && (
          <div className="space-y-2">
            <div className="flex flex-wrap items-center gap-2">
              <Badge tone={scheduleTone(schedule.data.status)}>{schedule.data.status}</Badge>
              <span className="font-mono text-xs text-slate-700">
                {schedule.data.cronExpression} · {schedule.data.timeZoneId}
              </span>
            </div>
            <p className="text-xs text-slate-500">
              Last: {schedule.data.lastTriggeredAt ? new Date(schedule.data.lastTriggeredAt).toLocaleString() : 'never'}
              {schedule.data.lastCandidatesDetected !== null && schedule.data.lastCandidatesDetected !== undefined
                ? ` · ${schedule.data.lastCandidatesDetected} candidates, ${schedule.data.lastProposalsCreated ?? 0} new proposals`
                : ''}
            </p>
            {nextRunRequested && (
              <p className="text-xs text-slate-500" aria-live="polite">
                {nextRun.isLoading
                  ? 'Loading next run…'
                  : nextRun.data?.nextRunAt
                    ? `Next run: ${new Date(nextRun.data.nextRunAt).toLocaleString()}`
                    : nextRun.isError
                      ? 'Next run unavailable.'
                      : ''}
              </p>
            )}
            <div className="flex flex-wrap gap-2">
              {!nextRunRequested && (
                <Button variant="ghost" size="sm" onClick={handleNextRun} aria-label="Show next scheduled run">
                  Next run
                </Button>
              )}
              {canManage && schedule.data.status === 'Active' && (
                <Button variant="ghost" size="sm" onClick={() => handleAction('pause')} aria-label="Pause maintenance schedule">
                  Pause
                </Button>
              )}
              {canManage && schedule.data.status === 'Disabled' && (
                <Button variant="ghost" size="sm" onClick={() => handleAction('resume')} aria-label="Resume maintenance schedule">
                  Resume
                </Button>
              )}
              {canManage && (
                <>
                  <Button variant="ghost" size="sm" onClick={openEdit} aria-label="Edit maintenance schedule">
                    Edit
                  </Button>
                  <Button variant="ghost" size="sm" onClick={() => handleAction('delete')} className="text-red-600 hover:text-red-700" aria-label="Delete maintenance schedule">
                    Delete
                  </Button>
                </>
              )}
            </div>
            <p className="text-xs text-slate-400">
              Scheduled scans only create proposals — approval and application stay manual.
            </p>
          </div>
        )}

        {canManage && showForm && (
          <form onSubmit={handleSave} className="space-y-2 border-t border-slate-100 pt-3">
            <h3 className="text-sm font-medium text-slate-900">{editing ? 'Edit schedule' : 'New schedule'}</h3>
            <div>
              <label htmlFor="maintenance-cron" className="block text-xs font-medium text-slate-500">Cron expression * (at most hourly)</label>
              <Input
                id="maintenance-cron"
                value={cron}
                onChange={(e) => setCron(e.target.value)}
                placeholder="0 * * * *"
                maxLength={200}
                className="mt-1 font-mono"
              />
            </div>
            <div>
              <label htmlFor="maintenance-tz" className="block text-xs font-medium text-slate-500">Timezone</label>
              <Input
                id="maintenance-tz"
                value={timeZone}
                onChange={(e) => setTimeZone(e.target.value)}
                placeholder="UTC"
                maxLength={100}
                list="maintenance-tz-suggestions"
                className="mt-1"
              />
              <datalist id="maintenance-tz-suggestions">
                {TIMEZONE_SUGGESTIONS.map((tz) => (
                  <option key={tz} value={tz} />
                ))}
              </datalist>
            </div>
            {formError && (
              <p role="alert" className="text-xs text-red-600">{formError}</p>
            )}
            <div className="flex gap-2">
              <Button type="submit" size="sm" disabled={saving}>
                {saving ? 'Saving…' : editing ? 'Save changes' : 'Create schedule'}
              </Button>
              <Button type="button" variant="ghost" size="sm" onClick={() => { setShowForm(false); setFormError(null); }}>
                Cancel
              </Button>
            </div>
          </form>
        )}
      </CardContent>
    </Card>
  );
}
