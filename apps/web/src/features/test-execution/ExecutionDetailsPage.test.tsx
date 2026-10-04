import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ExecutionDetailsPage } from './ExecutionDetailsPage';
import { executionEndpoints } from '../../lib/api/endpoints/executions';
import { useProfile } from '../../lib/auth/useProfile';
import { ApiError } from '../../lib/api/client';
import { Permissions } from '../../lib/auth/permissions';
import {
  ExecutionEvent,
  createExecutionHubConnection,
  subscribeToExecution,
} from '../../lib/realtime/executionHub';

vi.mock('../../lib/api/endpoints/executions', () => ({
  executionKeys: {
    all: ['executions'],
    details: (p: string, id: string) => ['executions', 'details', p, id],
    artifacts: (p: string, id: string) => ['executions', 'artifacts', p, id],
    logs: (p: string, id: string) => ['executions', 'logs', p, id],
  },
  executionEndpoints: {
    get: vi.fn(),
    artifacts: vi.fn(),
    logs: vi.fn(),
    cancel: vi.fn(),
    download: vi.fn(),
  },
}));

vi.mock('../../lib/realtime/executionHub', () => ({
  ExecutionEvent: {
    ExecutionStatusChanged: 'ExecutionStatusChanged',
    ExecutionStepStarted: 'ExecutionStepStarted',
    ExecutionStepCompleted: 'ExecutionStepCompleted',
    ExecutionLogReceived: 'ExecutionLogReceived',
    ExecutionCompleted: 'ExecutionCompleted',
    ExecutionFailed: 'ExecutionFailed',
  },
  createExecutionHubConnection: vi.fn(),
  subscribeToExecution: vi.fn(),
}));

vi.mock('../../lib/auth/useProfile', () => ({
  useProfile: vi.fn(),
}));

const mockedGet = vi.mocked(executionEndpoints.get);
const mockedArtifacts = vi.mocked(executionEndpoints.artifacts);
const mockedLogs = vi.mocked(executionEndpoints.logs);
const mockedCancel = vi.mocked(executionEndpoints.cancel);
const mockedDownload = vi.mocked(executionEndpoints.download);
const mockedProfile = vi.mocked(useProfile);
const mockedCreateConnection = vi.mocked(createExecutionHubConnection);
const mockedSubscribe = vi.mocked(subscribeToExecution);

type HandlerMap = Record<string, (payload: unknown) => void>;
let capturedHandlers: HandlerMap = {};
let connectionStopped = false;

const detail = (overrides = {}) => ({
  id: 'e1',
  projectId: 'p1',
  status: 'Running',
  triggerType: 'Manual',
  environmentId: null,
  workflowId: 'wf-e1',
  startedAt: '2026-09-28T10:00:00Z',
  completedAt: null,
  createdBy: null,
  createdAt: '2026-09-28T10:00:00Z',
  test: {
    id: 't1',
    testCaseId: 'c1',
    testKey: 'LOGIN-001',
    testTitle: 'Successful user login',
    testSourceType: 'ai',
    testCaseVersionId: 'v3',
    testCaseVersionNumber: 3,
    reviewStatus: 'Approved',
    status: 'Running',
    framework: 'playwright',
    browser: 'chromium',
    failureClassification: 'Unknown',
    attempt: 1,
    durationMs: null,
    errorType: null,
    errorMessage: null,
    steps: [
      {
        order: 1,
        action: 'navigate',
        target: 'https://example.test',
        status: 'Passed',
        startedAt: '2026-09-28T10:00:01Z',
        completedAt: '2026-09-28T10:00:02Z',
        durationMs: 900,
        errorMessage: null,
      },
    ],
    createdAt: '2026-09-28T10:00:00Z',
    updatedAt: '2026-09-28T10:00:02Z',
    ...overrides,
  },
});

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/projects/p1/executions/e1']}>
        <Routes>
          <Route path="/projects/:projectId/executions/:executionId" element={<ExecutionDetailsPage />} />
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

