import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AutoTicketSettings } from './AutoTicketSettings';
import { autoTicketEndpoints } from '../../lib/api/endpoints/autoTickets';
import { projectsEndpoints } from '../../lib/api/endpoints/projects';
import { useProfile } from '../../lib/auth/useProfile';
import { ApiError } from '../../lib/api/client';
import { Permissions } from '../../lib/auth/permissions';
import { useAppStore } from '../../stores/useAppStore';

vi.mock('../../lib/api/endpoints/autoTickets', () => ({
  autoTicketKeys: {
    all: ['auto-tickets'],
    policy: (p: string) => ['auto-tickets', 'policy', p],
    status: (p: string) => ['auto-tickets', 'status', p],
  },
  autoTicketEndpoints: {
    getPolicy: vi.fn(),
    upsertPolicy: vi.fn(),
    getStatus: vi.fn(),
    retryAutomation: vi.fn(),
  },
  autoTicketErrorMessage: (status: number) => `Automation error ${status}`,
}));

vi.mock('../../lib/api/endpoints/projects', () => ({
  projectsEndpoints: {
    list: vi.fn(),
  },
}));

vi.mock('../../lib/auth/useProfile', () => ({
  useProfile: vi.fn(),
}));

const mockedGetPolicy = vi.mocked(autoTicketEndpoints.getPolicy);
const mockedUpsert = vi.mocked(autoTicketEndpoints.upsertPolicy);
const mockedGetStatus = vi.mocked(autoTicketEndpoints.getStatus);
const mockedProjects = vi.mocked(projectsEndpoints.list);
const mockedProfile = vi.mocked(useProfile);

const policy = (overrides = {}) => ({
  projectId: 'p1',
  enabled: false,
  integrationId: null,
  severities: ['Critical', 'High'],
  defectStatuses: ['Open'],
  classifications: ['ApplicationDefect'],
  minimumConfidence: null,
  updatedAt: '2026-09-29T10:00:00Z',
  ...overrides,
});

const status = (overrides = {}) => ({
  projectId: 'p1',
  enabled: false,
  configured: false,
  integrationId: null,
  severities: [],
  defectStatuses: [],
  classifications: [],
  minimumConfidence: null,
  pendingCount: 0,
  failedCount: 0,
  syncedAutomaticCount: 0,
  lastAutomationAt: null,
  ...overrides,
});

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <AutoTicketSettings />
    </QueryClientProvider>,
  );
}

function profileWith(permissions: string[]) {
  mockedProfile.mockReturnValue({
    data: {
      id: 'u1',
      externalIdentityId: 'ext',
      email: 'admin@example.com',
      displayName: 'Admin',
      roles: ['admin'],
      permissions,
    },
    isLoading: false,
  } as never);
}

describe('AutoTicketSettings (Slice 10)', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    useAppStore.setState({ currentProjectId: 'p1' });
    mockedProjects.mockResolvedValue({ items: [{ id: 'p1', name: 'Alpha', key: 'A' }], totalCount: 1 } as never);
    mockedGetPolicy.mockResolvedValue(policy());
    mockedGetStatus.mockResolvedValue(status());
  });

  afterEach(() => {
    cleanup();
    useAppStore.setState({ currentProjectId: null });
  });

  it('renders the disabled state when no policy is configured', async () => {
    profileWith([Permissions.TicketsRead, Permissions.SettingsManage]);
    renderPage();
    expect(await screen.findByText(/automation disabled/i)).toBeTruthy();
    expect(await screen.findByText(/no policy configured/i)).toBeTruthy();
  });

  it('renders the enabled state with counts', async () => {
    profileWith([Permissions.TicketsRead, Permissions.SettingsManage]);
    mockedGetPolicy.mockResolvedValue(policy({ enabled: true }));
    mockedGetStatus.mockResolvedValue(status({ enabled: true, configured: true, syncedAutomaticCount: 3 }));
    renderPage();
    expect(await screen.findByText(/automation enabled/i)).toBeTruthy();
  });

  it('saves the policy with selected eligibility', async () => {
    profileWith([Permissions.TicketsRead, Permissions.SettingsManage]);
    mockedUpsert.mockResolvedValue(policy({ enabled: true }));
    renderPage();
    fireEvent.click(await screen.findByLabelText(/enable automatic jira ticket creation/i));
    fireEvent.click(screen.getByRole('button', { name: /save policy/i }));
    await waitFor(() =>
      expect(mockedUpsert).toHaveBeenCalledWith(
        'p1',
        expect.objectContaining({
          enabled: true,
          severities: expect.arrayContaining(['Critical', 'High']),
          defectStatuses: expect.arrayContaining(['Open']),
          classifications: expect.arrayContaining(['ApplicationDefect']),
        }),
      ),
    );
    expect(await screen.findByText(/automation policy saved/i)).toBeTruthy();
  });

  it('rejects empty eligibility client-side when enabling', async () => {
    profileWith([Permissions.TicketsRead, Permissions.SettingsManage]);
    renderPage();
    fireEvent.click(await screen.findByLabelText(/enable automatic jira ticket creation/i));
    for (const label of ['Critical', 'High']) {
      fireEvent.click(screen.getByLabelText(label, { exact: true }));
    }
    fireEvent.click(screen.getByRole('button', { name: /save policy/i }));
    expect(await screen.findByText(/at least one severity/i)).toBeTruthy();
    expect(mockedUpsert).not.toHaveBeenCalled();
  });

  it('rejects out-of-range confidence client-side', async () => {
    profileWith([Permissions.TicketsRead, Permissions.SettingsManage]);
    renderPage();
    await screen.findByRole('button', { name: /save policy/i });
    fireEvent.change(screen.getByLabelText(/minimum ai confidence/i), { target: { value: '2' } });
    fireEvent.click(screen.getByRole('button', { name: /save policy/i }));
    expect(await screen.findByText(/between 0 and 1/i)).toBeTruthy();
    expect(mockedUpsert).not.toHaveBeenCalled();
  });

  it('hides the form without the configure permission', async () => {
    profileWith([Permissions.TicketsRead]);
    renderPage();
    await screen.findByText(/ticket automation/i);
    expect(screen.queryByRole('button', { name: /save policy/i })).toBeNull();
    expect(await screen.findByText(/do not have permission to change/i)).toBeTruthy();
  });

  it('denies access without the read permission', async () => {
    profileWith([]);
    mockedProfile.mockReturnValue({
      data: { id: 'u1', permissions: [] },
      isLoading: false,
    } as never);
    renderPage();
    expect(await screen.findByText(/do not have permission to view/i)).toBeTruthy();
  });

  it('surfaces save errors accessibly', async () => {
    profileWith([Permissions.TicketsRead, Permissions.SettingsManage]);
    mockedUpsert.mockRejectedValue(new ApiError(400, 'VALIDATION_ERROR', 'bad'));
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: /save policy/i }));
    await waitFor(() => expect(mockedUpsert).toHaveBeenCalled());
    expect(await screen.findByRole('alert')).toBeTruthy();
  });

  it('shows loading and error states', async () => {
    profileWith([Permissions.TicketsRead, Permissions.SettingsManage]);
    mockedGetPolicy.mockImplementation(() => new Promise(() => {}));
    const { unmount } = renderPage();
    expect(await screen.findByLabelText(/loading automation policy/i)).toBeTruthy();
    unmount();
    cleanup();
    mockedGetPolicy.mockRejectedValue(new ApiError(503, 'DEPENDENCY_UNAVAILABLE', 'down'));
    renderPage();
    expect(await screen.findByRole('heading', { name: /something went wrong/i })).toBeTruthy();
  });
});
