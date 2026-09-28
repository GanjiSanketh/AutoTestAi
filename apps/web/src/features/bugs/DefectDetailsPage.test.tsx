import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DefectDetailsPage } from './DefectDetailsPage';
import { defectEndpoints } from '../../lib/api/endpoints/defects';
import { useProfile } from '../../lib/auth/useProfile';
import { ApiError } from '../../lib/api/client';
import { Permissions } from '../../lib/auth/permissions';

vi.mock('../../lib/api/endpoints/defects', () => ({
  defectKeys: {
    all: ['defects'],
    details: (p: string, id: string) => ['defects', 'details', p, id],
  },
  defectEndpoints: {
    get: vi.fn(),
    update: vi.fn(),
    changeStatus: vi.fn(),
  },
}));

vi.mock('../../lib/auth/useProfile', () => ({
  useProfile: vi.fn(),
}));

const mockedGet = vi.mocked(defectEndpoints.get);
const mockedUpdate = vi.mocked(defectEndpoints.update);
const mockedChangeStatus = vi.mocked(defectEndpoints.changeStatus);
const mockedProfile = vi.mocked(useProfile);

const detail = (overrides = {}) => ({
  id: 'd1',
  projectId: 'p1',
  title: 'Login returns 500',
  description: 'Staging login fails.',
  severity: 'High',
  status: 'Open',
  failureClassification: 'ApplicationDefect',
  executionId: 'e1',
  executionTestId: 't1',
  testCaseId: 'c1',
  testKey: 'LOGIN-001',
  testTitle: 'Successful user login',
  testCaseVersionId: 'v3',
  testCaseVersionNumber: 3,
  failureAnalysisId: 'a1',
  analysis: {
    id: 'a1',
    attempt: 1,
    status: 'Completed',
    classification: 'ApplicationDefect',
    summary: 'Backend rejects valid credentials.',
    confidence: 0.8,
    provider: 'stub',
    model: 'stub-1.0',
  },
  aiConfidence: 0.8,
  createdBy: null,
  createdAt: '2026-09-28T10:00:00Z',
  updatedAt: '2026-09-28T10:00:00Z',
  ...overrides,
});

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/projects/p1/bugs/d1']}>
        <Routes>
          <Route path="/projects/:projectId/bugs/:defectId" element={<DefectDetailsPage />} />
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

describe('DefectDetailsPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    profileWith([Permissions.BugsRead, Permissions.BugsManage]);
    mockedGet.mockResolvedValue(detail());
  });

  afterEach(() => {
    cleanup();
  });

  it('renders the defect with traceability and advisory analysis', async () => {
    renderPage();

    expect(await screen.findByText('Login returns 500')).toBeTruthy();
    expect(screen.getByText('Staging login fails.')).toBeTruthy();
    expect(screen.getAllByText('ApplicationDefect').length).toBeGreaterThanOrEqual(1);
    expect(screen.getByText('Backend rejects valid credentials.')).toBeTruthy();
    expect(screen.getByText('Advisory')).toBeTruthy();
    expect(screen.getByText('LOGIN-001')).toBeTruthy();
  });

  it('edits title and severity', async () => {
    mockedUpdate.mockImplementation(async (_p, _id, input) => detail({ ...input }));
    renderPage();
    await screen.findByText('Login returns 500');

    fireEvent.click(screen.getByRole('button', { name: /edit/i }));
    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Login 500 confirmed' } });
    fireEvent.change(screen.getByLabelText('Severity'), { target: { value: 'Critical' } });
    fireEvent.click(screen.getByRole('button', { name: /^save$/i }));

    await waitFor(() => expect(mockedUpdate).toHaveBeenCalledWith(
      'p1',
      'd1',
      expect.objectContaining({ title: 'Login 500 confirmed', severity: 'Critical' }),
    ));
  });

  it('changes status through the validated transition', async () => {
    mockedChangeStatus.mockImplementation(async () => detail({ status: 'InProgress' }));
    renderPage();
    await screen.findByText('Login returns 500');

    fireEvent.click(screen.getByRole('button', { name: /change status/i }));
    fireEvent.change(screen.getByLabelText('Status'), { target: { value: 'InProgress' } });
    fireEvent.click(screen.getByRole('button', { name: /save status/i }));

    await waitFor(() => expect(mockedChangeStatus).toHaveBeenCalledWith('p1', 'd1', 'InProgress'));
  });

  it('hides management actions without the manage permission', async () => {
    profileWith([Permissions.BugsRead]);
    renderPage();
    await screen.findByText('Login returns 500');

    expect(screen.queryByRole('button', { name: /edit/i })).toBeNull();
    expect(screen.queryByRole('button', { name: /change status/i })).toBeNull();
  });

  it('maps missing defects to a safe state', async () => {
    mockedGet.mockRejectedValue(new ApiError(404, 'NOT_FOUND', 'Missing'));
    renderPage();
    expect(await screen.findByText('This defect does not exist.')).toBeTruthy();
  });
});
