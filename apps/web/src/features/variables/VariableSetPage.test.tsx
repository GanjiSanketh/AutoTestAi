import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { VariableSetPage } from './VariableSetPage';
import { variablesEndpoints } from '../../lib/api/endpoints/variables';
import { secretsEndpoints } from '../../lib/api/endpoints/secrets';
import { projectsEndpoints } from '../../lib/api/endpoints/projects';
import { useProfile } from '../../lib/auth/useProfile';
import { ApiError } from '../../lib/api/client';
import { Permissions } from '../../lib/auth/permissions';

vi.mock('../../lib/api/endpoints/variables', () => ({
  variableKeys: {
    all: ['variable-sets'],
    list: (projectId: string) => ['variable-sets', 'list', projectId],
    details: (id: string) => ['variable-sets', 'details', id],
  },
  variablesEndpoints: {
    list: vi.fn(),
    get: vi.fn(),
    create: vi.fn(),
    update: vi.fn(),
    remove: vi.fn(),
  },
  VARIABLE_KEY_PATTERN: /^[A-Z0-9_]{1,64}$/,
  isValidVariableKey: (key: string) => /^[A-Z0-9_]{1,64}$/.test(key),
}));

vi.mock('../../lib/api/endpoints/secrets', () => ({
  secretKeys: {
    all: ['secrets'],
    list: (projectId: string, environmentId?: string) => ['secrets', 'list', projectId, environmentId ?? 'all'],
  },
  secretsEndpoints: {
    list: vi.fn(),
    create: vi.fn(),
    exists: vi.fn(),
    update: vi.fn(),
    remove: vi.fn(),
  },
}));

vi.mock('../../lib/api/endpoints/projects', () => ({
  projectKeys: {
    environments: (id: string) => ['projects', 'environments', id],
  },
  projectsEndpoints: {
    environments: vi.fn(),
  },
}));

vi.mock('../../lib/auth/useProfile', () => ({
  useProfile: vi.fn(),
}));

const mockedSets = vi.mocked(variablesEndpoints.list);
const mockedCreateSet = vi.mocked(variablesEndpoints.create);
const mockedUpdateSet = vi.mocked(variablesEndpoints.update);
const mockedSecrets = vi.mocked(secretsEndpoints.list);
const mockedCreateSecret = vi.mocked(secretsEndpoints.create);
const mockedEnvs = vi.mocked(projectsEndpoints.environments);
const mockedProfile = vi.mocked(useProfile);

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/projects/p1/variables']}>
        <Routes>
          <Route path="/projects/:projectId/variables" element={<VariableSetPage />} />
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

const managerPerms = [Permissions.ProjectsRead, Permissions.VariablesManage, Permissions.SecretsManage];
const viewerPerms = [Permissions.ProjectsRead];

