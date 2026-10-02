# ADR-007 — Mobile Slot Leasing on the Existing Execution Grid

**Status:** Accepted

## Decision

Mobile device-slot leasing reuses the existing execution-grid scheduler, assignment leases, fencing tokens, and concurrency model. The slot row itself (`MobileDeviceSlot`) is the authoritative lease resource (in-row `ClaimToken`/`ClaimExpiresAt`/`AssignmentId`/`WorkerId` + `RowVersion`); no second lease table exists. Worker capacity, slot claim, `GridAssignment` creation, and execution-test binding commit atomically in one `SaveChangesAsync` persistence boundary over the shared scoped `DbContext`. `ClaimToken` (slot ownership) and `AssignmentToken` (execution fencing) stay distinct control-plane values, never Appium capabilities.

## Consequences

One scheduler with a narrow mobile branch; deterministic least-loaded ordering extended to slots; assignment renew/release/reap piggyback linked slot transitions; slot reaper never frees live-assignment slots; `appium` worker registration alongside Playwright rules. Appium session creation, step execution, and dispatch remain future work; no public lease APIs; no new permissions.
