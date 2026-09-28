import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { VersionHistory } from './VersionHistory';
import type { TestCaseVersion } from '../../lib/api/endpoints/testcases';

const versions: TestCaseVersion[] = [
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
    generationProvider: 'openai',
    generationModel: 'gpt-x',
    generationLatencyMs: 1200,
    reviewStatus: 'Approved',
    createdBy: null,
    createdAt: '2026-09-28T00:00:00Z',
  },
];

describe('VersionHistory', () => {
  afterEach(() => {
    cleanup();
  });

  it('marks the newest version current and shows review states', () => {
    render(<VersionHistory versions={versions} selectedId={null} onSelect={() => {}} />);
    expect(screen.queryByText('Current')).not.toBeNull();
    expect(screen.queryByText('Pending')).not.toBeNull();
    expect(screen.queryByText('Approved')).not.toBeNull();
    expect(screen.queryByText(/openai\/gpt-x/)).not.toBeNull();
  });

  it('notifies selection of a historical version', () => {
    const onSelect = vi.fn();
    render(<VersionHistory versions={versions} selectedId={null} onSelect={onSelect} />);
    fireEvent.click(screen.getByRole('button', { name: /v1/ }));
    expect(onSelect).toHaveBeenCalledWith('v1');
  });
});
