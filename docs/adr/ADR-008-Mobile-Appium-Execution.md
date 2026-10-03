# ADR-008 — Mobile Appium Execution Foundations

**Status:** Accepted

## Decision

Mobile execution enters through additive start-path inputs (`MobileDevicePoolId`, `MobileAppId`) validated against project-owned registry rows, with browser validation bypassed only for `appium`-framework test cases. The worker contract (`IMobileWorkerClient` + `MobileAssignmentDto`) mirrors the Playwright envelope mechanics without browser concepts and never carries `ClaimToken`. Appium capabilities are built server-side by `MobileCapabilityBuilder` from validated structured device/app data into a fixed schema; the Appium endpoint stays worker-local configuration (`APPIUM_SERVER_URL`). A `workers/appium` scaffold (config/server/grid-loop/redaction/locators/steps) validates envelopes and returns a controlled deferred error until the execution slice lands.

## Slice 3C-4B-1 — Android session lifecycle (Accepted)

The worker gains a real session runtime behind a narrow `IMobileDriver` boundary (WebdriverIO `webdriverio@9.32.0` client only; Appium stays a sidecar server): server-built capabilities translate 1:1 to `appium:*` options, the endpoint derives exclusively from worker-local `APPIUM_SERVER_URL`, and `DELETE /v1/assignments/:id` aborts creation, deletes the Appium session, and reports cancellation. After session creation the worker holds a controlled "session established" state (steps validated, never executed) until cancel/timeout/shutdown; success is never reported for work not performed. Install/Reinstall binaries download from the server-minted https URL to worker-controlled temp files (never user paths) with guaranteed cleanup; Preinstalled skips download entirely.

The control plane gains `IMobileSessionService` owning `MobileDeviceSession` rows (`Creating → Active → Closed`, or `→ Orphaned` on ownership loss) with fencing on every mutation: project scope, session→assignment binding, active `GridAssignment` with matching `AssignmentToken`, and current slot ownership (server-side `ClaimToken` never leaves the control plane). Heartbeat rides the existing `GridScheduler.RenewLeaseAsync` seam (no new public endpoint); cleanup is idempotent and never releases by device/slot/execution id alone. Failure classification reuses the existing buckets (unreachable/timeout/device-absent → environment; capability/shape → automation; abort → cancelled). No schema changes: the `mobile_device_sessions` columns from Slice 3C-1 already cover the lifecycle.

## Consequences

Web execution behavior is byte-identical (chromium default preserved); mobile starts fail closed without pool/app; capability injection is impossible by construction (no dictionary input); worker contract tests pin the deferred result; real session creation, step execution, artifacts, and dispatch remain future work; no new permissions, migrations, or public lease APIs.
