import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { DefectDetailsPage } from './DefectDetailsPage';
import { defectEndpoints } from '../../lib/api/endpoints/defects';
import { ticketEndpoints } from '../../lib/api/endpoints/tickets';
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

vi.mock('../../lib/api/endpoints/tickets', () => ({
  ticketKeys: {
    all: ['tickets'],
    defectTicket: (p: string, id: string) => ['tickets', 'defect', p, id],
    jiraStatus: (p: string) => ['tickets', 'jira-status', p],
  },
  ticketEndpoints: {
    getForDefect: vi.fn(),
    createForDefect: vi.fn(),
    jiraStatus: vi.fn(),
  },
  ticketErrorMessage: (status: number) => `Ticket error ${status}`,
}));

vi.mock('../../lib/auth/useProfile', () => ({
  useProfile: vi.fn(),
}));

const mockedGet = vi.mocked(defectEndpoints.get);
const mockedTicketGet = vi.mocked(ticketEndpoints.getForDefect);
const mockedTicketCreate = vi.mocked(ticketEndpoints.createForDefect);
const mockedJiraStatus = vi.mocked(ticketEndpoints.jiraStatus);
const mockedProfile = vi.mocked(useProfile);

const detail = () => ({
  id: 'd1',
  projectId: 'p1',
  title: 'Login returns 500',
  description: 'Fails.',
  severity: 'High',
  status: 'Open',
  failureClassification: 'ApplicationDefect',
  executionId: 'e1',
  executionTestId: 't1',
  testCaseId: 'c1',
  testKey: 'LOGIN-001',
  testTitle: 'Login',
  testCaseVersionId: 'v1',
  testCaseVersionNumber: 1,
  failureAnalysisId: null,
  analysis: null,
  aiConfidence: null,
  createdBy: null,
  createdAt: '2026-09-28T10:00:00Z',
  updatedAt: '2026-09-28T10:00:00Z',
});