describe('ExecutionDetailsPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    capturedHandlers = {};
    connectionStopped = false;
    profileWith([Permissions.ExecutionsRead, Permissions.ExecutionsCancel]);
    mockedGet.mockResolvedValue(detail());
    mockedArtifacts.mockResolvedValue([]);
    mockedLogs.mockResolvedValue([
      { id: 1, timestamp: '2026-09-28T10:00:01Z', level: 'info', message: 'worker started' },
    ]);
    mockedCreateConnection.mockReturnValue({
      stop: async () => {
        connectionStopped = true;
      },
      onreconnected: vi.fn(),
    } as never);
    mockedSubscribe.mockImplementation(async (_conn, _id, handlers) => {
      capturedHandlers = (handlers ?? {}) as HandlerMap;
    });
  });

  afterEach(() => {
    cleanup();
  });

  it('renders detail with steps, logs, AI provenance, and cancel action', async () => {
    renderPage();

    expect(await screen.findByText('Successful user login')).toBeTruthy();
    expect(screen.getByText('LOGIN-001', { exact: false })).toBeTruthy();
    expect(screen.getByText('AI-generated')).toBeTruthy();
    expect(screen.getByText('navigate')).toBeTruthy();
    expect(screen.getByText('worker started')).toBeTruthy();
    expect(screen.getByRole('button', { name: /cancel execution/i })).toBeTruthy();
    expect(mockedSubscribe).toHaveBeenCalledWith(
      expect.anything(),
      'e1',
      expect.anything(),
    );
  });

  it('cancels a running execution and hides the action when terminal', async () => {
    mockedCancel.mockResolvedValue({ executionId: 'e1', status: 'Cancelled', cancellationRequested: true });
    renderPage();
    await screen.findByText('Successful user login');

    fireEvent.click(screen.getByRole('button', { name: /cancel execution/i }));
    await waitFor(() => expect(mockedCancel).toHaveBeenCalledWith('p1', 'e1'));

    cleanup();
    mockedGet.mockResolvedValue({ ...detail(), status: 'Cancelled' });
    mockedSubscribe.mockClear();
    renderPage();
    await screen.findByText('Successful user login');
    expect(screen.queryByRole('button', { name: /cancel execution/i })).toBeNull();
    expect(mockedSubscribe).not.toHaveBeenCalled();
  });

  it('applies live step events without refetching', async () => {
    renderPage();
    await screen.findByText('Successful user login');
    expect(mockedSubscribe).toHaveBeenCalledTimes(1);

    capturedHandlers[ExecutionEvent.ExecutionStepCompleted]?.({
      step: {
        order: 2,
        action: 'click',
        target: '#submit',
        status: 'Passed',
        startedAt: null,
        completedAt: null,
        durationMs: 120,
        errorMessage: null,
      },
    });

    expect(await screen.findByText('click')).toBeTruthy();
    // REST detail carries one step; the live step arrives via SignalR only.
    expect(mockedGet).toHaveBeenCalledTimes(1);
  });

  it('renders log content as text, never as HTML', async () => {
    mockedLogs.mockResolvedValue([
      { id: 1, timestamp: '2026-09-28T10:00:01Z', level: 'info', message: '<img src=x onerror=alert(1)>' },
    ]);
    const { container } = renderPage();
    await screen.findByText('Successful user login');

    expect(container.querySelector('img')).toBeNull();
    expect(screen.getByLabelText('Execution logs').textContent).toContain('<img src=x onerror=alert(1)>');
  });

  it('shows artifact errors without storage details', async () => {
    mockedArtifacts.mockResolvedValue([
      {
        id: 'a1',
        artifactType: 'screenshot',
        fileName: 'step-1-failure.png',
        stepOrder: 1,
        contentType: 'image/png',
        sizeBytes: 42,
        createdAt: '2026-09-28T10:00:03Z',
      },
    ]);
    mockedDownload.mockRejectedValue(new ApiError(404, 'NOT_FOUND', 'Gone'));
    renderPage();
    await screen.findByText('step-1-failure.png');

    fireEvent.click(screen.getByRole('button', { name: /open/i }));
    expect(await screen.findByText(/expired or been removed/)).toBeTruthy();
  });

  it('previews image artifacts inline without affecting other types', async () => {
    mockedArtifacts.mockResolvedValue([
      {
        id: 'shot-1',
        artifactType: 'screenshot',
        fileName: 'step-1-failure.png',
        stepOrder: 1,
        contentType: 'image/png',
        sizeBytes: 42,
        createdAt: '2026-09-28T10:00:03Z',
      },
      {
        id: 'diff-1',
        artifactType: 'visual-diff',
        fileName: 'step-3-visual-diff.png',
        stepOrder: 3,
        contentType: 'image/png',
        sizeBytes: 43,
        createdAt: '2026-09-28T10:00:04Z',
      },
      {
        id: 'log-1',
        artifactType: 'appium-log',
        fileName: 'appium.log',
        stepOrder: null,
        contentType: 'text/plain',
        sizeBytes: 44,
        createdAt: '2026-09-28T10:00:05Z',
      },
    ]);
    mockedDownload.mockResolvedValue({ downloadUrl: 'https://artifacts.example/preview.png?exp=900' });
    renderPage();
    await screen.findByText('step-3-visual-diff.png');

    // Image artifacts (screenshot and visual-diff) offer preview; text does not.
    const previewButtons = screen.getAllByRole('button', { name: 'Preview' });
    expect(previewButtons).toHaveLength(2);

    fireEvent.click(previewButtons[1]);
    await waitFor(() => expect(mockedDownload).toHaveBeenCalledWith('p1', 'e1', 'diff-1'));
    const images = await screen.findAllByAltText('step-3-visual-diff.png');
    expect(images.length).toBeGreaterThan(0);
    expect(document.body.textContent ?? '').not.toContain('https://artifacts.example/preview.png?exp=900');
  });

  it('maps forbidden and missing executions to safe states', async () => {
    mockedGet.mockRejectedValue(new ApiError(403, 'FORBIDDEN', 'Forbidden'));
    renderPage();
    expect(await screen.findByText('No access')).toBeTruthy();

    cleanup();
    mockedGet.mockRejectedValue(new ApiError(404, 'NOT_FOUND', 'Missing'));
    renderPage();
    expect(await screen.findByText('This execution does not exist.')).toBeTruthy();
  });

  it('stops the hub connection on unmount', async () => {
    const { unmount } = renderPage();
    await screen.findByText('Successful user login');
    unmount();
    expect(connectionStopped).toBe(true);
  });
});
