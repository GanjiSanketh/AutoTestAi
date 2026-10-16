import { describe, expect, it, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SlaComplianceCard } from './SlaComplianceCard';
import { slaTargetEndpoints } from '../../lib/api/endpoints/slaTargets';
import { useProfile } from '../../lib/auth/useProfile';
import { Permissions } from '../../lib/auth/permissions';

vi.mock('../../lib/api/endpoints/slaTargets', () => ({
  slaTargetKeys: {
    all: ['sla-targets'],
    detail: (p: string) => ['sla-targets', 'detail', p],
  },
  slaTargetEndpoints: {
    get: vi.fn(),
    upsert: vi.fn(),
  },
}));

vi.mock('../../lib/auth/useProfile', () => ({
  useProfile: vi.fn(),
}));

const mockedGet = vi.mocked(slaTargetEndpoints.get);
const mockedProfile = vi.mocked(useProfile);

function renderCard() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  mockedProfile.mockReturnValue({
    data: { permissions: [Permissions.ReportsRead, Permissions.DashboardRead] },
    isLoading: false,
  } as never);
  return render(
    <QueryClientProvider client={client}>
      <SlaComplianceCard projectId="p1" />
    </QueryClientProvider>,
  );
}

const compliantResponse = {
  targets: {
    projectId: 'p1', criticalDays: 1, highDays: 7, mediumDays: 14, lowDays: 30,
    updatedBy: null, updatedAt: '2026-10-16T12:00:00Z',
  },
  compliance: {
    evaluatedAt: '2026-10-16T12:00:00Z',
    eligible: 4,
    compliant: 3,
    breached: 1,
    compliancePercent: 75.0,
    bySeverity: [
      { severity: 'Critical', thresholdDays: 1, eligible: 2, breached: 1 },
      { severity: 'High', thresholdDays: 7, eligible: 2, breached: 0 },
    ],
    unconfiguredSeverityCount: 0,
  },
};

describe('SlaComplianceCard (Slice 13)', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders compliance counts and per-severity breaches', async () => {
    mockedGet.mockResolvedValue(compliantResponse);
    renderCard();
    expect(await screen.findByText('75.0% compliant')).toBeTruthy();
    expect(screen.getByText(/3 of 4 eligible/)).toBeTruthy();
    expect(screen.getByText(/1 breached of 2/)).toBeTruthy();
  });

  it('shows the empty state when targets are absent', async () => {
    mockedGet.mockResolvedValue({ targets: null, compliance: null });
    renderCard();
    expect(await screen.findByText(/no SLA targets configured/i)).toBeTruthy();
    expect(screen.queryByText(/compliant/)).toBeNull();
  });

  it('reports unavailable instead of 100% when nothing is eligible', async () => {
    mockedGet.mockResolvedValue({
      targets: compliantResponse.targets,
      compliance: {
        evaluatedAt: '2026-10-16T12:00:00Z',
        eligible: 0,
        compliant: 0,
        breached: 0,
        compliancePercent: null,
        bySeverity: [],
        unconfiguredSeverityCount: 0,
      },
    });
    renderCard();
    expect(await screen.findByText(/not 100%/i)).toBeTruthy();
  });

  it('calls out severities excluded from the denominator', async () => {
    mockedGet.mockResolvedValue({
      targets: compliantResponse.targets,
      compliance: { ...compliantResponse.compliance!, unconfiguredSeverityCount: 2 },
    });
    renderCard();
    expect(await screen.findByText(/excluded from the denominator/i)).toBeTruthy();
  });
});
