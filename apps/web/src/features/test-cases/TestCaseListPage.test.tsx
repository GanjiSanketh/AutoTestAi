import { afterEach, describe, expect, it, vi, beforeEach } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { TestCaseListPage } from './TestCaseListPage';
import { testcasesEndpoints } from '../../lib/api/endpoints/testcases';
import { projectsEndpoints } from '../../lib/api/endpoints/projects';
import { useProfile } from '../../lib/auth/useProfile';
import { ApiError } from '../../lib/api/client';
import { Permissions } from '../../lib/auth/permissions';

vi.mock('../../lib/api/endpoints/testcases', () => ({
  testcaseKeys: {
    all: ['test-cases'],
    list: (projectId: string, filters: unknown, page: number) => ['test-cases', 'list', projectId, filters, page],
  },
  testcasesEndpoints: {
    list: vi.fn(),
  },
}));

vi.mock('../../lib/api/endpoints/projects', () => ({
  projectKeys: {
    details: (id: string) => ['projects', 'details', id],
  },
  projectsEndpoints: {
    get: vi.fn(),
  },
}));

vi.mock('../../lib/auth/useProfile', () => ({
  useProfile: vi.fn(),
}));

const mockedList = vi.mocked(testcasesEndpoints.list);
const mockedProjectGet = vi.mocked(projectsEndpoints.get);
const mockedProfile = vi.mocked(useProfile);

function renderPage() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/projects/p1/test-cases']}>
        <Routes>
          <Route path="/projects/:projectId/test-cases" element={<TestCaseListPage />} />
        </Routes>
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
  id: 'c1',
  projectId: 'p1',
  testKey: 'LOGIN-001',
  title: 'Successful user login',
  module: 'Authentication',
  framework: 'playwright',
  platform: 'web',
  priority: 'High',
  status: 'Active',
  latestVersionNumber: 2,
  latestReviewStatus: 'Pending',
  updatedAt: '2026-09-28T00:00:00Z',
  ...overrides,
});

describe('TestCaseListPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockedProjectGet.mockResolvedValue({ id: 'p1', name: 'Portal' } as never);
  });
  afterEach(() => {
    cleanup();
  });

  it('shows a loading state', () => {
    profileWith([Permissions.TestCasesRead]);
    mockedList.mockReturnValue(new Promise(() => {}));
    renderPage();
    expect(screen.getByLabelText('Loading test cases')).toBeTruthy();
  });

  it('shows an empty state', async () => {
    profileWith([Permissions.TestCasesRead]);
    mockedList.mockResolvedValue({ items: [], totalCount: 0, page: 1, pageSize: 25 });
    renderPage();
    await waitFor(() => expect(screen.queryByText('No test cases found')).not.toBeNull());
  });

  it('renders the repository table with design columns', async () => {
    profileWith([Permissions.TestCasesRead, Permissions.TestCasesManage]);
    mockedList.mockResolvedValue({ items: [item()], totalCount: 1, page: 1, pageSize: 25 });
    renderPage();
    await waitFor(() => expect(screen.queryByText('LOGIN-001')).not.toBeNull());
    expect(screen.queryByText('Successful user login')).not.toBeNull();
    expect(screen.queryByText('Authentication')).not.toBeNull();
    expect(screen.queryByRole('columnheader', { name: 'Test ID' })).not.toBeNull();
    expect(screen.queryByText('View')).not.toBeNull();
  });

  it('keeps the AI generator as a disabled placeholder', async () => {
    profileWith([Permissions.TestCasesRead]);
    mockedList.mockResolvedValue({ items: [item()], totalCount: 1, page: 1, pageSize: 25 });
    renderPage();
    await waitFor(() => expect(screen.queryByText('AI Case Generator')).not.toBeNull());
    expect((screen.getByRole('button', { name: /AI Case Generator/ }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('gates creation on the manage permission', async () => {
    profileWith([Permissions.TestCasesRead]);
    mockedList.mockResolvedValue({ items: [item()], totalCount: 1, page: 1, pageSize: 25 });
    renderPage();
    await waitFor(() => expect(screen.queryByText('LOGIN-001')).not.toBeNull());
    expect(screen.queryByText('New test case')).toBeNull();
  });

  it('applies search and filters to the query', async () => {
    profileWith([Permissions.TestCasesRead]);
    mockedList.mockResolvedValue({ items: [], totalCount: 0, page: 1, pageSize: 25 });
    renderPage();
    fireEvent.change(screen.getByLabelText('Search test cases'), { target: { value: 'login' } });
    fireEvent.change(screen.getByLabelText('Status'), { target: { value: 'Active' } });
    fireEvent.click(screen.getByRole('button', { name: 'Search' }));
    await waitFor(() => {
      const calls = mockedList.mock.calls as unknown as [string, Record<string, string>][];
      const matched = calls.some(
        ([, filters]) => filters.search === 'login' && filters.status === 'Active',
      );
      expect(matched).toBe(true);
    });
  });

  it('shows a no-access state on 403', async () => {
    profileWith([Permissions.TestCasesRead]);
    mockedList.mockRejectedValue(new ApiError(403, 'FORBIDDEN', 'The caller has no access to this project.'));
    renderPage();
    await waitFor(() => expect(screen.queryByText('No access')).not.toBeNull());
  });
});
