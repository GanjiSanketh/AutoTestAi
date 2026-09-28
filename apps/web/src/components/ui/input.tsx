import { InputHTMLAttributes, forwardRef } from 'react';
import { cn } from './cn';

export const Input = forwardRef<
  HTMLInputElement,
  InputHTMLAttributes<HTMLInputElement>
>(({ className, ...props }, ref) => (
  <input
    ref={ref}
    className={cn(
      'w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 placeholder:text-slate-400 focus:border-brand-500',
      className,
    )}
    {...props}
  />
));
Input.displayName = 'Input';
