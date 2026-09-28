import { describe, expect, it } from 'vitest';
import { Permissions, hasPermission } from './permissions';

describe('hasPermission', () => {
  it('returns true when the permission is present', () => {
    expect(hasPermission([Permissions.ProjectsRead], Permissions.ProjectsRead)).toBe(true);
  });

  it('returns false when the permission is absent', () => {
    expect(hasPermission([Permissions.ProjectsRead], Permissions.ProjectsManage)).toBe(false);
  });

  it('returns false when permissions are unknown', () => {
    expect(hasPermission(undefined, Permissions.DashboardRead)).toBe(false);
  });
});
