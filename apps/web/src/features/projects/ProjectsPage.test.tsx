import { afterEach, describe, expect, it, vi, beforeEach } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ProjectsPage } from './ProjectsPage';
import { projectsEndpoints } from '../../lib/api/endpoints/projects';
import { useProfile } from '../../lib/auth/useProfile';
import { ApiError } from '../../lib/api/client';
import { Permissions } from '../../lib/auth/permissions';

vi.mock('../../lib/api/endpoints/projects', () => ({
  projectKeys: {
    all: ['projects'],
    list: (search: string, page: number) => ['projects', 'list', search, page],
  },
  projectsEndpoints: {
    list: vi.fn(),
  },
}));

vi.mock('../../lib/auth/useProfile', () => ({
  useProfile: vi.fn(),
}));

const mockedList = vi.mocked(projectsEndpoints.list);
const mockedProfile = vi.mocked(useProfile);

function renderPage() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <ProjectsPage />
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

const item = (overrides = {}) => ({
  id: 'p1',
  name: 'Customer Portal',
  key: 'CUSTPORTAL',
  description: 'Portal under test',
  repositoryUrl: null,
  targetUrl: 'https://portal.example.com',
  framework: 'playwright',
  platform: 'web',
  status: 'Active',
  memberCount: 2,
  updatedAt: '2026-09-28T00:00:00Z',
  ...overrides,
});

describe('ProjectsPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });
  afterEach(() => {
    cleanup();
  });

  it('shows a loading state', () => {
    profileWith([Permissions.ProjectsRead]);
    mockedList.mockReturnValue(new Promise(() => {}));
    renderPage();
    expect(screen.getByLabelText('Loading projects')).toBeTruthy();
  });

  it('shows an empty state when there are no projects', async () => {
    profileWith([Permissions.ProjectsRead]);
    mockedList.mockResolvedValue({ items: [], totalCount: 0, page: 1, pageSize: 12 });
    renderPage();
    await waitFor(() => expect(screen.queryByText('No projects found')).not.toBeNull());
  });

  it('renders project cards', async () => {
    profileWith([Permissions.ProjectsRead, Permissions.ProjectsManage]);
    mockedList.mockResolvedValue({
      items: [item(), item({ id: 'p2', name: 'Shop', key: 'SHOP' })],
      totalCount: 2,
      page: 1,
      pageSize: 12,
    });
    renderPage();
    await waitFor(() => expect(screen.queryByText('Customer Portal')).not.toBeNull());
    expect(screen.queryByText('Shop')).not.toBeNull();
    expect(screen.queryByText('CUSTPORTAL')).not.toBeNull();
  });

  it('hides Add Project without the manage permission', async () => {
    profileWith([Permissions.ProjectsRead]);
    mockedList.mockResolvedValue({ items: [item()], totalCount: 1, page: 1, pageSize: 12 });
    renderPage();
    await waitFor(() => expect(screen.queryByText('Customer Portal')).not.toBeNull());
    expect(screen.queryByText('Add Project')).toBeNull();
  });

  it('shows Add Project with the manage permission', async () => {
    profileWith([Permissions.ProjectsRead, Permissions.ProjectsManage]);
    mockedList.mockResolvedValue({ items: [item()], totalCount: 1, page: 1, pageSize: 12 });
    renderPage();
    await waitFor(() => expect(screen.queryByText('Add Project')).not.toBeNull());
  });

  it('shows a no-access state on 403', async () => {
    profileWith([Permissions.ProjectsRead]);
    mockedList.mockRejectedValue(new ApiError(403, 'FORBIDDEN', 'The caller has no access to this project.'));
    renderPage();
    await waitFor(() => expect(screen.queryByText('No access')).not.toBeNull());
  });
});
