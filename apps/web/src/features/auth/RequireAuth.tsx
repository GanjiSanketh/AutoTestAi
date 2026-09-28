import { Navigate, Outlet, useLocation } from 'react-router-dom';
import { useAuth } from './AuthContext';

/**
 * Route guard: UX-level only. Every API/SignalR call is independently
 * authorized server-side (docs/04 §10).
 */
export function RequireAuth() {
  const { isLoading, isAuthenticated } = useAuth();
  const location = useLocation();

  if (isLoading) {
    return (
      <div
        className="flex h-full items-center justify-center bg-slate-50"
        role="status"
        aria-label="Checking authentication"
      >
        <div className="flex flex-col items-center gap-3">
          <div className="h-8 w-8 animate-spin rounded-full border-2 border-brand-600 border-t-transparent" />
          <p className="text-sm text-slate-500">Checking authentication…</p>
        </div>
      </div>
    );
  }

  if (!isAuthenticated) {
    const from = location.pathname + location.search;
    return <Navigate to="/login" replace state={{ from }} />;
  }

  return <Outlet />;
}
