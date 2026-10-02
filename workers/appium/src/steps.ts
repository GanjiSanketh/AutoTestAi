/**
 * Mobile step validation (Slice 3C-4A). Structural, fail-fast, and purely
 * local: unknown actions and malformed steps are rejected at the assignment
 * boundary without touching any driver. No step is executed in this
 * checkpoint. No eval, no new Function, no child_process, no dynamic import.
 */
import type { MobileStep } from './types.js';
import { MOBILE_ACTIONS } from './types.js';
import { parseMobileTarget } from './locators.js';

const TARGET_REQUIRED = new Set([
  'tap',
  'inputtext',
  'cleartext',
  'assertvisible',
  'asserttext',
]);

const VALUE_REQUIRED = new Set(['inputtext', 'asserttext', 'wait']);

const SWIPE_VALUE = /^(up|down|left|right)(?::\d{1,5})?$/i;

/** Structural validation. Returns blocking problems, if any. */
export function validateMobileSteps(steps: MobileStep[]): string[] {
  const problems: string[] = [];
  if (!Array.isArray(steps) || steps.length === 0) {
    return ['At least one structured step is required.'];
  }
  if (steps.length > 500) return ['At most 500 steps are accepted.'];
  const seen = new Set<number>();
  steps.forEach((step, index) => {
    const label = `Step ${index + 1}`;
    if (!Number.isInteger(step.order) || step.order < 1) {
      problems.push(`${label}: 'order' must be an integer >= 1.`);
    } else if (seen.has(step.order)) {
      problems.push(`${label}: duplicate step order ${step.order}.`);
    } else {
      seen.add(step.order);
    }
    const action = (step.action ?? '').trim();
    if (!MOBILE_ACTIONS.has(action)) {
      problems.push(`${label}: unsupported action '${step.action}'.`);
      return;
    }
    const normalized = action.toLowerCase();
    if (TARGET_REQUIRED.has(normalized) && !parseMobileTarget(step.target)) {
      problems.push(
        `${label}: action '${action}' requires a locator target ('accessibilityId=' or 'resourceId=').`,
      );
    }
    if (VALUE_REQUIRED.has(normalized) &&
        (typeof step.value !== 'string' || step.value.trim().length === 0)) {
      problems.push(`${label}: action '${action}' requires a non-empty value.`);
    }
    if (normalized === 'swipe' &&
        typeof step.value === 'string' &&
        step.value.trim().length > 0 &&
        !SWIPE_VALUE.test(step.value.trim())) {
      problems.push(`${label}: swipe value must look like 'up', 'down:500', 'left', or 'right'.`);
    }
  });
  return problems;
}