const ticket = (overrides = {}) => ({
  id: 'k1',
  projectId: 'p1',
  defectId: 'd1',
  integrationId: 'i1',
  provider: 'jira',
  externalId: '10001',
  externalKey: 'ABC-123',
  externalUrl: 'https://jira.test/browse/ABC-123',
  title: '[AutoTestAI] Login returns 500',
  syncStatus: 'Synced',
  createdBy: null,
  createdAt: '2026-09-29T10:00:00Z',
  updatedAt: '2026-09-29T10:00:00Z',
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

describe('DefectDetailsPage Jira section (Slice 7)', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockedGet.mockResolvedValue(detail());
  });

  afterEach(() => {
    cleanup();
  });

  it('shows the create action when permitted and configured', async () => {
    profileWith([Permissions.BugsRead, Permissions.BugsManage, Permissions.TicketsRead, Permissions.TicketsCreate]);
    mockedJiraStatus.mockResolvedValue({ provider: 'jira', configured: true, enabled: true, projectKey: 'ABC', baseUrl: 'https://jira.test', issueType: 'Bug' });
    mockedTicketGet.mockResolvedValue(null);
    renderPage();
    expect(await screen.findByRole('button', { name: /create jira ticket/i })).toBeTruthy();
  });

  it('requires confirmation before creating', async () => {
    profileWith([Permissions.BugsRead, Permissions.TicketsRead, Permissions.TicketsCreate]);
    mockedJiraStatus.mockResolvedValue({ provider: 'jira', configured: true, enabled: true, projectKey: 'ABC', baseUrl: 'https://jira.test', issueType: 'Bug' });
    mockedTicketGet.mockResolvedValue(null);
    mockedTicketCreate.mockResolvedValue(ticket());
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: /create jira ticket/i }));
    expect(await screen.findByText(/manual action/i)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /confirm creation/i }));
    await waitFor(() => expect(mockedTicketCreate).toHaveBeenCalledWith('p1', 'd1'));
    expect(await screen.findByText(/ABC-123 created/)).toBeTruthy();
  });

  it('renders the existing ticket with a safe external link', async () => {
    profileWith([Permissions.BugsRead, Permissions.TicketsRead, Permissions.TicketsCreate]);
    mockedJiraStatus.mockResolvedValue({ provider: 'jira', configured: true, enabled: true, projectKey: 'ABC', baseUrl: 'https://jira.test', issueType: 'Bug' });
    mockedTicketGet.mockResolvedValue(ticket());
    renderPage();
    expect(await screen.findByText('ABC-123')).toBeTruthy();
    const link = await screen.findByText(/open in jira/i);
    expect(link.getAttribute('href')).toBe('https://jira.test/browse/ABC-123');
    expect(screen.queryByRole('button', { name: /create jira ticket/i })).toBeNull();
  });

  it('shows the unconfigured state', async () => {
    profileWith([Permissions.BugsRead, Permissions.TicketsRead, Permissions.TicketsCreate]);
    mockedJiraStatus.mockResolvedValue({ provider: 'jira', configured: false, enabled: false, projectKey: null, baseUrl: null, issueType: null });
    mockedTicketGet.mockResolvedValue(null);
    renderPage();
    expect(await screen.findByText(/not configured/i)).toBeTruthy();
  });

  it('hides the create action without the ticket permission', async () => {
    profileWith([Permissions.BugsRead, Permissions.TicketsRead]);
    mockedJiraStatus.mockResolvedValue({ provider: 'jira', configured: true, enabled: true, projectKey: 'ABC', baseUrl: 'https://jira.test', issueType: 'Bug' });
    mockedTicketGet.mockResolvedValue(null);
    renderPage();
    await screen.findByText('Login returns 500');
    expect(screen.queryByRole('button', { name: /create jira ticket/i })).toBeNull();
  });

  it('surfaces creation errors accessibly', async () => {
    profileWith([Permissions.BugsRead, Permissions.TicketsRead, Permissions.TicketsCreate]);
    mockedJiraStatus.mockResolvedValue({ provider: 'jira', configured: true, enabled: true, projectKey: 'ABC', baseUrl: 'https://jira.test', issueType: 'Bug' });
    mockedTicketGet.mockResolvedValue(null);
    mockedTicketCreate.mockRejectedValue(new ApiError(503, 'PROVIDER_TIMEOUT', 'down'));
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: /create jira ticket/i }));
    fireEvent.click(await screen.findByRole('button', { name: /confirm creation/i }));
    await waitFor(() => expect(mockedTicketCreate).toHaveBeenCalled());
    expect(await screen.findByRole('alert')).toBeTruthy();
  });

  it('closes the confirmation with Escape', async () => {
    profileWith([Permissions.BugsRead, Permissions.TicketsRead, Permissions.TicketsCreate]);
    mockedJiraStatus.mockResolvedValue({ provider: 'jira', configured: true, enabled: true, projectKey: 'ABC', baseUrl: 'https://jira.test', issueType: 'Bug' });
    mockedTicketGet.mockResolvedValue(null);
    renderPage();
    fireEvent.click(await screen.findByRole('button', { name: /create jira ticket/i }));
    expect(await screen.findByText(/manual action/i)).toBeTruthy();
    fireEvent.keyDown(window, { key: 'Escape' });
    await waitFor(() => expect(screen.queryByText(/manual action/i)).toBeNull());
  });

  it('marks automatically created tickets distinctly (Slice 10)', async () => {
    profileWith([Permissions.BugsRead, Permissions.TicketsRead, Permissions.TicketsCreate]);
    mockedJiraStatus.mockResolvedValue({ provider: 'jira', configured: true, enabled: true, projectKey: 'ABC', baseUrl: 'https://jira.test', issueType: 'Bug' });
    mockedTicketGet.mockResolvedValue(ticket({ origin: 'Automatic' }));
    renderPage();
    expect(await screen.findByText('ABC-123')).toBeTruthy();
    expect(await screen.findByText('Auto-created')).toBeTruthy();
  });

  it('marks manually created tickets distinctly (Slice 10)', async () => {
    profileWith([Permissions.BugsRead, Permissions.TicketsRead, Permissions.TicketsCreate]);
    mockedJiraStatus.mockResolvedValue({ provider: 'jira', configured: true, enabled: true, projectKey: 'ABC', baseUrl: 'https://jira.test', issueType: 'Bug' });
    mockedTicketGet.mockResolvedValue(ticket({ origin: 'Manual' }));
    renderPage();
    expect(await screen.findByText('ABC-123')).toBeTruthy();
    expect(await screen.findByText('Manual')).toBeTruthy();
  });
});
