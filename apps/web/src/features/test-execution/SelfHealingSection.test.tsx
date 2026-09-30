import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SelfHealingSection } from './SelfHealingSection';
import { selfHealingEndpoints } from '../../lib/api/endpoints/selfHealing';

vi.mock('../../lib/api/endpoints/selfHealing', () => ({
  selfHealingKeys: {
    all: ['self-healing'],
    attempts: (p: string, e: string) => ['self-healing', 'attempts', p, e],
  },
  selfHealingEndpoints: {
    listAttempts: vi.fn(),
  },
}));

const mockedList = vi.mocked(selfHealingEndpoints.listAttempts);

function renderSection() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <SelfHealingSection projectId="p1" executionId="e1" active={false} />
    </QueryClientProvider>,
  );
}

describe('SelfHealingSection (Slice 11)', () => {
  it('stays silent when no healing was attempted', async () => {
    mockedList.mockResolvedValue([]);
    renderSection();
    // Loading shows a skeleton title; wait until the settled empty state removes it.
    await vi.waitFor(() => expect(screen.queryByText(/self-healing activity/i)).toBeNull());
  });

  it('shows recovered and failed attempts with AI distinction', async () => {
    mockedList.mockResolvedValue([
      {
        id: 'h1',
        executionId: 'e1',
        stepOrder: 4,
        stepAction: 'click',
        originalStrategy: 'css',
        originalValue: '#old-submit',
        recoveredStrategy: 'testid',
        recoveredValue: 'submit-btn',
        healingStrategy: 'TestAttribute',
        status: 'Applied',
        candidateCount: 1,
        wasApplied: true,
        isAiAssisted: false,
        createdAt: '2026-09-30T10:00:00Z',
      },
      {
        id: 'h2',
        executionId: 'e1',
        stepOrder: 5,
        stepAction: 'fill',
        originalStrategy: 'css',
        originalValue: '#old-name',
        recoveredStrategy: null,
        recoveredValue: null,
        healingStrategy: 'None',
        status: 'Failed',
        candidateCount: 2,
        wasApplied: false,
        isAiAssisted: true,
        createdAt: '2026-09-30T10:01:00Z',
      },
    ]);
    renderSection();
    expect(await screen.findByText(/step recovered using self-healing/i)).toBeTruthy();
    expect(await screen.findByText(/could not safely recover/i)).toBeTruthy();
    expect(await screen.findByText(/ai-assisted/i)).toBeTruthy();
    expect((await screen.findByText(/recovered locator:/i)).textContent).toContain('submit-btn');
  });
});
