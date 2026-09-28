import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ExecutionListPage } from './ExecutionListPage';
import { executionEndpoints } from '../../lib/api/endpoints/executions';
import { projectsEndpoints } from '../../lib/api/endpoints/projects';
import { useProfile } from '../../lib/auth/useProfile';
import { ApiError } from '../../lib/api/client';
import { Permissions } from '../../lib/auth/permissions';

vi.mock('../../lib/api/endpoints/executions', () => ({
  executionKeys: {
    all: ['executions'],
    list: (projectId: string, filters: unknown, page: number) => ['executions', 'list', projectId, filters, page],
  },
  executionEndpoints: { list: vi.fn() },
}));

vi.mock('../../lib/api/endpoints/projects', () => ({
  projectKeys: { details: (id: string) => ['projects', 'details', id] },
  projectsEndpoints: { get: vi.fn() },
}));

vi.mock('../../lib/auth/useProfile', () => ({
  useProfile: vi.fn(),
}));

const mockedList = vi.mocked(executionEndpoints.list);
const mockedProjectGet = vi.mocked(projectsEndpoints.get);
const mockedProfile = vi.mocked(useProfile);

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/projects/p1/executions']}>
        <Routes>
          <Route path="/projects/:projectId/executions" element={<ExecutionListPage />} />
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
  id: 'e1',
  projectId: 'p1',
  status: 'Passed',
  triggerType: 'Manual',
  testCaseId: 'c1',
  testKey: 'LOGIN-001',
  testTitle: 'Successful user login',
  testCaseVersionNumber: 3,
  browser: 'chromium',
  failureClassification: null,
  durationMs: 4200,
  startedAt: '2026-09-28T10:00:00Z',
  completedAt: '2026-09-28T10:00:05Z',
  createdAt: '2026-09-28T10:00:00Z',
  ...overrides,
});

describe('ExecutionListPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    profileWith([Permissions.ExecutionsRead, Permissions.ExecutionsExecute]);
    mockedProjectGet.mockResolvedValue({ id: 'p1', name: 'Portal' } as never);
  });

  afterEach(() => {
    cleanup();
  });

  it('renders execution history with status and version', async () => {
    mockedList.mockResolvedValue({ items: [item()], totalCount: 1, page: 1, pageSize: 25 });
    renderPage();

    expect(await screen.findByText('LOGIN-001')).toBeTruthy();
    expect(screen.getByText('Successful user login')).toBeTruthy();
    expect(screen.getAllByText('Passed').length).toBeGreaterThanOrEqual(1);
    expect(screen.getByText('v3')).toBeTruthy();
  });

  it('shows loading and empty states', async () => {
    mockedList.mockReturnValue(new Promise(() => {}));
    const { unmount } = renderPage();
    expect(await screen.findByLabelText('Loading executions')).toBeTruthy();
    unmount();
    cleanup();

    mockedList.mockResolvedValue({ items: [], totalCount: 0, page: 1, pageSize: 25 });
    renderPage();
    expect(await screen.findByText('No executions yet')).toBeTruthy();
  });

  it('shows an error state with retry on failure', async () => {
    mockedList.mockRejectedValue(new ApiError(503, 'PROVIDER_UNAVAILABLE', 'Temporal down'));
    renderPage();
    expect(await screen.findByText('Temporal down')).toBeTruthy();
  });

  it('maps forbidden to an access state', async () => {
    mockedList.mockRejectedValue(new ApiError(403, 'FORBIDDEN', 'Forbidden'));
    renderPage();
    expect(await screen.findByText('No access')).toBeTruthy();
  });

  it('applies the status filter and paginates', async () => {
    mockedList.mockResolvedValue({ items: [item()], totalCount: 50, page: 1, pageSize: 25 });
    renderPage();
    await screen.findByText('LOGIN-001');

    fireEvent.change(screen.getByLabelText('Status filter'), { target: { value: 'Failed' } });
    await waitFor(() => {
      expect(mockedList).toHaveBeenCalledWith(
        'p1',
        expect.objectContaining({ status: 'Failed' }),
        1,
        25,
      );
    });
    await screen.findByText('LOGIN-001');

    fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    await waitFor(() => {
      expect(mockedList).toHaveBeenCalledWith('p1', expect.anything(), 2, 25);
    });
  });

  it('notes missing execute permission in the empty state', async () => {
    profileWith([Permissions.ExecutionsRead]);
    mockedList.mockResolvedValue({ items: [], totalCount: 0, page: 1, pageSize: 25 });
    renderPage();
    expect(await screen.findByText(/executions.execute permission/)).toBeTruthy();
  });
});
