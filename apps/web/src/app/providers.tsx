import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ReactNode, useEffect, useState } from 'react';
import { AuthProvider } from '../features/auth/AuthContext';
import { setUnauthorizedHandler } from '../lib/api/client';
import { userManager } from '../lib/auth/oidcClient';

/**
 * Registers the global expired-session handler once: an authenticated client
 * receiving 401 re-initiates sign-in (single redirect, loop-guarded).
 */
function UnauthorizedBridge({ children }: { children: ReactNode }) {
  useEffect(() => {
    let redirecting = false;
    setUnauthorizedHandler((_status, _path) => {
      if (redirecting) return;
      if (window.location.pathname.startsWith('/login')) return;
      redirecting = true;
      void userManager.getUser().then((user) => {
        if (user) {
          void userManager.signinRedirect();
        } else {
          window.location.assign('/login');
        }
      });
    });
    return () => setUnauthorizedHandler(null);
  }, []);
  return <>{children}</>;
}

export function AppProviders({ children }: { children: ReactNode }) {
  // TanStack Query owns ALL server state (docs/04 §3).
  const [queryClient] = useState(
    () =>
      new QueryClient({
        defaultOptions: {
          queries: {
            staleTime: 30_000,
            retry: 1,
            refetchOnWindowFocus: false,
          },
        },
      }),
  );
  return (
    <QueryClientProvider client={queryClient}>
      <AuthProvider>
        <UnauthorizedBridge>{children}</UnauthorizedBridge>
      </AuthProvider>
    </QueryClientProvider>
  );
}
