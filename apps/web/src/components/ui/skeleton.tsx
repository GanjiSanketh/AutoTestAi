import { cn } from './cn';

/** Skeleton placeholder block for loading states. */
export function Skeleton({ className }: { className?: string }) {
  return <div aria-hidden className={cn('animate-pulse rounded-md bg-slate-200', className)} />;
}
