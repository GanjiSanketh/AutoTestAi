import * as signalR from '@microsoft/signalr';
import { getAccessToken as defaultTokenFactory } from '../auth/oidcClient';

/**
 * SignalR event names for /hubs/execution (docs/06 §12).
 * Must match AutoTestAi.Application.TestExecution.ExecutionEvents exactly —
 * ExecutionEventsTests guards the backend side of this contract.
 */
export const ExecutionEvent = {
  ExecutionStarted: 'ExecutionStarted',
  ExecutionStatusChanged: 'ExecutionStatusChanged',
  ExecutionTestStarted: 'ExecutionTestStarted',
  ExecutionStepStarted: 'ExecutionStepStarted',
  ExecutionStepCompleted: 'ExecutionStepCompleted',
  ExecutionLogReceived: 'ExecutionLogReceived',
  ExecutionTestCompleted: 'ExecutionTestCompleted',
  FailureAnalysisCompleted: 'FailureAnalysisCompleted',
  ExecutionCompleted: 'ExecutionCompleted',
  ExecutionFailed: 'ExecutionFailed',
  SelfHealingApplied: 'SelfHealingApplied',
  SelfHealingFailed: 'SelfHealingFailed',
} as const;

export type ExecutionEventName =
  (typeof ExecutionEvent)[keyof typeof ExecutionEvent];

const HUB_URL = import.meta.env.VITE_SIGNALR_HUB_URL ?? '/hubs/execution';

/**
 * Reusable execution-hub connection. Pages subscribe via `on(event, handler)`
 * instead of embedding connection logic (docs/04 §8).
 */
export function createExecutionHubConnection(
  getToken: () => Promise<string | null> = defaultTokenFactory,
): signalR.HubConnection {
  return new signalR.HubConnectionBuilder()
    .withUrl(HUB_URL, {
      accessTokenFactory: async () => (await getToken()) ?? '',
    })
    .withAutomaticReconnect()
    .configureLogging(signalR.LogLevel.Information)
    .build();
}

export async function subscribeToExecution(
  connection: signalR.HubConnection,
  executionId: string,
  handlers: Partial<Record<ExecutionEventName, (payload: unknown) => void>>,
): Promise<void> {
  if (connection.state === signalR.HubConnectionState.Disconnected) {
    await connection.start();
  }
  for (const [event, handler] of Object.entries(handlers)) {
    if (handler) connection.on(event, handler);
  }
  await connection.invoke('SubscribeToExecution', executionId);
}
