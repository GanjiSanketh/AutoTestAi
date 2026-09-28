import { AlertTriangle, Lock, SearchX } from 'lucide-react';
import { ApiError } from '../../lib/api/client';
import { Button } from '../ui/button';

/** Maps API failures to friendly states. 401 is handled globally by the auth flow. */
export function ErrorState({
  error,
  onRetry,
  notFoundMessage,
}: {
  error: unknown;
  onRetry?: () => void;
  notFoundMessage?: string;
}) {
  const status = error instanceof ApiError ? error.status : undefined;
  const code = error instanceof ApiError ? error.code : undefined;

  const { icon: Icon, title, message } =
    status === 403 || code === 'FORBIDDEN'
      ? {
          icon: Lock,
          title: 'No access',
          message: 'You do not have access to this project. Contact a project manager if you need access.',
        }
      : status === 404 || code === 'NOT_FOUND'
        ? {
            icon: SearchX,
            title: 'Not found',
            message: notFoundMessage ?? 'The requested resource does not exist.',
          }
        : {
            icon: AlertTriangle,
            title: 'Something went wrong',
            message:
              error instanceof ApiError
                ? error.message
                : 'An unexpected error occurred. Please try again.',
          };

  return (
    <div className="flex flex-col items-center gap-3 rounded-lg border border-slate-200 bg-white px-6 py-12 text-center shadow-sm">
      <span className="flex h-11 w-11 items-center justify-center rounded-full bg-slate-100">
        <Icon className="h-5 w-5 text-slate-500" aria-hidden />
      </span>
      <h2 className="text-base font-semibold text-slate-900">{title}</h2>
      <p className="max-w-md text-sm text-slate-500">{message}</p>
      {onRetry && (
        <Button variant="secondary" size="sm" onClick={onRetry}>
          Try again
        </Button>
      )}
    </div>
  );
}
