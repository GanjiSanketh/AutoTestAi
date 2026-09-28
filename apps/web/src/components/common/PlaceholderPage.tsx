import { ReactNode } from 'react';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '../ui/card';

/** Consistent Phase-0 placeholder for screens whose features land in Phase 1. */
export function PlaceholderPage({
  title,
  description,
  children,
}: {
  title: string;
  description: string;
  children?: ReactNode;
}) {
  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-xl font-semibold text-slate-900">{title}</h1>
        <p className="mt-1 text-sm text-slate-500">{description}</p>
      </div>
      <Card>
        <CardHeader>
          <CardTitle>Phase-1 scope</CardTitle>
          <CardDescription>
            This area is scaffolded in Phase 0. Full functionality arrives with
            the Phase-1 implementation plan.
          </CardDescription>
        </CardHeader>
        {children ? <CardContent>{children}</CardContent> : null}
      </Card>
    </div>
  );
}
