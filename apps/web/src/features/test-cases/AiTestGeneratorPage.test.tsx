import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { AiTestGeneratorPage } from './AiTestGeneratorPage';
import { testGenerationEndpoints } from '../../lib/api/endpoints/testGeneration';
import { useProfile } from '../../lib/auth/useProfile';
import { ApiError } from '../../lib/api/client';
import { Permissions } from '../../lib/auth/permissions';

vi.mock('../../lib/api/endpoints/testGeneration', () => ({
  testGenerationKeys: {
    all: ['test-generation'],
    status: (projectId: string) => ['test-generation', 'status', projectId],
  },
  testGenerationEndpoints: {
    generate: vi.fn(),
    status: vi.fn(),
  },
}));

vi.mock('../../lib/api/endpoints/testcases', () => ({
  testcaseKeys: { all: ['test-cases'] },
}));

vi.mock('../../lib/auth/useProfile', () => ({
  useProfile: vi.fn(),
}));

const mockedGenerate = vi.mocked(testGenerationEndpoints.generate);
const mockedStatus = vi.mocked(testGenerationEndpoints.status);
const mockedProfile = vi.mocked(useProfile);

function renderPage() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/projects/p1/test-cases/generate']}>
        <Routes>
          <Route path="/projects/:projectId/test-cases/generate" element={<AiTestGeneratorPage />} />
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

function fillValidForm() {
  fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Successful user login' } });
  fireEvent.change(screen.getByLabelText('Requirements'), {
    target: { value: 'Validate successful login\nValidate dashboard navigation' },
  });
}

const successResult = {
  generationId: 'g1',
  testCaseId: 'c1',
  testKey: 'AI-LOGIN-AB12CD',
  versionId: 'v1',
  versionNumber: 1,
  status: 'Succeeded',
  title: 'Successful user login',
  description: 'Verify login.',
  framework: 'playwright',
  platform: 'web',
  structuredSteps: [
    { order: 1, action: 'navigate', target: 'https://example.test/login', value: null },
    { order: 2, action: 'fill', target: '#username', value: '{{username}}' },
  ],
  sourceCode: "import { test } from '@playwright/test';",
  assumptions: ['Username field uses #username.'],
  warnings: ['The selector was inferred and has not been verified.'],
  provider: 'stub',
  model: 'stub-1.0',
  promptVersion: 'test-generation-v1',
  latencyMs: 42,
  inputTokens: null,
  outputTokens: null,
  totalTokens: null,
  reviewStatus: 'Pending',
};

describe('AiTestGeneratorPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    profileWith([Permissions.TestCasesManage, Permissions.TestCasesRead]);
    mockedStatus.mockResolvedValue({
      provider: 'stub',
      model: 'stub-1.0',
      configured: true,
      detail: null,
      promptVersion: 'test-generation-v1',
    });
  });

  afterEach(() => {
    cleanup();
  });

  it('renders the generator form and provider status', async () => {
    renderPage();

    expect(screen.getByLabelText('Title')).toBeTruthy();
    expect(screen.getByLabelText('Requirements')).toBeTruthy();
    expect(screen.getByRole('button', { name: /generate test/i })).toBeTruthy();

    expect(await screen.findByLabelText('AI provider status')).toBeTruthy();
    expect((await screen.findByLabelText('AI provider status')).textContent).toContain('stub');
  });

  it('blocks submit with client validation and never calls the API', () => {
    renderPage();

    fireEvent.click(screen.getByRole('button', { name: /generate test/i }));

    expect(screen.getByText('Title is required.')).toBeTruthy();
    expect(screen.getByText('At least one requirement is required.')).toBeTruthy();
    expect(mockedGenerate).not.toHaveBeenCalled();
  });

  it('disables generation while a request is in flight', async () => {
    let resolve!: (value: typeof successResult) => void;
    mockedGenerate.mockReturnValue(new Promise((r) => { resolve = r; }));
    renderPage();
    fillValidForm();

    fireEvent.click(screen.getByRole('button', { name: /generate test/i }));

    const running = await screen.findByRole('button', { name: /generating/i });
    expect((running as HTMLButtonElement).disabled).toBe(true);
    expect(await screen.findByLabelText('Generation in progress')).toBeTruthy();
    expect(mockedGenerate).toHaveBeenCalledTimes(1);

    resolve(successResult);
    await screen.findByLabelText('Generation result');
  });

  it('shows a full preview on success with provenance and review state', async () => {
    mockedGenerate.mockResolvedValue(successResult);
    renderPage();
    fillValidForm();

    fireEvent.click(screen.getByRole('button', { name: /generate test/i }));

    const preview = await screen.findByLabelText('Generation result');
    expect(preview.textContent).toContain('AI-LOGIN-AB12CD');
    expect(preview.textContent).toContain('stub-1.0');
    expect(preview.textContent).toContain('test-generation-v1');
    expect(preview.textContent).toContain('Pending review');
    expect(preview.textContent).toContain('navigate');
    expect(preview.textContent).toContain('#username');
    expect(preview.textContent).toContain("@playwright/test");
    expect(preview.textContent).toContain('Username field uses #username.');
    expect(preview.textContent).toContain('has not been verified');
    expect(preview.textContent).toContain('has not been executed');

    expect(screen.getByRole('link', { name: /view in repository/i }).getAttribute('href')).toBe(
      '/projects/p1/test-cases/c1',
    );
    expect(screen.getByRole('link', { name: /review & edit/i }).getAttribute('href')).toBe(
      '/projects/p1/test-cases/c1/edit',
    );
  });

  it('maps forbidden errors to an access state with retry hidden', async () => {
    mockedGenerate.mockRejectedValue(new ApiError(403, 'FORBIDDEN', 'Forbidden'));
    renderPage();
    fillValidForm();

    fireEvent.click(screen.getByRole('button', { name: /generate test/i }));

    expect(await screen.findByText('No access')).toBeTruthy();
  });

  it('maps rate limits and unavailable providers with retry', async () => {
    mockedGenerate.mockRejectedValueOnce(
      new ApiError(429, 'RATE_LIMITED', 'Too many requests.'),
    );
    renderPage();
    fillValidForm();
    fireEvent.click(screen.getByRole('button', { name: /generate test/i }));
    expect(await screen.findByText('Generation rate limited')).toBeTruthy();

    cleanup();
    mockedGenerate.mockRejectedValueOnce(
      new ApiError(503, 'PROVIDER_UNAVAILABLE', 'Ollama provider is unreachable.'),
    );
    renderPage();
    fillValidForm();
    fireEvent.click(screen.getByRole('button', { name: /generate test/i }));
    expect(await screen.findByText('AI provider unavailable')).toBeTruthy();
    expect(screen.getByRole('button', { name: /retry generation/i })).toBeTruthy();
  });

  it('disables generation for callers without the manage permission', async () => {
    profileWith([Permissions.TestCasesRead]);
    renderPage();

    expect(await screen.findByText(/requires the[\s\S]*testcases.manage permission/)).toBeTruthy();
    expect((screen.getByRole('button', { name: /generate test/i }) as HTMLButtonElement).disabled).toBe(true);
    await waitFor(() => expect(mockedGenerate).not.toHaveBeenCalled());
  });
});
