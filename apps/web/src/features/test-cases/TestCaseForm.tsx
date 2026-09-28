import { useState, type FormEvent } from 'react';
import { ApiError } from '../../lib/api/client';
import {
  isValidTestCaseKey,
  normalizeTestCaseKey,
  validateStepsClient,
  type ClientTestStep,
} from '../../lib/validation/testcase';
import { Button } from '../../components/ui/button';
import { Input } from '../../components/ui/input';
import { SourceEditor, editorLanguageFor } from './SourceEditor';
import { StepsEditor } from './StepsEditor';
import type { TestCaseDetails, TestCaseVersion } from '../../lib/api/endpoints/testcases';

export interface TestCaseFormValues {
  testKey: string;
  title: string;
  description: string;
  module: string;
  framework: string;
  platform: string;
  priority: string;
  status: string;
  sourceType: string;
  sourceCode: string;
  structuredSteps: ClientTestStep[];
}

function Field({
  label,
  error,
  children,
  hint,
}: {
  label: string;
  error?: string;
  children: React.ReactNode;
  hint?: string;
}) {
  const id = `test-case-${label.toLowerCase().replace(/[^a-z0-9]+/g, '-')}`;
  return (
    <div>
      <label htmlFor={id} className="mb-1 block text-sm font-medium text-slate-700">
        {label}
      </label>
      {children}
      {hint && !error && <p className="mt-1 text-xs text-slate-400">{hint}</p>}
      {error && (
        <p role="alert" className="mt-1 text-xs text-rose-600">
          {error}
        </p>
      )}
    </div>
  );
}

const PRIORITIES = ['Critical', 'High', 'Medium', 'Low'];
const STATUSES = ['Draft', 'Active', 'Deprecated', 'Archived'];
const SOURCE_TYPES = ['manual', 'ai', 'imported'];

