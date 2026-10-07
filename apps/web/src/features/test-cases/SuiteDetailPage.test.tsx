import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SuiteDetailPage } from './SuiteDetailPage';
import { suitesEndpoints, suiteSchedulesEndpoints } from '../../lib/api/endpoints/suites';
import { useProfile } from '../../lib/auth/useProfile';
import { ApiError } from '../../lib/api/client';
import { Permissions } from '../../lib/auth/permissions';

vi.mock('../../lib/api/endpoints/suites', () => ({
  suiteKeys: {
    all: ['test-suites'],
    details: (id: string) => ['test-suites', 'details', id],
    executions: (id: string, filters: unknown, page: number) => ['test-suites', 'executions', id, filters, page],
    report: (id: string, filters: unknown) => ['test-suites', 'report', id, filters],
  },
  scheduleKeys: {
    all: ['test-suite-schedules'],
    list: (suiteId: string) => ['test-suite-schedules', 'list', suiteId],
    details: (id: string) => ['test-suite-schedules', 'details', id],
  },
  suitesEndpoints: {
    get: vi.fn(),
    getExecutions: vi.fn(),
    getReport: vi.fn(),
    execute: vi.fn(),
    archive: vi.fn(),
  },
  suiteSchedulesEndpoints: {
    list: vi.fn(),
    get: vi.fn(),
    create: vi.fn(),
    update: vi.fn(),
    archive: vi.fn(),
    pause: vi.fn(),
    resume: vi.fn(),
    runNow: vi.fn(),
  },
}));

vi.mock('../../lib/auth/useProfile', () => ({
  useProfile: vi.fn(),
}));

// jsdom has no canvas: stub the renderer so chart options can be asserted
// without zrender's asynchronous paint loop.
vi.mock('echarts', () => ({
  init: vi.fn(() => ({ setOption: vi.fn(), resize: vi.fn(), dispose: vi.fn() })),
}));

const mockedGet = vi.mocked(suitesEndpoints.get);
const mockedExecutions = vi.mocked(suitesEndpoints.getExecutions);
const mockedReport = vi.mocked(suitesEndpoints.getReport);
const mockedExecute = vi.mocked(suitesEndpoints.execute);
const mockedArchive = vi.mocked(suitesEndpoints.archive);
const mockedSchedules = vi.mocked(suiteSchedulesEndpoints.list);
const mockedScheduleGet = vi.mocked(suiteSchedulesEndpoints.get);
const mockedScheduleCreate = vi.mocked(suiteSchedulesEndpoints.create);
const mockedScheduleUpdate = vi.mocked(suiteSchedulesEndpoints.update);
const mockedScheduleArchive = vi.mocked(suiteSchedulesEndpoints.archive);
const mockedSchedulePause = vi.mocked(suiteSchedulesEndpoints.pause);
const mockedScheduleResume = vi.mocked(suiteSchedulesEndpoints.resume);
const mockedScheduleRun = vi.mocked(suiteSchedulesEndpoints.runNow);
const mockedProfile = vi.mocked(useProfile);

