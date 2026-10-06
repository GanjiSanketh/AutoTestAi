import { NavLink, useLocation } from 'react-router-dom';
import { useEffect } from 'react';
import {
  LayoutDashboard,
  FlaskConical,
  FolderKanban,
  PlayCircle,
  Bug,
  Ticket,
  BarChart3,
  Settings,
  Wrench,
  Zap,
  X,
  type LucideIcon,
} from 'lucide-react';
import { cn } from '../ui/cn';
import { useAppStore } from '../../stores/useAppStore';
import { NAV_ITEMS, filterNavItems } from '../../lib/auth/navigation';
import { useProfile } from '../../lib/auth/useProfile';

const ICONS: Record<string, LucideIcon> = {
  '/dashboard': LayoutDashboard,
  '/test-cases': FlaskConical,
  '/projects': FolderKanban,
  '/test-execution': PlayCircle,
  '/bugs': Bug,
  '/tickets': Ticket,
  '/reports': BarChart3,
  '/maintenance': Wrench,
  '/settings': Settings,
};

/**
 * Dark enterprise sidebar (docs/02 §4): dark slate, muted text,
 * indigo active state, module/system grouping.
 * Navigation is permission-aware (UX only — backend enforces access).
 * Desktop: persistent/collapsible. Mobile: overlay drawer.
 */
export function Sidebar() {
  const sidebarOpen = useAppStore((s) => s.sidebarOpen);
  const setSidebarOpen = useAppStore((s) => s.setSidebarOpen);
  const location = useLocation();

  const profile = useProfile();

  // Close the mobile drawer on navigation.
  useEffect(() => {
    if (window.innerWidth < 768) setSidebarOpen(false);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [location.pathname]);

  if (!sidebarOpen) return null;

  const visible = filterNavItems(NAV_ITEMS, profile.data?.permissions);
  const modules = visible.filter((i) => i.group === 'modules');
  const system = visible.filter((i) => i.group === 'system');

  const linkClass = ({ isActive }: { isActive: boolean }) =>
    cn(
      'flex items-center gap-3 rounded-md px-3 py-2 text-sm font-medium transition-colors',
      isActive
        ? 'bg-brand-600 text-white'
        : 'text-slate-400 hover:bg-slate-800 hover:text-white',
    );

  const nav = (
    <>
      <div className="flex items-center gap-2 px-5 pb-5 pt-6">
        <span className="flex h-9 w-9 items-center justify-center rounded-lg bg-brand-600">
          <Zap className="h-5 w-5 text-white" aria-hidden />
        </span>
        <div className="flex-1">
          <p className="text-sm font-semibold text-white">AutoTest AI</p>
          <p className="text-xs text-slate-400">Smart Quality Suite</p>
        </div>
        <button
          type="button"
          aria-label="Close navigation"
          onClick={() => setSidebarOpen(false)}
          className="rounded-md p-1 text-slate-400 hover:bg-slate-800 hover:text-white md:hidden"
        >
          <X className="h-5 w-5" aria-hidden />
        </button>
      </div>
      <nav className="flex-1 space-y-6 overflow-y-auto px-3" aria-label="Primary">
        {profile.isLoading ? (
          <div className="space-y-2 px-3" aria-label="Loading navigation">
            {[0, 1, 2, 3].map((i) => (
              <div key={i} className="h-9 animate-pulse rounded-md bg-slate-800" />
            ))}
          </div>
        ) : (
          <>
            <div>
              <p className="px-3 pb-2 text-xs font-semibold uppercase tracking-wider text-slate-500">
                Modules
              </p>
              <div className="space-y-1">
                {modules.map(({ to, label }) => {
                  const Icon = ICONS[to] ?? LayoutDashboard;
                  return (
                    <NavLink key={to} to={to} className={linkClass}>
                      <Icon className="h-4 w-4" aria-hidden />
                      {label}
                    </NavLink>
                  );
                })}
              </div>
            </div>
            {system.length > 0 && (
              <div>
                <p className="px-3 pb-2 text-xs font-semibold uppercase tracking-wider text-slate-500">
                  System
                </p>
                <div className="space-y-1">
                  {system.map(({ to, label }) => {
                    const Icon = ICONS[to] ?? Settings;
                    return (
                      <NavLink key={to} to={to} className={linkClass}>
                        <Icon className="h-4 w-4" aria-hidden />
                        {label}
                      </NavLink>
                    );
                  })}
                </div>
              </div>
            )}
          </>
        )}
      </nav>
      <div className="px-5 py-4 text-xs text-slate-500">Slice 1 · auth + shell</div>
    </>
  );

  return (
    <>
      {/* Desktop persistent sidebar */}
      <aside className="hidden w-64 shrink-0 flex-col bg-slate-900 md:flex">{nav}</aside>
      {/* Mobile overlay drawer */}
      <div className="fixed inset-0 z-40 md:hidden" role="dialog" aria-label="Navigation">
        <button
          type="button"
          aria-label="Close navigation"
          onClick={() => setSidebarOpen(false)}
          className="absolute inset-0 bg-slate-950/60"
        />
        <aside className="absolute inset-y-0 left-0 flex w-64 flex-col bg-slate-900">
          {nav}
        </aside>
      </div>
    </>
  );
}
