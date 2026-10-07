import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CiCdSettings } from './CiCdSettings';
import { ciCdEndpoints } from '../../lib/api/endpoints/cicd';
import { projectsEndpoints } from '../../lib/api/endpoints/projects';
import { suitesEndpoints } from '../../lib/api/endpoints/suites';
import { useProfile } from '../../lib/auth/useProfile';
import { Permissions } from '../../lib/auth/permissions';
import { useAppStore } from '../../stores/useAppStore';

vi.mock('../../lib/api/endpoints/cicd', () => ({
  ciCdKeys: {
    all: ['cicd'],
    list: (p: string) => ['cicd', 'list', p],
    integration: (p: string, provider: string) => ['cicd', 'integration', p, provider],
    deliveries: (p: string, provider: string, page: number) => ['cicd', 'deliveries', p, provider, page],
  },
  ciCdEndpoints: {
    list: vi.fn(),
    get: vi.fn(),
    upsert: vi.fn(),
    deliveries: vi.fn(),
    retry: vi.fn(),
  },
  ciCdErrorMessage: (status: number) => `CI/CD error ${status}`,
  PROVIDERS: ['github', 'gitlab', 'jenkins', 'azure'],
  PROVIDER_EVENTS: { github: ['push', 'pull_request'] },
}));

vi.mock('../../lib/api/endpoints/projects', () => ({
  projectsEndpoints: {
    list: vi.fn(),
    environments: vi.fn(),
  },
}));

vi.mock('../../lib/api/endpoints/suites', () => ({
  suiteKeys: {
    all: ['test-suites'],
    list: (projectId: string, filters: unknown, page: number) => ['test-suites', 'list', projectId, filters, page],
  },
  suitesEndpoints: {
    list: vi.fn(),
  },
}));

vi.mock('../../lib/auth/useProfile', () => ({
  useProfile: vi.fn(),
}));

const mockedList = vi.mocked(ciCdEndpoints.list);
const mockedDeliveries = vi.mocked(ciCdEndpoints.deliveries);
const mockedProjects = vi.mocked(projectsEndpoints.list);
const mockedEnvironments = vi.mocked(projectsEndpoints.environments);
const mockedSuites = vi.mocked(suitesEndpoints.list);
const mockedProfile = vi.mocked(useProfile);

function renderWithClient() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <CiCdSettings />
    </QueryClientProvider>,
  );
}

describe('CiCdSettings', () => {
  beforeEach(() => {
    useAppStore.setState({ currentProjectId: 'p1' });
    mockedProjects.mockResolvedValue({ items: [{ id: 'p1', name: 'Alpha' }], totalCount: 1, page: 1, pageSize: 100 } as never);
    mockedEnvironments.mockResolvedValue([{ id: 'env-1', name: 'QA' }] as never);
    mockedSuites.mockResolvedValue({
      items: [
        { id: 'suite-1', projectId: 'p1', name: 'Regression', testCount: 4, updatedAt: '2026-10-01T00:00:00Z' },
        { id: 'suite-2', projectId: 'p1', name: 'Smoke', testCount: 2, updatedAt: '2026-10-01T00:00:00Z' },
      ],
      totalCount: 2, page: 1, pageSize: 100,
    } as never);
    mockedList.mockResolvedValue([
      {
        id: 'int-1',
        projectId: 'p1',
        provider: 'github',
        enabled: true,
        configured: true,
        defaultSuiteId: null,
        defaultEnvironmentId: 'env-1',
        eventAllowlist: ['push'],
        branchAllowlist: [],
        repositoryAllowlist: [],
        variableMapping: {},
        username: null,
        secretMapping: {},
        hasSecret: true,
        webhookUrl: '/api/v1/webhooks/github/p1/int-1',
        updatedAt: '2026-10-01T00:00:00Z',
      },
    ] as never);
    mockedDeliveries.mockResolvedValue({ items: [], totalCount: 0, page: 1, pageSize: 25 } as never);
    mockedProfile.mockReturnValue({
      data: {
        permissions: [
          Permissions.ExecutionsRead,
          Permissions.SettingsManage,
        ],
      },
      isLoading: false,
    } as never);
  });

  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  it('renders provider tabs, secret metadata (never values), and webhook URL', async () => {
    renderWithClient();
    await waitFor(() => expect(screen.getByText('CI/CD integrations')).toBeTruthy());
    await waitFor(() => expect(screen.getByRole('tab', { name: /github/ })).toBeTruthy());
    for (const provider of ['gitlab', 'jenkins', 'azure']) {
      expect(screen.getByRole('tab', { name: new RegExp(provider) })).toBeTruthy();
    }
    await waitFor(() => expect(screen.getByText('Secret configured')).toBeTruthy());
    expect(screen.getByText('/api/v1/webhooks/github/p1/int-1')).toBeTruthy();
    // Secret values are never rendered.
    expect(document.body.textContent).not.toContain('gh-secret');
  });

  it('gates configuration behind settings.manage', async () => {
    mockedProfile.mockReturnValue({
      data: { permissions: [Permissions.ExecutionsRead] },
      isLoading: false,
    } as never);
    renderWithClient();
    await waitFor(() =>
      expect(
        screen.getByText('You do not have permission to change CI/CD integrations for this project.'),
      ).toBeTruthy(),
    );
  });

  it('shows delivery history with retry for failed deliveries', async () => {    mockedDeliveries.mockResolvedValue({
      items: [
        {
          id: 'del-1',
          integrationId: 'int-1',
          projectId: 'p1',
          provider: 'github',
          deliveryId: '72d3162e',
          eventType: 'push',
          verificationStatus: 'Verified',
          processingStatus: 'Failed',
          executionId: null,
          triggeredCount: 0,
          failureReason: 'invalid_suite',
          receivedAt: '2026-10-01T00:00:00Z',
          processedAt: '2026-10-01T00:01:00Z',
          createdAt: '2026-10-01T00:00:00Z',
        },
      ],
      totalCount: 1,
      page: 1,
      pageSize: 25,
    } as never);
    renderWithClient();
    await waitFor(() => expect(screen.getByText('Failed')).toBeTruthy());
    expect(screen.getByText('invalid_suite')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Retry' })).toBeTruthy();
  });

  it('offers project suites in a picker instead of a raw GUID input', async () => {
    renderWithClient();
    const picker = (await screen.findByLabelText('Default suite (required for execution)')) as HTMLSelectElement;
    expect(picker.tagName).toBe('SELECT');
    expect(screen.getByText('Regression (4 tests)')).toBeTruthy();
    expect(screen.getByText('Smoke (2 tests)')).toBeTruthy();
  });

  it('persists the picked suite through the existing backend contract', async () => {
    const mockedUpsert = vi.mocked(ciCdEndpoints.upsert);
    mockedUpsert.mockResolvedValue({} as never);
    renderWithClient();
    const picker = (await screen.findByLabelText('Default suite (required for execution)')) as HTMLSelectElement;
    fireEvent.change(picker, { target: { value: 'suite-2' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save integration' }));
    await waitFor(() => expect(mockedUpsert).toHaveBeenCalledWith('p1', expect.objectContaining({
      defaultSuiteId: 'suite-2',
    })));
  });

  it('explains an empty suite list without breaking the form', async () => {
    mockedSuites.mockResolvedValue({ items: [], totalCount: 0, page: 1, pageSize: 100 } as never);
    renderWithClient();
    expect(await screen.findByText(/This project has no test suites yet/)).toBeTruthy();
  });
});
