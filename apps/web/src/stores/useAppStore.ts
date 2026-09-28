import { create } from 'zustand';

interface AppState {
  /** Currently selected project context (header selector, Phase-1 backed). */
  currentProjectId: string | null;
  sidebarOpen: boolean;
  setCurrentProjectId: (id: string | null) => void;
  toggleSidebar: () => void;
  setSidebarOpen: (open: boolean) => void;
}

/**
 * Client/application state only. Server state belongs to TanStack Query
 * (docs/04 §3) — never cache API entities here.
 */
export const useAppStore = create<AppState>((set) => ({
  currentProjectId: null,
  sidebarOpen: true,
  setCurrentProjectId: (id) => set({ currentProjectId: id }),
  toggleSidebar: () => set((s) => ({ sidebarOpen: !s.sidebarOpen })),
  setSidebarOpen: (open) => set({ sidebarOpen: open }),
}));
