import { api } from '../client';

export interface VariableSet {
  id: string;
  projectId: string;
  scopeType: string;
  scopeId: string | null;
  name: string;
  variablesJson: string;
  keys: string[];
  secretKeys: string[];
  rowVersion: string | null;
  createdAt: string;
  updatedAt: string;
}

export const variableKeys = {
  all: ['variable-sets'] as const,
  list: (projectId: string) => [...variableKeys.all, 'list', projectId] as const,
  details: (id: string) => [...variableKeys.all, 'details', id] as const,
};

/** Centralized variable-set API surface — no raw fetch calls in components. */
export const variablesEndpoints = {
  list: (projectId: string) =>
    api.get<VariableSet[]>(`/api/v1/projects/${projectId}/variable-sets`),
  get: (id: string) => api.get<VariableSet>(`/api/v1/variable-sets/${id}`),
  create: (
    projectId: string,
    input: { scopeType: string; scopeId?: string | null; name: string; variables: Record<string, unknown> },
  ) => api.post<VariableSet>(`/api/v1/projects/${projectId}/variable-sets`, input),
  update: (
    id: string,
    input: { name: string; variables: Record<string, unknown>; rowVersion?: string | null },
  ) => api.put<VariableSet>(`/api/v1/variable-sets/${id}`, input),
  remove: (id: string) => api.delete<void>(`/api/v1/variable-sets/${id}`),
};

export const VARIABLE_KEY_PATTERN = /^[A-Z0-9_]{1,64}$/;

export function isValidVariableKey(key: string): boolean {
  return VARIABLE_KEY_PATTERN.test(key);
}
