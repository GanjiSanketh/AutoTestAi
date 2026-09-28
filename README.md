# AutoTest AI — Phase-0 Foundation

Enterprise-grade AI-driven automated testing and defect-management platform.
This repository currently contains the **Phase-0 foundation**: repository structure,
skeletons, infrastructure wiring, health checks and configuration — no business
features beyond what proves the foundation works.

Product flow (supported incrementally from Phase 1):

```text
LOGIN → PROJECT → TEST REPOSITORY → AI TEST GENERATION → HUMAN REVIEW
→ TEST EXECUTION → LIVE STATUS → RESULT → FAILURE ANALYSIS → DEFECT
→ JIRA TICKET → QUALITY ANALYTICS
```

## Architecture overview

```text
React Web App (apps/web)
        │ HTTPS
        ▼
ASP.NET Core Modular Monolith (src/)
  ├── AutoTestAi.Api            composition root, /api/v1, SignalR, health
  ├── AutoTestAi.Application    module seams + AI provider abstraction
  ├── AutoTestAi.Domain         clean entities/enums (no infra deps)
  ├── AutoTestAi.Infrastructure EF Core/PostgreSQL, Dapper, Valkey, MinIO
  └── AutoTestAi.Workflows      Temporal workflow skeleton + worker
        ├── PostgreSQL (metadata)   Temporal (workflows)   Valkey (cache)
        ├── MinIO/S3 (artifacts)    AI Gateway (stub → Ollama/OpenAI/Gemini)
        └── Playwright worker (workers/playwright, isolated container)
```

Key decisions (see `docs/adr/`): React frontend (ADR-001), modular monolith
(ADR-002), provider-independent AI (ADR-003), Temporal orchestration (ADR-004),
Playwright + TypeScript web automation (ADR-005), Docker first / K8s later (ADR-006).

## Technology stack

Frontend: React 19, TypeScript, Vite 7, React Router 7, TanStack Query 5,
Zustand 5, Tailwind CSS 4, shadcn-style UI, Apache ECharts (Phase 1), SignalR client.
Backend: ASP.NET Core 10, C#, EF Core 10, Dapper, Npgsql/PostgreSQL 16,
Keycloak/OIDC, Temporal, Valkey 8, MinIO, OpenTelemetry/OTLP, SignalR.
Workers: Playwright 1.55 + TypeScript (isolated container).

## Repository structure

```text
AutoTestAi/
├── apps/web/                  React frontend (shell + API/SignalR foundation)
├── src/
│   ├── AutoTestAi.Api/        Program, /api/v1, /hubs/execution, health
│   ├── AutoTestAi.Application/ Identity/Projects/TestCases/TestGeneration/
│   │                           TestExecution/Defects/Tickets/Reports/Integrations
│   ├── AutoTestAi.Domain/     entities + enums per docs/05-Database-Design.md
│   ├── AutoTestAi.Infrastructure/ DbContext, migrations, Valkey, MinIO, Dapper
│   └── AutoTestAi.Workflows/  Temporal starter, TestExecutionWorkflow, worker
├── workers/playwright/        isolated Playwright worker skeleton
├── tests/                     Unit / Integration / E2E (structure)
├── deploy/docker/             api/web Dockerfiles, nginx conf, Keycloak realm
├── scripts/                   new-migration.ps1, verify-backend.ps1
├── docs/                      product + architecture source of truth
├── docker-compose.yml         local stack (postgres, keycloak, temporal(+ui),
│                              valkey, minio, api, web, worker[profile])
├── .env.example               all environment variables (placeholders only)
└── AutoTestAi.sln
```

## Prerequisites

- .NET SDK 10 (`dotnet --version`)
- Node.js 22+ (`node --version`)
- Docker + Docker Compose plugin (for the infrastructure stack)
- EF Core CLI for migrations: `dotnet tool install --global dotnet-ef --version 10.0.0`

## Local setup

```powershell
# 1. Environment (never commit .env)
Copy-Item .env.example .env
# edit .env: set POSTGRES_PASSWORD, MINIO_ROOT_PASSWORD, KEYCLOAK_ADMIN_PASSWORD

# 2. Start infrastructure
docker compose up -d postgres valkey minio temporal keycloak
# artifacts bucket is created automatically by minio-init

# 3. Database — apply the Phase-0 migration
$cs = "Host=localhost;Port=5432;Database=autotestai;Username=postgres;Password=<from .env>"
$env:ConnectionStrings__Postgres = $cs
dotnet ef database update --project src/AutoTestAi.Infrastructure --startup-project src/AutoTestAi.Api

# 4. Backend
dotnet run --project src/AutoTestAi.Api
# API: http://localhost:5193  Swagger (dev): http://localhost:5193/swagger

# 5. Frontend (new shell)
cd apps/web
Copy-Item .env.example .env.local
npm install
npm run dev     # http://localhost:5173 (proxies /api + /hubs to the API)

# 6. Playwright worker skeleton (new shell)
cd workers/playwright
npm install
npm run build
$env:API_BASE_URL = "http://localhost:5193"
npm start       # health: http://localhost:8090/health
```

