import { api } from '../client';

export interface CurrentUserProfile {
  id: string | null;
  externalIdentityId: string | null;
  email: string | null;
  displayName: string | null;
  roles: string[];
  permissions: string[];
}

export const authEndpoints = {
  /** GET /api/v1/auth/me — safe profile only, never tokens. */
  me: () => api.get<CurrentUserProfile>('/api/v1/auth/me'),
};
