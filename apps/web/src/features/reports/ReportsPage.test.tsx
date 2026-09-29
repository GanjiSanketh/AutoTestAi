import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ReportsPage } from './ReportsPage';
import { reportEndpoints } from '../../lib/api/endpoints/reports';
import { projectsEndpoints } from '../../lib/api/endpoints/projects';
import { useProfile } from '../../lib/auth/useProfile';
import { ApiError } from '../../lib/api/client';
import { Permissions } from '../../lib/auth/permissions';
import { useAppStore } from '../../stores/useAppStore';

vi.mock('../../lib/api/endpoints/reports', () => ({
  reportKeys: {
    all: ['reports'],
    executions: (p: string, f: unknown, page: number) => ['reports', 'executions', p, f, page],
    defects: (p: string, f: unknown, page: number) => ['reports', 'defects', p, f, page],
    tickets: (p: string, f: unknown, page: number) => ['reports', 'tickets', p, f, page],
  },
  reportEndpoints: {
    executions: vi.fn(),
    defects: vi.fn(),
    tickets: vi.fn(),
  },
}));

vi.mock('../../lib/api/endpoints/projects', () => ({
  projectKeys: { all: ['projects'] },
  projectsEndpoints: { list: vi.fn() },
}));

vi.mock('../../lib/auth/useProfile', () => ({
  useProfile: vi.fn(),
}));

const mockedExecutions = vi.mocked(reportEndpoints.executions);
const mockedDefects = vi.mocked(reportEndpoints.defects);
const mockedTickets = vi.mocked(reportEndpoints.tickets);
const mockedProjectList = vi.mocked(projectsEndpoints.list);
const mockedProfile = vi.mocked(useProfile);

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <ReportsPage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

function profileWith(permissions: string[]) {
  mockedProfile.mockReturnValue({
    data: {
      id: 'u1',
      externalIdentityId: 'ext',
      email: 'qa@example.com',
      displayName: 'QA',
      roles: ['tester'],
      permissions,
    },
    isLoading: false,
  } as never);
}

describe('ReportsPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    useAppStore.setState({ currentProjectId: 'p1' });
    profileWith([Permissions.ReportsRead, Permissions.ProjectsRead]);
    mockedProjectList.mockResolvedValue({
      items: [{ id: 'p1', name: 'Alpha', key: 'A', description: null, repositoryUrl: null, targetUrl: null, framework: null, platform: null, status: 'Active', memberCount: 1, updatedAt: '2026-09-29T00:00:00Z' }],
      totalCount: 1, page: 1, pageSize: 100,
    });
    mockedExecutions.mockResolvedValue({
      items: [
        {
          id: 'e1', status: 'Failed', testCaseId: 'c1', testKey: 'LOGIN-001',
          testTitle: 'Login', testCaseVersionNumber: 3, failureClassification: 'ApplicationDefect',
          durationMs: 1200, startedAt: '2026-09-28T10:00:00Z',
          completedAt: '2026-09-28T10:01:00Z', createdAt: '2026-09-28T10:00:00Z',
        },
      ],
      totalCount: 1, page: 1, pageSize: 25,
    });
    mockedDefects.mockResolvedValue({ items: [], totalCount: 0, page: 1, pageSize: 25 });
    mockedTickets.mockResolvedValue({ items: [], totalCount: 0, page: 1, pageSize: 25 });
  });

  afterEach(() => {
    cleanup();
    useAppStore.setState({ currentProjectId: null });
  });

  it('renders the execution table with deep links', async () => {
    renderPage();
    expect(await screen.findByText('LOGIN-001')).toBeTruthy();
    const link = screen.getByText('e1').closest('a');
    expect(link?.getAttribute('href')).toBe('/projects/p1/executions/e1');
  });

  it('applies status filters to the execution query', async () => {
    renderPage();
    await screen.findByText('LOGIN-001');
    fireEvent.change(screen.getByLabelText('Status'), { target: { value: 'Failed' } });
    await waitFor(() =>
      expect(mockedExecutions).toHaveBeenCalledWith(
        'p1', expect.objectContaining({ status: 'Failed' }), 1, 25,
      ),
    );
  });

  it('switches tabs and loads the defect report', async () => {
    mockedDefects.mockResolvedValue({
      items: [
        {
          id: 'd1', title: 'Login returns 500', severity: 'High', status: 'Open',
          failureClassification: 'ApplicationDefect', jiraKey: 'ABC-123',
          createdAt: '2026-09-28T11:00:00Z',
        },
      ],
      totalCount: 1, page: 1, pageSize: 25,
    });
    renderPage();
    await screen.findByText('LOGIN-001');
    fireEvent.click(screen.getByRole('tab', { name: 'Defects' }));
    expect(await screen.findByText('Login returns 500')).toBeTruthy();
    expect(screen.getByText('ABC-123')).toBeTruthy();
  });

  it('shows an empty state when nothing matches', async () => {
    mockedExecutions.mockResolvedValue({ items: [], totalCount: 0, page: 1, pageSize: 25 });
    renderPage();
    expect(await screen.findByText('No executions match these filters.')).toBeTruthy();
  });

  it('shows an error state when the report fails', async () => {
    mockedExecutions.mockRejectedValue(new ApiError(503, 'DEPENDENCY_UNAVAILABLE', 'Down'));
    renderPage();
    expect(await screen.findByText('Something went wrong')).toBeTruthy();
  });

  it('hides reports without the reports permission', async () => {
    profileWith([Permissions.DashboardRead]);
    renderPage();
    expect(await screen.findByText('Reports')).toBeTruthy();
    expect(mockedExecutions).not.toHaveBeenCalled();
  });
});
