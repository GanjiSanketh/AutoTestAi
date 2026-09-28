import { Suspense, lazy } from 'react';

// Lazy-loaded so the Monaco bundle only downloads on editing surfaces,
// keeping the initial application bundle lean.
const MonacoEditor = lazy(() => import('@monaco-editor/react'));

/** Maps our free-form framework values onto Monaco language modes. */
export function editorLanguageFor(framework: string | null | undefined): string {
  const value = (framework ?? '').toLowerCase();
  if (value.includes('csharp') || value.includes('c#') || value.includes('selenium') && value.includes('c#'))
    return 'csharp';
  if (value.includes('python')) return 'python';
  if (value.includes('java')) return 'java';
  return 'typescript'; // Playwright TypeScript is the primary target (ADR-005)
}

interface SourceEditorProps {
  id: string;
  label: string;
  value: string;
  onChange?: (value: string) => void;
  language?: string;
  readOnly?: boolean;
  height?: string;
}

/**
 * Monaco-based source editor (docs/04 §3). Read-only for historical versions.
 * Never executes code.
 */
export function SourceEditor({
  id,
  label,
  value,
  onChange,
  language = 'typescript',
  readOnly = false,
  height = '320px',
}: SourceEditorProps) {
  return (
    <div>
      <label
        htmlFor={id}
        className="mb-1 block text-sm font-medium text-slate-700"
      >
        {label}
      </label>
      <div className="overflow-hidden rounded-md border border-slate-300 bg-white">
        <Suspense
          fallback={
            <textarea
              id={id}
              aria-label={label}
              className="h-80 w-full p-3 font-mono text-sm"
              value={value}
              onChange={(e) => onChange?.(e.target.value)}
              readOnly={readOnly}
            />
          }
        >
          <MonacoEditor
            height={height}
            language={language}
            value={value}
            onChange={(next) => onChange?.(next ?? '')}
            loading={
              <textarea
                id={id}
                aria-label={label}
                className="h-80 w-full p-3 font-mono text-sm"
                value={value}
                onChange={(e) => onChange?.(e.target.value)}
                readOnly={readOnly}
              />
            }
            options={{
              readOnly,
              minimap: { enabled: false },
              scrollBeyondLastLine: false,
              fontFamily: 'JetBrains Mono, ui-monospace, monospace',
              fontSize: 13,
              lineNumbers: 'on',
              renderWhitespace: 'boundary',
              tabSize: 2,
            }}
          />
        </Suspense>
      </div>
    </div>
  );
}
