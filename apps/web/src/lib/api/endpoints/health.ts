import { api } from '../client';
import type { ApiHealth } from '../../../types';

export const healthEndpoints = {
  /** GET /api/v1/health — API health plus dependency states. */
  getApiHealth: () => api.get<ApiHealth>('/api/v1/health'),
};
