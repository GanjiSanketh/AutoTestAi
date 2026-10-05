import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { FlakinessReportTab } from './FlakinessReportTab';
import { reportEndpoints } from '../../lib/api/endpoints/reports';
import { api, ApiError } from '../../lib/api/client';

vi.mock('../../lib/api/endpoints/reports', () => ({
  reportKeys: {
    all: ['reports'],
    flakiness: (p: string, f: unknown, page: number) => ['reports', 'flakiness', p, f, page],
  },
  reportEndpoints: {
    flakiness: vi.fn(),
    flakinessExportUrl: (p: string) => `/api/v1/projects/${p}/reports/flakiness/export`,
  },
}));

vi.mock('../../lib/api/client', async (importOriginal) => {
  const original = await importOriginal<typeof import('../../lib/api/client')>();
  return {
    ...original,
    api: { ...original.api, download: vi.fn() },
  };
});

const mockedReport = vi.mocked(reportEndpoints.flakiness);
const mockedDownload = vi.mocked(api.download);

const row = (overrides = {}) => ({
  testCaseId: 't1',
  testKey: 'LOGIN-001',
  title: 'Login',
  module: 'auth',
  priority: 'High',
  framework: 'playwright',
  platform: 'web',
  totalExecutions: 4,
  passed: 3,
  failed: 1,
  other: 0,
  isFlaky: true,
  flakinessRate: 25,
  lastOutcome: 'Failed',
  lastRunAt: '2026-09-28T12:00:00Z',
  healingAttempts: 1,
  healedRuns: 1,
  riskScore: 45,
  riskBand: 'Medium',
  riskFactors: ['Recent failure rate is elevated'],
  ...overrides,
});

function renderTab() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <FlakinessReportTab projectId="p1" range={{}} enabled />
    </QueryClientProvider>,
  );
}

describe('FlakinessReportTab (Slice 12)', () => {
  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  it('renders rows with flaky state and healing activity', async () => {
    mockedReport.mockResolvedValue({ items: [row()], totalCount: 1, page: 1, pageSize: 25 });
    renderTab();
    expect(await screen.findByText('LOGIN-001')).toBeTruthy();
    expect(screen.getByText('Flaky')).toBeTruthy();
    expect(screen.getByText('25.0%')).toBeTruthy();
    expect(screen.getByText('1/1 recovered')).toBeTruthy();
  });

  it('renders advisory risk score, band, and factors', async () => {
    mockedReport.mockResolvedValue({ items: [row()], totalCount: 1, page: 1, pageSize: 25 });
    renderTab();
    expect(await screen.findByText(/45 · Medium/)).toBeTruthy();
    expect(screen.getByText('Recent failure rate is elevated')).toBeTruthy();
    expect(screen.getByText(/advisory-only deterministic forecast/)).toBeTruthy();
  });

  it('renders a neutral state for insufficient history', async () => {
    mockedReport.mockResolvedValue({
      items: [row({ riskScore: null, riskBand: null, riskFactors: [] })],
      totalCount: 1,
      page: 1,
      pageSize: 25,
    });
    renderTab();
    expect(await screen.findByText('Insufficient history')).toBeTruthy();
  });

  it('shows an empty state when nothing matches', async () => {
    mockedReport.mockResolvedValue({ items: [], totalCount: 0, page: 1, pageSize: 25 });
    renderTab();
    expect(await screen.findByText(/no data for the selected period/i)).toBeTruthy();
  });

  it('filters server-side and paginates', async () => {
    mockedReport.mockResolvedValue({ items: [row()], totalCount: 40, page: 1, pageSize: 25 });
    renderTab();
    await screen.findByText('LOGIN-001');
    fireEvent.click(screen.getByLabelText('Flaky only'));
    await waitFor(() =>
      expect(mockedReport).toHaveBeenCalledWith(
        'p1',
        expect.objectContaining({ flakyOnly: true }),
        1,
        25,
      ),
    );
    // Wait for the refetched table before paging (filter change remounts rows).
    await screen.findByText('LOGIN-001');
    fireEvent.click(screen.getByRole('button', { name: /next/i }));
    await waitFor(() =>
      expect(mockedReport).toHaveBeenCalledWith('p1', expect.anything(), 2, 25),
    );
  });

  it('exports CSV through the authenticated download', async () => {
    mockedReport.mockResolvedValue({ items: [row()], totalCount: 1, page: 1, pageSize: 25 });
    mockedDownload.mockResolvedValue({ blob: new Blob(['a']), fileName: 'flakiness.csv' });
    const createSpy = vi.spyOn(document, 'createElement');
    renderTab();
    await screen.findByText('LOGIN-001');
    fireEvent.click(screen.getByRole('button', { name: /export csv/i }));
    await waitFor(() => expect(mockedDownload).toHaveBeenCalled());
    createSpy.mockRestore();
  });

  it('surfaces export errors accessibly', async () => {
    mockedReport.mockResolvedValue({ items: [row()], totalCount: 1, page: 1, pageSize: 25 });
    mockedDownload.mockRejectedValue(new ApiError(403, 'FORBIDDEN', 'no'));
    renderTab();
    await screen.findByText('LOGIN-001');
    fireEvent.click(screen.getByRole('button', { name: /export csv/i }));
    expect(await screen.findByRole('alert')).toBeTruthy();
  });
});
