import { render, screen, waitFor, act } from '@testing-library/react';
import { vi, describe, it, beforeEach, expect } from 'vitest';
import { BrowserRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MaintenanceInboxPage } from './MaintenanceInboxPage';
import { maintenanceEndpoints } from '../../lib/api/endpoints/maintenance';
import { projectsEndpoints } from '../../lib/api/endpoints/projects';
import { useProfile } from '../../lib/auth/useProfile';
import { hasPermission } from '../../lib/auth/permissions';

vi.mock('../../lib/api/endpoints/maintenance');
vi.mock('../../lib/api/endpoints/projects');
vi.mock('../../lib/auth/useProfile');
vi.mock('../../lib/auth/permissions');

const mockUseProfile = useProfile as vi.Mock;
const mockHasPermission = hasPermission as vi.Mock;
const mockMaintenanceEndpoints = maintenanceEndpoints as vi.Mocked<typeof maintenanceEndpoints>;
const mockProjectsEndpoints = projectsEndpoints as vi.Mocked<typeof projectsEndpoints>;

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

const mockProposal = {
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
};

const mockPagedResult = {
  items: [mockProposal],
  totalCount: 1,
  page: 1,
  pageSize: 25,
};

const mockProject = {
  id: projectId,
  name: 'Test Project',
  key: 'TST',
};

describe('MaintenanceInboxPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockProjectsEndpoints.get.mockResolvedValue(mockProject);
    mockMaintenanceEndpoints.list.mockResolvedValue(mockPagedResult);
    mockMaintenanceEndpoints.scan.mockResolvedValue({
      candidatesDetected: 1,
      proposalsCreated: 1,
      proposalsAlreadyExisting: 0,
      proposalsSkipped: 0,
      scannedAt: '2026-10-05T12:00:00Z',
    });
  });

  it('renders status filter', async () => {
    render(<MaintenanceInboxPage />, { wrapper: createWrapper() });
    await waitFor(() => {
      const selects = screen.getAllByLabelText('Status');
      expect(selects.length).toBeGreaterThan(0);
    });
  });

  it('renders signal filter', async () => {
    render(<MaintenanceInboxPage />, { wrapper: createWrapper() });
    await waitFor(() => {
      const selects = screen.getAllByLabelText('Signal');
      expect(selects.length).toBeGreaterThan(0);
    });
  });

  it('testcases.manage permission enables scan button', async () => {
    render(<MaintenanceInboxPage />, { wrapper: createWrapper(['testcases.read', 'testcases.manage']) });
    await waitFor(() => {
      const scanButtons = screen.getAllByRole('button', { name: /scan/i });
      const enabledButton = scanButtons.find(b => !b.disabled);
      expect(enabledButton).toBeDefined();
    });
  });

  it('viewer permission disables scan button', async () => {
    render(<MaintenanceInboxPage />, { wrapper: createWrapper(['testcases.read']) });
    await waitFor(() => {
      const scanButtons = screen.getAllByRole('button', { name: /scan/i });
      const disabledButton = scanButtons.find(b => b.disabled);
      expect(disabledButton).toBeDefined();
    });
  });

  it('renders page without error', async () => {
    render(<MaintenanceInboxPage />, { wrapper: createWrapper() });
    await waitFor(() => {
      const headings = screen.getAllByText('Maintenance Proposals');
      expect(headings.length).toBeGreaterThan(0);
    });
  });

  it('renders status filter options', async () => {
    render(<MaintenanceInboxPage />, { wrapper: createWrapper() });
    await waitFor(() => {
      const selects = screen.getAllByLabelText('Status');
      expect(selects.length).toBeGreaterThan(0);
    });
    const statusSelect = screen.getAllByLabelText('Status')[0];
    expect(statusSelect.options.length).toBeGreaterThan(1);
  });

  it('renders signal filter options', async () => {
    render(<MaintenanceInboxPage />, { wrapper: createWrapper() });
    await waitFor(() => {
      const selects = screen.getAllByLabelText('Signal');
      expect(selects.length).toBeGreaterThan(0);
    });
    const signalSelect = screen.getAllByLabelText('Signal')[0];
    expect(signalSelect.options.length).toBeGreaterThan(1);
  });

  it('testcases.manage permission enables scan button', async () => {
    render(<MaintenanceInboxPage />, { wrapper: createWrapper(['testcases.read', 'testcases.manage']) });
    await waitFor(() => {
      const scanButtons = screen.getAllByRole('button', { name: /scan/i });
      const enabledButton = scanButtons.find(b => !b.disabled);
      expect(enabledButton).toBeDefined();
    });
  });

  it('viewer permission disables scan button', async () => {
    render(<MaintenanceInboxPage />, { wrapper: createWrapper(['testcases.read']) });
    await waitFor(() => {
      const scanButtons = screen.getAllByRole('button', { name: /scan/i });
      const disabledButton = scanButtons.find(b => b.disabled);
      expect(disabledButton).toBeDefined();
    });
  });
});