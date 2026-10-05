import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DashboardPage } from './DashboardPage';
import { dashboardEndpoints } from '../../lib/api/endpoints/dashboard';
import { projectsEndpoints } from '../../lib/api/endpoints/projects';
import { useProfile } from '../../lib/auth/useProfile';
import { ApiError } from '../../lib/api/client';
import { Permissions } from '../../lib/auth/permissions';
import { useAppStore } from '../../stores/useAppStore';

vi.mock('../../lib/api/endpoints/dashboard', () => ({
  dashboardKeys: {
    all: ['dashboard'],
    summary: (p: string, r: unknown) => ['dashboard', 'summary', p, r],
    trend: (p: string, r: unknown) => ['dashboard', 'trend', p, r],
    failures: (p: string, r: unknown) => ['dashboard', 'failures', p, r],
    defects: (p: string, r: unknown) => ['dashboard', 'defects', p, r],
    tickets: (p: string, r: unknown) => ['dashboard', 'tickets', p, r],
    executive: (p: string, r: unknown) => ['dashboard', 'executive', p, r],
    flakinessTrend: (p: string, r: unknown) => ['dashboard', 'flakiness-trend', p, r],
    healing: (p: string, r: unknown) => ['dashboard', 'healing', p, r],
    durations: (p: string, r: unknown) => ['dashboard', 'durations', p, r],
    readiness: (p: string, r: unknown) => ['dashboard', 'readiness', p, r],
  },
  dashboardEndpoints: {
    summary: vi.fn(),
    trend: vi.fn(),
    failures: vi.fn(),
    defects: vi.fn(),
    tickets: vi.fn(),
    executive: vi.fn(),
    flakinessTrend: vi.fn(),
    healing: vi.fn(),
    durations: vi.fn(),
    readiness: vi.fn(),
  },
}));

vi.mock('../../lib/api/endpoints/projects', () => ({
  projectKeys: { all: ['projects'] },
  projectsEndpoints: { list: vi.fn() },
}));

vi.mock('../../lib/auth/useProfile', () => ({
  useProfile: vi.fn(),
}));

// jsdom has no canvas: stub the renderer so chart options can be asserted
// without zrender's asynchronous paint loop.
vi.mock('echarts', () => ({
  init: vi.fn(() => ({ setOption: vi.fn(), resize: vi.fn(), dispose: vi.fn() })),
}));

const mockedSummary = vi.mocked(dashboardEndpoints.summary);
const mockedTrend = vi.mocked(dashboardEndpoints.trend);
const mockedFailures = vi.mocked(dashboardEndpoints.failures);
const mockedDefectOverview = vi.mocked(dashboardEndpoints.defects);
const mockedExecutive = vi.mocked(dashboardEndpoints.executive);
const mockedFlakinessTrend = vi.mocked(dashboardEndpoints.flakinessTrend);
const mockedHealing = vi.mocked(dashboardEndpoints.healing);
const mockedDurations = vi.mocked(dashboardEndpoints.durations);
const mockedProjectList = vi.mocked(projectsEndpoints.list);
const mockedProfile = vi.mocked(useProfile);

const executivePayload = () => ({
  projectId: 'p1',
  from: '2026-08-30T00:00:00Z',
  to: '2026-09-29T00:00:00Z',
  terminalExecutions: 10,
  totalExecutions: 10,
  passRate: 0.7,
  failRate: 0.2,
  flakinessIndex: 25,
  flakyTests: 1,
  eligibleTests: 4,
  automationCoverage: 75,
  automatedCases: 3,
  eligibleCases: 4,
  releaseReadiness: 82,
  readinessStatus: 'Ready',
  readinessComponents: [
    { component: 'PassRate', value: 70, weight: 35, contribution: 24.5, threshold: 'Terminal pass rate ≥ 80%', detail: 'd' },
  ],
  openCriticalHighDefects: 0,
  defectsPer100Executions: 10,
  defectsCreated: 1,
  defectsPerCase: 0.5,
  averageDurationMs: 1200,
  totalDurationMs: 12000,
  durationSampleCount: 10,
  healingSuccessRate: 66.7,
  healingAttempts: 3,
  healingApplied: 2,
  unstableExecutions: 1,
  cancelledExecutions: 0,
  highRiskTests: 0,
  mediumRiskTests: 0,
  lowRiskTests: 0,
  insufficientHistoryTests: 0,
});

