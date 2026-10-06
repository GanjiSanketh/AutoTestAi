import { Permissions, type Permission } from './permissions';

export interface NavItem {
  to: string;
  label: string;
  permission: Permission;
  group: 'modules' | 'system';
}

/** Navigation model (docs/02 §7). Visibility is UX-only; backend enforces access. */
export const NAV_ITEMS: NavItem[] = [
  { to: '/dashboard', label: 'Dashboard', permission: Permissions.DashboardRead, group: 'modules' },
  { to: '/test-cases', label: 'Test Cases', permission: Permissions.TestCasesRead, group: 'modules' },
  { to: '/projects', label: 'Projects', permission: Permissions.ProjectsRead, group: 'modules' },
  { to: '/test-execution', label: 'Test Execution', permission: Permissions.ExecutionsRead, group: 'modules' },
  { to: '/bugs', label: 'Bugs', permission: Permissions.BugsRead, group: 'modules' },
  { to: '/tickets', label: 'Tickets', permission: Permissions.TicketsRead, group: 'modules' },
  { to: '/reports', label: 'Reports', permission: Permissions.ReportsRead, group: 'modules' },
  { to: '/maintenance', label: 'Maintenance', permission: Permissions.TestCasesRead, group: 'modules' },
  { to: '/settings', label: 'Settings', permission: Permissions.SettingsManage, group: 'system' },
];

/** Pure filter — unit-tested. Lacking permission hides the item (never grants). */
export function filterNavItems(
  items: readonly NavItem[],
  permissions: readonly string[] | undefined,
): NavItem[] {
  if (!permissions) return [];
  return items.filter((item) => permissions.includes(item.permission));
}
