import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { ProjectForm } from './ProjectForm';
import { ApiError } from '../../lib/api/client';

function renderForm(serverError: ApiError | null = null, onSubmit = vi.fn()) {
  return {
    onSubmit,
    view: render(
      <ProjectForm
        submitLabel="Create project"
        submitting={false}
        serverError={serverError}
        onSubmit={onSubmit}
      />,
    ),
  };
}

describe('ProjectForm', () => {
  afterEach(() => {
    cleanup();
  });

  it('blocks submit and shows an error for an invalid key', () => {
    const { onSubmit } = renderForm();
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Shop' } });
    fireEvent.change(screen.getByLabelText('Key'), { target: { value: 'bad key!' } });
    fireEvent.click(screen.getByRole('button', { name: 'Create project' }));

    expect(screen.queryByText(/must start with a letter/i)).not.toBeNull();
    expect(onSubmit).not.toHaveBeenCalled();
  });

  it('submits normalized values when valid', () => {
    const { onSubmit } = renderForm();
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Shop' } });
    fireEvent.change(screen.getByLabelText('Key'), { target: { value: 'shop' } });
    fireEvent.change(screen.getByLabelText('Target URL'), {
      target: { value: 'https://shop.example.com' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Create project' }));

    expect(onSubmit).toHaveBeenCalledTimes(1);
    expect(onSubmit.mock.calls[0][0]).toMatchObject({
      name: 'Shop',
      key: 'SHOP',
      targetUrl: 'https://shop.example.com',
    });
  });

  it('surfaces server field errors without internals', () => {
    renderForm(
      new ApiError(400, 'VALIDATION_ERROR', 'One or more validation errors occurred.', undefined, [
        { field: 'key', message: "Project key 'SHOP' is already in use." },
      ]),
    );
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Shop' } });
    fireEvent.change(screen.getByLabelText('Key'), { target: { value: 'SHOP' } });
    // Submit with a valid client payload so the server error stays visible.
    fireEvent.click(screen.getByRole('button', { name: 'Create project' }));
    expect(screen.queryByText(/already in use/i)).not.toBeNull();
  });

  it('disables the key field in edit mode with an immutability note', () => {
    render(
      <ProjectForm
        initial={{
          id: 'p1',
          name: 'Shop',
          key: 'SHOP',
          description: null,
          repositoryUrl: null,
          targetUrl: null,
          framework: null,
          platform: null,
          status: 'Active',
          defaultEnvironmentId: null,
          defaultEnvironment: null,
          memberCount: 1,
          createdBy: null,
          createdAt: '',
          updatedAt: '',
        }}
        submitLabel="Save changes"
        submitting={false}
        serverError={null}
        onSubmit={vi.fn()}
      />,
    );
    expect((screen.getByLabelText('Key') as HTMLInputElement).disabled).toBe(true);
    expect(screen.queryByText(/immutable after creation/i)).not.toBeNull();
  });
});
