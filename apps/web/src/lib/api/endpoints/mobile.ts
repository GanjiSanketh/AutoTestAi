import { api } from '../client';

export interface MobilePool {
  id: string;
  projectId: string;
  name: string;
  platform: string;
  enabled: boolean;
  deviceCount: number;
  rowVersion: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface MobileDevice {
  id: string;
  projectId: string;
  poolId: string;
  poolName: string;
  platform: string;
  platformVersion: string | null;
  manufacturer: string | null;
  model: string | null;
  udid: string | null;
  automationName: string;
  status: string;
  enabled: boolean;
  slotCount: number;
  lastSeenAt: string | null;
  rowVersion: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface MobileApp {
  id: string;
  projectId: string;
  platform: string;
  name: string;
  packageId: string | null;
  bundleId: string | null;
  version: string | null;
  storageKey: string | null;
  hasBinary: boolean;
  installPolicy: string;
  launchActivity: string | null;
  deepLink: string | null;
  rowVersion: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface MobilePoolInput {
  name: string;
  platform: string;
}

export interface MobileDeviceInput {
  poolId: string;
  platform: string;
  platformVersion?: string | null;
  manufacturer?: string | null;
  model?: string | null;
  udid?: string | null;
  automationName: string;
}

export interface MobileAppInput {
  platform: string;
  name: string;
  packageId?: string | null;
  bundleId?: string | null;
  version?: string | null;
  storageKey?: string | null;
  installPolicy: string;
  launchActivity?: string | null;
  deepLink?: string | null;
}

export const mobileKeys = {
  all: ['mobile'] as const,
  pools: (projectId: string) => [...mobileKeys.all, 'pools', projectId] as const,
  devices: (projectId: string, poolId?: string) =>
    [...mobileKeys.all, 'devices', projectId, poolId ?? 'all'] as const,
  apps: (projectId: string) => [...mobileKeys.all, 'apps', projectId] as const,
};

/** Centralized mobile registry API surface — no raw fetch calls in components. */
export const mobileEndpoints = {
  listPools: (projectId: string) =>
    api.get<MobilePool[]>(`/api/v1/projects/${projectId}/mobile/pools`),
  createPool: (projectId: string, input: MobilePoolInput) =>
    api.post<MobilePool>(`/api/v1/projects/${projectId}/mobile/pools`, input),
  updatePool: (projectId: string, poolId: string, input: { name: string; enabled: boolean; rowVersion?: string | null }) =>
    api.put<MobilePool>(`/api/v1/projects/${projectId}/mobile/pools/${poolId}`, input),
  setPoolEnabled: (projectId: string, poolId: string, enabled: boolean) =>
    api.post<MobilePool>(`/api/v1/projects/${projectId}/mobile/pools/${poolId}/${enabled ? 'enable' : 'disable'}`),
  listDevices: (projectId: string, poolId?: string) =>
    api.get<MobileDevice[]>(
      `/api/v1/projects/${projectId}/mobile/devices${poolId ? `?poolId=${poolId}` : ''}`,
    ),
  registerDevice: (projectId: string, input: MobileDeviceInput) =>
    api.post<MobileDevice>(`/api/v1/projects/${projectId}/mobile/devices`, input),
  updateDevice: (projectId: string, deviceId: string, input: Record<string, unknown>) =>
    api.put<MobileDevice>(`/api/v1/projects/${projectId}/mobile/devices/${deviceId}`, input),
  setDeviceEnabled: (projectId: string, deviceId: string, enabled: boolean) =>
    api.post<MobileDevice>(`/api/v1/projects/${projectId}/mobile/devices/${deviceId}/${enabled ? 'enable' : 'disable'}`),
  listApps: (projectId: string) =>
    api.get<MobileApp[]>(`/api/v1/projects/${projectId}/mobile/apps`),
  createApp: (projectId: string, input: MobileAppInput) =>
    api.post<MobileApp>(`/api/v1/projects/${projectId}/mobile/apps`, input),
  updateApp: (projectId: string, appId: string, input: Record<string, unknown>) =>
    api.put<MobileApp>(`/api/v1/projects/${projectId}/mobile/apps/${appId}`, input),
};

export function mobileErrorMessage(status: number, code?: string): string {
  if (status === 401) return 'Your session has expired. Please sign in again.';
  if (status === 403) return 'You do not have permission to manage mobile resources for this project.';
  if (status === 404) return 'The mobile pool, device, or app could not be found.';
  if (status === 409) return 'A mobile resource with the same identity already exists, or it was modified elsewhere. Reload and retry.';
  if (code === 'VALIDATION_ERROR' || status === 400)
    return 'The mobile configuration is invalid. Review the highlighted fields.';
  return 'Saving the mobile resource failed. Please try again.';
}
