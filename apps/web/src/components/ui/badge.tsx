import { HTMLAttributes } from 'react';
import { cva, type VariantProps } from 'class-variance-authority';
import { cn } from './cn';

const badgeVariants = cva(
  'inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium',
  {
    variants: {
      // Semantic status colors (docs/02 §16). Never use arbitrary colors.
      tone: {
        success: 'bg-emerald-50 text-emerald-700 ring-1 ring-emerald-200',
        danger: 'bg-rose-50 text-rose-700 ring-1 ring-rose-200',
        warning: 'bg-amber-50 text-amber-700 ring-1 ring-amber-200',
        ai: 'bg-purple-50 text-purple-700 ring-1 ring-purple-200',
        info: 'bg-sky-50 text-sky-700 ring-1 ring-sky-200',
        neutral: 'bg-slate-100 text-slate-600 ring-1 ring-slate-200',
        brand: 'bg-brand-50 text-brand-700 ring-1 ring-brand-100',
      },
    },
    defaultVariants: { tone: 'neutral' },
  },
);

export interface BadgeProps
  extends HTMLAttributes<HTMLSpanElement>,
    VariantProps<typeof badgeVariants> {}

export function Badge({ className, tone, ...props }: BadgeProps) {
  return <span className={cn(badgeVariants({ tone }), className)} {...props} />;
}
