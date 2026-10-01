import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MobileSettings } from './MobileSettings';
import { mobileEndpoints } from '../../lib/api/endpoints/mobile';
import { projectsEndpoints } from '../../lib/api/endpoints/projects';
import { useProfile } from '../../lib/auth/useProfile';
import { Permissions } from '../../lib/auth/permissions';
import { useAppStore } from '../../stores/useAppStore';

vi.mock('../../lib/api/endpoints/mobile', () => ({
  mobileKeys: {
    all: ['mobile'],
    pools: (p: string) => ['mobile', 'pools', p],
    devices: (p: string, pool?: string) => ['mobile', 'devices', p, pool ?? 'all'],
    apps: (p: string) => ['mobile', 'apps', p],
  },
  mobileEndpoints: {
    listPools: vi.fn(),
    createPool: vi.fn(),
    updatePool: vi.fn(),
    setPoolEnabled: vi.fn(),
    listDevices: vi.fn(),
    registerDevice: vi.fn(),
    updateDevice: vi.fn(),
    setDeviceEnabled: vi.fn(),
    listApps: vi.fn(),
    createApp: vi.fn(),
    updateApp: vi.fn(),
  },
  mobileErrorMessage: (status: number) => `Mobile error ${status}`,
}));

vi.mock('../../lib/api/endpoints/projects', () => ({
  projectsEndpoints: {
    list: vi.fn(),
  },
}));

vi.mock('../../lib/auth/useProfile', () => ({
  useProfile: vi.fn(),
}));

const mockedPools = vi.mocked(mobileEndpoints.listPools);
const mockedDevices = vi.mocked(mobileEndpoints.listDevices);
const mockedApps = vi.mocked(mobileEndpoints.listApps);
const mockedCreatePool = vi.mocked(mobileEndpoints.createPool);
const mockedProjects = vi.mocked(projectsEndpoints.list);
const mockedProfile = vi.mocked(useProfile);

function renderWithClient() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MobileSettings />
    </QueryClientProvider>,
  );
}

describe('MobileSettings', () => {
  beforeEach(() => {
    useAppStore.setState({ currentProjectId: 'p1' });
    mockedProjects.mockResolvedValue({ items: [{ id: 'p1', name: 'Alpha' }], totalCount: 1, page: 1, pageSize: 100 } as never);
    mockedPools.mockResolvedValue([
      {
        id: 'pool-1', projectId: 'p1', name: 'android-smoke', platform: 'Android',
        enabled: true, deviceCount: 1, rowVersion: null,
        createdAt: '2026-10-01T00:00:00Z', updatedAt: '2026-10-01T00:00:00Z',
      },
    ] as never);
    mockedDevices.mockResolvedValue([
      {
        id: 'dev-1', projectId: 'p1', poolId: 'pool-1', poolName: 'android-smoke',
        platform: 'Android', platformVersion: '14', manufacturer: 'Google', model: 'Pixel 8',
        udid: 'emulator-5554', automationName: 'UiAutomator2', status: 'Available',
        enabled: true, slotCount: 1, lastSeenAt: null, rowVersion: null,
        createdAt: '2026-10-01T00:00:00Z', updatedAt: '2026-10-01T00:00:00Z',
      },
    ] as never);
    mockedApps.mockResolvedValue([] as never);
    mockedProfile.mockReturnValue({
      data: { permissions: [Permissions.ExecutionsRead, Permissions.SettingsManage] },
      isLoading: false,
    } as never);
  });

  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  it('renders pools, devices, and apps tabs with registry data and no secrets', async () => {
    renderWithClient();
    await waitFor(() => expect(screen.getByText('Mobile devices')).toBeTruthy());
    await waitFor(() => expect(screen.getByText('android-smoke')).toBeTruthy());

    fireEvent.click(screen.getByRole('tab', { name: 'Devices' }));
    await waitFor(() => expect(screen.getByText('Google Pixel 8')).toBeTruthy());
    expect(screen.getByText('emulator-5554')).toBeTruthy();
    expect(screen.getAllByText('UiAutomator2').length).toBeGreaterThan(0);

    fireEvent.click(screen.getByRole('tab', { name: 'Mobile apps' }));
    await waitFor(() => expect(screen.getByText('No mobile apps registered.')).toBeTruthy());

    const text = document.body.textContent ?? '';
    expect(text).not.toMatch(/claimToken|authorization:\s*Bearer|apiToken/i);
    expect(document.querySelector('input[type="password"]')).toBeNull();
  });

  it('gates management behind settings.manage', async () => {
    mockedProfile.mockReturnValue({
      data: { permissions: [Permissions.ExecutionsRead] },
      isLoading: false,
    } as never);
    renderWithClient();
    await waitFor(() =>
      expect(
        screen.getByText('You do not have permission to change mobile resources for this project.'),
      ).toBeTruthy(),
    );
    expect(screen.queryByRole('button', { name: 'Create pool' })).toBeNull();
  });

  it('creates a pool through the typed endpoint', async () => {
    mockedCreatePool.mockResolvedValue({ id: 'pool-2' } as never);
    renderWithClient();
    await waitFor(() => expect(screen.getByText('android-smoke')).toBeTruthy());
    fireEvent.change(screen.getByLabelText(/Pool name/), { target: { value: 'ios-smoke' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create pool' }));
    await waitFor(() =>
      expect(mockedCreatePool).toHaveBeenCalledWith('p1', { name: 'ios-smoke', platform: 'android' }),
    );
  });
});
