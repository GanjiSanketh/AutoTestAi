import { ButtonHTMLAttributes, forwardRef } from 'react';
import { cva, type VariantProps } from 'class-variance-authority';
import { cn } from './cn';

const buttonVariants = cva(
  'inline-flex items-center justify-center gap-2 rounded-md text-sm font-medium transition-colors focus-visible:outline-2 disabled:pointer-events-none disabled:opacity-50',
  {
    variants: {
      variant: {
        // Primary: indigo (docs/02 §16).
        primary: 'bg-brand-600 text-white hover:bg-brand-700 px-4 py-2',
        // AI actions: indigo → purple gradient.
        ai: 'bg-gradient-to-r from-brand-600 to-purple-600 text-white hover:from-brand-700 hover:to-purple-700 px-4 py-2',
        // Success/start: emerald. Destructive/stop: rose.
        success: 'bg-emerald-600 text-white hover:bg-emerald-700 px-4 py-2',
        destructive: 'bg-rose-600 text-white hover:bg-rose-700 px-4 py-2',
        secondary: 'border border-slate-300 bg-white text-slate-700 hover:bg-slate-50 px-4 py-2',
        ghost: 'text-slate-600 hover:bg-slate-100 px-3 py-2',
      },
      size: {
        sm: 'text-xs px-3 py-1.5',
        md: '',
        lg: 'text-base px-6 py-3',
      },
    },
    defaultVariants: { variant: 'primary', size: 'md' },
  },
);

export interface ButtonProps
  extends ButtonHTMLAttributes<HTMLButtonElement>,
    VariantProps<typeof buttonVariants> {}

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(
  ({ className, variant, size, ...props }, ref) => (
    <button
      ref={ref}
      className={cn(buttonVariants({ variant, size }), className)}
      {...props}
    />
  ),
);
Button.displayName = 'Button';
