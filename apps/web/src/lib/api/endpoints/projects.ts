import { api } from '../client';

export interface ProjectListItem {
  id: string;
  name: string;
  key: string;
  description: string | null;
  repositoryUrl: string | null;
  targetUrl: string | null;
  framework: string | null;
  platform: string | null;
  status: string;
  memberCount: number;
  updatedAt: string;
}

export interface PagedProjects {
  items: ProjectListItem[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export interface EnvironmentSummary {
  id: string;
  name: string;
  baseUrl: string | null;
  status: string;
}

export interface ProjectDetails extends ProjectListItem {
  defaultEnvironmentId: string | null;
  defaultEnvironment: EnvironmentSummary | null;
  createdBy: string | null;
  createdAt: string;
}

export interface ProjectMember {
  userId: string;
  email: string;
  displayName: string;
  roleId: string;
  roleName: string;
  createdAt: string;
}

export interface ProjectRole {
  id: string;
  name: string;
  description: string | null;
}

export interface ProjectEnvironment {
  id: string;
  projectId: string;
  name: string;
  baseUrl: string | null;
  status: string;
  isDefault: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface CreateProjectInput {
  name: string;
  key: string;
  description?: string;
  repositoryUrl?: string;
  targetUrl?: string;
  framework?: string;
  platform?: string;
  status?: string;
}

export interface UpdateProjectInput {
  name: string;
  description?: string;
  repositoryUrl?: string;
  targetUrl?: string;
  framework?: string;
  platform?: string;
  status?: string;
  defaultEnvironmentId?: string | null;
}

export const projectKeys = {
  all: ['projects'] as const,
  list: (search: string, page: number) => [...projectKeys.all, 'list', search, page] as const,
  details: (id: string) => [...projectKeys.all, 'details', id] as const,
  members: (id: string) => [...projectKeys.all, 'members', id] as const,
  environments: (id: string) => [...projectKeys.all, 'environments', id] as const,
  roles: ['roles'] as const,
};

/** Centralized project API surface — no raw fetch calls in components. */
export const projectsEndpoints = {
  list: (search: string, page: number, pageSize = 12) => {
    const params = new URLSearchParams({
      page: String(page),
      pageSize: String(pageSize),
    });
    if (search.trim()) params.set('search', search.trim());
    return api.get<PagedProjects>(`/api/v1/projects?${params.toString()}`);
  },
  get: (id: string) => api.get<ProjectDetails>(`/api/v1/projects/${id}`),
  create: (input: CreateProjectInput) =>
    api.post<ProjectDetails>('/api/v1/projects', input),
  update: (id: string, input: UpdateProjectInput) =>
    api.put<ProjectDetails>(`/api/v1/projects/${id}`, input),
  remove: (id: string) => api.delete<void>(`/api/v1/projects/${id}`),
  members: (id: string) =>
    api.get<ProjectMember[]>(`/api/v1/projects/${id}/members`),
  addMember: (id: string, input: { email: string; roleId: string }) =>
    api.post<ProjectMember>(`/api/v1/projects/${id}/members`, input),
  updateMemberRole: (projectId: string, userId: string, roleId: string) =>
    api.put<ProjectMember>(`/api/v1/projects/${projectId}/members/${userId}`, { roleId }),
  removeMember: (projectId: string, userId: string) =>
    api.delete<void>(`/api/v1/projects/${projectId}/members/${userId}`),
  roles: () => api.get<ProjectRole[]>('/api/v1/roles'),
  environments: (id: string) =>
    api.get<ProjectEnvironment[]>(`/api/v1/projects/${id}/environments`),
  createEnvironment: (id: string, input: { name: string; baseUrl?: string }) =>
    api.post<ProjectEnvironment>(`/api/v1/projects/${id}/environments`, input),
  updateEnvironment: (
    envId: string,
    input: { name?: string; baseUrl?: string; status?: string; setAsDefault?: boolean },
  ) => api.put<ProjectEnvironment>(`/api/v1/environments/${envId}`, input),
  deleteEnvironment: (envId: string) =>
    api.delete<void>(`/api/v1/environments/${envId}`),
};
