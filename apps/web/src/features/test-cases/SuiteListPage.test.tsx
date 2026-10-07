import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SuiteListPage } from './SuiteListPage';
import { suitesEndpoints } from '../../lib/api/endpoints/suites';
import { projectsEndpoints } from '../../lib/api/endpoints/projects';
import { useProfile } from '../../lib/auth/useProfile';
import { ApiError } from '../../lib/api/client';
import { Permissions } from '../../lib/auth/permissions';

vi.mock('../../lib/api/endpoints/suites', () => ({
  suiteKeys: {
    all: ['test-suites'],
    list: (projectId: string, filters: unknown, page: number) => ['test-suites', 'list', projectId, filters, page],
    details: (id: string) => ['test-suites', 'details', id],
    executions: (id: string, filters: unknown, page: number) => ['test-suites', 'executions', id, filters, page],
    report: (id: string, filters: unknown) => ['test-suites', 'report', id, filters],
  },
  suitesEndpoints: {
    list: vi.fn(),
    execute: vi.fn(),
    archive: vi.fn(),
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

const mockedList = vi.mocked(suitesEndpoints.list);
const mockedExecute = vi.mocked(suitesEndpoints.execute);
const mockedArchive = vi.mocked(suitesEndpoints.archive);
const mockedProjectGet = vi.mocked(projectsEndpoints.get);
const mockedProfile = vi.mocked(useProfile);

function renderPage() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/projects/p1/test-suites']}>
        <Routes>
          <Route path="/projects/:projectId/test-suites" element={<SuiteListPage />} />
          <Route path="/projects/:projectId/executions/:executionId" element={<div>execution details</div>} />
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
      roles: ['qa-lead'],
      permissions,
    },
    isLoading: false,
  } as never);
}

const suite = (overrides = {}) => ({
  id: 's1',
  projectId: 'p1',
  name: 'Regression',
  description: 'Main suite',
  status: 'Active',
  testCount: 2,
  updatedAt: '2026-10-01T00:00:00Z',
  ...overrides,
});

describe('SuiteListPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('confirm', vi.fn(() => true));
    mockedProjectGet.mockResolvedValue({ id: 'p1', name: 'Portal' } as never);
  });
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it('shows a loading state', () => {
    profileWith([Permissions.TestCasesRead]);
    mockedList.mockReturnValue(new Promise(() => {}));
    renderPage();
    expect(screen.getByLabelText('Loading test suites')).toBeTruthy();
  });

  it('shows an empty state', async () => {
    profileWith([Permissions.TestCasesRead]);
    mockedList.mockResolvedValue({ items: [], totalCount: 0, page: 1, pageSize: 25 } as never);
    renderPage();
    expect(await screen.findByText('No test suites found')).toBeTruthy();
  });

  it('renders suite rows', async () => {
    profileWith([Permissions.TestCasesRead, Permissions.TestCasesManage]);
    mockedList.mockResolvedValue({
      items: [suite(), suite({ id: 's2', name: 'Smoke', status: 'Archived', testCount: 0 })],
      totalCount: 2, page: 1, pageSize: 25,
    } as never);
    renderPage();
    expect(await screen.findByText('Regression')).toBeTruthy();
    expect(screen.getByText('Smoke')).toBeTruthy();
  });

  it('hides management actions from viewers', async () => {
    profileWith([Permissions.TestCasesRead]);
    mockedList.mockResolvedValue({
      items: [suite()], totalCount: 1, page: 1, pageSize: 25,
    } as never);
    renderPage();
    await screen.findByText('Regression');
    expect(screen.queryByText('New test suite')).toBeNull();
    expect(screen.queryByLabelText('Run suite Regression')).toBeNull();
    expect(screen.queryByLabelText('Archive suite Regression')).toBeNull();
  });

  it('runs a suite and navigates to the execution', async () => {
    profileWith([Permissions.TestCasesRead, Permissions.TestCasesManage]);
    mockedList.mockResolvedValue({
      items: [suite()], totalCount: 1, page: 1, pageSize: 25,
    } as never);
    mockedExecute.mockResolvedValue({
      executionId: 'e1', suiteId: 's1', testCount: 2, status: 'Queued', createdAt: '2026-10-01T00:00:00Z',
    } as never);
    renderPage();
    fireEvent.click(await screen.findByLabelText('Run suite Regression'));
    await waitFor(() => expect(mockedExecute).toHaveBeenCalledWith({ projectId: 'p1', suiteId: 's1' }));
    expect(await screen.findByText('execution details')).toBeTruthy();
  });

  it('blocks Run Now for empty suites without calling the API', async () => {
    profileWith([Permissions.TestCasesRead, Permissions.TestCasesManage]);
    mockedList.mockResolvedValue({
      items: [suite({ testCount: 0 })], totalCount: 1, page: 1, pageSize: 25,
    } as never);
    renderPage();
    fireEvent.click(await screen.findByLabelText('Run suite Regression'));
    expect(await screen.findByRole('alert')).toBeTruthy();
    expect(mockedExecute).not.toHaveBeenCalled();
  });

  it('archives a suite', async () => {
    profileWith([Permissions.TestCasesRead, Permissions.TestCasesManage]);
    mockedList.mockResolvedValue({
      items: [suite()], totalCount: 1, page: 1, pageSize: 25,
    } as never);
    mockedArchive.mockResolvedValue(undefined as never);
    renderPage();
    fireEvent.click(await screen.findByLabelText('Archive suite Regression'));
    await waitFor(() => expect(mockedArchive).toHaveBeenCalledWith('s1'));
  });

  it('shows an error state with retry', async () => {
    profileWith([Permissions.TestCasesRead]);
    mockedList.mockRejectedValue(new ApiError(500, 'INTERNAL_ERROR', 'Boom'));
    renderPage();
    expect(await screen.findByText('Something went wrong')).toBeTruthy();
    expect(screen.getByText('Try again')).toBeTruthy();
  });
});
