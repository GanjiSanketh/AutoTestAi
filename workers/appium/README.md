# Appium Worker (Slice 3C-4B-1 runtime)

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
  cancellation, timeout, or shutdown. Steps are validated but NOT executed.
- Maps failures deterministically: unreachable/timeout/device-absent to
  `environment`, capability/shape problems to `automation`, abort to
  `cancelled`. Never reports success for work not performed.
- Stages Install/Reinstall binaries from the server-minted https URL to
  worker-controlled temp files (`src/appBinary.ts`) with guaranteed cleanup.
  Preinstalled apps skip download entirely.
- Registers with the execution grid as `appium`/`appium`, heartbeats capacity,
  respects drain/disable signals. Advertises no browsers.

## What it explicitly does NOT do yet

- No step execution (tap/input/assert/swipe/back/keyboard/wait/screenshot/
  terminateApp all deferred to the action-engine slice).
- No screenshot, page-source, or Appium-log collection.
- No self-healing, visual comparison, or video.
- No iOS runtime, no remote device farms.

## Security boundary (non-negotiable)

- Assignment tokens authenticate worker API calls only; the slot ClaimToken
  is never part of this contract and never reaches this process.
- No `eval`, no `new Function`, no `child_process`, no dynamic imports.
- Targets resolve through explicit prefixes only
  (`accessibilityId=`, `resourceId=`). Bare targets are rejected.
- Unknown actions and missing locators fail as automation failures.
- Password-like values travel and persist as `[REDACTED]`.
- Request bodies are bounded (4MB); logs are bounded (2000 entries).
