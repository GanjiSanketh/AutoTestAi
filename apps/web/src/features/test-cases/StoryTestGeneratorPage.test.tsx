import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { StoryTestGeneratorPage } from './StoryTestGeneratorPage';
import { testGenerationEndpoints, type StoryTestProposal } from '../../lib/api/endpoints/testGeneration';
import { testcasesEndpoints } from '../../lib/api/endpoints/testcases';
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
    generateFromStory: vi.fn(),
  },
}));

vi.mock('../../lib/api/endpoints/testcases', () => ({
  testcaseKeys: { all: ['test-cases'] },
  testcasesEndpoints: { create: vi.fn() },
}));

vi.mock('../../lib/auth/useProfile', () => ({
  useProfile: vi.fn(),
}));

const mockedStoryGenerate = vi.mocked(testGenerationEndpoints.generateFromStory);
const mockedStatus = vi.mocked(testGenerationEndpoints.status);
const mockedCreate = vi.mocked(testcasesEndpoints.create);
const mockedProfile = vi.mocked(useProfile);

function renderPage() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/projects/p1/test-cases/generate-story']}>
        <Routes>
          <Route path="/projects/:projectId/test-cases/generate-story" element={<StoryTestGeneratorPage />} />
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

function fillValidStory() {
  fireEvent.change(screen.getByLabelText('Story title'), { target: { value: 'Guest checkout' } });
  fireEvent.change(screen.getByLabelText('Acceptance criteria'), {
    target: { value: 'Guest can place an order\nOrder confirmation is shown' },
  });
}

const successProposal = (overrides = {}): StoryTestProposal => ({
  proposalId: 'a1b2c3d4e5f60718293a4b5c6d7e8f90',
  index: 1,
  status: 'Succeeded',
  title: 'Guest checkout happy path',
  description: 'Guest places an order.',
  framework: 'playwright',
  platform: 'web',
  module: 'Checkout',
  priority: 'High',
  focusCriterion: 'Guest can place an order',
  focusCriterionIndex: 0,
  structuredSteps: [{ order: 1, action: 'navigate', target: 'https://example.test/checkout', value: null }],
  sourceCode: "import { test } from '@playwright/test';",
  assumptions: ['Checkout page exists.'],
  warnings: [],
  provider: 'stub',
  model: 'stub-1.0',
  promptVersion: 'story-to-tests-v1',
  latencyMs: 7,
  provenance: { source: 'story-ai', promptVersion: 'story-to-tests-v1' },
  errorCode: null,
  errorMessage: null,
  ...overrides,
});

const failedProposal: StoryTestProposal = {
  proposalId: 'b1b2c3d4e5f60718293a4b5c6d7e8f91',
  index: 2,
  status: 'Failed',
  title: null,
  description: null,
  framework: null,
  platform: null,
  module: null,
  priority: null,
  focusCriterion: 'Order confirmation is shown',
  focusCriterionIndex: 1,
  structuredSteps: [],
  sourceCode: null,
  assumptions: [],
  warnings: [],
  provider: null,
  model: null,
  promptVersion: 'story-to-tests-v1',
  latencyMs: 0,
  provenance: null,
  errorCode: 'PROVIDER_UNAVAILABLE',
  errorMessage: 'Provider down.',
};

const storyResult = (proposals: StoryTestProposal[]) => ({
  generationId: 'gen-1',
  promptVersion: 'story-to-tests-v1',
  proposalCount: proposals.length,
  successCount: proposals.filter((p) => (p as { status: string }).status === 'Succeeded').length,
  failureCount: proposals.filter((p) => (p as { status: string }).status !== 'Succeeded').length,
  proposals,
});

