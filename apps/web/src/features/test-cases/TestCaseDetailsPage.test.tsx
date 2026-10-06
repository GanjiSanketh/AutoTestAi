import { afterEach, describe, expect, it, vi, beforeEach } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { TestCaseDetailsPage } from './TestCaseDetailsPage';
import { testcasesEndpoints } from '../../lib/api/endpoints/testcases';
import { executionEndpoints } from '../../lib/api/endpoints/executions';
import { mobileEndpoints } from '../../lib/api/endpoints/mobile';
import { useProfile } from '../../lib/auth/useProfile';
import { Permissions } from '../../lib/auth/permissions';

vi.mock('../../lib/api/endpoints/testcases', () => ({
  testcaseKeys: {
    all: ['test-cases'],
    details: (id: string) => ['test-cases', 'details', id],
    versions: (id: string) => ['test-cases', 'versions', id],
  },
  testcasesEndpoints: {
    get: vi.fn(),
    versions: vi.fn(),
    review: vi.fn(),
    remove: vi.fn(),
  },
}));

vi.mock('../../lib/auth/useProfile', () => ({
  useProfile: vi.fn(),
}));

vi.mock('../../lib/api/endpoints/executions', () => ({
  executionKeys: {
    all: ['executions'],
  },
  executionEndpoints: {
    start: vi.fn(),
  },
}));

vi.mock('../../lib/api/endpoints/mobile', () => ({
  mobileEndpoints: {
    listPools: vi.fn(),
    listApps: vi.fn(),
  },
}));

vi.mock('@monaco-editor/react', () => ({
  default: () => <div data-testid="monaco-stub" />,
}));

// StepsEditor is plain inputs; keep the real component.
vi.mock('./SourceEditor', async (importOriginal) => {
  const actual = await importOriginal<typeof import('./SourceEditor')>();
  return { ...actual };
});

const mockedGet = vi.mocked(testcasesEndpoints.get);
const mockedVersions = vi.mocked(testcasesEndpoints.versions);
const mockedReview = vi.mocked(testcasesEndpoints.review);
const mockedProfile = vi.mocked(useProfile);

const details = {
  id: 'c1',
  projectId: 'p1',
  testKey: 'LOGIN-001',
  title: 'Successful user login',
  description: null,
  module: 'Authentication',
  framework: 'playwright',
  platform: 'web',
  priority: 'High',
  status: 'Active',
  sourceType: 'manual',
  latestVersionNumber: 2,
  latestReviewStatus: 'Pending',
  createdBy: null,
  createdAt: '',
  updatedAt: '',
};

const versions = [
  {
    id: 'v2',
    testCaseId: 'c1',
    versionNumber: 2,
    sourceCode: '// v2',
    structuredSteps: [],
    generationProvider: null,
    generationModel: null,
    generationLatencyMs: null,
    reviewStatus: 'Pending',
    createdBy: null,
    createdAt: '2026-09-28T01:00:00Z',
  },
  {
    id: 'v1',
    testCaseId: 'c1',
    versionNumber: 1,
    sourceCode: '// v1',
    structuredSteps: [],
    generationProvider: null,
    generationModel: null,
    generationLatencyMs: null,
    reviewStatus: 'Approved',
    createdBy: null,
    createdAt: '2026-09-28T00:00:00Z',
  },
];

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

