import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from 'react';
import type { User } from 'oidc-client-ts';
import { userManager, type SignInState } from '../../lib/auth/oidcClient';

interface AuthContextValue {
  isLoading: boolean;
  isAuthenticated: boolean;
  user: User | null;
  error: string | null;
  signIn: (returnUrl?: string) => Promise<void>;
  signOut: () => Promise<void>;
}

export const AuthContext = createContext<AuthContextValue | null>(null);

const RETURN_URL_KEY = 'autotestai.return_url';

/**
 * Centralized authentication state. The OIDC UserManager session is
 * authoritative — this context only mirrors it for rendering.
 */
export function AuthProvider({ children }: { children: ReactNode }) {
  const [isLoading, setIsLoading] = useState(true);
  const [user, setUser] = useState<User | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    userManager
      .getUser()
      .then((loaded) => {
        if (!cancelled) setUser(loaded && !loaded.expired ? loaded : null);
      })
      .catch(() => {
        if (!cancelled) setError('Could not restore the session. Please sign in again.');
      })
      .finally(() => {
        if (!cancelled) setIsLoading(false);
      });

    const onLoaded = (loaded: User) => setUser(loaded);
    const onUnloaded = () => setUser(null);
    const onRenewError = () => setError('Session renewal failed. Please sign in again.');
    userManager.events.addUserLoaded(onLoaded);
    userManager.events.addUserUnloaded(onUnloaded);
    userManager.events.addSilentRenewError(onRenewError);
    return () => {
      cancelled = true;
      userManager.events.removeUserLoaded(onLoaded);
      userManager.events.removeUserUnloaded(onUnloaded);
      userManager.events.removeSilentRenewError(onRenewError);
    };
  }, []);

  const signIn = useCallback(async (returnUrl?: string) => {
    setError(null);
    if (returnUrl) sessionStorage.setItem(RETURN_URL_KEY, returnUrl);
    const state: SignInState = returnUrl ? { returnUrl } : {};
    await userManager.signinRedirect({ state });
  }, []);

  const signOut = useCallback(async () => {
    setError(null);
    try {
      await userManager.signoutRedirect();
    } catch {
      // Identity provider unreachable: clear the local session at minimum.
      await userManager.removeUser();
      setUser(null);
      window.location.assign('/login');
    }
  }, []);

  const value = useMemo<AuthContextValue>(
    () => ({
      isLoading,
      isAuthenticated: user !== null,
      user,
      error,
      signIn,
      signOut,
    }),
    [isLoading, user, error, signIn, signOut],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used within AuthProvider.');
  return ctx;
}

export function takeReturnUrl(): string {
  const url = sessionStorage.getItem(RETURN_URL_KEY);
  sessionStorage.removeItem(RETURN_URL_KEY);
  return url && url.startsWith('/') ? url : '/dashboard';
}
