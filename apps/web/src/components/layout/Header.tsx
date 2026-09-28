import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Bell, LogOut, Menu, Play, User } from 'lucide-react';
import { Button } from '../ui/button';
import { Badge } from '../ui/badge';
import { useAppStore } from '../../stores/useAppStore';
import { useAuth } from '../../features/auth/AuthContext';
import { useProfile } from '../../lib/auth/useProfile';

/** White header with project context, run action, notifications, user menu (docs/02 §4). */
export function Header() {
  const toggleSidebar = useAppStore((s) => s.toggleSidebar);
  const currentProjectId = useAppStore((s) => s.currentProjectId);
  const { signOut } = useAuth();
  const navigate = useNavigate();
  const [menuOpen, setMenuOpen] = useState(false);

  const profile = useProfile();

  const displayName = profile.data?.displayName ?? profile.data?.email ?? 'User';
  const initials = displayName
    .split(/[\s@._-]+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0]?.toUpperCase() ?? '')
    .join('') || 'U';

  return (
    <header className="flex h-16 shrink-0 items-center gap-3 border-b border-slate-200 bg-white px-4">
      <Button variant="ghost" size="sm" onClick={toggleSidebar} aria-label="Toggle navigation">
        <Menu className="h-5 w-5" aria-hidden />
      </Button>
      <div className="hidden items-center gap-2 sm:flex">
        <span className="text-sm text-slate-500">Project</span>
        <span className="rounded-md border border-slate-200 bg-slate-50 px-3 py-1.5 font-mono text-xs text-slate-700">
          {currentProjectId ?? 'select-project'}
        </span>
      </div>
      <div className="flex-1" />
      <Button variant="success" size="sm">
        <Play className="h-4 w-4" aria-hidden />
        <span className="hidden sm:inline">Run</span>
      </Button>
      <Button variant="ghost" size="sm" aria-label="Notifications">
        <Bell className="h-5 w-5" aria-hidden />
      </Button>
      <div className="relative">
        <button
          type="button"
          onClick={() => setMenuOpen((v) => !v)}
          aria-label="User menu"
          aria-haspopup="menu"
          aria-expanded={menuOpen}
          className="flex h-8 w-8 items-center justify-center rounded-full bg-brand-100 text-xs font-semibold text-brand-700"
        >
          {initials}
        </button>
        {menuOpen && (
          <>
            <button
              type="button"
              aria-label="Close user menu"
              onClick={() => setMenuOpen(false)}
              className="fixed inset-0 z-40 cursor-default"
            />
            <div
              role="menu"
              className="absolute right-0 z-50 mt-2 w-64 rounded-lg border border-slate-200 bg-white p-2 shadow-lg"
            >
              <div className="px-3 py-2">
                <p className="truncate text-sm font-semibold text-slate-900">{displayName}</p>
                {profile.data?.email && (
                  <p className="truncate text-xs text-slate-500">{profile.data.email}</p>
                )}
                <div className="mt-2 flex flex-wrap gap-1">
                  {(profile.data?.roles ?? []).slice(0, 3).map((role) => (
                    <Badge key={role} tone="brand">
                      {role}
                    </Badge>
                  ))}
                </div>
                {profile.isError && (
                  <p className="mt-2 text-xs text-amber-600">
                    Profile unavailable — session may have expired.
                  </p>
                )}
              </div>
              <div className="border-t border-slate-100 pt-1">
                <button
                  type="button"
                  role="menuitem"
                  onClick={() => {
                    setMenuOpen(false);
                    navigate('/settings');
                  }}
                  className="flex w-full items-center gap-2 rounded-md px-3 py-2 text-sm text-slate-700 hover:bg-slate-50"
                >
                  <User className="h-4 w-4" aria-hidden />
                  Profile
                </button>
                <button
                  type="button"
                  role="menuitem"
                  onClick={() => {
                    setMenuOpen(false);
                    void signOut();
                  }}
                  className="flex w-full items-center gap-2 rounded-md px-3 py-2 text-sm text-slate-700 hover:bg-slate-50"
                >
                  <LogOut className="h-4 w-4" aria-hidden />
                  Logout
                </button>
              </div>
            </div>
          </>
        )}
      </div>
    </header>
  );
}
