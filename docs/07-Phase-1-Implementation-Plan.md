# AutoTest AI — Phase 1 Implementation Plan

**Phase:** MVP Core

## 1. Scope

Included: authentication/authorization, application shell, real dashboard data, projects, test repository, manual test creation/editing, AI test generation, human review, Playwright TypeScript execution, execution history, live logs/status, basic failure analysis, defect creation, manual Jira ticket creation, basic audit events, observability and Docker local deployment.

Deferred: full 100-worker production grid, Appium/mobile execution, autonomous authoring, predictive flakiness, autonomous suite maintenance, complete CI/CD ecosystem, advanced visual regression, Kubernetes-first deployment, mandatory local AI and automatic ticketing for every failure.

## 2. Delivery Sequence

### Phase 0 — Foundation
Repository structure, Docker Compose, PostgreSQL, Keycloak, Valkey, MinIO, Temporal, API/React skeletons, configuration, logging and health endpoints.

### Phase 1 — Authentication + Shell
Login/logout, protected routes, role-aware navigation, sidebar/topbar, responsive layout and design tokens.

### Phase 2 — Projects
CRUD, project details, members and environment metadata.

### Phase 3 — Test Repository
List, search/filter, create/edit, version history, source editor, structured steps and review status.

### Phase 4 — AI Generator
Generation form, AI gateway, provider abstraction, Ollama development adapter, cloud adapter, structured output validation and generated-test preview.

### Phase 5 — Execution
Playwright TypeScript worker, Docker worker, Temporal workflow, execution creation, persisted status, logs, screenshots, traces and history.

### Phase 6 — Failure Analysis + Bugs
Failure classification, AI root-cause analysis, confidence/evidence, defect creation and bug views.

### Phase 7 — Jira (implemented, Slice 7)
Project-scoped Jira configuration (admin, secret-safe), safe status
endpoint, and manual ticket creation from internal defects with external
key/URL reference. Idempotent per defect per integration; failure states
are retryable. No automatic ticketing, AI ticketing, bidirectional sync,
webhooks, polling, or Azure DevOps.

### Phase 8 — Dashboard + Reports (implemented, Slice 8)
Project-scoped read-only dashboard (KPI cards, execution trend,
deterministic failure-classification breakdown, severity distribution,
recent executions/defects/tickets, audit activity feed) and paginated
execution/defect/ticket reports with server-side filtering. Descriptive
metrics only: no prediction, AI analytics, exports, or real-time engine.

## Phase 2 — Scalable Execution

### Phase 2 — Slice 9: Distributed Parallel Execution Grid (implemented, Slice 9)
Project-scoped execution grid with worker registration, heartbeat,
capacity-based scheduling, assignment leases, lease renewal/expiry,
stale worker detection, draining/disabling, deterministic scheduling.
Playwright workers enforce local capacity; API enforces global/project
concurrency ceilings. Temporal remains orchestration authority.
No auto-ticketing, self-healing, AI scheduling, or Phase 2+ features.

## 3. Definition of Done

A feature is complete only when its backend endpoint, authorization, validation, migration, frontend integration, loading/empty/error states, tests, OpenAPI update and security/audit considerations are present. No production-looking hardcoded data remains.

## 4. Testing Strategy

Unit: domain rules, application services, validation, AI parsing and permissions. Integration: PostgreSQL, API, Keycloak, workflow initiation and artifact storage. E2E: login → project → test → AI generation → review → execution → result → failure analysis → defect → Jira.

## 5. Repository

```text
AutoTestAi/
├── apps/web/
├── src/
│   ├── AutoTestAi.Api/
│   ├── AutoTestAi.Application/
│   ├── AutoTestAi.Domain/
│   ├── AutoTestAi.Infrastructure/
│   └── AutoTestAi.Workflows/
├── workers/playwright/
├── tests/
├── deploy/docker/
├── docs/
├── scripts/
└── docker-compose.yml
```

## 6. First Vertical Slice

```text
Login → Create Project → Create Test Case → Generate Test with AI
→ Review/Save Version → Execute with Playwright Worker
→ Persist Result → Live Status → Failure Analysis
```

Build this slice before implementing every screen independently. It validates frontend, API, database, workflow, worker, storage and AI architecture together.
