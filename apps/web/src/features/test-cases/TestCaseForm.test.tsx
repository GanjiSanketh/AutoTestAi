import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { TestCaseForm } from './TestCaseForm';
import { ApiError } from '../../lib/api/client';

vi.mock('@monaco-editor/react', () => ({
  default: ({
    value,
    onChange,
  }: {
    value: string;
    onChange?: (value: string) => void;
  }) => (
    <textarea
      aria-label="monaco-stub"
      value={value}
      onChange={(e) => onChange?.(e.target.value)}
    />
  ),
}));

function renderForm(serverError: ApiError | null = null, onSubmit = vi.fn(), initial = undefined) {
  return {
    onSubmit,
    view: render(
      <TestCaseForm
        initial={initial}
        submitLabel="Create test case"
        submitting={false}
        serverError={serverError}
        onSubmit={onSubmit}
      />,
    ),
  };
}

describe('TestCaseForm', () => {
  afterEach(() => {
    cleanup();
  });

  it('blocks submit and shows an error for an invalid key', () => {
    const { onSubmit } = renderForm();
    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Login' } });
    fireEvent.change(screen.getByLabelText('Test key'), { target: { value: 'bad key!' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create test case' }));

    expect(screen.queryByText(/must start with a letter/i)).not.toBeNull();
    expect(onSubmit).not.toHaveBeenCalled();
  });

  it('submits normalized values when valid', () => {
    const { onSubmit } = renderForm();
    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Login' } });
    fireEvent.change(screen.getByLabelText('Test key'), { target: { value: 'login-001' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create test case' }));

    expect(onSubmit).toHaveBeenCalledTimes(1);
    expect(onSubmit.mock.calls[0][0]).toMatchObject({
      title: 'Login',
      testKey: 'LOGIN-001',
    });
  });

  it('disables the key field in edit mode with an immutability note', () => {
    renderForm(null, vi.fn(), {
      id: 'c1',
      projectId: 'p1',
      testKey: 'LOGIN-001',
      title: 'Login',
      description: null,
      module: null,
      framework: 'playwright',
      platform: 'web',
      priority: 'High',
      status: 'Draft',
      sourceType: 'manual',
      latestVersionNumber: 1,
      latestReviewStatus: 'Pending',
      createdBy: null,
      createdAt: '',
      updatedAt: '',
    } as never);
    expect((screen.getByLabelText('Test key') as HTMLInputElement).disabled).toBe(true);
    expect(screen.queryByText(/immutable after creation/i)).not.toBeNull();
  });

  it('supports adding and removing structured steps', () => {
    renderForm();
    fireEvent.click(screen.getByRole('button', { name: 'Add step' }));
    expect(screen.queryByLabelText('Step 1 action')).not.toBeNull();
    fireEvent.change(screen.getByLabelText('Step 1 action'), { target: { value: 'click' } });
    fireEvent.click(screen.getByRole('button', { name: 'Remove step 1' }));
    expect(screen.queryByLabelText('Step 1 action')).toBeNull();
  });

  it('surfaces server field errors without internals', () => {
    const { onSubmit } = renderForm(
      new ApiError(400, 'VALIDATION_ERROR', 'One or more validation errors occurred.', undefined, [
        { field: 'testKey', message: "Test key 'LOGIN-001' is already in use in this project." },
      ]),
    );
    fireEvent.change(screen.getByLabelText('Title'), { target: { value: 'Login' } });
    fireEvent.change(screen.getByLabelText('Test key'), { target: { value: 'LOGIN-001' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create test case' }));
    expect(screen.queryByText(/already in use/i)).not.toBeNull();
    expect(onSubmit).toHaveBeenCalledTimes(1);
  });
});