describe('StoryTestGeneratorPage', () => {
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

  afterEach(cleanup);

  it('renders the story form', () => {
    renderPage();
    expect(screen.getByLabelText('Story title')).toBeTruthy();
    expect(screen.getByLabelText('Story description')).toBeTruthy();
    expect(screen.getByLabelText('Acceptance criteria')).toBeTruthy();
    expect(screen.getByLabelText('Max proposals')).toBeTruthy();
    expect(screen.getByRole('button', { name: /generate proposals/i })).toBeTruthy();
  });

  it('requires title and acceptance criteria', async () => {
    renderPage();
    fireEvent.click(screen.getByRole('button', { name: /generate proposals/i }));
    expect(await screen.findByText('Story title is required.')).toBeTruthy();
    expect(screen.getByText('At least one acceptance criterion is required.')).toBeTruthy();
    expect(mockedStoryGenerate).not.toHaveBeenCalled();
  });

  it('shows a loading state while generating', async () => {
    mockedStoryGenerate.mockReturnValue(new Promise(() => {}));
    renderPage();
    fillValidStory();
    fireEvent.click(screen.getByRole('button', { name: /generate proposals/i }));
    expect(await screen.findByLabelText('Story generation in progress')).toBeTruthy();
  });

  it('renders proposals with focus, steps, and selection', async () => {
    mockedStoryGenerate.mockResolvedValue(storyResult([successProposal(), failedProposal]));
    renderPage();
    fillValidStory();
    fireEvent.click(screen.getByRole('button', { name: /generate proposals/i }));
    expect(await screen.findByText('Guest checkout happy path')).toBeTruthy();
    expect(screen.getByText('Guest can place an order')).toBeTruthy();
    expect(screen.getByText('navigate')).toBeTruthy();
    expect(screen.getByText('Generated proposal')).toBeTruthy();
    expect(screen.getByText('Provider down.')).toBeTruthy();
    expect(screen.getByLabelText('Select proposal 1')).toBeTruthy();
    expect(screen.queryByLabelText('Select proposal 2')).toBeNull();
  });

  it('supports selection and deselection with a live save count', async () => {
    mockedStoryGenerate.mockResolvedValue(
      storyResult([
        successProposal(),
        successProposal({ proposalId: 'c'.repeat(32), index: 2, title: 'Order confirmation check' }),
      ]),
    );
    renderPage();
    fillValidStory();
    fireEvent.click(screen.getByRole('button', { name: /generate proposals/i }));
    await screen.findByText('Order confirmation check');
    expect(screen.getByRole('button', { name: /save selected \(2\)/i })).toBeTruthy();
    fireEvent.click(screen.getByLabelText('Select proposal 1'));
    expect(screen.getByRole('button', { name: /save selected \(1\)/i })).toBeTruthy();
  });

  it('shows an empty state when nothing is generated', async () => {
    mockedStoryGenerate.mockResolvedValue(storyResult([]));
    renderPage();
    fillValidStory();
    fireEvent.click(screen.getByRole('button', { name: /generate proposals/i }));
    expect(await screen.findByText('No proposals were generated for this story.')).toBeTruthy();
  });

  it('saves selected proposals through TestCase creation as Pending', async () => {
    mockedStoryGenerate.mockResolvedValue(storyResult([successProposal()]));
    mockedCreate.mockResolvedValue({ id: 'case-1', testKey: 'AI-GUEST-ABCDEF' } as never);
    renderPage();
    fillValidStory();
    fireEvent.click(screen.getByRole('button', { name: /generate proposals/i }));
    await screen.findByText('Guest checkout happy path');
    fireEvent.click(screen.getByRole('button', { name: /save selected \(1\)/i }));
    await waitFor(() =>
      expect(mockedCreate).toHaveBeenCalledWith(
        'p1',
        expect.objectContaining({
          title: 'Guest checkout happy path',
          sourceType: 'ai',
          generationProvider: 'stub',
          generationRequest: { source: 'story-ai', promptVersion: 'story-to-tests-v1' },
        }),
      ),
    );
    expect(await screen.findByText(/View AI-GUEST-ABCDEF in repository/)).toBeTruthy();
    const link = screen.getByText(/View AI-GUEST-ABCDEF in repository/).closest('a');
    expect(link?.getAttribute('href')).toBe('/projects/p1/test-cases/case-1');
    expect(screen.getByText('Saved · Pending review')).toBeTruthy();
  });

  it('preserves successes when an individual save fails', async () => {
    mockedStoryGenerate.mockResolvedValue(
      storyResult([
        successProposal(),
        successProposal({ proposalId: 'd'.repeat(32), index: 2, title: 'Order confirmation check' }),
      ]),
    );
    mockedCreate
      .mockResolvedValueOnce({ id: 'case-1', testKey: 'AI-ONE' } as never)
      .mockRejectedValueOnce(new ApiError(500, 'ERROR', 'Save exploded.'));
    renderPage();
    fillValidStory();
    fireEvent.click(screen.getByRole('button', { name: /generate proposals/i }));
    await screen.findByText('Order confirmation check');
    fireEvent.click(screen.getByRole('button', { name: /save selected \(2\)/i }));
    expect(await screen.findByText(/View AI-ONE in repository/)).toBeTruthy();
    expect(await screen.findByText(/Save failed: Save exploded\./)).toBeTruthy();
  });

  it('prevents duplicate saves of already-saved proposals', async () => {
    mockedStoryGenerate.mockResolvedValue(storyResult([successProposal()]));
    mockedCreate.mockResolvedValue({ id: 'case-1', testKey: 'AI-ONE' } as never);
    renderPage();
    fillValidStory();
    fireEvent.click(screen.getByRole('button', { name: /generate proposals/i }));
    await screen.findByText('Guest checkout happy path');
    fireEvent.click(screen.getByRole('button', { name: /save selected \(1\)/i }));
    await screen.findByText(/View AI-ONE in repository/);
    // Saved proposals lose their checkbox and leave the save set.
    expect(screen.queryByLabelText('Select proposal 1')).toBeNull();
    expect(screen.queryByRole('button', { name: /save selected/i })).toBeNull();
    expect(mockedCreate).toHaveBeenCalledTimes(1);
  });

  it('blocks generation without testcases.manage', async () => {
    profileWith([Permissions.TestCasesRead]);
    renderPage();
    expect(await screen.findByText(/testcases.manage permission/)).toBeTruthy();
    fillValidStory();
    expect(screen.getByRole('button', { name: /generate proposals/i }).hasAttribute('disabled')).toBe(true);
    expect(mockedStoryGenerate).not.toHaveBeenCalled();
  });

  it('maps API errors to user-safe panels', async () => {
    mockedStoryGenerate.mockRejectedValue(new ApiError(429, 'RATE_LIMITED', 'Slow down.'));
    renderPage();
    fillValidStory();
    fireEvent.click(screen.getByRole('button', { name: /generate proposals/i }));
    expect(await screen.findByText('Generation rate limited')).toBeTruthy();
  });
});
