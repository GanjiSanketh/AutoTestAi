# AutoTest AI — Architecture Specification

**Project:** AutoTest AI  
**Date:** 2026-09-28  
**Status:** Baseline Architecture

## 1. Architecture Goals

AutoTest AI is a web-only enterprise application that allows teams to create, manage, execute, analyze, and track automated tests across Web, Mobile, and API targets. The platform itself has no mobile UI; mobile testing is performed by later Appium workers.

Goals:
- Keep Phase 1 understandable and deployable.
- Avoid premature microservice complexity.
- Isolate test execution from the API.
- Make AI providers replaceable.
- Support asynchronous durable workflows.
- Provide real-time execution status through SignalR.
- Keep project/security boundaries explicit.
- Allow later Docker-to-k3s/Kubernetes scaling.

## 2. Phase-1 Architecture

```text
React Web App
      │ HTTPS
      ▼
ASP.NET Core Modular Monolith
      ├── Identity / Projects / Test Repository
      ├── AI Generation / Execution / Defects / Tickets / Reports
      └── Integrations
          │
          ├── PostgreSQL (metadata)
          ├── Temporal (durable workflows)
          ├── Valkey (cache/ephemeral state)
          ├── MinIO/S3 (artifacts)
          └── AI Gateway
                ├── Ollama (optional/local)
                ├── OpenAI adapter
                └── Gemini adapter

Temporal → isolated Web/API/Mobile workers
Web worker → Playwright + TypeScript
Mobile worker → Appium (later phase)
```

## 3. Frontend

React + TypeScript + Vite, React Router, TanStack Query, Zustand, Tailwind CSS, shadcn/ui, Apache ECharts and Monaco where required.

```text
apps/web/src/
  app/             providers, router, layout
  components/      ui, common
  features/        auth, dashboard, projects, test-cases,
                   test-execution, bugs, tickets, reports, settings
  lib/             api, auth, realtime, validation
  stores/
  types/
  styles/
```

Rules: feature boundaries are explicit; server state belongs to TanStack Query; Zustand is for client state; API calls do not live in presentational components; authentication/authorization are backend-enforced; design must follow `02-Design-System.md` and must not introduce Angular Material, MUI, or unrelated UI systems.

## 4. Backend

```text
src/
  AutoTestAi.Api/
  AutoTestAi.Application/
    Identity/ Projects/ TestCases/ TestGeneration/
    TestExecution/ Defects/ Tickets/ Reports/ Integrations/
  AutoTestAi.Domain/
  AutoTestAi.Infrastructure/
  AutoTestAi.Workflows/
```

Dependency direction:

```text
Api → Application → Domain
Infrastructure → Application/Domain
Workflows → Application/Domain
```

Domain must not depend on infrastructure implementations.

## 5. Execution Isolation

The API must never execute arbitrary Playwright/Appium/test code directly. Workers run in isolated containers. A worker receives only the execution ID, test revision, environment reference, approved secret references, and execution policy. It returns status, structured result, logs and artifacts.

Slice-5 execution planes (no second orchestrator — Temporal is the only one):

```text
ASP.NET Core (control/API plane)
  → Temporal (durable workflow orchestration: TestExecutionWorkflow)
  → Playwright worker (execution plane: controlled TestStep interpreter)
  → PostgreSQL (authoritative results) + MinIO/S3 (artifact bytes) + SignalR (live events)
```

The MVP engine executes the structured TestStep contract
(order/action/target/value) through an explicit action vocabulary
(navigate, click, fill, type, select, check, uncheck, press, wait,
assertVisible, assertText, assertValue, screenshot). Generated `sourceCode` is
displayed for review but is NEVER executed — no eval, no Function
constructor, no child_process, no dynamic imports of untrusted files.
Unknown actions fail as automation failures. Approval is the SSRF control:
the worker navigates only URLs from Approved versions; password-like step
values travel and persist as `[REDACTED]` (real secret injection is future
work requiring a vault design).

Slice-6 failure analysis and defects (advisory AI, human-owned bugs):

```text
Failed execution → bounded redacted evidence → IAiProvider.AnalyzeFailureAsync
  → validated advisory analysis (attempt history, never overwrites)
  → human review → explicit defect creation (bugs.manage)
```

Analysis never mutates execution history and never creates defects; defects
derive project/execution/test/version relationships server-side from the
referenced failed execution.

Slice-7 manual Jira ticketing (human-owned external sync):

```text
Human (tickets.create) → POST …/defects/{id}/ticket → TicketService
  → IntegrationResolver (project Jira row) → IJiraTicketProvider
  → Infrastructure Jira HTTP adapter (Basic auth server-side)
  → Jira REST /rest/api/3/issue → Ticket (Synced) + audit
```

