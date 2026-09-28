import { Navigate, createBrowserRouter } from 'react-router-dom';
import { AppLayout } from '../components/layout/AppLayout';
import { LoginPage } from '../features/auth/LoginPage';
import { CallbackPage } from '../features/auth/CallbackPage';
import { SilentRenewPage } from '../features/auth/SilentRenewPage';
import { RequireAuth } from '../features/auth/RequireAuth';
import { DashboardPage } from '../features/dashboard/DashboardPage';
import { TestCaseListPage } from '../features/test-cases/TestCaseListPage';
import { AiTestGeneratorPage } from '../features/test-cases/AiTestGeneratorPage';
import { TestCaseNewPage } from '../features/test-cases/TestCaseNewPage';
import { TestCaseDetailsPage } from '../features/test-cases/TestCaseDetailsPage';
import { TestCaseEditPage } from '../features/test-cases/TestCaseEditPage';
import { ProjectsPage } from '../features/projects/ProjectsPage';
import { ProjectNewPage } from '../features/projects/ProjectNewPage';
import { ProjectDetailsPage } from '../features/projects/ProjectDetailsPage';
import { ProjectEditPage } from '../features/projects/ProjectEditPage';
import { ProjectMembersPage } from '../features/projects/ProjectMembersPage';
import { ProjectEnvironmentsPage } from '../features/projects/ProjectEnvironmentsPage';
import { ExecutionListPage } from '../features/test-execution/ExecutionListPage';
import { ExecutionDetailsPage } from '../features/test-execution/ExecutionDetailsPage';
import { BugsPage } from '../features/bugs/BugsPage';
import { TicketsPage } from '../features/tickets/TicketsPage';
import { ReportsPage } from '../features/reports/ReportsPage';
import { SettingsPage } from '../features/settings/SettingsPage';

/**
 * Route structure (docs/02 §7). /login, /callback and /silent-renew are
 * public; everything else requires authentication (UX guard only —
 * the backend enforces authorization independently).
 */
export const router = createBrowserRouter([
  { path: '/login', element: <LoginPage /> },
  { path: '/callback', element: <CallbackPage /> },
  { path: '/silent-renew', element: <SilentRenewPage /> },
  {
    path: '/',
    element: <RequireAuth />,
    children: [
      {
        element: <AppLayout />,
        children: [
          { index: true, element: <Navigate to="/dashboard" replace /> },
          { path: 'dashboard', element: <DashboardPage /> },
          // The repository is project-scoped: /test-cases redirects to project selection.
          { path: 'test-cases', element: <Navigate to="/projects" replace /> },
          { path: 'projects/:projectId/test-cases', element: <TestCaseListPage /> },
          { path: 'projects/:projectId/test-cases/generate', element: <AiTestGeneratorPage /> },
          { path: 'projects/:projectId/test-cases/new', element: <TestCaseNewPage /> },
          { path: 'projects/:projectId/test-cases/:testCaseId', element: <TestCaseDetailsPage /> },
          { path: 'projects/:projectId/test-cases/:testCaseId/edit', element: <TestCaseEditPage /> },
          { path: 'projects', element: <ProjectsPage /> },
          { path: 'projects/new', element: <ProjectNewPage /> },
          { path: 'projects/:projectId', element: <ProjectDetailsPage /> },
          { path: 'projects/:projectId/edit', element: <ProjectEditPage /> },
          { path: 'projects/:projectId/members', element: <ProjectMembersPage /> },
          { path: 'projects/:projectId/environments', element: <ProjectEnvironmentsPage /> },
          { path: 'projects/:projectId/executions', element: <ExecutionListPage /> },
          { path: 'projects/:projectId/executions/:executionId', element: <ExecutionDetailsPage /> },
          // Legacy console shell replaced by project-scoped execution history in Slice 5.
          { path: 'test-execution', element: <Navigate to="/projects" replace /> },
          { path: 'bugs', element: <BugsPage /> },
          { path: 'tickets', element: <TicketsPage /> },
          { path: 'reports', element: <ReportsPage /> },
          { path: 'settings', element: <SettingsPage /> },
        ],
      },
    ],
  },
  { path: '*', element: <Navigate to="/dashboard" replace /> },
]);
