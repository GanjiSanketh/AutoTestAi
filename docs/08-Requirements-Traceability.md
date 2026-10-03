# AutoTest AI — Requirements Traceability

## 1. Authority

- `01-BRD.md` — product scope/business requirements
- `02-Design-System.md` — visual/interaction requirements
- `03-Tech-Stack-and-Architecture.md` — technology baseline
- `04-Architecture.md` — implementation architecture
- `05-Database-Design.md` — persistence model
- `06-API-Contract.md` — service contract
- `07-Phase-1-Implementation-Plan.md` — delivery scope
- `adr/` — architecture decisions

## 2. Functional Traceability

| Requirement | Implementation |
|---|---|
| RBAC | Keycloak + JWT + ICurrentUserService + IAuthorizationService + project_members (Slice 1: /auth/me, JIT provisioning, SignalR subscription checks) |
| Project management | Projects + project_members (Slice 2: CRUD, soft-delete archive, members + roles, environment metadata, default environment, audit events; list scoped server-side; IDOR-tested) |
| Test repository | test_cases + test_case_versions (Slice 3: CRUD, project-scoped keys, immutable sequential versioning with concurrency retry, structured-steps JSONB, review lifecycle, archive, filters/search/pagination, list omits source; IDOR-tested) |
| AI test generation | AI gateway (`IAiTestGenerator` → `IAiProviderResolver` → `IAiProvider`) + Ollama/OpenAI HTTP adapters (Slice 4: project-scoped `POST …/test-generation`, prompt `test-generation-v1`, structured-output validation, secret redaction, `sourceType=ai` + `Pending` versions via `TestCaseService`, safe provider-status endpoint, audit events; Gemini planned/unsupported; generated code never executed) |
| Test execution | Execution control plane + Temporal `TestExecutionWorkflow` + Playwright step-interpreter worker (Slice 5: exact-version binding, approval gate, Queued→Running→Passed/Failed/Cancelled/TimedOut/Error, single infra retry, idempotent cancel/timeout finalization, step results/logs/screenshot artifacts via MinIO + presigned downloads, SignalR live events, execution history/detail UI; source code never executed) |
| Live execution | SignalR |
| Failure analysis | `failure_analyses` attempts + `IAiProvider.AnalyzeFailureAsync` (Slice 6: bounded redacted evidence, prompt `failure-analysis-v1`, validated advisory output, attempt history, no execution mutation) |
| Defect management | `defects` (Slice 6: explicit human creation from failed executions, server-derived relationships, validated status lifecycle, audited changes, list/detail UI; no external tickets yet) |
| Jira | tickets + integrations (Slice 7: manual `POST …/defects/{id}/ticket` via `TicketService` → `IJiraTicketProvider` → Infrastructure Jira adapter; project Jira config via `PUT …/integrations/jira` with secret-safe status; idempotent per defect per integration; no auto-ticketing, sync, or webhooks) |
| Automated defect ticketing | auto_ticket_policies + tickets (Slice 10: project auto-ticket policy via `PUT …/auto-ticket-policy` with `settings.manage`; deterministic eligibility over severity/status/classification/confidence with no AI decision; defect creation persists a Pending automatic intent and enqueues `AutoTicketBackgroundService` → `AutomatedTicketService` reusing the Slice-7 provider; bounded retries for transient Jira failures, permanent stop for config/validation failures; manual/automatic idempotent convergence; `Ticket.Origin` Manual/Automatic; audited `ticket.automation.*` with system attribution; no self-healing, predictive analytics, or autonomous authoring) |
| Quality dashboard | dashboard + reports API + ECharts (Slice 8: project-scoped read-only `IDashboardService`/`IReportService` over EF aggregates; summary/trend/failure-breakdown/defect/ticket overviews; paginated execution/defect/ticket reports; pass rate = Passed ÷ terminal, null when empty; deterministic classification authoritative; ticket metrics from internal records, never Jira; no prediction, AI analytics, or exports) |
| Execution grid | grid_workers + grid_assignments (Slice 9: project-scoped read/write `IExecutionGridService`/`IGridScheduler` over EF aggregates; worker registration/heartbeat, capacity leases, lease renewal/expiry, stale worker reaping; deterministic least-loaded scheduling with capability matching; atomic claim via unique filtered index; global/project concurrency ceilings; no auto-scaling, AI scheduling, self-healing, or Phase 2+ features) |
| Self-healing test engine | self_healing_policies + self_healing_attempts + worker healing hooks (Slice 11: project policy via `PUT …/self-healing-policy` with `settings.manage`, disabled by default; deterministic-first candidates validated against the live DOM with one retry per step; optional AI fallback via `IAiProvider.SuggestHealingCandidatesAsync` over bounded redacted evidence with schema + safety validation; validation — never AI — is authoritative; ambiguous/unsupported candidates rejected; recovered locators never mutate stored versions; fenced lease-scoped persistence; healed steps skip defects, failed healing uses the normal defect/ticket pipeline; execution-time recovery ONLY — no autonomous test maintenance, predictive flakiness, self-authoring, mobile/API/visual healing, or selector commits) |
| Executive Quality Dashboard (FR-2.1) | executive analytics over existing tables (Slice 12: pass/fail ratios, deterministic flakiness index + trend + test-level report, automation coverage %, deterministic release-readiness score with transparent components; descriptive history only — no prediction, no AI release decisions, no autonomous gates) |
| Quality Analytics & SLA Reports (FR-2.4) | Slice 12: defect density proxy (defects created per 100 terminal executions, documented), average/min/max/p50/p90 durations + trend, root-cause distribution reusing Slice 6 deterministic classification, self-healing outcomes reusing Slice 11 telemetry; SLA compliance is partial by design (no SLA targets persisted — open-defect aging reported instead); bounded CSV export of the flakiness report |
| Environment Secret Vault (FR-1.5) | variable_sets + environment_secrets + execution_variables (Slice 3A: project/environment/suite scopes, deterministic System→Project→Environment→Suite→Override precedence, `${{ KEY }}` single-pass substitution, `ISecretResolver`/`ISecretStore` split, AES-256-GCM vault with external KEK, trusted activity-scope resolution, secret-bearing worker transport controls, secret-aware error/SignalR/audit redaction, `variables.manage`/`secrets.manage` for admin + qa-lead; no CI/CD, mobile, or visual regression) |
| CI/CD webhooks (FR-1.3, FR-2.2) | integrations (`integration_type=cicd`) + webhook_deliveries (Slice 3B: per-provider adapters — GitHub HMAC-SHA256, GitLab token, Jenkins bearer, Azure Basic — AllowAnonymous ingress `POST …/webhooks/{provider}/{projectId}/{integrationId}` with 1 MB guard + per-project rate budget, durable `(integration_id,delivery_id)` idempotency, claim/lease crash recovery, async fan-out across suite members in persisted order via `TestExecutionService.StartAsSystemAsync` with `TriggerType.Ci` and deterministic `wh:…` idempotency keys, mandatory default suite + active environment, allowlisted variable mapping, `settings.manage` management + `executions.read` history, audited `webhook.*`; at-least-once provider delivery with idempotent processing — never global exactly-once; no scheduled execution, outbound status sync, Slack/Teams, mobile, or visual regression) |
| Mobile registry (FR-1.4 devices, Phase 3) | mobile_device_pools + mobile_devices + mobile_device_slots + mobile_device_sessions + mobile_apps (Slice 3C-1/3C-2: project-scoped pool/device/app registry with `settings.manage` mutations + `executions.read` reads, closed android/ios platform + UiAutomator2/XCUITest automation allowlists, structured capabilities only (no arbitrary JSON), atomic device+default-slot registration, optimistic concurrency, disable-instead-of-delete, audited `mobile.*` with safe metadata; slot lease fields and session rows are structural preparation only; no slot leasing, Appium worker, mobile execution, CI fan-out, self-healing, video, cloud farms, or real-iOS support) |
| Mobile slot leasing (FR-1.4 devices, Phase 3) | slot ClaimToken/AssignmentId/WorkerId + GridAssignment + worker capacity committed atomically (Slice 3C-3: in-row slot lease on MobileDeviceSlot, no second lease table; `GridScheduler.TryClaimMobileAsync` deterministic worker/slot selection reusing Slice 9 ordering; `IMobileSlotLeaseService` stages without committing; assignment renew/release/reap piggyback linked slot transitions; slot reaper never frees live-assignment slots; `appium` worker registration; audited `mobile.slot_*`, metrics `mobile_slot_*`; no public lease APIs, no Appium execution, no dispatch yet) |
| Mobile execution foundations (FR-1.4 devices, Phase 3) | additive start inputs + worker contract + capability builder + scaffold (Slice 3C-4A: nullable `mobileDevicePoolId`/`mobileAppId` on start with same-project/active-pool/platform-match validation, browser bypass only for appium, `Execution` refs populated; `IMobileWorkerClient`/`MobileAssignmentDto` without `ClaimToken`; fixed-schema `MobileCapabilityBuilder` with install-policy mapping; `workers/appium` scaffold validating envelopes and returning deferred `error/automation/NotImplemented`; worker-local `APPIUM_SERVER_URL`; closed 12-action set + `accessibilityId`/`resourceId` locators validated-not-executed; ADR-008; no session creation, step execution, artifacts, dispatch, or iOS runtime yet) |
| Mobile session lifecycle (FR-1.4 devices, Phase 3) | real Android session runtime + fenced session ownership (Slice 3C-4B-1: narrow `IMobileDriver` over WebdriverIO with 1:1 capability translation and worker-local `APPIUM_SERVER_URL`; session established/held/closed via create/poll/DELETE with cancellation + execution timeouts; temp-staged app binaries with cleanup; `IMobileSessionService` Creating/Active/Closed/Orphaned with AssignmentToken + slot-ownership fencing; heartbeat via renewal piggyback, no new public API; idempotent cleanup; deterministic environment/automation/cancelled classification; no schema changes; ADR-008; action execution, screenshots, page source, logs, self-healing, visual regression, video, iOS still deferred) |
| Audit | audit_events |

## 3. NFR Traceability

| NFR | Implementation |
|---|---|
| Parallel execution | Horizontally scalable worker pool |
| AI latency | Server-measured `generation_latency_ms` on every generation (provider/model/prompt-version metadata; token usage captured when reported) |
| UI performance | Code splitting, query caching, optimized APIs |
| Encryption | TLS + encrypted persistent storage/secrets infrastructure |
| Sensitive-data masking | Centralized log redaction |
| Availability | Stateless API + durable workflows |
| Worker isolation | Dockerized execution workers |
| Retry | Temporal retry policy |

## 4. Design Traceability

Preserve Inter + JetBrains Mono, indigo brand palette, dark enterprise sidebar, white/slate surfaces, semantic status colors, responsive navigation, AI visual affordances, dashboard KPI cards, terminal execution panel, bug/root-cause evidence and ticket synchronization states.

## 5. MVP Traceability

```text
Authentication → Project → Test Repository → AI Generation
→ Review → Execution → Result → Failure Analysis → Defect → Jira
```
