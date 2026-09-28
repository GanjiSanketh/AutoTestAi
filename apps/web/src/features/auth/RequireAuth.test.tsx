import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { AuthContext } from './AuthContext';
import { RequireAuth } from './RequireAuth';

function stubAuth(overrides: object = {}) {
  return {
    isLoading: false,
    isAuthenticated: false,
    user: null,
    error: null,
    signIn: vi.fn(),
    signOut: vi.fn(),
    ...overrides,
  };
}

function renderShell(authValue: object, initialEntries = ['/dashboard']) {
  return render(
    <AuthContext.Provider value={stubAuth(authValue)}>
      <MemoryRouter initialEntries={initialEntries}>
        <Routes>
          <Route path="/login" element={<div>Login Page</div>} />
          <Route element={<RequireAuth />}>
            <Route path="/dashboard" element={<div>Protected Content</div>} />
          </Route>
        </Routes>
      </MemoryRouter>
    </AuthContext.Provider>,
  );
}

describe('RequireAuth', () => {
  it('shows a loading state while authentication resolves', () => {
    renderShell({ isLoading: true });
    expect(screen.getByRole('status').textContent).toContain('Checking authentication');
  });

  it('redirects unauthenticated users to login', () => {
    renderShell({ isLoading: false, isAuthenticated: false });
    expect(screen.queryByText('Login Page')).not.toBeNull();
    expect(screen.queryByText('Protected Content')).toBeNull();
  });

  it('renders protected content for authenticated users', () => {
    renderShell({ isLoading: false, isAuthenticated: true, user: {} as never });
    expect(screen.queryByText('Protected Content')).not.toBeNull();
  });
});
