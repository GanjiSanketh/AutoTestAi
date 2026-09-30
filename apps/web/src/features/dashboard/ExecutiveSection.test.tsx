import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ExecutiveSection } from './ExecutiveSection';
import { dashboardEndpoints } from '../../lib/api/endpoints/dashboard';
import { ApiError } from '../../lib/api/client';

vi.mock('../../lib/api/endpoints/dashboard', () => ({
  dashboardKeys: {
    all: ['dashboard'],
    executive: (p: string, r: unknown) => ['dashboard', 'executive', p, r],
    flakinessTrend: (p: string, r: unknown) => ['dashboard', 'flakiness-trend', p, r],
    healing: (p: string, r: unknown) => ['dashboard', 'healing', p, r],
    durations: (p: string, r: unknown) => ['dashboard', 'durations', p, r],
    readiness: (p: string, r: unknown) => ['dashboard', 'readiness', p, r],
  },
  dashboardEndpoints: {
    executive: vi.fn(),
    flakinessTrend: vi.fn(),
    healing: vi.fn(),
    durations: vi.fn(),
    readiness: vi.fn(),
  },
}));

vi.mock('echarts', () => ({
  init: vi.fn(() => ({ setOption: vi.fn(), resize: vi.fn(), dispose: vi.fn() })),
}));

const mockedExecutive = vi.mocked(dashboardEndpoints.executive);
const mockedFlakinessTrend = vi.mocked(dashboardEndpoints.flakinessTrend);
const mockedHealing = vi.mocked(dashboardEndpoints.healing);
const mockedDurations = vi.mocked(dashboardEndpoints.durations);

const overview = (overrides = {}) => ({
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
  ...overrides,
});

function renderSection() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <ExecutiveSection projectId="p1" range={{ from: '2026-08-30' }} enabled />
    </QueryClientProvider>,
  );
}

describe('ExecutiveSection (Slice 12)', () => {
  it('renders KPI values with accessible text', async () => {
    mockedExecutive.mockResolvedValue(overview());
    mockedFlakinessTrend.mockResolvedValue({
      projectId: 'p1', from: '', to: '', granularity: 'day',
      points: [{ date: '2026-09-28', eligibleTests: 2, flakyTests: 1, index: 50 }],
    });
    mockedHealing.mockResolvedValue({
      projectId: 'p1', from: '', to: '', attempts: 3, applied: 2, failed: 1,
      deterministic: 2, aiAssisted: 1, successRate: 66.7,
      testsWithHealing: 2, executionsWithHealing: 2, testsHealedAndFlaky: 1,
      points: [],
    });
    mockedDurations.mockResolvedValue({
      projectId: 'p1', from: '', to: '', count: 10, averageMs: 1200,
      minMs: 100, maxMs: 5000, totalMs: 12000, p50Ms: 900, p90Ms: 3000,
      slaConfigured: false, openDefectAging: [{ name: '0-7 days', count: 2 }], points: [],
    });
    renderSection();
    expect(await screen.findByText('25.0%')).toBeTruthy();
    expect(screen.getByText('75.0%')).toBeTruthy();
    expect(screen.getByText('Ready')).toBeTruthy();
    expect(screen.getByText(/never an AI release decision/i)).toBeTruthy();
    // Chart fallback text stays in the accessibility tree.
    expect(await screen.findByText(/2026-09-28: 50.0%/)).toBeTruthy();
  });

  it('shows insufficient-data states instead of fake zeroes', async () => {
    mockedExecutive.mockResolvedValue(
      overview({
        flakinessIndex: null,
        flakyTests: 0,
        eligibleTests: 0,
        releaseReadiness: null,
        readinessStatus: 'InsufficientData',
        readinessComponents: [],
        healingSuccessRate: null,
        healingAttempts: 0,
        averageDurationMs: null,
      }),
    );
    mockedFlakinessTrend.mockResolvedValue({
      projectId: 'p1', from: '', to: '', granularity: 'day', points: [],
    });
    mockedHealing.mockResolvedValue({
      projectId: 'p1', from: '', to: '', attempts: 0, applied: 0, failed: 0,
      deterministic: 0, aiAssisted: 0, successRate: null,
      testsWithHealing: 0, executionsWithHealing: 0, testsHealedAndFlaky: 0, points: [],
    });
    mockedDurations.mockResolvedValue({
      projectId: 'p1', from: '', to: '', count: 0, averageMs: null,
      minMs: null, maxMs: null, totalMs: null, p50Ms: null, p90Ms: null,
      slaConfigured: false, openDefectAging: [], points: [],
    });
    renderSection();
    expect(await screen.findByText(/needs 2\+ verdicts per test/i)).toBeTruthy();
    expect(screen.getByText('Insufficient data')).toBeTruthy();
    expect(screen.getByText(/no healing attempts in this period/i)).toBeTruthy();
  });

  it('shows an error state with retry', async () => {
    mockedExecutive.mockRejectedValue(new ApiError(500, 'INTERNAL_ERROR', 'Boom'));
    renderSection();
    expect(await screen.findByText(/unavailable right now/i)).toBeTruthy();
  });
});
