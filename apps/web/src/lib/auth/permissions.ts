/**
 * Permission identifiers. Must match
 * AutoTestAi.Application.Authorization.Permissions exactly.
 * The backend enforces these; the frontend only gates visibility.
 */
export const Permissions = {
  DashboardRead: 'dashboard.read',
  ProjectsRead: 'projects.read',
  ProjectsManage: 'projects.manage',
  TestCasesRead: 'testcases.read',
  TestCasesManage: 'testcases.manage',
  ExecutionsRead: 'executions.read',
  ExecutionsExecute: 'executions.execute',
  ExecutionsCancel: 'executions.cancel',
  ExecutionsAnalyze: 'executions.analyze',
  BugsRead: 'bugs.read',
  BugsManage: 'bugs.manage',
  TicketsRead: 'tickets.read',
  TicketsCreate: 'tickets.create',
  ReportsRead: 'reports.read',
  SettingsManage: 'settings.manage',
} as const;

export type Permission = (typeof Permissions)[keyof typeof Permissions];

export function hasPermission(
  permissions: readonly string[] | undefined,
  required: Permission,
): boolean {
  return permissions?.includes(required) ?? false;
}
