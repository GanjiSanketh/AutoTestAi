import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { AuthContext } from './AuthContext';
import { LoginPage } from './LoginPage';

describe('LoginPage', () => {
  it('offers SSO sign-in and delegates to the identity provider', () => {
    const signIn = vi.fn();
    render(
      <AuthContext.Provider
        value={{
          isLoading: false,
          isAuthenticated: false,
          user: null,
          error: null,
          signIn,
          signOut: vi.fn(),
        }}
      >
        <MemoryRouter initialEntries={['/login']}>
          <Routes>
            <Route path="/login" element={<LoginPage />} />
            <Route path="/dashboard" element={<div>Dashboard</div>} />
          </Routes>
        </MemoryRouter>
      </AuthContext.Provider>,
    );

    const button = screen.getByRole('button', { name: /sign in with sso/i });
    fireEvent.click(button);
    expect(signIn).toHaveBeenCalledWith('/dashboard');
  });

  it('shows authentication errors without raw exceptions', () => {
    render(
      <AuthContext.Provider
        value={{
          isLoading: false,
          isAuthenticated: false,
          user: null,
          error: 'Session renewal failed. Please sign in again.',
          signIn: vi.fn(),
          signOut: vi.fn(),
        }}
      >
        <MemoryRouter initialEntries={['/login']}>
          <Routes>
            <Route path="/login" element={<LoginPage />} />
          </Routes>
        </MemoryRouter>
      </AuthContext.Provider>,
    );

    expect(screen.getByRole('alert').textContent).toContain('sign in again');
  });
});
