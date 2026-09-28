import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DefectsListPage } from './DefectsListPage';
import { defectEndpoints } from '../../lib/api/endpoints/defects';
import { projectsEndpoints } from '../../lib/api/endpoints/projects';
import { useProfile } from '../../lib/auth/useProfile';
import { ApiError } from '../../lib/api/client';
import { Permissions } from '../../lib/auth/permissions';

vi.mock('../../lib/api/endpoints/defects', () => ({
  defectKeys: {
    all: ['defects'],
    list: (projectId: string, filters: unknown, page: number) => ['defects', 'list', projectId, filters, page],
  },
  defectEndpoints: { list: vi.fn() },
}));

vi.mock('../../lib/api/endpoints/projects', () => ({
  projectKeys: { details: (id: string) => ['projects', 'details', id] },
  projectsEndpoints: { get: vi.fn() },
}));

vi.mock('../../lib/auth/useProfile', () => ({
  useProfile: vi.fn(),
}));

const mockedList = vi.mocked(defectEndpoints.list);
const mockedProjectGet = vi.mocked(projectsEndpoints.get);
const mockedProfile = vi.mocked(useProfile);

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/projects/p1/bugs']}>
        <Routes>
          <Route path="/projects/:projectId/bugs" element={<DefectsListPage />} />
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
  id: 'd1',
  projectId: 'p1',
  title: 'Login returns 500',
  severity: 'High',
  status: 'Open',
  failureClassification: 'ApplicationDefect',
  executionId: 'e1',
  testKey: 'LOGIN-001',
  createdAt: '2026-09-28T10:00:00Z',
  updatedAt: '2026-09-28T10:00:00Z',
  ...overrides,
});

describe('DefectsListPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    profileWith([Permissions.BugsRead, Permissions.BugsManage]);
    mockedProjectGet.mockResolvedValue({ id: 'p1', name: 'Portal' } as never);
  });

  afterEach(() => {
    cleanup();
  });

  it('renders defects with severity, status, and classification', async () => {
    mockedList.mockResolvedValue({ items: [item()], totalCount: 1, page: 1, pageSize: 25 });
    renderPage();

    expect(await screen.findByText('Login returns 500')).toBeTruthy();
    expect(screen.getAllByText('High').length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText('Open').length).toBeGreaterThanOrEqual(1);
    expect(screen.getByText('ApplicationDefect')).toBeTruthy();
  });

  it('shows loading and empty states', async () => {
    mockedList.mockReturnValue(new Promise(() => {}));
    const { unmount } = renderPage();
    expect(await screen.findByLabelText('Loading defects')).toBeTruthy();
    unmount();
    cleanup();

    mockedList.mockResolvedValue({ items: [], totalCount: 0, page: 1, pageSize: 25 });
    renderPage();
    expect(await screen.findByText('No defects found')).toBeTruthy();
  });

  it('maps forbidden to an access state', async () => {
    mockedList.mockRejectedValue(new ApiError(403, 'FORBIDDEN', 'Forbidden'));
    renderPage();
    expect(await screen.findByText('No access')).toBeTruthy();
  });

  it('applies filters and paginates', async () => {
    mockedList.mockResolvedValue({ items: [item()], totalCount: 30, page: 1, pageSize: 25 });
    renderPage();
    await screen.findByText('Login returns 500');

    fireEvent.change(screen.getByLabelText('Severity filter'), { target: { value: 'Critical' } });
    await waitFor(() => {
      expect(mockedList).toHaveBeenCalledWith(
        'p1',
        expect.objectContaining({ severity: 'Critical' }),
        1,
        25,
      );
    });
    await screen.findByText('Login returns 500');

    fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    await waitFor(() => {
      expect(mockedList).toHaveBeenCalledWith('p1', expect.anything(), 2, 25);
    });
  });
});