const summaryPayload = (overrides = {}) => ({
  projectId: 'p1',
  from: '2026-08-30T00:00:00Z',
  to: '2026-09-29T00:00:00Z',
  testCases: { total: 12, approved: 9 },
  executions: {
    total: 40, passed: 30, failed: 8, cancelled: 1, timedOut: 1,
    error: 0, queuedOrRunning: 0, passRate: 0.75,
  },
  defects: { total: 5, open: 2, inProgress: 1, resolved: 1, closed: 1, rejected: 0, highSeverity: 2 },
  tickets: { total: 3, synced: 2, failed: 1, pending: 0 },
  recentExecutions: [
    {
      id: 'e1', status: 'Failed', testKey: 'LOGIN-001', testTitle: 'Login',
      testCaseVersionNumber: 3, failureClassification: 'ApplicationDefect',
      durationMs: 1200, startedAt: '2026-09-28T10:00:00Z',
      completedAt: '2026-09-28T10:01:00Z', createdAt: '2026-09-28T10:00:00Z',
    },
  ],
  recentDefects: [
    {
      id: 'd1', title: 'Login returns 500', severity: 'High', status: 'Open',
      failureClassification: 'ApplicationDefect', jiraKey: 'ABC-123',
      createdAt: '2026-09-28T11:00:00Z',
    },
  ],
  recentTickets: [
    {
      id: 'k1', provider: 'jira', externalKey: 'ABC-123', syncStatus: 'Synced',
      defectId: 'd1', createdAt: '2026-09-28T12:00:00Z',
    },
  ],
  recentActivity: [
    { action: 'defect.created', entityType: 'defect', entityId: 'd1', createdAt: '2026-09-28T11:00:00Z' },
  ],
  ...overrides,
});

const emptySummary = () =>
  summaryPayload({
    testCases: { total: 0, approved: 0 },
    executions: { total: 0, passed: 0, failed: 0, cancelled: 0, timedOut: 0, error: 0, queuedOrRunning: 0, passRate: null },
    defects: { total: 0, open: 0, inProgress: 0, resolved: 0, closed: 0, rejected: 0, highSeverity: 0 },
    tickets: { total: 0, synced: 0, failed: 0, pending: 0 },
    recentExecutions: [],
    recentDefects: [],
    recentTickets: [],
    recentActivity: [],
  });

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <DashboardPage />
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
      roles: ['tester'],
      permissions,
    },
    isLoading: false,
  } as never);
}

