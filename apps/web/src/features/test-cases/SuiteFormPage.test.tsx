import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SuiteFormPage } from './SuiteFormPage';
import { suitesEndpoints } from '../../lib/api/endpoints/suites';
import { testcasesEndpoints } from '../../lib/api/endpoints/testcases';
import { useProfile } from '../../lib/auth/useProfile';
import { ApiError } from '../../lib/api/client';
import { Permissions } from '../../lib/auth/permissions';

vi.mock('../../lib/api/endpoints/suites', () => ({
  suiteKeys: {
    all: ['test-suites'],
    details: (id: string) => ['test-suites', 'details', id],
  },
  suitesEndpoints: {
    get: vi.fn(),
    create: vi.fn(),
    update: vi.fn(),
    addTestCase: vi.fn(),
    removeTestCase: vi.fn(),
    reorder: vi.fn(),
  },
}));

vi.mock('../../lib/api/endpoints/testcases', () => ({
  testcaseKeys: {
    all: ['test-cases'],
    list: (projectId: string, filters: unknown, page: number) => ['test-cases', 'list', projectId, filters, page],
  },
  testcasesEndpoints: {
    list: vi.fn(),
  },
}));

vi.mock('../../lib/auth/useProfile', () => ({
  useProfile: vi.fn(),
}));

const mockedGet = vi.mocked(suitesEndpoints.get);
const mockedCreate = vi.mocked(suitesEndpoints.create);
const mockedUpdate = vi.mocked(suitesEndpoints.update);
const mockedAdd = vi.mocked(suitesEndpoints.addTestCase);
const mockedRemove = vi.mocked(suitesEndpoints.removeTestCase);
const mockedReorder = vi.mocked(suitesEndpoints.reorder);
const mockedCases = vi.mocked(testcasesEndpoints.list);
const mockedProfile = vi.mocked(useProfile);

function renderCreate() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/projects/p1/test-suites/new']}>
        <Routes>
          <Route path="/projects/:projectId/test-suites/new" element={<SuiteFormPage />} />
          <Route path="/projects/:projectId/test-suites" element={<div>suite list</div>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

function renderEdit() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/projects/p1/test-suites/s1/edit']}>
        <Routes>
          <Route path="/projects/:projectId/test-suites/:suiteId/edit" element={<SuiteFormPage />} />
          <Route path="/projects/:projectId/test-suites/:suiteId" element={<div>suite details</div>} />
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

const available = [
  { id: 'c1', testKey: 'LOGIN-001', title: 'Login works' },
  { id: 'c2', testKey: 'SMOKE-001', title: 'Smoke works' },
];

const existing = {
  id: 's1',
  projectId: 'p1',
  name: 'Regression',
  description: 'Main suite',
  status: 'Active',
  createdAt: '2026-09-01T00:00:00Z',
  updatedAt: '2026-10-01T00:00:00Z',
  members: [
    { testCaseId: 'c1', testKey: 'LOGIN-001', title: 'Login works', executionOrder: 1, jiraIssueKey: null, freshnessState: null },
  ],
};

describe('SuiteFormPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockedCases.mockResolvedValue({ items: available, totalCount: 2, page: 1, pageSize: 100 } as never);
  });
  afterEach(() => {
    cleanup();
  });

  it('validates the suite name on create', async () => {
    profileWith([Permissions.TestCasesManage]);
    renderCreate();
    await screen.findByPlaceholderText('Search by key or title…');
    fireEvent.click(screen.getByText('Create suite'));
    expect(await screen.findByText('Suite name is required.')).toBeTruthy();
    expect(mockedCreate).not.toHaveBeenCalled();
  });

  it('creates a suite with selected members and navigates to the list', async () => {
    profileWith([Permissions.TestCasesManage]);
    mockedCreate.mockResolvedValue({ id: 's9', projectId: 'p1', name: 'Smoke', testKey: 'Smoke' } as never);
    renderCreate();

    fireEvent.change(screen.getByLabelText('Name *'), { target: { value: 'Smoke' } });
    fireEvent.click(await screen.findByText('LOGIN-001'));
    fireEvent.click(screen.getByText('Create suite'));

    await waitFor(() => expect(mockedCreate).toHaveBeenCalledWith('p1', expect.objectContaining({
      name: 'Smoke',
      members: [{ testCaseId: 'c1', executionOrder: 1 }],
    })));
    expect(await screen.findByText('suite list')).toBeTruthy();
  });

  it('loads the suite in edit mode and saves metadata', async () => {
    profileWith([Permissions.TestCasesManage]);
    mockedGet.mockResolvedValue(existing as never);
    mockedUpdate.mockResolvedValue(existing as never);
    renderEdit();

    const name = (await screen.findByDisplayValue('Regression')) as HTMLInputElement;
    expect(name).toBeTruthy();
    expect(screen.getByText('LOGIN-001')).toBeTruthy();

    fireEvent.change(name, { target: { value: 'Regression v2' } });
    fireEvent.click(screen.getByText('Save changes'));

    await waitFor(() => expect(mockedUpdate).toHaveBeenCalledWith('s1', expect.objectContaining({
      name: 'Regression v2',
    })));
    expect(await screen.findByText('suite details')).toBeTruthy();
  });

  it('removes a member in edit mode', async () => {
    profileWith([Permissions.TestCasesManage]);
    mockedGet.mockResolvedValue(existing as never);
    mockedRemove.mockResolvedValue(undefined as never);
    renderEdit();

    fireEvent.click(await screen.findByLabelText('Remove LOGIN-001'));
    await waitFor(() => expect(mockedRemove).toHaveBeenCalledWith('s1', 'c1'));
  });

  it('adds a member in edit mode through the API', async () => {
    profileWith([Permissions.TestCasesManage]);
    mockedGet.mockResolvedValue(existing as never);
    mockedAdd.mockResolvedValue({ suiteId: 's1', testCaseId: 'c2', executionOrder: 2 } as never);
    renderEdit();

    await screen.findByText('LOGIN-001');
    fireEvent.click(await screen.findByText('SMOKE-001'));
    await waitFor(() => expect(mockedAdd).toHaveBeenCalledWith('s1', { testCaseId: 'c2', executionOrder: 2 }));
  });

  it('saves the member order in edit mode', async () => {
    profileWith([Permissions.TestCasesManage]);
    mockedGet.mockResolvedValue({
      ...existing,
      members: [
        ...existing.members,
        { testCaseId: 'c2', testKey: 'SMOKE-001', title: 'Smoke works', executionOrder: 2, jiraIssueKey: null, freshnessState: null },
      ],
    } as never);
    mockedReorder.mockResolvedValue(undefined as never);
    renderEdit();

    await screen.findByText('SMOKE-001');
    fireEvent.click(await screen.findByText('Save order'));
    await waitFor(() => expect(mockedReorder).toHaveBeenCalledWith('s1', expect.objectContaining({
      members: expect.any(Array),
    })));
  });

  it('shows an error state when the suite fails to load', async () => {
    profileWith([Permissions.TestCasesManage]);
    mockedGet.mockRejectedValue(new ApiError(500, 'INTERNAL_ERROR', 'Boom'));
    renderEdit();
    expect(await screen.findByText('Something went wrong')).toBeTruthy();
  });
});
