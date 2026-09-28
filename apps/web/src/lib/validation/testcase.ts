/**
 * Client-side mirrors of the server test-case rules (docs/06 §6).
 * Server validation is authoritative; this is UX-only.
 */
export function normalizeTestCaseKey(key: string): string {
  return key.trim().toUpperCase();
}

export function isValidTestCaseKey(key: string): boolean {
  return /^[A-Z][A-Z0-9_-]{1,31}$/.test(key);
}

export interface ClientTestStep {
  action: string;
  target: string;
  value: string;
}

export function validateStepsClient(steps: ClientTestStep[]): string[] {
  const problems: string[] = [];
  if (steps.length > 500) problems.push('No more than 500 steps are allowed.');
  steps.forEach((step, index) => {
    const n = index + 1;
    if (!step.action.trim()) problems.push(`Step ${n}: action is required.`);
    if (step.action.length > 200) problems.push(`Step ${n}: action is too long.`);
    if (step.target.length > 2000) problems.push(`Step ${n}: target is too long.`);
    if (step.value.length > 8000) problems.push(`Step ${n}: value is too long.`);
  });
  return problems;
}
