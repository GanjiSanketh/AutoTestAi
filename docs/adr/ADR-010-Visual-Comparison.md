# ADR-010 — Deterministic Visual Comparison (Slice 3C-4D-2)

**Status:** Accepted

## Context

Slice 3C-4D-1 established the visual baseline lifecycle (`visual_baselines`
with Candidate → Active → Superseded, explicit approval) and the
capture-only `verifyScreenshot` checkpoint, explicitly deferring comparison.
Executions now produce checkpoint screenshots with no verdict logic.

## Decision

Compare server-side, inside fenced terminal persistence
(`ExecutionEngine.PersistResultAsync`, after evidence persist, before
finalization), for otherwise-`Passed` outcomes carrying `verifyScreenshot`
evidence only:

- Bounded server-side reads (`IArtifactStorage.DownloadAsync`, new) feed
  baseline bytes; browsers never see comparison traffic (presigned URLs
  stay browser-only).
- Pure `RgbaPixelComparer` (`v1-rgba-bps`) on SixLabors.ImageSharp v3
  (server-side only): canonical RGBA, alpha-against-black, tolerance
  16/255, integer basis points, O(N) single pass. Dimension mismatch is a
  deterministic mismatch, never a silent pass.
- Threshold: per-baseline `MismatchThresholdBps`, default 10 bps (0.1%),
  equality passes. Mismatch verdict is ordinary `Failed`/`TestFailure`
  with a `visual-diff` difference-highlight artifact; every skip/error
  path is warning-only and verdict-neutral.
- Metrics travel in the error message and `visual.compared` audit
  (identifier-only); no migration, no new endpoints.

## Explicitly out of scope

- No AI visual triage or image interpretation of any kind.
- No masked regions or region-specific thresholds.
- No baseline auto-refresh or automatic replacement.
- No Playwright, iOS, video, OCR, or semantic comparison.
- No worker-side comparison: the Appium worker stays capture-only and
  never sees baselines, thresholds, credentials, or verdict logic.

## Consequences

- SixLabors.ImageSharp v3 is the first image-processing dependency, pinned
  and confined to the comparison service. Note: v3 ships under the Six
  Labors Split License (not Apache-2.0); confirm commercial-license posture
  before release. Usage is isolated to one service, so swapping later is cheap.
- Pixel-embedded secrets remain unscrubbable; project-scoped access plus
  guidance against baselining secret-bearing screens is the control.
- Screenshot nondeterminism (animations, timestamps, DPI) can false-positive;
  masked regions are a documented future, not a silent addition.
