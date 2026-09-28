import { ArrowDown, ArrowUp, Plus, Trash2 } from 'lucide-react';
import { Button } from '../../components/ui/button';
import { Input } from '../../components/ui/input';
import type { ClientTestStep } from '../../lib/validation/testcase';

interface StepsEditorProps {
  idPrefix: string;
  steps: ClientTestStep[];
  onChange: (steps: ClientTestStep[]) => void;
  readOnly?: boolean;
  error?: string;
}

/**
 * Intentionally simple structured-step editor (Phase 3 scope): add, remove,
 * reorder, and edit action/target/value. Steps stay structured JSON —
 * compatible with future AI generation.
 */
export function StepsEditor({ idPrefix, steps, onChange, readOnly = false, error }: StepsEditorProps) {
  const update = (index: number, patch: Partial<ClientTestStep>) =>
    onChange(steps.map((step, i) => (i === index ? { ...step, ...patch } : step)));

  const move = (index: number, delta: -1 | 1) => {
    const next = index + delta;
    if (next < 0 || next >= steps.length) return;
    const copy = [...steps];
    [copy[index], copy[next]] = [copy[next], copy[index]];
    onChange(copy);
  };

  return (
    <div>
      <span id={`${idPrefix}-label`} className="mb-1 block text-sm font-medium text-slate-700">
        Structured steps
      </span>
      {steps.length === 0 && (
        <p className="mb-2 rounded-md border border-dashed border-slate-300 bg-slate-50 px-3 py-4 text-sm text-slate-500">
          No steps yet. {readOnly ? '' : 'Add the first step below.'}
        </p>
      )}
      <ol aria-labelledby={`${idPrefix}-label`} className="space-y-2">
        {steps.map((step, index) => (
          <li
            key={index}
            className="grid grid-cols-[2rem_1fr] gap-2 rounded-md border border-slate-200 bg-white p-2 sm:grid-cols-[2rem_1fr_1fr_1fr_auto]"
          >
            <span className="flex items-center justify-center font-mono text-xs text-slate-500">
              {index + 1}
            </span>
            <Input
              aria-label={`Step ${index + 1} action`}
              value={step.action}
              onChange={(e) => update(index, { action: e.target.value })}
              placeholder="action (e.g. click)"
              readOnly={readOnly}
              disabled={readOnly}
            />
            <Input
              aria-label={`Step ${index + 1} target`}
              value={step.target}
              onChange={(e) => update(index, { target: e.target.value })}
              placeholder="target (e.g. #login)"
              readOnly={readOnly}
              disabled={readOnly}
              className="font-mono"
            />
            <Input
              aria-label={`Step ${index + 1} value`}
              value={step.value}
              onChange={(e) => update(index, { value: e.target.value })}
              placeholder="value (optional)"
              readOnly={readOnly}
              disabled={readOnly}
              className="font-mono"
            />
            {!readOnly && (
              <span className="col-span-2 flex gap-1 sm:col-span-1">
                <Button
                  type="button"
                  variant="ghost"
                  size="sm"
                  aria-label={`Move step ${index + 1} up`}
                  disabled={index === 0}
                  onClick={() => move(index, -1)}
                >
                  <ArrowUp className="h-4 w-4" aria-hidden />
                </Button>
                <Button
                  type="button"
                  variant="ghost"
                  size="sm"
                  aria-label={`Move step ${index + 1} down`}
                  disabled={index === steps.length - 1}
                  onClick={() => move(index, 1)}
                >
                  <ArrowDown className="h-4 w-4" aria-hidden />
                </Button>
                <Button
                  type="button"
                  variant="ghost"
                  size="sm"
                  aria-label={`Remove step ${index + 1}`}
                  onClick={() => onChange(steps.filter((_, i) => i !== index))}
                >
                  <Trash2 className="h-4 w-4 text-rose-600" aria-hidden />
                </Button>
              </span>
            )}
          </li>
        ))}
      </ol>
      {!readOnly && (
        <Button
          type="button"
          variant="secondary"
          size="sm"
          className="mt-2"
          onClick={() => onChange([...steps, { action: '', target: '', value: '' }])}
        >
          <Plus className="h-4 w-4" aria-hidden />
          Add step
        </Button>
      )}
      {error && (
        <p role="alert" className="mt-1 text-xs text-rose-600">
          {error}
        </p>
      )}
    </div>
  );
}
