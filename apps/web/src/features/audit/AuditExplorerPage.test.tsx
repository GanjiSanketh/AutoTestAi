import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { AuditExplorerPage } from './AuditExplorerPage';
import { reportEndpoints } from '../../lib/api/endpoints/reports';
import { api, ApiError } from '../../lib/api/client';

vi.mock('../../lib/api/endpoints/reports', () => ({
  reportKeys: {
    all: ['reports'],
    audit: (p: string, f: unknown, page: number) => ['reports', 'audit', p, f, page],
  },
  reportEndpoints: {
    audit: vi.fn(),
    auditExportUrl: (p: string, f: object) => `/api/v1/projects/${p}/audit/export?${JSON.stringify(f)}`,
  },
}));

vi.mock('../../lib/api/client', async (importOriginal) => {
  const original = await importOriginal<typeof import('../../lib/api/client')>();
  return {
    ...original,
    api: { ...original.api, download: vi.fn() },
  };
});

const profilePermissions = ['reports.read'];
vi.mock('../../lib/auth/useProfile', () => ({
  useProfile: () => ({ data: { permissions: profilePermissions }, isLoading: false }),
}));

const mockedAudit = vi.mocked(reportEndpoints.audit);
const mockedDownload = vi.mocked(api.download);

const row = (overrides = {}) => ({
  id: 7,
  timestamp: '2026-09-28T12:00:00Z',
  action: 'defect.created',
  entityType: 'defect',
  entityId: 'd-1',
  actorUserId: 'user-1',
  ...overrides,
});

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <MemoryRouter initialEntries={['/projects/p1/audit']}>
      <Routes>
        <Route
          path="/projects/:projectId/audit"
          element={
            <QueryClientProvider client={client}>
              <AuditExplorerPage />
            </QueryClientProvider>
          }
        />
      </Routes>
    </MemoryRouter>,
  );
}