The internal defect remains the system of record; Jira is an external
creation target. Creation is synchronous and manual-only — execution
failure, analysis completion, defect creation/status changes, and AI
never create tickets. Application code never touches Jira HTTP details;
Infrastructure owns transport, auth, DTOs, and status interpretation.
One successful ticket per defect per integration is enforced by an
application check plus a unique filtered index; failed attempts persist a
retryable Failed row and never a false success.

Slice-8 quality dashboard and reports (read-only descriptive analytics):

```text
React Dashboard/Reports (ECharts, TanStack Query)
  → REST reporting API (project-scoped, dashboard.read / reports.read)
  → DashboardService / ReportService (auth, range validation, shaping)
  → IReportQueryStore → EF Core server-side aggregates → PostgreSQL
```

Reporting reuses the source-of-truth tables (no analytics tables, no
migration); queries are bounded by a validated UTC date range (default
last 30 days, max 365). Pass rate = Passed ÷ terminal executions
(Passed/Failed/Cancelled/TimedOut/Error; Queued/Running excluded), null
when nothing is terminal. Failure charts use the deterministic
`ExecutionTest.FailureClassification`; AI advisory output never feeds
metrics. Ticket metrics come from internal `Ticket` records — rendering
never contacts Jira, so dashboard availability never depends on it.

Slice-9 distributed parallel execution grid (Phase 2):

```text
React Grid UI / Admin API
      ↓
ASP.NET Core Execution Grid Service
      ↓
Temporal → Grid Scheduler (capacity/lease) → WorkerHttpTransport
      ↓
Playwright Worker Pool (capacity-constrained, lease-based)
```

The grid service manages worker registration, heartbeat, capacity, and
assignment leases. Workers register with a provisioning token, receive a
credential, and heartbeat to advertise availability. The scheduler uses
a least-loaded deterministic algorithm with capability matching to claim
leases atomically (unique filtered index on ExecutionTestId + Status).
Leases are renewed while work is active; expired leases are reaped and
executions re-queued. Workers enforce local capacity; the API enforces
global/project concurrency ceilings. Temporal remains the sole workflow
orchestrator; no second scheduler is introduced. Cancellation, timeout,
and retry semantics from Slice 5 are preserved.
```

Slice-10 automated defect ticketing (Phase 2, policy-controlled):

```text
Defect created (DefectService, human action, bugs.manage)
  → AutomatedTicketService.RequestAutomationAsync (Jira-free, fast)
  → deterministic AutoTicketPolicy evaluation (no AI/LLM)
  → eligible? Pending Ticket intent (Origin=Automatic) persisted
  → AutoTicketQueue handoff → AutoTicketBackgroundService
  → AutomatedTicketService.ExecutePendingAsync (scoped)
  → existing TicketService/Jira provider stack (IJiraTicketProvider)
  → Jira REST /rest/api/3/issue → Ticket (Synced) + audit
```

Automation reuses the Slice-7 Jira provider abstraction, content
builder, severity mapper, URL validator, and the same idempotency
constraint (one Synced ticket per defect per integration). The defect
request path never calls Jira: policy evaluation plus a Pending intent
row is persisted synchronously, and the Jira call runs in the
background service. Durability comes from the persisted intent row, not
the in-memory queue — a startup/startup-interval reconciliation pass
re-discovers Pending/Failed-due rows, so eligible automation survives an
API process restart. A Temporal workflow was deliberately not added:
automation is a short idempotent side effect with bounded retries, and
the Pending-row plus reconciliation design is durable enough without a
second orchestration path. No AI decides eligibility; deterministic
defect fields plus the project policy decide. No policy (or a disabled
policy) means no automation. Manual Slice-7 creation is unchanged and
converges idempotently with automatic tickets. Ticket.Origin
(Manual/Automatic) distinguishes the two in the UI and reports.

Cross-instance safety comes from persistence, not in-process locks
(semaphores remain only as a local fast path). Each intent carries a
claim lease (`ClaimToken` + `ClaimExpiresAt`, default 300s) guarded by
an optimistic-concurrency `RowVersion`, plus a unique filtered index
allowing at most one Pending intent per defect per integration:

```text
Pending (unclaimed)
  → Pending (claimed by token T, lease L) → Jira attempt
  → Synced (terminal; claim cleared) — only if T still holds a live lease
  → Failed retryable (claim released, NextAttemptAt set, bounded x5)
  → Failed permanent (claim released, no schedule)
