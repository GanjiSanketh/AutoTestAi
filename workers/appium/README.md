# Appium Worker (Slice 3C-4A scaffold)

Isolated mobile-execution worker for AutoTest AI (docs/04, ADR-008).
The API never executes test code; this container will be the only mobile
execution site once the execution slice lands.

## What it does in this checkpoint

- Serves a small authenticated HTTP API mirroring the Playwright worker plane:
  - `POST /v1/assignments` — submit a structured mobile assignment (202, background run)
  - `GET /v1/assignments/:id` — poll progress (steps, logs, terminal result)
  - `DELETE /v1/assignments/:id` — best-effort abort
  - `GET /health` — orchestrator probe (no auth)
- Validates the **structured mobile assignment contract only**
  (`assignmentId/executionId/framework/platform/steps/timeouts/assignmentToken`).
- Registers with the execution grid as `appium`/`appium`, heartbeats capacity,
  respects drain/disable signals. Advertises no browsers.
- Returns a controlled deferred result (`error` / `automation` /
  `NotImplemented`) for accepted assignments. It never reports success for
  work it did not perform.

## What it explicitly does NOT do yet

- No Appium server communication (`APPIUM_SERVER_URL` is reserved worker-local
  configuration for the later Docker/Appium sidecar architecture).
- No WebdriverIO driver creation (no `webdriverio` dependency installed).
- No step execution, screenshots, page source, or Appium logs.
- No mobile session creation or cleanup.
- No self-healing, visual comparison, or video.

## Security boundary (non-negotiable)

- Assignment tokens authenticate worker API calls only; the slot ClaimToken
  is never part of this contract and never reaches this process.
- No `eval`, no `new Function`, no `child_process`, no dynamic imports.
- Targets resolve through explicit prefixes only
  (`accessibilityId=`, `resourceId=`). Bare targets are rejected.
- Unknown actions and missing locators fail as automation failures.
- Password-like values travel and persist as `[REDACTED]`.
- Request bodies are bounded (4MB); logs are bounded (2000 entries).
