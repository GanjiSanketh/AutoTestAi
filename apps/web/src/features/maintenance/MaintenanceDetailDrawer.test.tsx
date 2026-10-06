import { render, screen, waitFor, act, fireEvent } from '@testing-library/react';
import { vi, describe, it, beforeEach, expect } from 'vitest';
import { BrowserRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MaintenanceDetailDrawer } from './MaintenanceDetailDrawer';
import { maintenanceEndpoints } from '../../lib/api/endpoints/maintenance';
import { useProfile } from '../../lib/auth/useProfile';
import { hasPermission } from '../../lib/auth/permissions';

vi.mock('../../lib/api/endpoints/maintenance');
vi.mock('../../lib/auth/useProfile');
vi.mock('../../lib/auth/permissions');

const mockUseProfile = useProfile as vi.Mock;
const mockHasPermission = hasPermission as vi.Mock;
const mockMaintenanceEndpoints = maintenanceEndpoints as vi.Mocked<typeof maintenanceEndpoints>;

const projectId = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const proposalId = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';

const createWrapper = (userPermissions: string[] = ['testcases.read', 'testcases.manage']) => {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });

  mockUseProfile.mockReturnValue({
    data: { permissions: userPermissions },
    isLoading: false,
    error: null,
    isError: false,
    isSuccess: true,
    fetchStatus: 'idle',
    status: 'success',
  });
  mockHasPermission.mockImplementation((perms: string[] | undefined, perm: string) => perms?.includes(perm) ?? false);

  return ({ children }: { children: React.ReactNode }) => (
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>{children}</BrowserRouter>
    </QueryClientProvider>
  );
};

const mockProposalDetail = {
  proposal: {
    id: proposalId,
    projectId,
    testCaseId: 'cccccccc-cccc-cccc-cccc-cccccccccccc',
    testKey: 'MNT-001',
    title: 'Login test',
    testCaseVersionId: 'dddddddd-dddd-dddd-dddd-dddddddddddd',
    testCaseVersionNumber: 1,
    stepOrder: 2,
    stepAction: 'click',
    originalStrategy: 'css',
    originalValue: '#login',
    proposedStrategy: 'testid',
    proposedValue: 'testLoginBtn',
    healingStrategy: 'TestAttribute',
    signalType: 'healed-locator',
    confidence: 70,
    occurrenceCount: 3,
    status: 'Proposed',
    reviewedBy: null,
    reviewedAt: null,
    rejectionReason: null,
    createdVersionId: null,
    createdVersionNumber: null,
    createdAt: '2026-10-05T12:00:00Z',
    updatedAt: '2026-10-05T12:00:00Z',
  },
  evidence: {
    occurrenceCount: 3,
    failedCorroborationCount: 2,
    healingSuccessRatio: 0.75,
    forecastBand: 'High',
    confidenceFactors: [
      '3 successful recoveries of the same locator',
      '2 related automation failures',
      'flakiness forecast High',
      'healing success ratio 75% on this step',
    ],
    executionIds: [
      '11111111-1111-1111-1111-111111111111',
      '22222222-2222-2222-2222-222222222222',
      '33333333-3333-3333-3333-333333333333',
    ],
    healingAttemptIds: [
      'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
      'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
      'cccccccc-cccc-cccc-cccc-cccccccccccc',
    ],
    firstSeen: '2026-10-04T12:00:00Z',
    lastSeen: '2026-10-04T14:00:00Z',
  },
};

const mockRejectedProposal = {
  ...mockProposalDetail,
  proposal: {
    ...mockProposalDetail.proposal,
    status: 'Rejected',
    rejectionReason: 'Locator still valid',
    reviewedBy: 'user-id',
    reviewedAt: '2026-10-05T12:00:00Z',
  },
};

const mockStaleProposal = {
  ...mockProposalDetail,
  proposal: {
    ...mockProposalDetail.proposal,
    status: 'Superseded',
  },
};

