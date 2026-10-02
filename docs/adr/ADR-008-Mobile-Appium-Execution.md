# ADR-008 — Mobile Appium Execution Foundations

**Status:** Accepted

## Decision

Mobile execution enters through additive start-path inputs (`MobileDevicePoolId`, `MobileAppId`) validated against project-owned registry rows, with browser validation bypassed only for `appium`-framework test cases. The worker contract (`IMobileWorkerClient` + `MobileAssignmentDto`) mirrors the Playwright envelope mechanics without browser concepts and never carries `ClaimToken`. Appium capabilities are built server-side by `MobileCapabilityBuilder` from validated structured device/app data into a fixed schema; the Appium endpoint stays worker-local configuration (`APPIUM_SERVER_URL`). A `workers/appium` scaffold (config/server/grid-loop/redaction/locators/steps) validates envelopes and returns a controlled deferred error until the execution slice lands.

## Consequences

Web execution behavior is byte-identical (chromium default preserved); mobile starts fail closed without pool/app; capability injection is impossible by construction (no dictionary input); worker contract tests pin the deferred result; real session creation, step execution, artifacts, and dispatch remain future work; no new permissions, migrations, or public lease APIs.
