import { describe, expect, it } from 'vitest';
import { NAV_ITEMS, filterNavItems } from './navigation';
import { Permissions } from './permissions';

const ALL = Object.values(Permissions);
const VIEWER = [
  Permissions.DashboardRead,
  Permissions.ProjectsRead,
  Permissions.TestCasesRead,
  Permissions.ExecutionsRead,
  Permissions.BugsRead,
  Permissions.TicketsRead,
  Permissions.ReportsRead,
];

describe('filterNavItems', () => {
  it('shows every item for a fully-permissioned caller', () => {
    expect(filterNavItems(NAV_ITEMS, ALL)).toHaveLength(NAV_ITEMS.length);
  });

  it('hides settings without settings.manage', () => {
    const visible = filterNavItems(NAV_ITEMS, VIEWER).map((i) => i.to);
    expect(visible).not.toContain('/settings');
    expect(visible).toContain('/dashboard');
  });

  it('hides everything when permissions are unknown', () => {
    expect(filterNavItems(NAV_ITEMS, undefined)).toHaveLength(0);
    expect(filterNavItems(NAV_ITEMS, [])).toHaveLength(0);
  });

  it('never grants: unknown permissions match nothing', () => {
    expect(filterNavItems(NAV_ITEMS, ['superpower'])).toHaveLength(0);
  });
});
