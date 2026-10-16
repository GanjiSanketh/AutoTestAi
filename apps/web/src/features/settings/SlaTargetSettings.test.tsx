import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SlaTargetSettings } from './SlaTargetSettings';
import { slaTargetEndpoints } from '../../lib/api/endpoints/slaTargets';
import { projectsEndpoints } from '../../lib/api/endpoints/projects';
import { useProfile } from '../../lib/auth/useProfile';
import { Permissions } from '../../lib/auth/permissions';
import { useAppStore } from '../../stores/useAppStore';

vi.mock('../../lib/api/endpoints/slaTargets', () => ({
  slaTargetKeys: {
    all: ['sla-targets'],
    detail: (p: string) => ['sla-targets', 'detail', p],
  },
  slaTargetEndpoints: {
    get: vi.fn(),
    upsert: vi.fn(),
  },
  slaTargetErrorMessage: (status: number) => `SLA error ${status}`,
}));

vi.mock('../../lib/api/endpoints/projects', () => ({
  projectsEndpoints: {
    list: vi.fn(),
  },
}));

vi.mock('../../lib/auth/useProfile', () => ({
  useProfile: vi.fn(),
}));

const mockedGet = vi.mocked(slaTargetEndpoints.get);
const mockedUpsert = vi.mocked(slaTargetEndpoints.upsert);
const mockedProjects = vi.mocked(projectsEndpoints.list);
const mockedProfile = vi.mocked(useProfile);

const savedResponse = (overrides = {}) => ({
  targets: {
    projectId: 'p1',
    criticalDays: 1,
    highDays: 7,
    mediumDays: 14,
    lowDays: 30,
    updatedBy: null,
    updatedAt: '2026-10-16T12:00:00Z',
  },
  compliance: null,
  ...overrides,
});

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <SlaTargetSettings />
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

describe('SlaTargetSettings (Slice 13)', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    useAppStore.setState({ currentProjectId: 'p1' });
    mockedProjects.mockResolvedValue({ items: [{ id: 'p1', name: 'Alpha', key: 'A' }] } as never);
    mockedGet.mockResolvedValue(savedResponse());
  });

  afterEach(() => {
    cleanup();
    useAppStore.setState({ currentProjectId: null });
  });

  it('loads saved thresholds into the form', async () => {
    profileWith([Permissions.ReportsRead, Permissions.SettingsManage]);
    renderPage();
    expect(await screen.findByDisplayValue('1')).toBeTruthy();
    expect(screen.getByDisplayValue('30')).toBeTruthy();
  });

  it('shows the empty state when nothing is configured', async () => {
    profileWith([Permissions.ReportsRead, Permissions.SettingsManage]);
    mockedGet.mockResolvedValue({ targets: null, compliance: null });
    renderPage();
    expect(await screen.findByText(/no targets configured/i)).toBeTruthy();
  });

  it('validates input without calling the API', async () => {
    profileWith([Permissions.ReportsRead, Permissions.SettingsManage]);
    renderPage();
    const critical = await screen.findByLabelText(/Critical \(days\)/i);
    fireEvent.change(critical, { target: { value: '0' } });
    fireEvent.click(screen.getByText('Save targets'));
    expect(await screen.findByRole('alert')).toBeTruthy();
    expect(mockedUpsert).not.toHaveBeenCalled();
  });

  it('saves valid thresholds and confirms', async () => {
    profileWith([Permissions.ReportsRead, Permissions.SettingsManage]);
    mockedUpsert.mockResolvedValue(savedResponse().targets);
    renderPage();
    await screen.findByDisplayValue('7');
    fireEvent.click(screen.getByText('Save targets'));
    await waitFor(() => {
      expect(mockedUpsert).toHaveBeenCalledWith('p1', {
        criticalDays: 1,
        highDays: 7,
        mediumDays: 14,
        lowDays: 30,
      });
    });
    expect(await screen.findByRole('status')).toBeTruthy();
  });

  it('surfaces server validation errors', async () => {
    profileWith([Permissions.ReportsRead, Permissions.SettingsManage]);
    mockedUpsert.mockRejectedValue({ status: 400, code: 'VALIDATION_ERROR' });
    renderPage();
    await screen.findByDisplayValue('7');
    fireEvent.click(screen.getByText('Save targets'));
    expect(await screen.findByRole('alert')).toBeTruthy();
  });

  it('hides the form from viewers without settings.manage', async () => {
    profileWith([Permissions.ReportsRead]);
    renderPage();
    expect(await screen.findByText(/do not have permission to change/i)).toBeTruthy();
    expect(screen.queryByText('Save targets')).toBeNull();
  });
});
