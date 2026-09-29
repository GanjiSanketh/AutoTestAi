import { api } from '../client';

export interface GridWorker {
  id: string;
  workerKey: string;
  displayName: string;
  workerType: string;
  framework: string;
  browsers: string[];
  version: string;
  status: string;
  effectiveStatus: string;
  capacity: number;
  activeAssignmentCount: number;
  availableSlots: number;
  lastHeartbeatAt: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface GridStatus {
  totalWorkers: number;
  availableWorkers: number;
  busyWorkers: number;
  drainingWorkers: number;
  unhealthyWorkers: number;
  offlineWorkers: number;
  disabledWorkers: number;
  totalCapacity: number;
  activeAssignments: number;
  availableSlots: number;
  queuedExecutions: number;
  activeLeases: number;
  expiredLeasesLastHour: number;
  workers: GridWorker[];
}

export interface GridWorkerDetail extends GridWorker {
  // Extended details if needed
}

export const executionGridKeys = {
  all: ['execution-grid'] as const,
  status: () => [...executionGridKeys.all, 'status'] as const,
  workers: () => [...executionGridKeys.all, 'workers'] as const,
  worker: (workerId: string) => [...executionGridKeys.all, 'worker', workerId] as const,
};

export const executionGridEndpoints = {
  getStatus: () => api.get<GridStatus>('/api/v1/execution-grid/status'),
  getWorkers: () => api.get<GridWorker[]>('/api/v1/execution-grid/workers'),
  getWorker: (workerId: string) => api.get<GridWorker>(`/api/v1/execution-grid/workers/${workerId}`),
  drainWorker: (workerId: string) => api.post<void>(`/api/v1/execution-grid/workers/${workerId}/drain`),
  disableWorker: (workerId: string) => api.post<void>(`/api/v1/execution-grid/workers/${workerId}/disable`),
  enableWorker: (workerId: string) => api.post<void>(`/api/v1/execution-grid/workers/${workerId}/enable`),
};