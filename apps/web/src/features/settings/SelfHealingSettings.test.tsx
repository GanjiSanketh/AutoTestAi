import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SelfHealingSettings } from './SelfHealingSettings';
import { selfHealingEndpoints } from '../../lib/api/endpoints/selfHealing';
import { projectsEndpoints } from '../../lib/api/endpoints/projects';
import { useProfile } from '../../lib/auth/useProfile';
import { ApiError } from '../../lib/api/client';
import { Permissions } from '../../lib/auth/permissions';
import { useAppStore } from '../../stores/useAppStore';

vi.mock('../../lib/api/endpoints/selfHealing', () => ({
  selfHealingKeys: {
    all: ['self-healing'],
    policy: (p: string) => ['self-healing', 'policy', p],
    status: (p: string) => ['self-healing', 'status', p],
    attempts: (p: string, e: string) => ['self-healing', 'attempts', p, e],
  },
  selfHealingEndpoints: {
    getPolicy: vi.fn(),
    upsertPolicy: vi.fn(),
    getStatus: vi.fn(),
    listAttempts: vi.fn(),
  },
  selfHealingErrorMessage: (status: number) => `Healing error ${status}`,
}));

vi.mock('../../lib/api/endpoints/projects', () => ({
  projectsEndpoints: {
    list: vi.fn(),
  },
}));

vi.mock('../../lib/auth/useProfile', () => ({
  useProfile: vi.fn(),
}));

const mockedGetPolicy = vi.mocked(selfHealingEndpoints.getPolicy);
const mockedUpsert = vi.mocked(selfHealingEndpoints.upsertPolicy);
const mockedGetStatus = vi.mocked(selfHealingEndpoints.getStatus);
const mockedProjects = vi.mocked(projectsEndpoints.list);
const mockedProfile = vi.mocked(useProfile);

const policy = (overrides = {}) => ({
  projectId: 'p1',
  enabled: false,
  aiFallbackEnabled: false,
  maxAttemptsPerStep: 1,
  minDeterministicScore: null,
  minAiConfidence: null,
  allowedStrategies: ['css', 'xpath', 'role', 'text', 'testid'],
  updatedAt: '2026-09-30T10:00:00Z',
  ...overrides,
});

const status = (overrides = {}) => ({
  projectId: 'p1',
  enabled: false,
  configured: false,
  aiFallbackEnabled: false,
  attemptCount: 0,
  appliedCount: 0,
  lastHealedAt: null,
  ...overrides,
});

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <SelfHealingSettings />
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

describe('SelfHealingSettings (Slice 11)', () => {
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
    profileWith([Permissions.ExecutionsRead, Permissions.SettingsManage]);
    renderPage();
    expect(await screen.findByText(/self-healing disabled/i)).toBeTruthy();
    expect(await screen.findByText(/no policy configured/i)).toBeTruthy();
  });

  it('renders the enabled state with recovery counts', async () => {
    profileWith([Permissions.ExecutionsRead, Permissions.SettingsManage]);
    mockedGetPolicy.mockResolvedValue(policy({ enabled: true, aiFallbackEnabled: true }));
    mockedGetStatus.mockResolvedValue(status({ enabled: true, configured: true, appliedCount: 2 }));
    renderPage();
    expect(await screen.findByText(/self-healing enabled/i)).toBeTruthy();
  });

  it('saves the policy and communicates that tests are never mutated', async () => {
    profileWith([Permissions.ExecutionsRead, Permissions.SettingsManage]);
    mockedUpsert.mockResolvedValue(policy({ enabled: true }));
    renderPage();
    fireEvent.click(await screen.findByLabelText(/enable self-healing recovery/i));
    fireEvent.click(await screen.findByLabelText(/enable ai fallback/i));
    fireEvent.click(screen.getByRole('button', { name: /save policy/i }));
    await waitFor(() =>
      expect(mockedUpsert).toHaveBeenCalledWith(
        'p1',
        expect.objectContaining({ enabled: true, aiFallbackEnabled: true }),
      ),
    );
    expect(await screen.findByText(/self-healing policy saved/i)).toBeTruthy();
    expect(await screen.findByText(/never mutate the stored test/i)).toBeTruthy();
  });

  it('rejects out-of-range thresholds client-side', async () => {
    profileWith([Permissions.ExecutionsRead, Permissions.SettingsManage]);
    renderPage();
    await screen.findByRole('button', { name: /save policy/i });
    fireEvent.change(screen.getByLabelText(/minimum ai confidence/i), { target: { value: '2' } });
    fireEvent.click(screen.getByRole('button', { name: /save policy/i }));
    expect(await screen.findByText(/between 0 and 1/i)).toBeTruthy();
    expect(mockedUpsert).not.toHaveBeenCalled();
  });

  it('hides the form without the configure permission', async () => {
    profileWith([Permissions.ExecutionsRead]);
    renderPage();
    await screen.findByText(/self-healing test engine/i);
    expect(screen.queryByRole('button', { name: /save policy/i })).toBeNull();
    expect(await screen.findByText(/do not have permission to change/i)).toBeTruthy();
  });

  it('denies access without the read permission', async () => {
    mockedProfile.mockReturnValue({
      data: { id: 'u1', permissions: [] },
      isLoading: false,
    } as never);
    renderPage();
    expect(await screen.findByText(/do not have permission to view/i)).toBeTruthy();
  });

  it('surfaces save errors accessibly', async () => {
    profileWith([Permissions.ExecutionsRead, Permissions.SettingsManage]);
    mockedUpsert.mockRejectedValue(new ApiError(400, 'VALIDATION_ERROR', 'bad'));
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: /save policy/i }));
    await waitFor(() => expect(mockedUpsert).toHaveBeenCalled());
    expect(await screen.findByRole('alert')).toBeTruthy();
  });
});