describe('AuditExplorerPage (Phase 4 Slice 2)', () => {
  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  it('renders audit rows with safe columns', async () => {
    mockedAudit.mockResolvedValue({ items: [row()], totalCount: 1, page: 1, pageSize: 25 });
    renderPage();
    expect(await screen.findByText('defect.created')).toBeTruthy();
    expect(screen.getByText('defect')).toBeTruthy();
    expect(screen.getByText('d-1')).toBeTruthy();
    expect(screen.getByText('user-1')).toBeTruthy();
  });

  it('shows a loading state', () => {
    mockedAudit.mockReturnValue(new Promise(() => {}));
    renderPage();
    expect(screen.getByLabelText('Loading audit events')).toBeTruthy();
  });

  it('shows an empty state without compliance claims', async () => {
    mockedAudit.mockResolvedValue({ items: [], totalCount: 0, page: 1, pageSize: 25 });
    renderPage();
    expect(await screen.findByText('No audit events found for the selected filters.')).toBeTruthy();
  });

  it('shows an error state with retry', async () => {
    mockedAudit.mockRejectedValue(new ApiError(500, 'ERROR', 'boom'));
    renderPage();
    expect(await screen.findByText('Something went wrong')).toBeTruthy();
    expect(screen.getByRole('button', { name: /try again/i })).toBeTruthy();
  });

  it('filters by action, entity type, actor, and dates', async () => {
    mockedAudit.mockResolvedValue({ items: [row()], totalCount: 1, page: 1, pageSize: 25 });
    renderPage();
    await screen.findByText('defect.created');

    fireEvent.change(screen.getByLabelText('Action'), { target: { value: 'ticket.created' } });
    await waitFor(() =>
      expect(mockedAudit).toHaveBeenCalledWith('p1', expect.objectContaining({ action: 'ticket.created' }), 1, 25),
    );

    fireEvent.change(screen.getByLabelText('Entity type'), { target: { value: 'ticket' } });
    await waitFor(() =>
      expect(mockedAudit).toHaveBeenCalledWith(
        'p1',
        expect.objectContaining({ action: 'ticket.created', entityType: 'ticket' }),
        1,
        25,
      ),
    );

    fireEvent.change(screen.getByLabelText('Actor ID'), { target: { value: 'user-9' } });
    await waitFor(() =>
      expect(mockedAudit).toHaveBeenCalledWith('p1', expect.objectContaining({ actor: 'user-9' }), 1, 25),
    );

    fireEvent.change(screen.getByLabelText('From'), { target: { value: '2026-09-01' } });
    await waitFor(() =>
      expect(mockedAudit).toHaveBeenCalledWith('p1', expect.objectContaining({ from: '2026-09-01' }), 1, 25),
    );
  });

  it('paginates server-side', async () => {
    mockedAudit.mockResolvedValue({ items: [row()], totalCount: 40, page: 1, pageSize: 25 });
    renderPage();
    await screen.findByText('defect.created');
    fireEvent.click(screen.getByRole('button', { name: /next/i }));
    await waitFor(() => expect(mockedAudit).toHaveBeenCalledWith('p1', expect.anything(), 2, 25));
  });

  it('exports CSV through the authenticated download with current filters', async () => {
    mockedAudit.mockResolvedValue({ items: [row()], totalCount: 1, page: 1, pageSize: 25 });
    mockedDownload.mockResolvedValue({ blob: new Blob(['a']), fileName: 'audit.csv' });
    const createSpy = vi.spyOn(document, 'createElement');
    renderPage();
    await screen.findByText('defect.created');
    fireEvent.change(screen.getByLabelText('Action'), { target: { value: 'ticket.created' } });
    await waitFor(() =>
      expect(mockedAudit).toHaveBeenCalledWith('p1', expect.objectContaining({ action: 'ticket.created' }), 1, 25),
    );
    fireEvent.click(screen.getByRole('button', { name: /export csv/i }));
    await waitFor(() => expect(mockedDownload).toHaveBeenCalled());
    createSpy.mockRestore();
  });

  it('surfaces export errors accessibly', async () => {
    mockedAudit.mockResolvedValue({ items: [row()], totalCount: 1, page: 1, pageSize: 25 });
    mockedDownload.mockRejectedValue(new ApiError(403, 'FORBIDDEN', 'no'));
    renderPage();
    await screen.findByText('defect.created');
    fireEvent.click(screen.getByRole('button', { name: /export csv/i }));
    expect(await screen.findByRole('alert')).toBeTruthy();
  });

  it('renders unknown future actions and null actors safely', async () => {
    mockedAudit.mockResolvedValue({
      items: [row({ id: 8, action: 'something.brand_new', entityId: null, actorUserId: null })],
      totalCount: 1,
      page: 1,
      pageSize: 25,
    });
    renderPage();
    expect(await screen.findByText('something.brand_new')).toBeTruthy();
    expect(screen.getAllByText('—').length).toBeGreaterThan(0);
  });

  it('blocks users without reports.read', async () => {
    profilePermissions.length = 0;
    try {
      renderPage();
      expect(await screen.findByText('No access')).toBeTruthy();
      expect(mockedAudit).not.toHaveBeenCalled();
    } finally {
      profilePermissions.push('reports.read');
    }
  });
  it('never renders metadata, IP, or user-agent fields', async () => {
    // Extra wire fields must be ignored by the safe-column rendering.
    const poisoned = {
      ...row(),
      metadataJson: '{"apiKey":"sk-live"}',
      ipAddress: '10.0.0.1',
      userAgent: 'ua',
    } as unknown as ReturnType<typeof row>;
    mockedAudit.mockResolvedValue({
      items: [poisoned],
      totalCount: 1,
      page: 1,
      pageSize: 25,
    });
    renderPage();
    await screen.findByText('defect.created');
    expect(screen.queryByText(/sk-live/)).toBeNull();
    expect(screen.queryByText('10.0.0.1')).toBeNull();
  });
});
