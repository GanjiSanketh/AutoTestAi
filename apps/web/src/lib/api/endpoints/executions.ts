import { api } from '../client';

export interface StartExecutionRequest {
  suiteId?: string;
  environmentId?: string;
  testCaseIds?: string[];
}

export interface StartExecutionResponse {
  executionId: string;
  workflowId: string;
  status: string;
}

export const executionEndpoints = {
  /** POST /api/v1/projects/{projectId}/executions */
  start: (projectId: string, body: StartExecutionRequest) =>
    api.post<StartExecutionResponse>(`/api/v1/projects/${projectId}/executions`, body),
};
