import type { ApiErrorEnvelope } from '../../types';

const BASE_URL = import.meta.env.VITE_API_BASE_URL ?? '';

export interface ApiFieldError {
  field?: string;
  message: string;
}

export class ApiError extends Error {
  code: string;
  status: number;
  traceId?: string;
  details: ApiFieldError[];

  constructor(
    status: number,
    code: string,
    message: string,
    traceId?: string,
    details: ApiFieldError[] = [],
  ) {
    super(message);
    this.status = status;
    this.code = code;
    this.traceId = traceId;
    this.details = details;
  }
}

import { getAccessToken } from '../auth/oidcClient';

type UnauthorizedHandler = (status: number, path: string) => void;
let unauthorizedHandler: UnauthorizedHandler | null = null;

/** Registered once by AuthProvider: redirects expired sessions to sign-in. */
export function setUnauthorizedHandler(handler: UnauthorizedHandler | null): void {
  unauthorizedHandler = handler;
}

async function parseError(response: Response): Promise<never> {
  let code = 'UNKNOWN';
  let message = `Request failed with status ${response.status}.`;
  let traceId: string | undefined;
  let details: ApiFieldError[] = [];
  try {
    const body = (await response.json()) as ApiErrorEnvelope;
    code = body.error?.code ?? code;
    message = body.error?.message ?? message;
    traceId = body.error?.traceId;
    details = Array.isArray(body.error?.details) ? body.error.details : [];
  } catch {
    // Non-JSON error body: keep the generic message.
  }
  throw new ApiError(response.status, code, message, traceId, details);
}

/**
 * Centralized API client. All server communication goes through here —
 * never scatter fetch/axios calls across components (docs/04 §3).
 */
export async function apiRequest<T>(
  path: string,
  init: RequestInit = {},
): Promise<T> {
  const headers = new Headers(init.headers);
  headers.set('Content-Type', 'application/json');
  const token = await getAccessToken();
  if (token) headers.set('Authorization', `Bearer ${token}`);

  const response = await fetch(`${BASE_URL}${path}`, { ...init, headers });
  if (response.status === 401 && unauthorizedHandler) {
    unauthorizedHandler(response.status, path);
  }
  if (!response.ok) await parseError(response);
  if (response.status === 204) return undefined as T;
  return (await response.json()) as T;
}

/** Authenticated binary download (CSV exports); errors surface as ApiError. */
export async function apiDownload(path: string): Promise<{ blob: Blob; fileName: string }> {
  const token = await getAccessToken();
  const headers = new Headers();
  if (token) headers.set('Authorization', `Bearer ${token}`);
  const response = await fetch(`${BASE_URL}${path}`, { method: 'GET', headers });
  if (response.status === 401 && unauthorizedHandler) {
    unauthorizedHandler(response.status, path);
  }
  if (!response.ok) await parseError(response);
  const disposition = response.headers.get('content-disposition') ?? '';
  const match = /filename="?([^";]+)"?/.exec(disposition);
  return { blob: await response.blob(), fileName: match?.[1] ?? 'export.csv' };
}

export const api = {
  get: <T>(path: string) => apiRequest<T>(path, { method: 'GET' }),
  post: <T>(path: string, body?: unknown) =>
    apiRequest<T>(path, { method: 'POST', body: body ? JSON.stringify(body) : undefined }),
  put: <T>(path: string, body?: unknown) =>
    apiRequest<T>(path, { method: 'PUT', body: body ? JSON.stringify(body) : undefined }),
  delete: <T>(path: string) => apiRequest<T>(path, { method: 'DELETE' }),
  download: (path: string) => apiDownload(path),
};
