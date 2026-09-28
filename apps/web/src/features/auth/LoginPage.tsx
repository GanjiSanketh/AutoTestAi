import { useState } from 'react';
import { Navigate, useLocation } from 'react-router-dom';
import { ShieldCheck, Zap } from 'lucide-react';
import { Button } from '../../components/ui/button';
import { useAuth } from './AuthContext';

/**
 * Dark authentication experience (docs/02 §5). Sign-in is delegated to
 * Keycloak via OIDC — no local password form, no fake authentication.
 */
export function LoginPage() {
  const { isLoading, isAuthenticated, error: authError, signIn } = useAuth();
  const location = useLocation();
  const [signingIn, setSigningIn] = useState(false);
  const [error, setError] = useState<string | null>(
    (location.state as { from?: string; error?: string } | null)?.error ?? null,
  );

  const from =
    (location.state as { from?: string } | null)?.from &&
    (location.state as { from?: string }).from!.startsWith('/')
      ? (location.state as { from?: string }).from!
      : '/dashboard';

  if (!isLoading && isAuthenticated) {
    return <Navigate to={from} replace />;
  }

  const handleSignIn = async () => {
    setError(null);
    setSigningIn(true);
    try {
      await signIn(from);
    } catch {
      setError('Could not reach the sign-in service. Check your connection and try again.');
      setSigningIn(false);
    }
  };

  return (
    <div className="relative flex min-h-full items-center justify-center overflow-hidden bg-slate-950 p-4">
      <div
        aria-hidden
        className="pointer-events-none absolute -left-32 -top-32 h-96 w-96 rounded-full bg-brand-600/30 blur-3xl"
      />
      <div
        aria-hidden
        className="pointer-events-none absolute -bottom-32 -right-32 h-96 w-96 rounded-full bg-purple-600/20 blur-3xl"
      />
      <div className="relative grid w-full max-w-4xl overflow-hidden rounded-2xl border border-white/10 bg-white/5 backdrop-blur-xl md:grid-cols-2">
        <div className="hidden flex-col justify-center p-10 md:flex">
          <div className="flex items-center gap-2">
            <span className="flex h-10 w-10 items-center justify-center rounded-xl bg-brand-600">
              <Zap className="h-5 w-5 text-white" aria-hidden />
            </span>
            <div>
              <p className="font-semibold text-white">AutoTest AI</p>
              <p className="text-xs text-slate-400">Smart Quality Suite</p>
            </div>
          </div>
          <h1 className="mt-8 text-2xl font-semibold text-white">
            AI-driven automated testing platform
          </h1>
          <ul className="mt-6 space-y-3 text-sm text-slate-300">
            <li>Zero-manual setup test generation</li>
            <li>Self-healing locators</li>
            <li>Automated Jira bug tracking</li>
          </ul>
        </div>
        <div className="bg-white p-8 md:p-10">
          <h2 className="text-lg font-semibold text-slate-900">Sign in</h2>
          <p className="mt-1 text-sm text-slate-500">
            Continue with your organization's single sign-on.
          </p>
          {(error ?? authError) && (
            <div
              role="alert"
              className="mt-4 rounded-md border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700"
            >
              {error ?? authError}
            </div>
          )}
          <div className="mt-6 space-y-3">
            <Button
              type="button"
              className="w-full"
              disabled={isLoading || signingIn}
              onClick={handleSignIn}
            >
              <ShieldCheck className="h-4 w-4" aria-hidden />
              {signingIn ? 'Redirecting…' : 'Sign in with SSO'}
            </Button>
            <p className="text-center text-xs text-slate-400">
              You will be redirected to the secure identity provider to complete sign-in.
            </p>
          </div>
        </div>
      </div>
    </div>
  );
}