```

Only the live claimant's writes land: a concurrent claimant loses the
compare-and-set and converges; a stale claimant (crashed, timed out,
reclaimed) fails the live-ownership check after its Jira call and
persists nothing, so it can never overwrite current state or regress a
Synced row. Expired claims are reclaimable, so a crashed holder never
parks automation. Every execution (including retries and reconciliation)
re-evaluates the current policy, so disabling the policy stops future
Jira creation. Rate-limit retries honor a bounded server `Retry-After`
hint. Residual limitation (explicit): Jira POST has no exactly-once
guarantee — if the process crashes after Jira accepts the issue but
before local persistence, a bounded retry may create a second external
issue; reclamation after an incomplete attempt is audited as ambiguous
(`ticket.automation.recovered`) with the same warning.

Slice-11 self-healing test engine (Phase 2, deterministic-first recovery):

```text
Locator failure on a healable action (click/fill/type/select/check/uncheck/press/assertVisible)
  → eligibility predicate (locator-like signal, no assertion/env/auth/cancel signal)
  → policy enabled? → bounded DOM snapshots (allowlisted attributes only, ≤60 nodes)
  → deterministic candidates (testid → role → label → text → stable id/name)
  → live-DOM validation: EXACTLY ONE visible+enabled action-compatible match
  → none qualify + AI fallback enabled? → POST lease-token healing/suggest
      → IAiProvider.SuggestHealingCandidatesAsync (bounded redacted evidence)
      → schema + safety validation (allowlist, no code, bounds, min confidence)
      → live-DOM validation (identical gates)
  → exactly one safe candidate → ONE retry of the original action
  → Applied (step passes, original target preserved in history) or Failed (original failure preserved)
  → worker reports healingAttempts → engine persists fenced SelfHealingAttempt rows
  → SignalR SelfHealingApplied + ExecutionLogReceived (self-healing.* stream)
