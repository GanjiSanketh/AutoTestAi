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
