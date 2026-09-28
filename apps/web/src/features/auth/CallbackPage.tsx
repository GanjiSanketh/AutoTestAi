import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { userManager } from '../../lib/auth/oidcClient';
import { takeReturnUrl } from './AuthContext';

/** OIDC redirect target: completes sign-in, then returns to the saved route. */
export function CallbackPage() {
  const navigate = useNavigate();
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    userManager
      .signinRedirectCallback()
      .then(() => {
        if (!cancelled) navigate(takeReturnUrl(), { replace: true });
      })
      .catch(() => {
        if (!cancelled) {
          setError('Sign-in failed. The request may have expired — please try again.');
        }
      });
    return () => {
      cancelled = true;
    };
  }, [navigate]);

  if (error) {
    return (
      <div className="flex h-full items-center justify-center bg-slate-950 p-4">
        <div className="max-w-sm rounded-xl border border-white/10 bg-white/5 p-8 text-center backdrop-blur-xl">
          <h1 className="text-lg font-semibold text-white">Sign-in failed</h1>
          <p className="mt-2 text-sm text-slate-300">{error}</p>
          <button
            type="button"
            onClick={() => navigate('/login', { replace: true })}
            className="mt-6 w-full rounded-md bg-brand-600 px-4 py-2 text-sm font-medium text-white hover:bg-brand-700"
          >
            Back to sign in
          </button>
        </div>
      </div>
    );
  }

  return (
    <div
      className="flex h-full items-center justify-center bg-slate-950"
      role="status"
      aria-label="Completing sign-in"
    >
      <div className="flex flex-col items-center gap-3">
        <div className="h-8 w-8 animate-spin rounded-full border-2 border-brand-500 border-t-transparent" />
        <p className="text-sm text-slate-300">Completing sign-in…</p>
      </div>
    </div>
  );
}
