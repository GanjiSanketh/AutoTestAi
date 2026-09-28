export interface ApiErrorEnvelope {
  error: {
    code: string;
    message: string;
    details?: Array<{ field?: string; message: string }>;
    traceId?: string;
  };
}

export type HealthStatus = 'healthy' | 'degraded' | 'up' | 'down' | 'disabled' | 'enabled';

export interface DependencyState {
  name: string;
  status: string;
  detail: string | null;
}

export interface ApiHealth {
  status: string;
  version: string;
  timestamp: string;
  dependencies: DependencyState[];
}

export type ExecutionStatus =
  | 'Queued'
  | 'Running'
  | 'Passed'
  | 'Failed'
  | 'Cancelled'
  | 'Error';
