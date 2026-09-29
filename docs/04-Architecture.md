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

SignalR provides execution status, live logs, worker state and other long-running operation updates. Access to project/execution channels must be authorized before subscription.

## 9. Storage

- PostgreSQL: authoritative relational metadata.
- MinIO/S3: screenshots, videos, traces, generated artifacts and report exports.
- Valkey: ephemeral cache, rate limits and short-lived coordination.

## 10. Security

TLS, encrypted persistent storage, Keycloak/OIDC, server-side RBAC, project authorization, secret references, log redaction and audit events are mandatory architectural concerns. Credentials must not appear in generated test source or logs unless explicitly approved and masked.

## 11. Deployment Evolution

Phase 1 uses Docker/Docker Compose. Later k3s/Kubernetes can scale API replicas, workflow workers and execution worker pools. Application code should not require Kubernetes-specific assumptions in MVP.

## 12. Observability

Use OpenTelemetry, Prometheus, Grafana, Loki and Tempo. Major operations carry correlation/execution IDs. Track API latency, AI latency, execution duration, worker utilization, pass/fail rate, flakiness, ticket latency, workflow failures and backlog.

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