describe('MaintenanceDetailDrawer', () => {
  const onClose = vi.fn();
  const onRefresh = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
    mockMaintenanceEndpoints.detail.mockResolvedValue(mockProposalDetail);
    mockMaintenanceEndpoints.approve.mockResolvedValue({
      proposalId,
      status: 'Applied',
      createdVersionId: 'eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee',
      createdVersionNumber: 2,
    });
    mockMaintenanceEndpoints.reject.mockResolvedValue({ ...mockProposalDetail.proposal, status: 'Rejected', rejectionReason: 'Locator still valid' });
  });

  it('renders original → proposed locator', async () => {
    render(<MaintenanceDetailDrawer projectId={projectId} proposalId={proposalId} onClose={onClose} onRefresh={onRefresh} />, { wrapper: createWrapper() });
    await waitFor(() => expect(screen.getByText('css=#login')).toBeInTheDocument());
    expect(screen.getByText('testid=testLoginBtn')).toBeInTheDocument();
  });

  it('renders evidence/confidence information', async () => {
    render(<MaintenanceDetailDrawer projectId={projectId} proposalId={proposalId} onClose={onClose} onRefresh={onRefresh} />, { wrapper: createWrapper() });
    await waitFor(() => expect(screen.getByText('3 successful recoveries of the same locator')).toBeInTheDocument());
    await waitFor(() => expect(screen.getByText('2 related automation failures')).toBeInTheDocument());
    await waitFor(() => expect(screen.getByText('flakiness forecast High')).toBeInTheDocument());
    await waitFor(() => expect(screen.getByText('healing success ratio 75% on this step')).toBeInTheDocument());
    await waitFor(() => expect(screen.getByText('3')).toBeInTheDocument());
    await waitFor(() => expect(screen.getByText('75%')).toBeInTheDocument());
  });

  it('testcases.manage shows Approve and Reject buttons', async () => {
    render(<MaintenanceDetailDrawer projectId={projectId} proposalId={proposalId} onClose={onClose} onRefresh={onRefresh} />, { wrapper: createWrapper(['testcases.read', 'testcases.manage']) });
    await waitFor(() => {
      const buttons = screen.getAllByText('Approve & Create Pending Version');
      expect(buttons.length).toBeGreaterThan(0);
    });
    await waitFor(() => {
      const buttons = screen.getAllByText('Reject');
      expect(buttons.length).toBeGreaterThan(0);
    });
  });

  it('viewer cannot see mutation buttons', async () => {
    render(<MaintenanceDetailDrawer projectId={projectId} proposalId={proposalId} onClose={onClose} onRefresh={onRefresh} />, { wrapper: createWrapper(['testcases.read']) });
    await waitFor(() => expect(screen.queryByText('Approve & Create Pending Version')).not.toBeInTheDocument());
    await waitFor(() => expect(screen.queryByText('Reject')).not.toBeInTheDocument());
  });

  it('approve confirms and calls approve endpoint', async () => {
    render(<MaintenanceDetailDrawer projectId={projectId} proposalId={proposalId} onClose={onClose} onRefresh={onRefresh} />, { wrapper: createWrapper(['testcases.read', 'testcases.manage']) });
    await waitFor(() => {
      const buttons = screen.getAllByText('Approve & Create Pending Version');
      expect(buttons.length).toBeGreaterThan(0);
    });
    const approveButtons = screen.getAllByText('Approve & Create Pending Version');
    const approveButton = approveButtons[0];
    act(() => approveButton.click());
    await waitFor(() => expect(mockMaintenanceEndpoints.approve).toHaveBeenCalledWith(projectId, proposalId));
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    await waitFor(() => expect(onRefresh).toHaveBeenCalled());
  });

  it('reject opens modal and validates reason', async () => {
    render(<MaintenanceDetailDrawer projectId={projectId} proposalId={proposalId} onClose={onClose} onRefresh={onRefresh} />, { wrapper: createWrapper(['testcases.read', 'testcases.manage']) });
    await waitFor(() => {
      const buttons = screen.getAllByText('Reject');
      expect(buttons.length).toBeGreaterThan(0);
    });
    const rejectButtons = screen.getAllByText('Reject');
    const rejectButton = rejectButtons[0];
    act(() => rejectButton.click());
    await waitFor(() => expect(screen.getByText('Reject maintenance proposal')).toBeInTheDocument());
    const submitButton = screen.getByText('Reject proposal');
    expect(submitButton).toBeDisabled();
  });

  it('reject with valid reason calls reject endpoint', async () => {
    render(<MaintenanceDetailDrawer projectId={projectId} proposalId={proposalId} onClose={onClose} onRefresh={onRefresh} />, { wrapper: createWrapper(['testcases.read', 'testcases.manage']) });
    await waitFor(() => {
      const buttons = screen.getAllByText('Reject');
      expect(buttons.length).toBeGreaterThan(0);
    });
    const rejectButtons = screen.getAllByText('Reject');
    const rejectButton = rejectButtons[0];
    act(() => rejectButton.click());
    await waitFor(() => expect(screen.getByLabelText('Rejection reason (required, max 500 characters)')).toBeInTheDocument());
    const reasonInput = screen.getByLabelText('Rejection reason (required, max 500 characters)');
    await act(async () => {
      fireEvent.change(reasonInput, { target: { value: 'Locator still valid in staging' } });
    });
    const submitButton = screen.getByText('Reject proposal');
    await waitFor(() => expect(submitButton).not.toBeDisabled());
    await act(async () => {
      fireEvent.click(submitButton);
    });
    await waitFor(() => expect(mockMaintenanceEndpoints.reject).toHaveBeenCalledWith(projectId, proposalId, 'Locator still valid in staging'), { timeout: 3000 });
    await waitFor(() => expect(onClose).toHaveBeenCalled(), { timeout: 3000 });
    await waitFor(() => expect(onRefresh).toHaveBeenCalled(), { timeout: 3000 });
  });

  it('handles stale 409 on approve', async () => {
    mockMaintenanceEndpoints.approve.mockRejectedValue({
      code: 'CONFLICT',
      message: 'The maintenance proposal is stale because the test definition changed. No version was created. Run a new maintenance scan.',
    });
    render(<MaintenanceDetailDrawer projectId={projectId} proposalId={proposalId} onClose={onClose} onRefresh={onRefresh} />, { wrapper: createWrapper(['testcases.read', 'testcases.manage']) });
    await waitFor(() => {
      const buttons = screen.getAllByText('Approve & Create Pending Version');
      expect(buttons.length).toBeGreaterThan(0);
    });
    const approveButtons = screen.getAllByText('Approve & Create Pending Version');
    const approveButton = approveButtons[0];
    act(() => approveButton.click());
    await waitFor(() => {
      const staleTexts = screen.getAllByText(/stale/i);
      expect(staleTexts.length).toBeGreaterThan(0);
    });
  });

  it('successful approval exposes created Pending version', async () => {
    render(<MaintenanceDetailDrawer projectId={projectId} proposalId={proposalId} onClose={onClose} onRefresh={onRefresh} />, { wrapper: createWrapper(['testcases.read', 'testcases.manage']) });
    await waitFor(() => {
      const buttons = screen.getAllByText('Approve & Create Pending Version');
      expect(buttons.length).toBeGreaterThan(0);
    });
    const approveButtons = screen.getAllByText('Approve & Create Pending Version');
    const approveButton = approveButtons[0];
    act(() => approveButton.click());
    await waitFor(() => expect(mockMaintenanceEndpoints.approve).toHaveBeenCalledWith(projectId, proposalId));
    await waitFor(() => expect(onClose).toHaveBeenCalled());
    await waitFor(() => expect(onRefresh).toHaveBeenCalled());
  });
});