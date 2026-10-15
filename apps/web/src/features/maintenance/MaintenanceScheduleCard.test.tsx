import { render, screen, waitFor, fireEvent, act } from '@testing-library/react';
import { vi, describe, it, beforeEach, expect } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MaintenanceScheduleCard } from './MaintenanceScheduleCard';
import { maintenanceEndpoints } from '../../lib/api/endpoints/maintenance';
import { useProfile } from '../../lib/auth/useProfile';

vi.mock('../../lib/api/endpoints/maintenance', () => ({
  maintenanceKeys: {
    all: ['maintenance'],
    list: (projectId: string, filters: unknown, page: number) =>
      ['maintenance', 'list', projectId, filters, page],
    detail: (projectId: string, proposalId: string) =>
      ['maintenance', 'detail', projectId, proposalId],
    schedule: (projectId: string) => ['maintenance', 'schedule', projectId],
  },
  maintenanceEndpoints: {
    list: vi.fn(),
    detail: vi.fn(),
    scan: vi.fn(),
    approve: vi.fn(),
    reject: vi.fn(),
    getSchedule: vi.fn(),
    createSchedule: vi.fn(),
    updateSchedule: vi.fn(),
    pauseSchedule: vi.fn(),
    resumeSchedule: vi.fn(),
    deleteSchedule: vi.fn(),
    nextScheduleRun: vi.fn(),
  },
}));

vi.mock('../../lib/auth/useProfile', () => ({
  useProfile: vi.fn(),
}));

const mockedGetSchedule = vi.mocked(maintenanceEndpoints.getSchedule);
const mockedCreateSchedule = vi.mocked(maintenanceEndpoints.createSchedule);
const mockedPauseSchedule = vi.mocked(maintenanceEndpoints.pauseSchedule);
const mockedNextRun = vi.mocked(maintenanceEndpoints.nextScheduleRun);
const mockedProfile = vi.mocked(useProfile);

const projectId = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';

function renderCard(props: { canManage: boolean; onError?: (message: string) => void }) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });
  mockedProfile.mockReturnValue({
    data: { permissions: props.canManage ? ['testcases.read', 'testcases.manage'] : ['testcases.read'] },
    isLoading: false,
  } as never);
  return render(
    <QueryClientProvider client={client}>
      <MaintenanceScheduleCard projectId={projectId} canManage={props.canManage} onError={props.onError ?? (() => {})} />
    </QueryClientProvider>,
  );
}

const mockSchedule = {
  id: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
  projectId,
  cronExpression: '30 2 * * *',
  timeZoneId: 'UTC',
  status: 'Active',
  lastTriggeredAt: '2026-10-08T02:30:00Z',
  lastCandidatesDetected: 2,
  lastProposalsCreated: 1,
  nextRunAt: null,
  createdAt: '2026-10-07T00:00:00Z',
  updatedAt: '2026-10-07T00:00:00Z',
};

describe('MaintenanceScheduleCard', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockedNextRun.mockResolvedValue({ nextRunAt: '2026-10-09T02:30:00Z' });
  });

  it('shows empty state with configure action for managers', async () => {
    mockedGetSchedule.mockRejectedValue(Object.assign(new Error('Not Found'), { status: 404 }));
    renderCard({ canManage: true });
    await waitFor(() => {
      expect(screen.getByText(/no scheduled scans/i)).toBeDefined();
    });
    expect(screen.getByText('Configure schedule')).toBeDefined();
  });

  it('renders schedule status, cadence, and last result', async () => {
    mockedGetSchedule.mockResolvedValue(mockSchedule);
    renderCard({ canManage: true });
    await waitFor(() => {
      expect(screen.getByText('Active')).toBeDefined();
    });
    expect(screen.getByText(/30 2 \* \* \* · UTC/)).toBeDefined();
    expect(screen.getByText(/2 candidates, 1 new proposals/)).toBeDefined();
  });

  it('does not fetch next run until explicitly requested', async () => {
    mockedGetSchedule.mockResolvedValue(mockSchedule);
    renderCard({ canManage: true });
    await waitFor(() => {
      expect(screen.getByText('Active')).toBeDefined();
    });
    expect(mockedNextRun).not.toHaveBeenCalled();
    await act(async () => {
      fireEvent.click(screen.getByLabelText('Show next scheduled run'));
    });
    await waitFor(() => {
      expect(mockedNextRun).toHaveBeenCalledWith(projectId);
    });
  });

  it('pauses through the endpoint', async () => {
    mockedGetSchedule.mockResolvedValue(mockSchedule);
    mockedPauseSchedule.mockResolvedValue(undefined);
    renderCard({ canManage: true });
    await waitFor(() => {
      expect(screen.getByLabelText('Pause maintenance schedule')).toBeDefined();
    });
    await act(async () => {
      fireEvent.click(screen.getByLabelText('Pause maintenance schedule'));
    });
    await waitFor(() => {
      expect(mockedPauseSchedule).toHaveBeenCalledWith(projectId);
    });
  });

  it('validates the form without calling the API', async () => {
    mockedGetSchedule.mockRejectedValue(Object.assign(new Error('Not Found'), { status: 404 }));
    renderCard({ canManage: true });
    await waitFor(() => {
      expect(screen.getByText('Configure schedule')).toBeDefined();
    });
    await act(async () => {
      fireEvent.click(screen.getByText('Configure schedule'));
    });
    await act(async () => {
      fireEvent.click(screen.getByText('Create schedule'));
    });
    expect(screen.getByRole('alert')).toBeDefined();
    expect(mockedCreateSchedule).not.toHaveBeenCalled();
  });

  it('hides management actions from viewers', async () => {
    mockedGetSchedule.mockResolvedValue(mockSchedule);
    renderCard({ canManage: false });
    await waitFor(() => {
      expect(screen.getByText('Active')).toBeDefined();
    });
    expect(screen.queryByLabelText('Pause maintenance schedule')).toBeNull();
    expect(screen.queryByLabelText('Edit maintenance schedule')).toBeNull();
    expect(screen.queryByLabelText('Delete maintenance schedule')).toBeNull();
  });

  it('surfaces server validation errors', async () => {
    mockedGetSchedule.mockRejectedValue(Object.assign(new Error('Not Found'), { status: 404 }));
    mockedCreateSchedule.mockRejectedValue(
      new Error('Maintenance scans run at most once per hour.'),
    );
    renderCard({ canManage: true });
    await waitFor(() => {
      expect(screen.getByText('Configure schedule')).toBeDefined();
    });
    await act(async () => {
      fireEvent.click(screen.getByText('Configure schedule'));
    });
    await act(async () => {
      fireEvent.change(screen.getByLabelText(/cron expression/i), { target: { value: '*/5 * * * *' } });
    });
    await act(async () => {
      fireEvent.click(screen.getByText('Create schedule'));
    });
    await waitFor(() => {
      expect(screen.getByRole('alert')).toBeDefined();
    });
  });
});