Full local stack (after infra is healthy):

```powershell
docker compose up -d --build            # api + web
docker compose --profile workers up -d --build   # + playwright worker
```

## Environment variables

See `.env.example` (root) and `apps/web/.env.example`. ASP.NET Core sections map
to env vars with `__` nesting (e.g. `ConnectionStrings__Postgres`,
`Authentication__Authority`, `Temporal__Address`, `Valkey__ConnectionString`,
`Minio__Endpoint/AccessKey/SecretKey`, `Ai__DefaultProvider`,
`Observability__OtlpEndpoint`). Never expose server secrets via `VITE_*`.

## Starting infrastructure / services

| Service | URL | Notes |
|---|---|---|
| API | http://localhost:5193 | `/api/v1/health`, `/health/live`, `/health/ready`, `/swagger` (dev) |
| Web | http://localhost:5173 | Vite dev (npm run dev) |
| Keycloak | http://localhost:8080 | realm `autotestai`, admin from `.env` |
| Temporal UI | http://localhost:8088 | workflow visibility |
| MinIO console | http://localhost:9001 | bucket `autotestai-artifacts` |
| PostgreSQL | localhost:5432 | db `autotestai` |
| Valkey | localhost:6379 | Redis protocol |

## Running tests

```powershell
dotnet test AutoTestAi.sln        # 23 tests: domain, AI stub, event contract, API health
cd apps/web; npm run build        # tsc + vite production build
cd workers/playwright; npm run build
.\scripts\verify-backend.ps1      # restore + release build + tests
```

New migration: `.\scripts\new-migration.ps1 -Name <MigrationName>`.

## Health checks

- `GET /health/live` — process liveness only; never fails due to external deps.
- `GET /health/ready` — readiness; `Unhealthy` (503) only when a *configured*
  PostgreSQL is unreachable. Unconfigured optional deps (Valkey/MinIO/Temporal)
  report `Degraded` (HTTP 200) so absence is visible, not fatal.
- `GET /api/v1/health` — always-200 report with per-dependency states, consumed
  by the dashboard shell.

## Current Phase-0 scope

Done: solution + module boundaries, EF model + `InitialCreate` migration (20 tables),
Keycloak/OIDC config + realm import, Temporal starter + workflow skeleton + worker
service, Valkey + MinIO abstractions, `IAiProvider` + stub, Playwright worker
skeleton with health endpoint, React shell (8 routes, design system, centralized
API client, SignalR abstraction, TanStack Query + Zustand), Compose stack,
OpenTelemetry foundation, structured logging, correlation IDs, tests.

Explicitly NOT in Phase 0: feature screens/CRUD, real AI adapters, worker test
execution, failure analysis, Jira sync, dashboards with real data, Appium,
self-healing, Kubernetes, CI/CD integrations.

## Phase-1 roadmap

Per `docs/07-Phase-1-Implementation-Plan.md`, build the first vertical slice
before isolated screens: login → project → test case → AI generation → review →
Playwright execution → result → failure analysis → defect → Jira ticket.

**Slice 1 — authentication + shell (done):** Keycloak OIDC login/logout
(`oidc-client-ts`, code flow, sessionStorage), protected routes, `GET
/api/v1/auth/me`, `ICurrentUserService` + `IAuthorizationService`, permission
constants, project-membership enforcement on execution endpoints and the
SignalR hub, JIT user provisioning on `external_identity_id`, permission-aware
navigation, user menu, mobile drawer. Security boundary is server-side;
frontend guards are UX only.

**Exact next recommended task:** Phase-1 slice 2 — Projects: project CRUD,
project details, members management and environment metadata, reusing the
Slice-1 authorization boundary (`projects.read`/`projects.manage` + membership).

## Important architecture decisions

- Domain has zero infrastructure dependencies (no EF/Temporal/Keycloak types).
- Business code depends on `IAiProvider`, never on Ollama/OpenAI/Gemini SDKs.
- Test code never executes inside the API process (isolated workers only).
- Secrets are referenced, never stored in plaintext or logged.
- Frontend: server state in TanStack Query, client state in Zustand, one API client.