describe('DashboardPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    useAppStore.setState({ currentProjectId: null });
    profileWith([Permissions.DashboardRead, Permissions.ReportsRead, Permissions.ProjectsRead]);
    mockedProjectList.mockResolvedValue({
      items: [{ id: 'p1', name: 'Alpha', key: 'A', description: null, repositoryUrl: null, targetUrl: null, framework: null, platform: null, status: 'Active', memberCount: 1, updatedAt: '2026-09-29T00:00:00Z' }],
      totalCount: 1, page: 1, pageSize: 100,
    });
    mockedSummary.mockResolvedValue(summaryPayload());
    mockedTrend.mockResolvedValue({
      projectId: 'p1', from: '', to: '', granularity: 'day',
      points: [{ date: '2026-09-28', total: 3, passed: 2, failed: 1, cancelled: 0, timedOut: 0, error: 0 }],
    });
    mockedFailures.mockResolvedValue({
      projectId: 'p1', from: '', to: '', total: 3,
      items: [{ name: 'ApplicationDefect', count: 2 }, { name: 'Unknown', count: 1 }],
    });
    mockedDefectOverview.mockResolvedValue({
      projectId: 'p1', from: '', to: '',
      totals: { total: 5, open: 2, inProgress: 1, resolved: 1, closed: 1, rejected: 0, highSeverity: 2 },
      bySeverity: [{ name: 'High', count: 2 }],
      byClassification: [{ name: 'ApplicationDefect', count: 2 }],
      recent: [],
    });
    mockedExecutive.mockResolvedValue(executivePayload());
    mockedFlakinessTrend.mockResolvedValue({
      projectId: 'p1', from: '', to: '', granularity: 'day',
      points: [{ date: '2026-09-28', eligibleTests: 2, flakyTests: 1, index: 50 }],
    });
    mockedHealing.mockResolvedValue({
      projectId: 'p1', from: '', to: '', attempts: 3, applied: 2, failed: 1,
      deterministic: 2, aiAssisted: 1, successRate: 66.7,
      testsWithHealing: 2, executionsWithHealing: 2, testsHealedAndFlaky: 1,
      points: [{ date: '2026-09-28', attempts: 3, applied: 2 }],
    });
    mockedDurations.mockResolvedValue({
      projectId: 'p1', from: '', to: '', count: 10, averageMs: 1200,
      minMs: 100, maxMs: 5000, totalMs: 12000, p50Ms: 900, p90Ms: 3000,
      slaConfigured: false, openDefectAging: [{ name: '0-7 days', count: 2 }], points: [],
    });
  });

  afterEach(() => {
    cleanup();
    useAppStore.setState({ currentProjectId: null });
  });

  it('renders real KPI values from the summary API', async () => {
    renderPage();
    expect(await screen.findByText('12')).toBeTruthy();
    expect(screen.getByText('9 approved')).toBeTruthy();
    expect(screen.getByText('75.0%')).toBeTruthy();
    expect(screen.getByText('LOGIN-001')).toBeTruthy();
    expect(screen.getByText('Login returns 500')).toBeTruthy();
    expect(screen.getByText('ABC-123')).toBeTruthy();
    // No fake delta percentages anywhere.
    expect(screen.queryByText(/from last week/i)).toBeNull();
  });

  it('shows intentional empty states and a neutral pass rate', async () => {
    mockedSummary.mockResolvedValue(emptySummary());
    mockedTrend.mockResolvedValue({ projectId: 'p1', from: '', to: '', granularity: 'day', points: [] });
    mockedFailures.mockResolvedValue({ projectId: 'p1', from: '', to: '', total: 0, items: [] });
    renderPage();
    expect(await screen.findByText('No executions yet.')).toBeTruthy();
    expect(screen.getByText('No terminal executions yet')).toBeTruthy();
    expect(screen.getByText('No defects filed.')).toBeTruthy();
  });

  it('shows a page-level error when the summary fails', async () => {
    mockedSummary.mockRejectedValue(new ApiError(500, 'INTERNAL_ERROR', 'Boom'));
    renderPage();
    expect(await screen.findByText('Something went wrong')).toBeTruthy();
  });

  it('hides the dashboard without the dashboard permission', async () => {
    profileWith([Permissions.ProjectsRead]);
    renderPage();
    expect(await screen.findByText('System Overview')).toBeTruthy();
    expect(mockedSummary).not.toHaveBeenCalled();
  });

  it('switches projects and reloads metrics', async () => {
    mockedProjectList.mockResolvedValue({
      items: [
        { id: 'p1', name: 'Alpha', key: 'A', description: null, repositoryUrl: null, targetUrl: null, framework: null, platform: null, status: 'Active', memberCount: 1, updatedAt: '2026-09-29T00:00:00Z' },
        { id: 'p2', name: 'Beta', key: 'B', description: null, repositoryUrl: null, targetUrl: null, framework: null, platform: null, status: 'Active', memberCount: 1, updatedAt: '2026-09-29T00:00:00Z' },
      ],
      totalCount: 2, page: 1, pageSize: 100,
    });
    renderPage();
    await screen.findByText('12');
    fireEvent.change(screen.getByLabelText('Project'), { target: { value: 'p2' } });
    await waitFor(() =>
      expect(mockedSummary).toHaveBeenCalledWith('p2', expect.anything()),
    );
  });

  it('keeps chart data available as text', async () => {
    renderPage();
    // Fallback text is always in the accessibility tree, even when canvas renders.
    expect(await screen.findByText('ApplicationDefect: 2')).toBeTruthy();
  });
});
