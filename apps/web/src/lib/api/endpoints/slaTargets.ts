import { api } from '../client';

export interface SlaTargetThresholds {
  criticalDays: number;
  highDays: number;
  mediumDays: number;
  lowDays: number;
}

export interface SlaTarget extends SlaTargetThresholds {
  projectId: string;
  updatedBy: string | null;
  updatedAt: string;
}

export interface SlaSeverityBreach {
  severity: string;
  thresholdDays: number;
  eligible: number;
  breached: number;
}

export interface SlaCompliance {
  evaluatedAt: string;
  eligible: number;
  compliant: number;
  breached: number;
  /** Null when nothing is eligible — never a fabricated 100%. */
  compliancePercent: number | null;
  bySeverity: SlaSeverityBreach[];
  unconfiguredSeverityCount: number;
}

export interface SlaTargetsResponse {
  /** Null when the project never configured thresholds — no defaults are invented. */
  targets: SlaTarget | null;
  compliance: SlaCompliance | null;
}

export interface UpsertSlaTargetsInput {
  criticalDays?: number | null;
  highDays?: number | null;
  mediumDays?: number | null;
  lowDays?: number | null;
}

export const slaTargetKeys = {
  all: ['sla-targets'] as const,
  detail: (projectId: string) => [...slaTargetKeys.all, 'detail', projectId] as const,
};

/** Centralized SLA target API surface — no raw fetch calls in components. */
export const slaTargetEndpoints = {
  get: (projectId: string) =>
    api.get<SlaTargetsResponse>(`/api/v1/projects/${projectId}/sla-targets`),
  upsert: (projectId: string, input: UpsertSlaTargetsInput) =>
    api.put<SlaTarget>(`/api/v1/projects/${projectId}/sla-targets`, input),
};

export function slaTargetErrorMessage(status: number, code?: string): string {
  if (status === 401) return 'Your session has expired. Please sign in again.';
  if (status === 403) return 'You do not have permission to configure SLA targets for this project.';
  if (status === 404) return 'The project could not be found.';
  if (code === 'VALIDATION_ERROR' || status === 400)
    return 'SLA thresholds are invalid. Enter whole days from 1 to 365 for every severity.';
  return 'Saving the SLA targets failed. Please try again.';
}
