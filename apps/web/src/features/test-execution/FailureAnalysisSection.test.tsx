import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { FailureAnalysisSection } from './FailureAnalysisSection';
import { failureAnalysisEndpoints } from '../../lib/api/endpoints/failureAnalysis';
import { defectEndpoints } from '../../lib/api/endpoints/defects';
import { ApiError } from '../../lib/api/client';

vi.mock('../../lib/api/endpoints/failureAnalysis', () => ({
  failureAnalysisKeys: {
    all: ['failure-analysis'],
    latest: (p: string, e: string) => ['failure-analysis', 'latest', p, e],
    attempts: (p: string, e: string) => ['failure-analysis', 'attempts', p, e],
  },
  failureAnalysisEndpoints: {
    analyze: vi.fn(),
    latest: vi.fn(),
  },
}));

vi.mock('../../lib/api/endpoints/defects', () => ({
  defectEndpoints: { create: vi.fn() },
}));

const mockedAnalyze = vi.mocked(failureAnalysisEndpoints.analyze);
const mockedLatest = vi.mocked(failureAnalysisEndpoints.latest);
const mockedCreate = vi.mocked(defectEndpoints.create);

const analysis = {
  id: 'a1',
  executionId: 'e1',
  executionTestId: 't1',
  attempt: 1,
  status: 'Completed',
  classification: 'ApplicationDefect',
  summary: 'Login returned HTTP 500.',
  probableCause: 'The staging backend rejects valid credentials.',
  confidence: 0.8,
  evidence: ['HTTP 500 on POST /login'],
  assumptions: ['Staging was deployed.'],
  warnings: ['Only one log line was available.'],
  recommendedAction: 'Check application logs.',
  isLikelyDefect: true,
  provider: 'stub',
  model: 'stub-1.0',
  promptVersion: 'failure-analysis-v1',
  latencyMs: 12,
  inputTokens: null,
  outputTokens: null,
  totalTokens: null,
  createdAt: '2026-09-28T10:05:00Z',
};

function renderSection(props = {}) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const onDefectCreated = vi.fn();
  const view = render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <FailureAnalysisSection
          projectId="p1"
          executionId="e1"
          executionClassification="ApplicationDefect"
          failedStepSummary="step 1 (navigate) Failed"
          errorMessage="HTTP 500"
          testKey="LOGIN-001"
          canAnalyze
          canCreateDefect
          onDefectCreated={onDefectCreated}
          {...props}
        />
      </MemoryRouter>
    </QueryClientProvider>,
  );
  return { onDefectCreated, ...view };
}

describe('FailureAnalysisSection', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  afterEach(() => {
    cleanup();
  });

  it('shows the advisory result with classification, confidence, and provenance', async () => {
    mockedLatest.mockResolvedValue(analysis);
    renderSection();

    expect(await screen.findByText('Login returned HTTP 500.')).toBeTruthy();
    expect(screen.getByText('AI advisory')).toBeTruthy();
    expect(screen.getByText('The staging backend rejects valid credentials.')).toBeTruthy();
    expect(screen.getByText('HTTP 500 on POST /login')).toBeTruthy();
    expect(screen.getByText('Staging was deployed.')).toBeTruthy();
    expect(screen.getByText('Only one log line was available.')).toBeTruthy();
    expect(screen.getByLabelText('AI confidence 80 percent')).toBeTruthy();
    expect(screen.getByText(/failure-analysis-v1/)).toBeTruthy();
  });

  it('flags disagreement with the deterministic classification', async () => {
    mockedLatest.mockResolvedValue({ ...analysis, classification: 'TestFailure' });
    renderSection({ executionClassification: 'ApplicationDefect' });

    expect(await screen.findByText(/Differs from the deterministic/)).toBeTruthy();
  });

  it('runs analysis on demand and surfaces provider errors with retry', async () => {
    mockedLatest.mockRejectedValue(new ApiError(404, 'NOT_FOUND', 'Missing'));
    mockedAnalyze.mockRejectedValueOnce(
      new ApiError(503, 'PROVIDER_UNAVAILABLE', 'Ollama is unreachable.'),
    );
    renderSection();

    expect(await screen.findByText(/No analysis yet/)).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /analyze failure/i }));
    expect(await screen.findByText(/Analysis failed/)).toBeTruthy();

    mockedAnalyze.mockResolvedValueOnce(analysis);
    mockedLatest.mockResolvedValue(analysis);
    fireEvent.click(screen.getByRole('button', { name: /retry analysis/i }));
    await waitFor(() => expect(mockedAnalyze).toHaveBeenCalledTimes(2));
    expect(await screen.findByText('Login returned HTTP 500.')).toBeTruthy();
  });

  it('disables the action without the analyze permission', async () => {
    mockedLatest.mockResolvedValue(analysis);
    renderSection({ canAnalyze: false });

    expect(await screen.findByText('Login returned HTTP 500.')).toBeTruthy();
    expect(screen.queryByRole('button', { name: /analyze failure|retry analysis/i })).toBeNull();
    expect(screen.getByText(/unavailable for your role/)).toBeTruthy();
  });

  it('creates a defect explicitly from the analysis', async () => {
    mockedLatest.mockResolvedValue(analysis);
    mockedCreate.mockResolvedValue({ id: 'd1' } as never);
    const { onDefectCreated } = renderSection();

    fireEvent.click(await screen.findByRole('button', { name: /create defect/i }));
    expect(await screen.findByRole('dialog', { name: /create defect/i })).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: /^file defect$/i }));

    await waitFor(() => expect(mockedCreate).toHaveBeenCalledWith(
      'p1',
      expect.objectContaining({
        executionId: 'e1',
        failureAnalysisId: 'a1',
        severity: 'Medium',
      }),
    ));
    await waitFor(() => expect(onDefectCreated).toHaveBeenCalledWith('d1'));
  });

  it('validates the defect title client-side', async () => {
    mockedLatest.mockResolvedValue(analysis);
    renderSection();

    fireEvent.click(await screen.findByRole('button', { name: /create defect/i }));
    fireEvent.change(screen.getByLabelText('Title'), { target: { value: '   ' } });
    fireEvent.click(screen.getByRole('button', { name: /^file defect$/i }));

    expect(await screen.findByText('Title is required.')).toBeTruthy();
    expect(mockedCreate).not.toHaveBeenCalled();
  });
});