function renderPage() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/projects/p1/test-suites/s1']}>
        <Routes>
          <Route path="/projects/:projectId/test-suites/:suiteId" element={<SuiteDetailPage />} />
          <Route path="/projects/:projectId/test-suites" element={<div>suite list</div>} />
          <Route path="/projects/:projectId/executions/:executionId" element={<div>execution details</div>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

function profileWith(permissions: string[]) {
  mockedProfile.mockReturnValue({
    data: {
      id: 'u1',
      externalIdentityId: 'ext',
      email: 'qa@example.com',
      displayName: 'QA',
      roles: ['qa-lead'],
      permissions,
    },
    isLoading: false,
  } as never);
}

const detail = (overrides = {}) => ({
  id: 's1',
  projectId: 'p1',
  name: 'Regression',
  description: 'Main suite',
  status: 'Active',
  createdAt: '2026-09-01T00:00:00Z',
  updatedAt: '2026-10-01T00:00:00Z',
  members: [
    { testCaseId: 'c1', testKey: 'LOGIN-001', title: 'Login works', executionOrder: 1, jiraIssueKey: null, freshnessState: null },
    { testCaseId: 'c2', testKey: 'SMOKE-001', title: 'Smoke works', executionOrder: 2, jiraIssueKey: 'PROJ-1', freshnessState: null },
  ],
  ...overrides,
});

const managerPerms = [Permissions.TestCasesRead, Permissions.TestCasesManage, Permissions.ExecutionsRead, Permissions.ReportsRead];

describe('SuiteDetailPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('confirm', vi.fn(() => true));
    mockedExecutions.mockResolvedValue({ items: [], totalCount: 0, page: 1, pageSize: 25 } as never);
    mockedReport.mockResolvedValue(null as never);
    mockedSchedules.mockResolvedValue([]);
    mockedScheduleGet.mockResolvedValue(null as never);
  });
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it('shows a loading state', () => {
    profileWith(managerPerms);
    mockedGet.mockReturnValue(new Promise(() => {}));
    renderPage();
    expect(screen.getByLabelText('Loading suite details')).toBeTruthy();
  });

  it('shows a not-found state', async () => {
    profileWith(managerPerms);
    mockedGet.mockResolvedValue(null as never);
    renderPage();
    expect(await screen.findByText('Suite not found')).toBeTruthy();
  });

  it('renders members with deterministic order', async () => {
    profileWith(managerPerms);
    mockedGet.mockResolvedValue(detail() as never);
    renderPage();
    expect(await screen.findByText('LOGIN-001')).toBeTruthy();
    expect(screen.getByText('SMOKE-001')).toBeTruthy();
    expect(screen.getByText('PROJ-1')).toBeTruthy();
  });

  it('runs the suite and navigates to the execution', async () => {
    profileWith(managerPerms);
    mockedGet.mockResolvedValue(detail() as never);
    mockedExecute.mockResolvedValue({
      executionId: 'e1', suiteId: 's1', testCount: 2, status: 'Queued', createdAt: '2026-10-01T00:00:00Z',
    } as never);
    renderPage();
    fireEvent.click(await screen.findByText('Run now (2)'));
    await waitFor(() => expect(mockedExecute).toHaveBeenCalledWith(
      expect.objectContaining({ projectId: 'p1', suiteId: 's1' })));
    expect(await screen.findByText('execution details')).toBeTruthy();
  });

  it('surfaces execution failures inline', async () => {
    profileWith(managerPerms);
    mockedGet.mockResolvedValue(detail() as never);
    mockedExecute.mockRejectedValue(new ApiError(400, 'VALIDATION_ERROR', 'No approved version'));
    renderPage();
    fireEvent.click(await screen.findByText('Run now (2)'));
    expect(await screen.findByRole('alert')).toBeTruthy();
    expect(screen.getByRole('alert').textContent).toContain('No approved version');
  });

  it('renders execution history and report', async () => {
    profileWith(managerPerms);
    mockedGet.mockResolvedValue(detail() as never);
    mockedExecutions.mockResolvedValue({
      items: [{
        executionId: 'e1', status: 'Passed', triggerType: 'Manual',
        createdAt: '2026-10-01T00:00:00Z', startedAt: '2026-10-01T00:00:01Z',
        completedAt: '2026-10-01T00:05:00Z', testCount: 2, passedCount: 2, failedCount: 0,
      }],
      totalCount: 1, page: 1, pageSize: 25,
    } as never);
    mockedReport.mockResolvedValue({
      suiteId: 's1', suiteName: 'Regression', totalExecutions: 1,
      passedCount: 2, failedCount: 0, cancelledCount: 0, timedOutCount: 0, errorCount: 0,
      passRate: 100, totalDurationMs: 2000, averageDurationMs: 1000, latestExecutionAt: '2026-10-01T00:00:00Z',
      triggerBreakdown: [
        { trigger: 'Manual', total: 1, passed: 2, failed: 0, passRate: 100 },
      ],
      trend: [
        { date: '2026-10-01', total: 1, passed: 2, failed: 0, cancelled: 0, timedOut: 0, error: 0, passRate: 100, totalDurationMs: 2000 },
      ],
    } as never);
    renderPage();
    expect(await screen.findByText('Execution history')).toBeTruthy();
    expect(screen.getByText('Execution report')).toBeTruthy();
    expect(screen.getAllByText('Passed').length).toBeGreaterThanOrEqual(2);
    expect(screen.getByText('Total executions')).toBeTruthy();
    expect(screen.getByText('By trigger')).toBeTruthy();
    expect(screen.getAllByText('Manual').length).toBeGreaterThanOrEqual(1);
    expect(screen.getByText('Daily trend (UTC)')).toBeTruthy();
  });

  it('hides management actions from viewers but keeps history and report', async () => {
    profileWith([Permissions.TestCasesRead, Permissions.ExecutionsRead, Permissions.ReportsRead]);
    mockedGet.mockResolvedValue(detail() as never);
    renderPage();
    await screen.findByText('LOGIN-001');
    expect(screen.queryByText(/Run now/)).toBeNull();
    expect(screen.queryByText('Edit')).toBeNull();
    expect(screen.queryByText('Archive')).toBeNull();
    expect(screen.getByText('Execution history')).toBeTruthy();
  });

  it('archives and returns to the list', async () => {
    profileWith(managerPerms);
    mockedGet.mockResolvedValue(detail() as never);
    mockedArchive.mockResolvedValue(undefined as never);
    renderPage();
    fireEvent.click(await screen.findByText('Archive'));
    await waitFor(() => expect(mockedArchive).toHaveBeenCalledWith('s1'));
    expect(await screen.findByText('suite list')).toBeTruthy();
  });

  it('shows an error state when loading fails', async () => {
    profileWith(managerPerms);
    mockedGet.mockRejectedValue(new ApiError(500, 'INTERNAL_ERROR', 'Boom'));
    renderPage();
    expect(await screen.findByText('Something went wrong')).toBeTruthy();
  });

  it('renders schedules with status and cadence', async () => {
    profileWith(managerPerms);
    mockedGet.mockResolvedValue(detail() as never);
    mockedSchedules.mockResolvedValue([{
      id: 'sch1', projectId: 'p1', suiteId: 's1', suiteName: 'Regression',
      name: 'Nightly', cronExpression: '30 2 * * *', timeZoneId: 'UTC',
      status: 'Active', overlapPolicy: 'Skip',
      lastTriggeredAt: null, lastExecutionId: null, nextRunAt: null,
      createdAt: '2026-10-01T00:00:00Z', updatedAt: '2026-10-01T00:00:00Z',
    }]);
    renderPage();
    expect(await screen.findByText('Schedules')).toBeTruthy();
    expect(await screen.findByText('Nightly')).toBeTruthy();
    expect(screen.getByText('30 2 * * * · UTC · overlap Skip')).toBeTruthy();
  });

  it('creates a schedule from the form', async () => {
    profileWith(managerPerms);
    mockedGet.mockResolvedValue(detail() as never);
    mockedSchedules.mockResolvedValue([]);
    mockedScheduleCreate.mockResolvedValue({ id: 'sch9' } as never);
    renderPage();
    fireEvent.click(await screen.findByText('New schedule'));
    fireEvent.change(screen.getByLabelText('Name *'), { target: { value: 'Nightly' } });
    fireEvent.change(screen.getByLabelText('Cron expression *'), { target: { value: '30 2 * * *' } });
    fireEvent.click(screen.getByText('Create schedule'));
    await waitFor(() => expect(mockedScheduleCreate).toHaveBeenCalledWith('p1', 's1', expect.objectContaining({
      name: 'Nightly',
      cronExpression: '30 2 * * *',
      timeZoneId: 'UTC',
      overlapPolicy: 'Skip',
    })));
  });

  it('validates the schedule form without calling the API', async () => {
    profileWith(managerPerms);
    mockedGet.mockResolvedValue(detail() as never);
    mockedSchedules.mockResolvedValue([]);
    renderPage();
    fireEvent.click(await screen.findByText('New schedule'));
    fireEvent.click(screen.getByText('Create schedule'));
    expect(await screen.findByText('Schedule name is required.')).toBeTruthy();
    expect(mockedScheduleCreate).not.toHaveBeenCalled();
  });

  it('pauses and runs a schedule now', async () => {
    profileWith(managerPerms);
    mockedGet.mockResolvedValue(detail() as never);
    mockedSchedules.mockResolvedValue([{
      id: 'sch1', projectId: 'p1', suiteId: 's1', suiteName: 'Regression',
      name: 'Nightly', cronExpression: '30 2 * * *', timeZoneId: 'UTC',
      status: 'Active', overlapPolicy: 'Skip',
      lastTriggeredAt: null, lastExecutionId: null, nextRunAt: null,
      createdAt: '2026-10-01T00:00:00Z', updatedAt: '2026-10-01T00:00:00Z',
    }]);
    mockedSchedulePause.mockResolvedValue(undefined as never);
    mockedScheduleRun.mockResolvedValue({ executionId: 'e9' } as never);
    renderPage();
    fireEvent.click(await screen.findByLabelText('Pause schedule Nightly'));
    await waitFor(() => expect(mockedSchedulePause).toHaveBeenCalledWith('sch1'));
    fireEvent.click(await screen.findByLabelText('Run schedule Nightly now'));
    await waitFor(() => expect(mockedScheduleRun).toHaveBeenCalledWith('sch1'));
  });

  it('expands a schedule to show the next run', async () => {
    profileWith(managerPerms);
    mockedGet.mockResolvedValue(detail() as never);
    mockedSchedules.mockResolvedValue([{
      id: 'sch1', projectId: 'p1', suiteId: 's1', suiteName: 'Regression',
      name: 'Nightly', cronExpression: '30 2 * * *', timeZoneId: 'UTC',
      status: 'Active', overlapPolicy: 'Skip',
      lastTriggeredAt: null, lastExecutionId: null, nextRunAt: null,
      createdAt: '2026-10-01T00:00:00Z', updatedAt: '2026-10-01T00:00:00Z',
    }]);
    mockedScheduleGet.mockResolvedValue({
      id: 'sch1', nextRunAt: '2026-10-08T02:30:00Z',
    } as never);
    renderPage();
    fireEvent.click(await screen.findByText('Nightly'));
    expect(await screen.findByText(/Next:/)).toBeTruthy();
    expect(mockedScheduleGet).toHaveBeenCalledWith('sch1');
  });

  it('edits and deletes a schedule', async () => {
    profileWith(managerPerms);
    mockedGet.mockResolvedValue(detail() as never);
    const sched = {
      id: 'sch1', projectId: 'p1', suiteId: 's1', suiteName: 'Regression',
      name: 'Nightly', cronExpression: '30 2 * * *', timeZoneId: 'UTC',
      status: 'Active', overlapPolicy: 'Skip',
      lastTriggeredAt: null, lastExecutionId: null, nextRunAt: null,
      createdAt: '2026-10-01T00:00:00Z', updatedAt: '2026-10-01T00:00:00Z',
    };
    mockedSchedules.mockResolvedValue([sched]);
    mockedScheduleUpdate.mockResolvedValue(sched as never);
    mockedScheduleArchive.mockResolvedValue(undefined as never);
    renderPage();
    fireEvent.click(await screen.findByLabelText('Edit schedule Nightly'));
    fireEvent.change(screen.getByLabelText('Name *'), { target: { value: 'Nightly v2' } });
    fireEvent.click(screen.getByText('Save changes'));
    await waitFor(() => expect(mockedScheduleUpdate).toHaveBeenCalledWith('sch1', expect.objectContaining({
      name: 'Nightly v2',
    })));
    fireEvent.click(await screen.findByLabelText('Delete schedule Nightly'));
    await waitFor(() => expect(mockedScheduleArchive).toHaveBeenCalledWith('sch1'));
  });

  it('resumes a disabled schedule', async () => {
    profileWith(managerPerms);
    mockedGet.mockResolvedValue(detail() as never);
    mockedSchedules.mockResolvedValue([{
      id: 'sch1', projectId: 'p1', suiteId: 's1', suiteName: 'Regression',
      name: 'Nightly', cronExpression: '30 2 * * *', timeZoneId: 'UTC',
      status: 'Disabled', overlapPolicy: 'Skip',
      lastTriggeredAt: null, lastExecutionId: null, nextRunAt: null,
      createdAt: '2026-10-01T00:00:00Z', updatedAt: '2026-10-01T00:00:00Z',
    }]);
    mockedScheduleResume.mockResolvedValue(undefined as never);
    renderPage();
    fireEvent.click(await screen.findByLabelText('Resume schedule Nightly'));
    await waitFor(() => expect(mockedScheduleResume).toHaveBeenCalledWith('sch1'));
  });

  it('hides schedule management from viewers', async () => {
    profileWith([Permissions.TestCasesRead, Permissions.ExecutionsRead, Permissions.ReportsRead]);
    mockedGet.mockResolvedValue(detail() as never);
    mockedSchedules.mockResolvedValue([{
      id: 'sch1', projectId: 'p1', suiteId: 's1', suiteName: 'Regression',
      name: 'Nightly', cronExpression: '30 2 * * *', timeZoneId: 'UTC',
      status: 'Active', overlapPolicy: 'Skip',
      lastTriggeredAt: null, lastExecutionId: null, nextRunAt: null,
      createdAt: '2026-10-01T00:00:00Z', updatedAt: '2026-10-01T00:00:00Z',
    }]);
    renderPage();
    await screen.findByText('Nightly');
    expect(screen.queryByText('New schedule')).toBeNull();
    expect(screen.queryByLabelText('Pause schedule Nightly')).toBeNull();
  });
});