```

Safety authority is deterministic validation, never the model: AI
suggests locator DATA only; output is schema-validated and then
validated against the live page with the same gates (zero/multi-match,
hidden/disabled, incompatible element, unsupported strategy all
reject). `first()`/`last()`/`nth()` ambiguity suppression is never
used. Assertion failures, navigation, network/auth/environment faults,
cancellations, and all non-Playwright scopes (mobile/API/DB/visual)
never enter healing. At most one healing retry per step (engine guard);
a failed retry preserves the original failure and flows into the
normal Slice-6 classification → defect → Slice-10 ticketing pipeline
unchanged (healed steps do not create defects). Recovered locators
exist for the current run only — `test_case_versions` are never
mutated (no autonomous test maintenance). Persistence is one outcome
row per (execution test, step) with a unique index, so concurrent
workers converge instead of duplicating state; stale leases are
rejected by StartedAssignmentId/AssignmentToken fencing. Evidence is
bounded (≤4000 chars) and redacted before AI transmission, persistence,
logging, or audit; the worker never holds provider keys (AI resolves
server-side via the existing provider abstraction). Normal passing
steps pay no healing overhead (no per-step DB/AI/DOM work).

Slice-12 executive analytics & flakiness (Phase 2, deterministic history):

```text
PostgreSQL (Slices 1–11 tables, no new tables)
  → IReportQueryStore extensions (server-side grouped aggregates only)
  → AnalyticsCalculations (pure formulas) + DashboardService/ReportService
  → dashboard/* + reports/flakiness* endpoints (project-scoped, bounded ranges)
  → React ExecutiveSection + FlakinessReportTab (ECharts + table fallbacks)
```

Metric specification (all UTC, window-scoped, reproducible):

- One logical execution = one Execution row (the engine's single infra
  retry reuses the execution, so retries never double-count). Verdicts:
  Passed/Failed; Cancelled excluded; TimedOut/Error reported as unstable
  (neither flaky-making nor flaky-blocking); Queued/Running excluded.
- Pass rate = Passed ÷ terminal (Slice 8 convention), null when empty.
- Flaky test = ≥1 Passed AND ≥1 Failed in the window (always-failing is
  failure-prone, always-passing is not flaky). Test rate =
  100·min(P,F)/(P+F), null when <2 verdicts. Project index =
  100·flaky/eligible(≥2 verdicts), null (insufficient data, never zero)
  when no test qualifies.
- Trend buckets are daily (weekly rollup past 62 days); a null bucket
  index means no data, never zero.
- Automation coverage = 100·(non-archived cases with latest version
  Approved)/(non-archived cases), null when empty.
- Release readiness (0–100, informational only): 35% pass rate + 20%
  flakiness health (100−index) + 15% coverage + 15% defect health
  (max(0,100−25·open Critical/High)) + 15% completion health
  (terminal÷total); unknown components excluded with renormalized
  weights; null when pass rate unknown. Bands: ≥80 Ready, ≥60 Caution,
  else NeedsAttention. No LLM, no autonomous decisions.
- Defect density proxy = 100·(defects created in window)/(terminal
  executions), null when empty (creation-date based, documented).
- Durations over valid samples (non-null, ≥0ms): count/avg/min/max/total
  server-side; p50/p90 in memory over a capped (50k) sorted fetch; daily
  averages. No SLA targets exist in the domain, so `slaConfigured` is
  false and open-defect aging (<7d/7–30d/>30d) is reported instead.
- Healing success = 100·applied/attempts (Slice 11 Applied+WasApplied
  semantics), null when empty; deterministic vs AI-assisted split;
  healing-alongside-flaky counts are co-occurrence only ("observed
  alongside"), never causal claims. No predictive analytics (Phase 4).

Query rules: project predicate + CreatedAt window on every query; two
composite indexes (`IX_executions_Project_Created`,
`IX_healing_Project_Created`) support the range scans; grouped
aggregates stay in the database (EF LINQ, parameterized, sort
allowlists); per-test shaping and paging happen over grouped rows
(never raw history to the browser); flakiness CSV export is capped at
5000 rows, deterministic TestKey order, safe fields only. Dashboard
reads need `dashboard.read`, reports/exports need `reports.read`.

## 6. AI Provider Abstraction

Business modules depend on an internal abstraction, not vendor SDKs.

```csharp
public interface IAiProvider
{
    Task<AiGenerationResult> GenerateTestAsync(
        AiGenerationRequest request,
        CancellationToken cancellationToken);

    Task<AiAnalysisResult> AnalyzeFailureAsync(
        AiFailureAnalysisRequest request,
        CancellationToken cancellationToken);

    // Slice 11: locator recovery over bounded redacted DOM evidence.
    // Returns locator DATA only (prompt self-healing-v1); the caller
    // validates every candidate and never executes provider output.
    Task<AiHealingResult> SuggestHealingCandidatesAsync(
        AiHealingRequest request,
        CancellationToken cancellationToken);
}
```

Adapters may include Ollama, OpenAI and Gemini. Local AI is a deployment option, not a platform dependency.

Slice-4 implementation notes (no new ADR — ADR-003 already covers this):

- `Application.TestGeneration.TestGenerationService` (behind `IAiTestGenerator`)
  is the single orchestrator: authorize → resolve provider → generate →
  validate → redact → persist through `ITestCaseService` → audit. Controllers
  never touch providers.
- `Application.AI.AiProviderResolver` maps `AI:Provider` configuration to an
  adapter; unknown names and missing required configuration fail loudly, never
  silently fall back. `AI:ApiKey` and endpoints stay server-side.
- Prompt construction lives in `IAiTestGenerationPromptBuilder`
  (version `test-generation-v1`, stored in generation metadata).
- `OllamaAiProvider` / `OpenAiAiProvider` (Infrastructure, HTTP only, no vendor
  SDKs) return normalized structured results with token usage when reported.
  Gemini has no adapter yet and resolves as unsupported.
- A small per-project generation budget plus provider timeouts, cancellation
  propagation, and upstream-429 mapping bound cost without a quota system.

## 7. Workflow

Temporal orchestrates long-running execution:

```text
CreateExecution → ValidateRevision → PrepareEnvironment
→ AllocateWorker → RunTest → CollectArtifacts → PersistResult
→ AnalyzeFailure(if failed) → Defect/Ticket actions → Complete
```

PostgreSQL remains the application system of record.

## 8. Real-Time

SignalR provides execution status, live logs, worker state and other long-running operation updates. Access to project/execution channels must be authorized before subscription. Slice 11 reuses the existing hub (no second transport): the granular `self-healing.*` stream travels as execution logs, and `SelfHealingApplied` / `SelfHealingFailed` events supplement `ExecutionStepCompleted`; when the hub cannot carry a new event safely, logs remain authoritative.

## 9. Storage

- PostgreSQL: authoritative relational metadata.
- MinIO/S3: screenshots, videos, traces, generated artifacts and report exports.
- Valkey: ephemeral cache, rate limits and short-lived coordination.

## 10. Security

TLS, encrypted persistent storage, Keycloak/OIDC, server-side RBAC, project authorization, secret references, log redaction and audit events are mandatory architectural concerns. Credentials must not appear in generated test source or logs unless explicitly approved and masked.

## 11. Deployment Evolution

Phase 1 uses Docker/Docker Compose. Later k3s/Kubernetes can scale API replicas, workflow workers and execution worker pools. Application code should not require Kubernetes-specific assumptions in MVP.

## 12. Observability

Use OpenTelemetry, Prometheus, Grafana, Loki and Tempo. Major operations carry correlation/execution IDs. Track API latency, AI latency, execution duration, worker utilization, pass/fail rate, flakiness, ticket latency, workflow failures and backlog. Slice 11 intentionally defers dedicated healing counters (`healing_attempts_total/success/failure/skipped`, `ai_healing_attempts_total`, `healing_duration`) to the structured `self-healing.*` execution-log stream plus `self-healing.*` audit events, which carry the same dimensions (execution/test/step/strategy/AI-assisted/outcome) through existing conventions; no new telemetry framework is introduced.

## 13. Authentication & Authorization (Slice 1)

Keycloak is the sole identity provider (OIDC). The chain is:

```text
Keycloak login (React/oidc-client-ts, code flow)
  → JWT access token (aud: autotestai-api via realm audience mapper)
  → API validates issuer/audience/signature/expiry (ASP.NET Core JwtBearer)
  → ICurrentUserService (claims → sub/email/name/roles, realm_access aware)
  → permissions resolved from realm roles (Application/Authorization)
  → project access via project_members (admin role bypasses membership)
  → execution/SignalR access via execution → project resolution
```

Rules:

- Frontend route guards and hidden navigation are UX only; the backend
  enforces every boundary (401 anonymous, 403 unauthorized, never 500).
- `users.external_identity_id` (Keycloak `sub`) is the stable identity key;
  email is never used as the key. First authenticated API access provisions
  the application user row (JIT); provisioning never fails the request.
- Without a database, authorization fails closed (deny-all stores).
- `/hubs/execution` requires authentication; `SubscribeToExecution` resolves
  the execution's project and enforces `executions.read` + membership before
  joining the group. Event names are unchanged.

## 14. Multi-Environment Vault & Dynamic Variables (Phase 3 Slice 3A)

Single `VariableSet` aggregate (`Project` | `Environment` | `Suite` scopes,
one set per scope) with entries shaped `{ "KEY": { "value": "..." } }` or
`{ "KEY": { "secretRef": "env_secret:<id>" } }`. Keys match
`^[A-Z0-9_]{1,64}$`; raw-string entries and raw secrets are rejected.
Precedence is deterministic: System (read-only `BROWSER` default) →
Project → Environment → Suite → Execution Override (plain values +
`secretRef` only). Missing variables fail deterministically (never
null/empty substitution). Substitution is single-pass `${{ KEY }}` — no
eval, no recursion.

Secrets split capabilities: `ISecretResolver` (`ResolveAsync`/`ExistsAsync`,
execution path only) vs `ISecretStore` (create/update/delete, management
path only). Values are sealed with AES-256-GCM under an externally
controlled KEK (`Secrets:KekBase64`; explicit non-production dev key with a
production refusal guard) in `environment_secrets` (ciphertext + nonce +
key version only). The API returns metadata (`id`, `name`,
`secretReference`, `hasValue`) — never values.

Resolution happens only inside the Temporal `RunWorkerExecutionAsync`
activity scope: `ExecutionCommand` (source-agnostic; `EnvironmentId`
required — explicit id, else the project's Active default, else deterministic
rejection; legacy null-`EnvironmentId` executions keep Phase 2 behavior) →
`VariableSet` merge → `ISecretResolver` → in-memory `SecretValue` → HTTPS
worker dispatch (Bearer + assignment-token fencing, no body/header logging).
`PrepareAsync` masks secret-derived content so Temporal history never sees
plaintext; activity returns are sanitized with exact-match secret masking
plus heuristic redaction. Live SignalR `LogReceived` applies the same
secret-aware redaction as persistence. New permissions `variables.manage`
(admin + qa-lead) and `secrets.manage` (admin + qa-lead); testers cannot
manage secrets; reads expose metadata/plain values only.

Limitations: secrets baked into screenshot pixels cannot be scrubbed in 3A
(artifacts stay `executions.read`-gated with 900s URLs); browser-artifact
policy prefers password-type inputs and forbids secrets in URLs.
CI/CD webhooks, Appium/mobile, and visual regression are explicitly NOT part
of Slice 3A.

## 15. CI/CD Webhooks & Execution Triggering (Phase 3 Slice 3B)

Inbound-only CI ingress reusing the `Integration` aggregate
(`integration_type='cicd'`, `provider ∈ {github,gitlab,jenkins,azure}`; the
existing unique `(project_id,provider)` index yields one row per provider
per project). `CiIntegrationConfig` (JSONB) carries default suite /
environment, event / branch / repository allowlists, variable mapping
(`TARGET=SOURCE` over a closed source set), optional Basic username, and
secret mapping (opaque `env_secret:<id>` only); provider secrets are
provisioned as `CI_WEBHOOK_<PROVIDER>` Slice 3A environment secrets and
resolved at runtime via `ISecretResolver` (in-memory, constant-time
compare, discarded after verification) — stricter than the Slice 7 Jira
raw-token precedent.

`POST /api/v1/webhooks/{provider}/{projectId}/{integrationId}` is
`AllowAnonymous` at the HTTP layer (providers hold no user session);
provider verification is the trust boundary: GitHub HMAC-SHA256 over raw
bytes (`X-Hub-Signature-256` + `X-GitHub-Delivery`), GitLab plaintext token
(`X-Gitlab-Token` + `X-Gitlab-Event-UUID`; HMAC signing deferred),
Jenkins configured bearer token (`Authorization: Bearer` / `token` header;
`X-Jenkins-Delivery`/`Idempotency-Key` is OUR convention with a documented
best-effort hash fallback), Azure Basic over HTTPS (username in config,
password via secret ref; `notificationId` delivery identity; no native
HMAC claimed). Guards run before expensive work: 1 MB bounded body, 100
deliveries/min/project process-local budget (DB constraint is the
correctness boundary), project/integration/active/type/provider cross-checks
(404 without enumeration).

Each accepted delivery persists one `WebhookDelivery` (hash + redacted
normalized metadata only — no raw body, headers, signatures, or secrets)
and returns 202; duplicates return 200 without new rows or executions
(unique `(integration_id,delivery_id)` + unique-violation convergence,
multi-instance safe). `WebhookBackgroundService` drains the Channel handoff
and reconciles stale `Accepted` rows via claim-token leases
(`StartAsSystemAsync` runs the unchanged execution pipeline with
`TriggerType.Ci`; manual callers keep `Manual` default; `Execution.SuiteId`
is now populated for suite runs). Fan-out resolves suite members in
persisted `(ExecutionOrder, TestCaseId)` order, caps at 100 (fail closed,
no partial execution), binds each member's latest `Approved` version, and
uses deterministic `wh:{integration}:{delivery}:{index}` execution
idempotency keys (≤100 chars) so retries converge; Temporal
`test-execution-{executionId}` IDs add a further idempotency layer.
At-least-once provider delivery + idempotent processing is documented
honestly — global exactly-once is NOT claimed. Management
(`settings.manage`) and history/retry (`executions.read` reads,
`settings.manage` retry of `Failed` only) plus `CiCdSettings` UI follow
existing Settings patterns with write-only secrets. Metrics
`webhook_{received,rejected,duplicate,triggered,failed}_total` +
`webhook_processing_duration` carry provider/reason labels only.
Retention defaults to 90 days; the purge job is deferred and documented.
Out of scope: scheduled execution, outbound CI status sync, Slack/Teams,
Azure DevOps ticketing, mobile/Appium, visual regression.

## 16. Mobile Device Registry (Phase 3 Slice 3C-1/3C-2)

Project-scoped administrative registry with no runtime behavior: `mobile_device_pools`
(selection groups, unique name per project, android/ios, Active/Disabled),
`mobile_devices` (structured identity/capabilities — platformVersion,
manufacturer, model, UDID, UiAutomator2/XCUITest automation name; never
arbitrary capabilities JSON), `mobile_device_slots` (one default Free slot
per registered device; claim fields structural only), `mobile_device_sessions`
(persistence model only; never created by registry CRUD), and `mobile_apps`
(package/bundle identity, install policy, storage-key reference only — never
binaries, never secrets). All relationships use Restrict delete behavior;
registry retires via disable, never destructive deletion. `executions` gains
nullable `mobile_device_pool_id/mobile_app_id/mobile_device_session_id`
references (unused until future slices; web/API executions unaffected).
Management requires `settings.manage`, reads `executions.read`; audit
`mobile.pool/device/app_created/updated/enabled/disabled/registered` carries
safe metadata only. Slot leasing, Appium worker (`workers/appium`),
mobile execution, CI fan-out, self-healing, video, cloud farms, and real-iOS
support are explicitly FUTURE and must not be inferred from this foundation.

## 17. Mobile Slot Leasing & Grid Scheduling (Phase 3 Slice 3C-3)

Mobile scheduling reuses the existing execution grid: no second scheduler,
no second lease architecture. A mobile claim atomically establishes worker
capacity (`ActiveAssignmentCount` + `RowVersion`), slot lease
(`Status=Claimed`, `ClaimToken`, `ClaimExpiresAt`, `WorkerId`,
`AssignmentId`), `GridAssignment` (with its own `AssignmentToken`), and
`ExecutionTest` binding in ONE `SaveChangesAsync` persistence boundary over
the shared scoped `DbContext` — the repository has no explicit transaction
abstraction, and none was introduced. `ClaimToken` (slot ownership) and
`AssignmentToken` (execution fencing) stay distinct control-plane values,
never Appium capabilities. `GridScheduler.TryClaimMobileAsync` selects
deterministically (appium workers by least-load/`WorkerKey`, slots by
`SlotNumber`/`SlotId`) after validating project/pool/device/app platform
compatibility; `IMobileSlotLeaseService` stages ownership without committing
(the scheduler owns the save), while activate/renew/release/reap commit
themselves. Assignment renew/release/reap piggyback linked-slot transitions
in the same transaction (no-ops when no slot is linked, so web behavior is
unchanged). The slot reaper never frees a slot whose linked assignment is
still active; `Claimed + AssignmentId NULL` is not a valid scheduler output
(grace-gated anomaly recovery only). Renewal piggybacks assignment renewal;
no second heartbeat architecture. Metrics `mobile_slot_*` carry
platform/result labels only; audit `mobile.slot_claimed/conflict/renewed/
released/expired/recovered` carries identifiers only. Worker registration
accepts `appium`/`appium` (browsers must be empty); Playwright rules
unchanged. Appium session creation, step execution, and dispatch remain
FUTURE: this slice schedules and leases only.

## 18. Mobile Execution Foundations (Phase 3 Slice 3C-4A)

Execution start accepts additive nullable `mobileDevicePoolId`/`mobileAppId`
inputs (API body, command, and `Execution.MobileDevicePoolId/MobileAppId`
populated at start; `MobileDeviceSessionId` stays null until a later slice
creates sessions). Appium-framework test cases skip browser validation
entirely (web/API keep the exact chromium default and allowlist); mobile
targets require a same-project active pool plus a same-project app whose
platform matches the pool, and mobile refs on non-appium executions are
rejected. Authorization, approval gate, idempotency, and environment
resolution are unchanged. The worker contract (`IMobileWorkerClient` +
`MobileAssignmentDto`) mirrors the Playwright envelope mechanics without
browser concepts and never carries `ClaimToken`; server-side
`MobileCapabilityBuilder` emits a fixed capability schema (platformName,
automationName, deviceName, udid?, appPackage/appActivity/bundleId/app?,
noReset/fullReset from `InstallPolicy`, `newCommandTimeout`) from validated
structured data only — dictionary/JSON capability input is impossible by
construction. The `workers/appium` scaffold (config/server/grid-loop/
redaction/locators/steps) validates envelopes and returns a controlled
deferred `error/automation/NotImplemented` result; no driver exists, and it
never reports success unperformed. The Appium endpoint is worker-local
configuration (`APPIUM_SERVER_URL`, default `http://localhost:4723`),
never user input. Closed MVP action set
(launchApp/tap/inputText/clearText/assertVisible/assertText/swipe/back/
hideKeyboard/wait/screenshot/terminateApp) and `accessibilityId=`/
`resourceId=` locators are validated, never executed, in this checkpoint.
Actual Appium session creation, step execution, screenshots, page source,
logs, dispatch, and iOS runtime remain FUTURE (ADR-008).

## 18.1. Mobile Session Lifecycle (Phase 3 Slice 3C-4B-1)

The Appium worker creates real sessions behind a narrow `IMobileDriver`
boundary (`createSession`/`deleteSession`/`hasSession` only; no
`executeScript`, no arbitrary commands, no capability mutation). Trusted
server-built capabilities translate 1:1 to WebdriverIO `appium:*` options;
transport (`hostname`/`port`/`path`) derives exclusively from worker-local
`APPIUM_SERVER_URL`, never from the assignment. After creation the worker
holds a controlled "session established" state until cancellation, timeout,
or shutdown: steps are validated but never executed, and success is never
reported for work not performed. `DELETE /v1/assignments/:id` aborts pending
creation, deletes the Appium session, and reports cancellation; timeouts reuse
`WorkerTimeoutsDto`/`MobileOptions.NewCommandTimeoutSeconds` plus execution
semantics. Install/Reinstall binaries download from the server-minted https
URL to worker-controlled temp files with guaranteed cleanup (Preinstalled
skips download); URLs, tokens, and session ids are never logged.

The control plane owns `MobileDeviceSession` rows via `IMobileSessionService`
(`Creating → Active → Closed`, or `→ Orphaned` when the slot no longer proves
ownership). Every mutation is fenced on project scope, session→assignment
binding, an active `GridAssignment` with matching `AssignmentToken`, and
current slot ownership; stale callers get deterministic conflicts and can
never activate/update/close/orphan a newer session. Heartbeat rides the
existing assignment-renewal seam (`GridScheduler.RenewLeaseAsync` piggyback,
no new public endpoint); cleanup is idempotent and ownership-scoped. No
schema changes were required. Mobile action execution, screenshots, page
source, Appium logs, self-healing, visual regression, video, and iOS runtime
remain FUTURE.

## 18.2. Mobile Failure Evidence (Phase 3 Slice 3C-4B-3)

Failed mobile runs attach bounded, redacted failure evidence that flows
through the existing artifact infrastructure — no new tables, endpoints,
storage abstractions, or migrations:

- Page-source snapshots (`text/xml`, artifact type `page-source`): captured
best-effort at the step-failure evidence point behind the same gate as
screenshots, via a narrow `IMobileDriver.getPageSource` addition
(Android-gated, same classification semantics as screenshots, no
`executeScript`). Snapshot filename `step-{order}-pagesource.xml`.
- Worker log tail (artifact type `appium-log`, `text/plain`, filename
`appium.log`): serialized from the assignment's own bounded in-memory log
ring at terminal time, most-recent tail only, attached to non-passed
results.

Required sanitization order — raw evidence → exact known-secret masking
(assignment token, presigned download URL, typed step values) → heuristic
redaction (bearer credentials, password shapes, signature query parameters)
→ hard size bound (page source: head 1 MB; server logs: most-recent 256 KB
tail). Raw evidence is never logged, never thrown, never persisted, and
never placed in artifact metadata; only deterministic filenames
(no user input, secrets, tokens, or URLs) enter metadata. The worker
enforces masking, redaction, and bounds; the coordinator re-applies exact
secret masking plus bounds in `SanitizeOutcome`, and persistence re-checks
bounds, skipping oversized entries with a warning.

Reliability: evidence capture is best-effort and capture failures log a
bounded warning only — they never create retryable infrastructure failures,
never consume `MaxAttempts`, never change test/environment classification,
and never prevent terminal persistence. Persistence runs inside the existing
fenced `PersistResultAsync` path (`StartedAssignmentId` fencing and
idempotency unchanged) and upload failures never corrupt the execution
result. `ClaimToken` remains control-plane-only and `AssignmentToken`
remains envelope-only; neither enters capabilities or evidence.
Self-healing, visual regression, video, and iOS runtime remain FUTURE.

## 18.3. Mobile Self-Healing (Phase 3 Slice 3C-4C)

Deterministic-first locator recovery for Android, mirroring the Slice 11
web engine. When a healable step (`tap`, `inputText`, `clearText`,
`assertVisible`, `assertText`) fails with a locator-like error and the
project policy enables healing, the worker attempts exactly one retry with
a recovered locator before the original failure becomes terminal:

- Candidates come only from deterministic relationships observed in the
fresh failure page-source snapshot — cross-strategy token matches
(`accessibilityId` ↔ `resourceId`) and, for `assertText`, expected-text
anchors. Closed strategy set (`accessibilityId`, `resourceId`, the approved
mobile locator contract); element text anchors candidates but is never
emitted as an executable locator. No XPath, CSS, UIAutomator, or predicates.
- Every candidate must resolve to exactly one enabled, action-compatible
node (ambiguity rejects), meet the score threshold, and carry no
executable content. The stored test version is never mutated; the
recovered locator exists only for the current attempt.
- `assertText` heals locator recovery only: a text-match mismatch never
heals, and the expectation is never rewritten. The backend accepts
`assertText` Applied rows because both workers guarantee this shape;
mismatch exclusions are enforced worker-side before reporting.
- Optional AI fallback reuses the existing `healing/suggest` endpoint
(assignment-token auth, redacted envelope, bounded round trip). AI output
is untrusted data: schema, closed allowlist, and the same deterministic
validation apply; `aiAssisted` is true only when an AI-proposed candidate
drove the successful retry. The worker holds no provider keys.
- Persisted strategy labels reuse the `SelfHealingStrategy` enum surface
(`accessibilityId` → `TestAttribute`, `resourceId` → `Structural`,
AI → `Ai`): no model or migration change. Attempt records flow through
the shared lease-scoped `RecordAttemptsAsync` path, so fencing,
idempotency, and healed-step defect rules apply unchanged.
- Healing never consumes `MaxAttempts`, creates assignments/sessions,
changes cancellation, or alters action ordering/skip semantics. Policy
absent/disabled preserves pre-healing behavior exactly (no records, no
retries). Visual regression, video, and iOS runtime remain FUTURE.