/** Shared create/edit form. Key is editable only on create (immutable afterwards). */
export function TestCaseForm({
  initial,
  initialContent,
  submitLabel,
  submitting,
  serverError,
  onSubmit,
}: {
  initial?: TestCaseDetails | null;
  initialContent?: Pick<TestCaseVersion, 'sourceCode' | 'structuredSteps'> | null;
  submitLabel: string;
  submitting: boolean;
  serverError: ApiError | null;
  onSubmit: (values: TestCaseFormValues) => void;
}) {
  const [values, setValues] = useState<TestCaseFormValues>({
    testKey: initial?.testKey ?? '',
    title: initial?.title ?? '',
    description: initial?.description ?? '',
    module: initial?.module ?? '',
    framework: initial?.framework ?? 'playwright',
    platform: initial?.platform ?? 'web',
    priority: initial?.priority ?? 'Medium',
    status: initial?.status ?? 'Draft',
    sourceType: initial?.sourceType ?? 'manual',
    sourceCode: initialContent?.sourceCode ?? '',
    structuredSteps: (initialContent?.structuredSteps ?? []).map((s) => ({
      action: s.action,
      target: s.target ?? '',
      value: s.value ?? '',
    })),
  });
  const [clientErrors, setClientErrors] = useState<Record<string, string>>({});

  const set =
    (field: keyof TestCaseFormValues) =>
    (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>) =>
      setValues((v) => ({ ...v, [field]: e.target.value }));

  const serverFieldError = (field: string) =>
    serverError?.details.find((d) => d.field?.toLowerCase() === field.toLowerCase())?.message;

  const fieldError = (field: string) => clientErrors[field] ?? serverFieldError(field);

  const handleSubmit = (e: FormEvent) => {
    e.preventDefault();
    const errors: Record<string, string> = {};
    if (!values.title.trim()) errors.title = 'Title is required.';
    if (!initial) {
      const key = normalizeTestCaseKey(values.testKey);
      if (!key) errors.testKey = 'Test key is required.';
      else if (!isValidTestCaseKey(key))
        errors.testKey = 'Test key must start with a letter and contain only letters, digits, _ or -.';
    }
    const stepProblems = validateStepsClient(values.structuredSteps);
    if (stepProblems.length > 0) errors.structuredSteps = stepProblems[0];
    setClientErrors(errors);
    if (Object.keys(errors).length > 0) return;

    onSubmit({
      ...values,
      title: values.title.trim(),
      testKey: normalizeTestCaseKey(values.testKey),
      description: values.description.trim(),
      module: values.module.trim(),
      framework: values.framework.trim(),
      platform: values.platform.trim(),
    });
  };

  const generalError =
    serverError && serverError.code !== 'VALIDATION_ERROR' ? serverError.message : null;

  return (
    <form onSubmit={handleSubmit} className="space-y-4" noValidate>
      {generalError && (
        <div role="alert" className="rounded-md border border-rose-200 bg-rose-50 px-3 py-2 text-sm text-rose-700">
          {generalError}
        </div>
      )}
      <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
        <Field label="Test key" error={fieldError('testKey')} hint={initial ? 'Test key is immutable after creation.' : 'Unique within the project. Example: LOGIN-001.'}>
          <Input
            id="test-case-test-key"
            value={values.testKey}
            onChange={set('testKey')}
            placeholder="LOGIN-001"
            maxLength={32}
            disabled={!!initial}
            className="font-mono"
          />
        </Field>
        <Field label="Title" error={fieldError('title')}>
          <Input id="test-case-title" value={values.title} onChange={set('title')} placeholder="Successful user login" maxLength={200} />
        </Field>
      </div>
      <Field label="Description" error={fieldError('description')}>
        <textarea
          id="test-case-description"
          value={values.description}
          onChange={set('description')}
          placeholder="Verify that a registered user can log in."
          rows={3}
          className="w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 placeholder:text-slate-400 focus:border-brand-500"
        />
      </Field>
      <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
        <Field label="Module" error={fieldError('module')}>
          <Input id="test-case-module" value={values.module} onChange={set('module')} placeholder="Authentication" maxLength={100} />
        </Field>
        <Field label="Framework" error={fieldError('framework')}>
          <Input id="test-case-framework" value={values.framework} onChange={set('framework')} placeholder="playwright" maxLength={100} />
        </Field>
        <Field label="Platform" error={fieldError('platform')}>
          <Input id="test-case-platform" value={values.platform} onChange={set('platform')} placeholder="web" maxLength={100} />
        </Field>
      </div>
      <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
        <Field label="Priority" error={fieldError('priority')}>
          <select id="test-case-priority" value={values.priority} onChange={set('priority')} className="w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 focus:border-brand-500">
            {PRIORITIES.map((p) => (
              <option key={p} value={p}>{p}</option>
            ))}
          </select>
        </Field>
        <Field label="Status" error={fieldError('status')}>
          <select id="test-case-status" value={values.status} onChange={set('status')} className="w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 focus:border-brand-500">
            {STATUSES.map((s) => (
              <option key={s} value={s}>{s}</option>
            ))}
          </select>
        </Field>
        <Field label="Source type" error={fieldError('sourceType')}>
          <select id="test-case-source-type" value={values.sourceType} onChange={set('sourceType')} className="w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 focus:border-brand-500">
            {SOURCE_TYPES.map((s) => (
              <option key={s} value={s}>{s}</option>
            ))}
          </select>
        </Field>
      </div>
      <SourceEditor
        id="test-case-source-code"
        label="Source code"
        value={values.sourceCode}
        onChange={(next) => setValues((v) => ({ ...v, sourceCode: next }))}
        language={editorLanguageFor(values.framework)}
      />
      <StepsEditor
        idPrefix="test-case-steps"
        steps={values.structuredSteps}
        onChange={(structuredSteps) => setValues((v) => ({ ...v, structuredSteps }))}
        error={fieldError('structuredSteps')}
      />
      <div>
        <Button type="submit" disabled={submitting}>
          {submitting ? 'Saving…' : submitLabel}
        </Button>
      </div>
    </form>
  );
}