describe('VariableSetPage', () => {
  beforeEach(() => {
    mockedSets.mockResolvedValue([]);
    mockedSecrets.mockResolvedValue([]);
    mockedEnvs.mockResolvedValue([
      { id: 'env-qa', projectId: 'p1', name: 'QA', baseUrl: 'https://qa.example.com', status: 'Active', isDefault: true, createdAt: '', updatedAt: '' },
    ]);
  });

  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  it('shows precedence and empty states', async () => {
    profileWith(managerPerms);
    renderPage();
    expect(await screen.findByText(/Precedence: System → Project → Environment/i)).toBeTruthy();
    expect(await screen.findByText(/No secrets stored/i)).toBeTruthy();
  });

  it('rejects lowercase keys client-side', async () => {
    profileWith(managerPerms);
    renderPage();
    await screen.findByText(/Variable set scope/i);
    const keyInput = await screen.findByLabelText('Variable key 1');
    fireEvent.change(keyInput, { target: { value: 'base_url' } });
    fireEvent.change(screen.getByLabelText('Variable value 1'), { target: { value: 'x' } });
    fireEvent.click(screen.getByText('Create set'));
    expect((await screen.findByRole('alert')).textContent).toMatch('must match');
    expect(mockedCreateSet).not.toHaveBeenCalled();
  });

  it('creates a plain variable set', async () => {
    profileWith(managerPerms);
    mockedCreateSet.mockResolvedValue({ id: 's1' } as never);
    renderPage();
    fireEvent.change(await screen.findByLabelText('Variable key 1'), { target: { value: 'BASE_URL' } });
    fireEvent.change(screen.getByLabelText('Variable value 1'), { target: { value: 'https://qa.example.com' } });
    fireEvent.click(screen.getByText('Create set'));
    await waitFor(() => expect(mockedCreateSet).toHaveBeenCalled());
    expect(mockedCreateSet).toHaveBeenCalledWith(
      'p1',
      expect.objectContaining({
        scopeType: 'Project',
        variables: { BASE_URL: { value: 'https://qa.example.com' } },
      }),
    );
  });

  it('requires selecting a stored secret for secretRef entries (never raw)', async () => {
    profileWith(managerPerms);
    mockedSecrets.mockResolvedValue([
      {
        id: 'sec-1', projectId: 'p1', environmentId: 'env-qa', name: 'API_TOKEN', description: null,
        secretReference: 'env_secret:11111111-1111-1111-1111-111111111111', hasValue: true,
        rowVersion: null, createdAt: '', updatedAt: '',
      },
    ]);
    renderPage();
    fireEvent.change(await screen.findByLabelText('Variable key 1'), { target: { value: 'API_TOKEN' } });
    fireEvent.change(screen.getByLabelText('Variable kind 1'), { target: { value: 'secretRef' } });
    // No secret selected yet: blocked client-side.
    fireEvent.click(screen.getByText('Create set'));
    expect((await screen.findByRole('alert')).textContent).toMatch('Select a stored secret');
    expect(mockedCreateSet).not.toHaveBeenCalled();
    // Select the stored secret: payload carries the reference, not a value.
    fireEvent.change(screen.getByLabelText('Secret reference 1'), { target: { value: 'sec-1' } });
    fireEvent.click(screen.getByText('Create set'));
    await waitFor(() => expect(mockedCreateSet).toHaveBeenCalled());
    expect(mockedCreateSet).toHaveBeenCalledWith(
      'p1',
      expect.objectContaining({
        variables: { API_TOKEN: { secretRef: 'env_secret:11111111-1111-1111-1111-111111111111' } },
      }),
    );
  });

  it('shows concurrency conflict deterministically', async () => {
    profileWith(managerPerms);
    mockedSets.mockResolvedValue([
      {
        id: 's1', projectId: 'p1', scopeType: 'Project', scopeId: null, name: 'defaults',
        variablesJson: '{"BASE_URL":{"value":"https://qa.example.com"}}',
        keys: ['BASE_URL'], secretKeys: [], rowVersion: 'AAAA', createdAt: '', updatedAt: '',
      },
    ]);
    mockedUpdateSet.mockRejectedValue(new ApiError(409, 'CONFLICT', 'conflict'));
    renderPage();
    await screen.findByText(/1 keys/i);
    fireEvent.click(screen.getByText('Reload scope'));
    fireEvent.click(screen.getByText('Save changes'));
    expect((await screen.findByRole('alert')).textContent).toMatch('modified by another user');
  });

  it('read-only viewers see keys without editors and secrets redacted', async () => {
    profileWith(viewerPerms);
    mockedSets.mockResolvedValue([
      {
        id: 's1', projectId: 'p1', scopeType: 'Project', scopeId: null, name: 'defaults',
        variablesJson: '{}', keys: ['BASE_URL'], secretKeys: ['API_TOKEN'],
        rowVersion: null, createdAt: '', updatedAt: '',
      },
    ]);
    mockedSecrets.mockResolvedValue([
      {
        id: 'sec-1', projectId: 'p1', environmentId: 'env-qa', name: 'API_TOKEN', description: null,
        secretReference: 'env_secret:11111111-1111-1111-1111-111111111111', hasValue: true,
        rowVersion: null, createdAt: '', updatedAt: '',
      },
    ]);
    renderPage();
    const matches = await screen.findAllByText('API_TOKEN');
    expect(matches.length).toBeGreaterThan(0);
    expect(screen.queryByLabelText('Variable key 1')).toBeNull();
    expect(screen.queryByText('Store a secret')).toBeNull();
    // Reference visible, value never rendered.
    expect(screen.getByText('env_secret:11111111-1111-1111-1111-111111111111')).toBeTruthy();
  });

  it('stores secrets without displaying the value', async () => {
    profileWith(managerPerms);
    mockedCreateSecret.mockResolvedValue({ id: 'sec-2', hasValue: true } as never);
    renderPage();
    await screen.findByText(/Variable set scope/i);
    expect((await screen.findAllByRole('option', { name: 'QA' })).length).toBeGreaterThan(0);
    fireEvent.change(screen.getByLabelText('Secret environment'), { target: { value: 'env-qa' } });
    fireEvent.change(screen.getByLabelText('Secret name'), { target: { value: 'API_TOKEN' } });
    fireEvent.change(screen.getByLabelText('Secret value'), { target: { value: 'super-secret-123' } });
    fireEvent.click(screen.getByText('Store'));
    await waitFor(() => expect(mockedCreateSecret).toHaveBeenCalled());
    expect(document.body.textContent).not.toContain('super-secret-123');
  });
});
