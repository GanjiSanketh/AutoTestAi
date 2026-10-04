# Appium Worker (Slices 3C-4B-1 through 3C-4B-3 runtime)

Isolated mobile-execution worker for AutoTest AI (docs/04, ADR-008).
The API never executes test code; this container is the only mobile
execution site.

## What it does in this checkpoint

- Serves a small authenticated HTTP API mirroring the Playwright worker plane:
  - `POST /v1/assignments` — submit a structured mobile assignment (202, background run)
  - `GET /v1/assignments/:id` — poll progress (steps, logs, terminal result, `appiumSessionId` once established)
  - `DELETE /v1/assignments/:id` — abort: cancels pending creation, deletes the Appium session, reports cancellation
  - `GET /health` — orchestrator probe (no auth)
- Validates the **structured mobile assignment contract only**
  (`assignmentId/executionId/framework/platform/steps/timeouts/assignmentToken`).
- Creates a real Appium session behind the narrow `IMobileDriver` boundary
  (`src/driver.ts`: `createSession`/`deleteSession`/`hasSession`; WebdriverIO
  client only, Appium remains a sidecar server). Server-built capabilities
  translate 1:1 to `appium:*` options; transport derives exclusively from
  worker-local `APPIUM_SERVER_URL` (validated at startup, never from the
  assignment).
- Holds a controlled "session established" state after creation until
  cancellation, timeout, or shutdown, then executes the closed 12-action
  set in order (tap/input/assert/swipe/back/keyboard/wait/screenshot/
  terminateApp), stopping after the first terminal failure. Never reports
  success for work not performed.
- Maps failures deterministically: unreachable/timeout/device-absent to
  `environment`, capability/shape problems to `automation`, abort to
  `cancelled`. Never reports success for work not performed.
- Stages Install/Reinstall binaries from the server-minted https URL to
  worker-controlled temp files (`src/appBinary.ts`) with guaranteed cleanup.
  Preinstalled apps skip download entirely.
- Registers with the execution grid as `appium`/`appium`, heartbeats capacity,
  respects drain/disable signals. Advertises no browsers.

## What it explicitly does NOT do yet

- No page-source collection beyond bounded failure snapshots, no separate
  Appium server-log pipeline beyond the bounded worker log tail.
- No visual comparison or video.
- No iOS runtime, no remote device farms.

## Security boundary (non-negotiable)

- Assignment tokens authenticate worker API calls only; the slot ClaimToken
  is never part of this contract and never reaches this process.
- No `eval`, no `new Function`, no `child_process`, no dynamic imports.
- Targets resolve through explicit prefixes only
  (`accessibilityId=`, `resourceId=`). Bare targets are rejected.
- Unknown actions and missing locators fail as automation failures.
- Password-like values travel and persist as `[REDACTED]`.
- Failure evidence (page-source snapshots, log tails) is exact-masked,
  heuristically redacted, and hard-bounded (1 MB / 256 KB tail) before it
  enters any result; raw evidence is never logged or persisted.
- Self-healing (Slice 3C-4C, policy-gated): deterministic cross-strategy
  locator recovery with exactly one validated retry per failed step.
  Closed strategy set (`accessibilityId`, `resourceId`); AI fallback only
  through the control-plane suggest endpoint, validated identically.
- Visual checkpoints (Slice 3C-4D-1): the `verifyScreenshot` action captures
  a normal passing screenshot for future baseline comparison. No baseline
  lookup, no comparison, no verdict logic in the worker.
- Request bodies are bounded (4MB); logs are bounded (2000 entries).
