import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { VisualBaselines } from './VisualBaselines';
import { visualBaselineEndpoints } from '../../lib/api/endpoints/visualBaselines';
import { projectsEndpoints } from '../../lib/api/endpoints/projects';
import { useProfile } from '../../lib/auth/useProfile';
import { Permissions } from '../../lib/auth/permissions';
import { useAppStore } from '../../stores/useAppStore';

vi.mock('../../lib/api/endpoints/visualBaselines', () => ({
  visualBaselineKeys: {
    all: ['visual-baselines'],
    list: (p: string) => ['visual-baselines', 'list', p],
  },
  visualBaselineEndpoints: {
    list: vi.fn(),
    propose: vi.fn(),
    approve: vi.fn(),
    reject: vi.fn(),
    download: vi.fn(),
  },
  visualBaselineErrorMessage: (status: number) => `Visual error ${status}`,
}));

vi.mock('../../lib/api/endpoints/projects', () => ({
  projectsEndpoints: {
    list: vi.fn(),
  },
}));

vi.mock('../../lib/auth/useProfile', () => ({
  useProfile: vi.fn(),
}));

const mockedList = vi.mocked(visualBaselineEndpoints.list);
const mockedApprove = vi.mocked(visualBaselineEndpoints.approve);
const mockedReject = vi.mocked(visualBaselineEndpoints.reject);
const mockedDownload = vi.mocked(visualBaselineEndpoints.download);
const mockedProjects = vi.mocked(projectsEndpoints.list);
const mockedProfile = vi.mocked(useProfile);

function renderWithClient() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <VisualBaselines />
    </QueryClientProvider>,
  );
}

const candidate = {
  id: 'base-1',
  projectId: 'p1',
  testCaseId: 'tc-1',
  testCaseVersionId: 'v-1',
  stepOrder: 2,
  status: 'Candidate',
  storageKey: 'projects/p1/visual-baselines/v-1/step-002-abc.png',
  sha256: 'abc',
  width: 100,
  height: 200,
  contentType: 'image/png',
  mismatchThresholdBps: null,
  createdBy: null,
  approvedBy: null,
  createdAt: '2026-10-04T00:00:00Z',
  approvedAt: null,
  updatedAt: '2026-10-04T00:00:00Z',
};

describe('VisualBaselines', () => {
  beforeEach(() => {
    useAppStore.setState({ currentProjectId: 'p1' });
    mockedProjects.mockResolvedValue({ items: [{ id: 'p1', name: 'Alpha' }], totalCount: 1, page: 1, pageSize: 100 } as never);
    mockedList.mockResolvedValue([candidate] as never);
    mockedApprove.mockResolvedValue({ ...candidate, status: 'Active' } as never);
    mockedReject.mockResolvedValue(undefined as never);
    mockedDownload.mockResolvedValue({
      downloadUrl: 'https://artifacts.example/review.png?exp=900',
      expiresInSeconds: 900,
    } as never);
    mockedProfile.mockReturnValue({
      data: { permissions: [Permissions.ExecutionsRead, Permissions.SettingsManage] },
      isLoading: false,
    } as never);
  });

  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  it('lists baselines with approve/reject and no secrets', async () => {
    renderWithClient();
    await waitFor(() => expect(screen.getByText('Visual baselines')).toBeTruthy());
    await waitFor(() => expect(screen.getByText(/Step 2/)).toBeTruthy());
    expect(screen.getByRole('button', { name: 'Approve' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Reject' })).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Review' }));
    await waitFor(() => expect(screen.getByAltText('Visual baseline reference')).toBeTruthy());

    const text = document.body.textContent ?? '';
    expect(text).not.toMatch(/claimToken|assignmentToken|authorization:\s*Bearer|apiToken/i);
    expect(document.querySelector('input[type="password"]')).toBeNull();
  });

  it('approves a candidate and shows confirmation', async () => {
    renderWithClient();
    await waitFor(() => expect(screen.getByRole('button', { name: 'Approve' })).toBeTruthy());
    fireEvent.click(screen.getByRole('button', { name: 'Approve' }));
    await waitFor(() => expect(mockedApprove).toHaveBeenCalledWith('p1', 'base-1'));
    await waitFor(() => expect(screen.getByText('Baseline approved.')).toBeTruthy());
  });

  it('gates management behind settings.manage', async () => {
    mockedProfile.mockReturnValue({
      data: { permissions: [Permissions.ExecutionsRead] },
      isLoading: false,
    } as never);
    renderWithClient();
    await waitFor(() => expect(screen.getByText(/Step 2/)).toBeTruthy());
    expect(screen.queryByRole('button', { name: 'Approve' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Reject' })).toBeNull();
  });

  it('hides the section without read permission', async () => {
    mockedProfile.mockReturnValue({
      data: { permissions: [] },
      isLoading: false,
    } as never);
    renderWithClient();
    await waitFor(() =>
      expect(screen.getByText('You do not have permission to view visual baselines for this project.')).toBeTruthy(),
    );
  });
});
