import { api } from '../client';

export interface SecretMetadata {
  id: string;
  projectId: string;
  environmentId: string;
  name: string;
  description: string | null;
  secretReference: string;
  hasValue: boolean;
  rowVersion: string | null;
  createdAt: string;
  updatedAt: string;
}

export const secretKeys = {
  all: ['secrets'] as const,
  list: (projectId: string, environmentId?: string) =>
    [...secretKeys.all, 'list', projectId, environmentId ?? 'all'] as const,
};

/**
 * Secret metadata API. Values flow in on create/replace only and are never
 * returned by any endpoint — the UI only handles metadata + references.
 */
export const secretsEndpoints = {
  list: (projectId: string, environmentId?: string) => {
    const params = environmentId ? `?environmentId=${encodeURIComponent(environmentId)}` : '';
    return api.get<SecretMetadata[]>(`/api/v1/projects/${projectId}/secrets${params}`);
  },
  create: (
    projectId: string,
    input: { environmentId: string; name: string; value: string; description?: string },
  ) => api.post<SecretMetadata>(`/api/v1/projects/${projectId}/secrets`, input),
  exists: (id: string) => api.get<boolean>(`/api/v1/secrets/${id}/exists`),
  update: (
    id: string,
    input: { name?: string; value?: string; description?: string; rowVersion?: string | null },
  ) => api.put<SecretMetadata>(`/api/v1/secrets/${id}`, input),
  remove: (id: string) => api.delete<void>(`/api/v1/secrets/${id}`),
};
