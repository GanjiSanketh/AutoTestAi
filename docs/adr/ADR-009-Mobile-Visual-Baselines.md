# ADR-009 — Mobile Visual Baselines (Slice 3C-4D-1)

**Status:** Accepted

## Context

Slice 3C-4C completed deterministic-first mobile self-healing. The remaining
Phase 3 visual-regression roadmap item (BRD §8) has all prerequisites landed
(screenshot evidence, artifact storage, fenced persistence, audit/events) but
no baseline store, no comparison, and no approval flow. This record scopes the
first visual sub-slice so review stays a single unit.

## Decision

Implement visual baseline lifecycle only (Slice 3C-4D-1):

- New closed `verifyScreenshot` action: capture-only checkpoint, normal
  passing artifact, fails only on capture failure. Ordinary screenshots stay
  evidence-only; existing tests behave identically.
- `visual_baselines` table with `Candidate → Active → Superseded` lifecycle,
  explicit approval (`settings.manage`, audited), idempotent candidate
  creation, filtered unique active baseline per (version, step).
- Bytes via the existing `IArtifactStorage` under a project-scoped
  `visual-baselines/` prefix; review through short-lived presigned URLs.

## Explicitly deferred to Slice 3C-4D-2

- Control-plane image comparison (backend-owned; the worker stays thin).
- The image-processing dependency (e.g. ImageSharp) belongs to 3C-4D-2,
  not this slice. No pixel code ships in 3C-4D-1.
- Baseline lookup during execution, mismatch verdicts, `visual-diff`
  artifacts, diff viewer, masked regions, baseline auto-refresh.
- `verifyScreenshot` is opt-in; normal screenshots remain evidence only.

## Consequences

- Visual comparison is not yet implemented; baselines accumulate without
  affecting any execution verdict.
- Pixel-embedded secrets cannot be scrubbed by text redaction; project-scoped
  access plus guidance against baselining secret-bearing screens is the
  control until comparison lands.
- No automatic baseline replacement under any circumstance.
- No schema changes beyond the single new table; no new endpoints beyond
  baseline management; no Playwright behavior change.

## Alternatives considered

- Worker-side comparison: rejected — needs baseline bytes in the worker
  (new assignment surface), a worker image dependency, and contradicts the
  thin-worker precedent.
- Automatic comparison of every screenshot: rejected — failure screenshots
  depict broken states and ordinary capture must never become an assertion.
- Separate candidate/history tables: rejected — single table with status
  follows the established status-flag convention with fewer joins.
