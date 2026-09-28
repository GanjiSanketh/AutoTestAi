import { useState, type FormEvent } from 'react';
import { ApiError } from '../../lib/api/client';
import { isAbsoluteHttpUrl, isValidProjectKey, normalizeProjectKey } from '../../lib/validation/project';
import { Button } from '../../components/ui/button';
import { Input } from '../../components/ui/input';
import type { CreateProjectInput, ProjectDetails } from '../../lib/api/endpoints/projects';

export interface ProjectFormValues {
  name: string;
  key: string;
  description: string;
  repositoryUrl: string;
  targetUrl: string;
  framework: string;
  platform: string;
  status: string;
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
  const id = `project-${label.toLowerCase().replace(/[^a-z0-9]+/g, '-')}`;
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

/** Shared create/edit form. Key is editable only on create (immutable afterwards). */
export function ProjectForm({
  initial,
  submitLabel,
  submitting,
  serverError,
  onSubmit,
}: {
  initial?: ProjectDetails | null;
  submitLabel: string;
  submitting: boolean;
  serverError: ApiError | null;
  onSubmit: (values: CreateProjectInput) => void;
}) {
  const [values, setValues] = useState<ProjectFormValues>({
    name: initial?.name ?? '',
    key: initial?.key ?? '',
    description: initial?.description ?? '',
    repositoryUrl: initial?.repositoryUrl ?? '',
    targetUrl: initial?.targetUrl ?? '',
    framework: initial?.framework ?? '',
    platform: initial?.platform ?? '',
    status: initial?.status ?? 'Active',
  });
  const [clientErrors, setClientErrors] = useState<Record<string, string>>({});

  const set = (field: keyof ProjectFormValues) => (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>) =>
    setValues((v) => ({ ...v, [field]: e.target.value }));

  const serverFieldError = (field: string) =>
    serverError?.details.find((d) => d.field?.toLowerCase() === field.toLowerCase())?.message;

  const fieldError = (field: keyof ProjectFormValues) =>
    clientErrors[field] ?? serverFieldError(field);

  const handleSubmit = (e: FormEvent) => {
    e.preventDefault();
    const errors: Record<string, string> = {};
    if (!values.name.trim()) errors.name = 'Name is required.';
    if (!initial) {
      const key = normalizeProjectKey(values.key);
      if (!key) errors.key = 'Key is required.';
      else if (!isValidProjectKey(key))
        errors.key = 'Key must start with a letter and contain only letters, digits, _ or -.';
    }
    if (!isAbsoluteHttpUrl(values.repositoryUrl)) errors.repositoryUrl = 'Must be an absolute http(s) URL.';
    if (!isAbsoluteHttpUrl(values.targetUrl)) errors.targetUrl = 'Must be an absolute http(s) URL.';
    setClientErrors(errors);
    if (Object.keys(errors).length > 0) return;

    onSubmit({
      name: values.name.trim(),
      key: normalizeProjectKey(values.key),
      description: values.description.trim() || undefined,
      repositoryUrl: values.repositoryUrl.trim() || undefined,
      targetUrl: values.targetUrl.trim() || undefined,
      framework: values.framework.trim() || undefined,
      platform: values.platform.trim() || undefined,
      status: values.status,
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
        <Field label="Name" error={fieldError('name')}>
          <Input id="project-name" value={values.name} onChange={set('name')} placeholder="Customer Portal" maxLength={200} />
        </Field>
        <Field
          label="Key"
          error={fieldError('key')}
          hint={initial ? 'Key is immutable after creation.' : 'Uppercase letters, digits, _ or -. Example: CUSTPORTAL.'}
        >
          <Input
            id="project-key"
            value={values.key}
            onChange={set('key')}
            placeholder="CUSTPORTAL"
            maxLength={32}
            disabled={!!initial}
            className="font-mono"
          />
        </Field>
      </div>
      <Field label="Description" error={fieldError('description')}>
        <textarea
          id="project-description"
          value={values.description}
          onChange={set('description')}
          placeholder="Customer-facing portal under test."
          rows={3}
          className="w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 placeholder:text-slate-400 focus:border-brand-500"
        />
      </Field>
      <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
        <Field label="Repository URL" error={fieldError('repositoryUrl')}>
          <Input
            id="project-repository-url"
            value={values.repositoryUrl}
            onChange={set('repositoryUrl')}
            placeholder="https://git.example.com/org/repo"
            inputMode="url"
            className="font-mono"
          />
        </Field>
        <Field label="Target URL" error={fieldError('targetUrl')}>
          <Input
            id="project-target-url"
            value={values.targetUrl}
            onChange={set('targetUrl')}
            placeholder="https://staging.example.com"
            inputMode="url"
            className="font-mono"
          />
        </Field>
        <Field label="Platform" error={fieldError('platform')}>
          <Input id="project-platform" value={values.platform} onChange={set('platform')} placeholder="web" maxLength={100} />
        </Field>
        <Field label="Framework" error={fieldError('framework')}>
          <Input id="project-framework" value={values.framework} onChange={set('framework')} placeholder="playwright" maxLength={100} />
        </Field>
      </div>
      <Field label="Status" error={fieldError('status')}>
        <select
          id="project-status"
          value={values.status}
          onChange={set('status')}
          className="w-full rounded-md border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 focus:border-brand-500 md:w-64"
        >
          <option value="Active">Active</option>
          <option value="Archived">Archived</option>
        </select>
      </Field>
      <div>
        <Button type="submit" disabled={submitting}>
          {submitting ? 'Saving…' : submitLabel}
        </Button>
      </div>
    </form>
  );
}