function renderPage() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/projects/p1/test-cases/c1']}>
        <Routes>
          <Route path="/projects/:projectId/test-cases/:testCaseId" element={<TestCaseDetailsPage />} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('TestCaseDetailsPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockedGet.mockResolvedValue(details as never);
    mockedVersions.mockResolvedValue(versions as never);
  });
  afterEach(() => {
    cleanup();
  });

  it('shows review controls to managers and submits the review', async () => {
    profileWith([Permissions.TestCasesRead, Permissions.TestCasesManage]);
    mockedReview.mockResolvedValue({ ...versions[0], reviewStatus: 'Approved' } as never);
    renderPage();

    await waitFor(() => expect(screen.queryByText('Version history')).not.toBeNull());
    fireEvent.click(screen.getByRole('button', { name: /Review v2/ }));
    fireEvent.change(screen.getByLabelText('Review status'), { target: { value: 'Approved' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save review' }));

    await waitFor(() =>
      expect(mockedReview).toHaveBeenCalledWith('c1', 'v2', 'Approved'),
    );
  });

  it('hides review controls without the manage permission', async () => {
    profileWith([Permissions.TestCasesRead]);
    renderPage();
    await waitFor(() => expect(screen.queryByText('Version history')).not.toBeNull());
    expect(screen.queryByRole('button', { name: /Review v2/ })).toBeNull();
    expect(
      screen.queryByText(/Review requires the test-case manage permission/),
    ).not.toBeNull();
  });

  it('shows a read-only historical version when selected', async () => {
    profileWith([Permissions.TestCasesRead, Permissions.TestCasesManage]);
    renderPage();
    await waitFor(() => expect(screen.queryByText('Version history')).not.toBeNull());
    fireEvent.click(screen.getByRole('button', { name: /v1/ }));
    await waitFor(() =>
      expect(screen.queryByText(/Historical version v1 \(read-only\)/)).not.toBeNull(),
    );
  });

  it('hides mobile target selects for web test cases', async () => {
    profileWith([Permissions.TestCasesRead, Permissions.ExecutionsExecute]);
    renderPage();
    await waitFor(() => expect(screen.queryByText('Version history')).not.toBeNull());
    expect(screen.queryByLabelText('Device pool')).toBeNull();
    expect(screen.queryByLabelText('Application')).toBeNull();
  });

  it('requires pool and app before running an appium test case', async () => {
    profileWith([Permissions.TestCasesRead, Permissions.ExecutionsExecute]);
    mockedGet.mockResolvedValue({ ...details, framework: 'appium', platform: 'android' } as never);
    vi.mocked(mobileEndpoints.listPools).mockResolvedValue([
      { id: 'pool-1', name: 'android-smoke', platform: 'Android' },
    ] as never);
    vi.mocked(mobileEndpoints.listApps).mockResolvedValue([
      { id: 'app-1', name: 'Shop', platform: 'Android' },
    ] as never);
    renderPage();
    await waitFor(() => expect(screen.queryByText('Version history')).not.toBeNull());
    fireEvent.click(screen.getByRole('button', { name: /v1/ }));
    await waitFor(() => expect(screen.queryByLabelText('Device pool')).not.toBeNull());
    await waitFor(() => expect(screen.queryByRole('button', { name: /Run v1/ })).not.toBeNull());

    fireEvent.click(screen.getByRole('button', { name: /Run v1/ }));
    await waitFor(() =>
      expect(screen.queryByText(/Select a device pool and an application/)).not.toBeNull(),
    );
    expect(vi.mocked(executionEndpoints.start)).not.toHaveBeenCalled();

    vi.mocked(executionEndpoints.start).mockResolvedValue({ executionId: 'e1' } as never);
    fireEvent.change(screen.getByLabelText('Device pool'), { target: { value: 'pool-1' } });
    fireEvent.change(screen.getByLabelText('Application'), { target: { value: 'app-1' } });
    fireEvent.click(screen.getByRole('button', { name: /Run v1/ }));
    await waitFor(() =>
      expect(vi.mocked(executionEndpoints.start)).toHaveBeenCalledWith(
        'p1',
        expect.objectContaining({
          testCaseVersionId: 'v1',
          mobileDevicePoolId: 'pool-1',
          mobileAppId: 'app-1',
        }),
      ),
    );
  });

  it('shows Jira provenance for the version that has it', async () => {
    profileWith([Permissions.TestCasesRead]);
    mockedVersions.mockResolvedValue([
      { ...versions[0], jiraProvenance: null },
      {
        ...versions[1],
        jiraProvenance: {
          origin: 'jira-import',
          jiraIssueKey: 'PROJ-9',
          jiraIssueType: 'Story',
          jiraBaseUrlHost: 'company.atlassian.net',
          jiraFetchedAt: '2026-10-06T00:00:00Z',
        },
      },
    ] as never);
    renderPage();
    await waitFor(() => expect(screen.queryByText('Version history')).not.toBeNull());
    // History marks only the Jira version; the current version shows nothing.
    expect(screen.queryByText('From PROJ-9')).not.toBeNull();
    expect(screen.queryByText(/Generated from PROJ-9/)).toBeNull();
    // Selecting v1 reveals the version-level provenance section.
    fireEvent.click(screen.getByRole('button', { name: /v1/ }));
    await waitFor(() => expect(screen.queryByText(/Generated from PROJ-9/)).not.toBeNull());
    expect(screen.queryByText('Story')).not.toBeNull();
    expect(screen.queryByText('company.atlassian.net')).not.toBeNull();
  });

  it('hides Jira provenance when versions have none', async () => {
    profileWith([Permissions.TestCasesRead]);
    renderPage();
    await waitFor(() => expect(screen.queryByText('Version history')).not.toBeNull());
    expect(screen.queryByText(/Generated from /)).toBeNull();
    expect(screen.queryByText(/From PROJ-/)).toBeNull();
  });

  it('never renders raw generation request content', async () => {
    profileWith([Permissions.TestCasesRead]);
    mockedVersions.mockResolvedValue([
      {
        ...versions[0],
        jiraProvenance: {
          origin: 'jira-import',
          jiraIssueKey: 'PROJ-9',
          jiraIssueType: null,
          jiraBaseUrlHost: null,
          jiraFetchedAt: null,
        },
        generationRequest: { acceptanceCriteria: ['SECRET-CRITERION'], storyTitle: 'Raw title' },
      },
    ] as never);
    renderPage();
    await waitFor(() => expect(screen.queryByText('Version history')).not.toBeNull());
    expect(screen.queryByText('SECRET-CRITERION')).toBeNull();
    expect(screen.queryByText('Raw title')).toBeNull();
    expect(screen.queryByText(/Generated from PROJ-9/)).not.toBeNull();
  });
});
