# AutoTest AI — Technology Stack & Architecture Decision Document

**Project:** AutoTest AI  
**Repository:** `H:\Applications\AutoTestAi`  
**Application Type:** Web application  
**Architecture Style:** Modular monolith + durable workflow orchestration + isolated execution workers  
**Decision Status:** Proposed baseline for Phase 1

> This document intentionally evaluates technology independently of the team's existing skills. It records the recommended implementation stack for a maintainable, scalable, open-source-first product.

---

# 1. Technology Selection Principles

The technology stack should optimize for:

1. Zero or minimal software licensing cost.
2. Strong open-source ecosystem.
3. Enterprise security and extensibility.
4. Excellent support for browser automation.
5. AI provider independence.
6. Strong real-time execution support.
7. Durable long-running workflows.
8. Horizontal scaling of test workers.
9. Avoidance of premature microservices complexity.
10. Easy local development with a path to production.

Infrastructure/cloud/GPU/API inference costs are separate from software licensing costs.

---

# 2. Recommended Stack

| Area | Recommended technology | Role |
|---|---|---|
| Frontend | React + TypeScript + Vite | Web application |
| Frontend routing | React Router | Application navigation |
| Server-state | TanStack Query | API data fetching/cache/synchronization |
| Client-state | Zustand | Small global UI/session state |
| Styling | Tailwind CSS | Design-system implementation |
| Components | shadcn/ui | Accessible reusable UI primitives |
| Charts | Apache ECharts | Analytics/quality dashboards |
| Code editor | Monaco Editor | Automation-code editing/diff |
| Backend | ASP.NET Core | Core API/business platform |
| Language | C# | Backend implementation |
| API contract | OpenAPI | API documentation/contract |
| Real-time | ASP.NET Core SignalR | Execution/log/status streaming |
| ORM | EF Core | Domain persistence/migrations |
| Query optimization | Dapper | Reporting/complex/read-heavy queries |
| Database | PostgreSQL | Primary relational database |
| Identity | Keycloak | Authentication/SSO/RBAC foundation |
| Workflow | Temporal | Durable test/execution workflows |
| Cache/ephemeral state | Valkey | Cache, locks, transient state |
| Object storage | MinIO/S3-compatible storage | Screenshots, videos, traces, reports |
| Web automation | Playwright | Primary web execution engine |
| Automation language | TypeScript | Primary generated Playwright target |
| Mobile | Appium | Phase 3 mobile execution |
| Container runtime | Docker | Worker isolation |
| Orchestration | k3s/Kubernetes | Phase 2+ execution scaling |
| AI gateway | Provider abstraction / LiteLLM-compatible gateway | AI routing and provider independence |
| Local AI | Ollama | Development/private AI inference option |
| Cloud AI | Provider adapters (e.g. OpenAI/Gemini) | Production AI options |
| Telemetry | OpenTelemetry | Unified telemetry |
| Metrics | Prometheus | Metrics/alerts |
| Logs | Loki | Centralized logs |
| Traces | Tempo | Distributed tracing |
| Visualization | Grafana | Observability dashboards |
| CI/CD | GitHub Actions initially | Build/test/deploy automation |
| Source control | Git | Version control |

---

# 3. Architecture Style

## 3.1 Phase 1: Modular Monolith

The initial backend should **not** be split into many microservices.

```text
ASP.NET Core Application
│
├── Identity & Access
├── Organizations / Workspaces
├── Projects
├── Test Repository
├── Test Plans / Suites
├── Execution
├── Defects / Triage
├── Integrations
├── AI Orchestration
├── Reporting / Analytics
└── Audit / Security
```

Benefits:

- Fast initial delivery
- Easier local development
- One deployment unit for business APIs
- Strong module boundaries
- Easy extraction later if scale demands it

---

# 4. Execution Architecture

Test execution is separated from the business API.

```text
ASP.NET Core
     |
     v
Temporal Workflow
     |
     v
Execution Worker
     |
     +---- Playwright Worker
     +---- API Worker
     +---- Mobile Worker (Phase 3)
```

Workers run in isolated containers.

The platform should not execute arbitrary customer-generated code directly inside the main API process.

---

# 5. Why Temporal

The original BRD proposes RabbitMQ as an async message bus. For AutoTest AI, the primary requirement is more than simple messaging: test execution contains long-running workflows, retries, worker failures, artifact collection, failure analysis, and multi-step orchestration.

Therefore the recommended decision is:

**Temporal for durable workflow orchestration.**

RabbitMQ may still be introduced later for specialized event/message patterns if a concrete requirement emerges.

Example workflow:

```text
StartExecution
   |
   +--> Validate environment
   |
   +--> Allocate workers
   |
   +--> Execute test set
   |
   +--> Collect artifacts
   |
   +--> Retry eligible failures
   |
   +--> Analyze failures
   |
   +--> Create/update defects
   |
   +--> Update analytics
   |
   +--> Complete execution
```

---

# 6. Why PostgreSQL

PostgreSQL is the primary system of record because the domain is strongly relational.

Core entities include:

- Organization
- User
- Role
- Permission
- Project
- Environment
- TestCase
- TestSuite
- TestPlan
- Execution
- ExecutionStep
- Worker
- Failure
- Defect
- Ticket
- Integration
- AIRequest
- Artifact
- AuditLog

Large binary artifacts should not be stored in PostgreSQL.

---

# 7. Artifact Storage

Use object storage for:

- Screenshots
- Videos
- Playwright traces
- HTML reports
- Large logs
- Visual comparison images
- AI evidence

Store only metadata/references in PostgreSQL.

```text
PostgreSQL
   |
   +-- artifact id
   +-- execution id
   +-- type
   +-- storage key
   +-- created timestamp

Object Storage
   |
   +-- screenshots/
   +-- videos/
   +-- traces/
   +-- reports/
   +-- visual-diffs/
```

---

# 8. Identity & Authorization

Use Keycloak as the identity layer rather than implementing password/SSO infrastructure from scratch.

Target capabilities:

- Login
- OIDC
- SAML federation where required
- Azure AD / Microsoft Entra integration
- Okta
- Google Workspace
- Roles
- Groups
- Token issuance

Application-level authorization remains necessary for project/resource permissions.

---

# 9. AI Architecture

AI must be provider-independent.

```text
Application
    |
    v
AI Orchestrator
    |
    v
AI Provider Abstraction
    |
    +---- Local/private provider
    +---- OpenAI provider
    +---- Gemini provider
    +---- Future providers
```

### Local AI

Ollama is an optional local/private inference runtime. It is useful for:

- Local development
- Cost-controlled development
- Private/on-premise deployments
- Customers who cannot send test data to external AI providers

### Production SaaS

Cloud AI providers can be used when operational simplicity and model capability are more important than self-hosting inference.

### Important rule

The application must not directly depend on Ollama/OpenAI/Gemini-specific code throughout business modules. All provider calls must go through the AI abstraction/orchestration layer.

---

# 10. AI Test Generation Safety Pipeline

AI should not directly produce arbitrary executable code and immediately execute it.

Recommended flow:

```text
Natural Language / Story / OpenAPI
              |
              v
        AI Generation
              |
              v
     Structured Test JSON
              |
              v
       Schema Validation
              |
              v
       Test Specification
              |
              v
        Code Generator
              |
              v
     Playwright TypeScript
              |
              v
       Static Validation
              |
              v
       Sandboxed Worker
              |
              v
          Execution
```

This directly addresses the BRD risk around AI hallucinated/broken automation code.

---

# 11. Primary Web Automation Decision

**Playwright is the primary web execution framework for Phase 1.**

Reasons:

- Modern browser automation
- Chromium/Firefox/WebKit support
- Strong tracing
- Screenshots/video
- Network and console visibility
- Parallel execution
- Good TypeScript integration
- Strong foundation for AI-generated automation

Selenium remains an integration/secondary framework target if required by customers or later phases.

---

# 12. Generated Test Language

The recommended primary generated web-test language is **TypeScript**.

Reason:

```text
React frontend
      |
TypeScript ecosystem
      |
AI generation
      |
Playwright TypeScript
```

This minimizes ecosystem fragmentation.

The BRD's C# generation requirement should be treated as a supported target that can be added where customer demand requires it; it does not need to be the only generation target in Phase 1.

---

# 13. Real-Time Execution

Use SignalR for:

- Execution state changes
- Worker state
- Step progress
- Live log events
- Failure notifications
- Completion events

Example:

```text
Worker
  |
  | execution event
  v
ASP.NET Core / SignalR
  |
  v
Browser
  |
  +-- status
  +-- progress
  +-- terminal
  +-- artifacts
```

---

# 14. Cache & Ephemeral Data

Use Valkey for:

- Cache
- Distributed locks
- Short-lived state
- Rate limiting
- Temporary coordination

PostgreSQL remains the source of truth.

---

# 15. Observability

All backend and worker components should emit OpenTelemetry telemetry.

```text
OpenTelemetry
   |
   +--> Prometheus -> Grafana
   +--> Loki       -> Grafana
   +--> Tempo      -> Grafana
```

Minimum correlation fields:

- request ID
- organization ID
- project ID
- execution ID
- test case ID
- worker ID
- workflow ID
- AI request ID

This is important for reproducing failures across distributed execution.

---

# 16. Security Architecture

Security baseline:

- TLS for network traffic
- Encryption for sensitive configuration
- Secrets never stored in source control
- Secrets never exposed in frontend payloads
- RBAC and least privilege
- Audit logs for administrative/security actions
- Sensitive log/screenshot redaction
- Isolated test workers
- Sandboxed automation execution
- Separate customer/project data boundaries

The exact cryptographic implementation must be reviewed against deployment and compliance requirements rather than assuming a specific library implementation prematurely.

---

# 17. Deployment Strategy

## Phase 1 Development

```text
Docker Compose
├── Web
├── API
├── PostgreSQL
├── Keycloak
├── Valkey
├── MinIO
├── Temporal
└── Ollama (optional)
```

## Phase 1 Production

Start with a small number of deployable units:

```text
Web
API
Worker(s)
PostgreSQL
Object Storage
Temporal
Identity
AI Provider
```

## Phase 2+ Scale

Introduce Kubernetes/k3s for horizontally scalable execution workers.

---

# 18. Repository Structure

Recommended high-level repository layout:

```text
AutoTestAi/
├── docs/
│   ├── 01-BRD.md
│   ├── 02-Design-System.md
│   ├── 03-Tech-Stack-and-Architecture.md
│   ├── 04-Architecture.md
│   ├── 05-Database-Design.md
│   ├── 06-API-Contract.md
│   └── adr/
│
├── src/
│   ├── web/
│   ├── api/
│   ├── workers/
│   └── shared/
│
├── tests/
│   ├── unit/
│   ├── integration/
│   └── e2e/
│
├── infra/
│   ├── docker/
│   ├── compose/
│   └── kubernetes/
│
└── README.md
```

Exact project names may be finalized during scaffolding.

---

# 19. Architecture Decisions

## ADR-001 — React over Angular

**Decision:** React + TypeScript + Vite.

**Reason:** Strong ecosystem for developer tooling, code editors, AI interfaces, complex dashboards, and browser-automation products.

## ADR-002 — PostgreSQL as primary database

**Decision:** PostgreSQL.

**Reason:** Strong relational model, mature ecosystem, open-source licensing, excellent support for transactional and analytical workloads.

## ADR-003 — Temporal over RabbitMQ for primary execution orchestration

**Decision:** Temporal.

**Reason:** Durable workflows, retries, failure recovery, long-running execution orchestration.

## ADR-004 — Playwright as primary web automation engine

**Decision:** Playwright.

**Reason:** Browser coverage, tracing, artifacts, parallelism, modern web support, TypeScript integration.

## ADR-005 — Provider-independent AI

**Decision:** AI abstraction/orchestration layer.

**Reason:** Avoid vendor lock-in and support local/private and cloud providers.

## ADR-006 — Modular monolith first

**Decision:** Keep core business APIs in a modular monolith during MVP.

**Reason:** Faster delivery and lower operational complexity while preserving module boundaries.

## ADR-007 — Docker workers, Kubernetes later

**Decision:** Containerize execution workers immediately; introduce Kubernetes when parallel execution scale justifies it.

**Reason:** Avoid premature infrastructure complexity.

---

# 20. Phase 1 Technology Scope

Only the following should be considered mandatory for the first implementation increment:

### Frontend

- React
- TypeScript
- Vite
- React Router
- TanStack Query
- Tailwind CSS
- shadcn/ui
- Monaco where code editing is required

### Backend

- ASP.NET Core
- C#
- OpenAPI
- SignalR
- EF Core
- Dapper where required

### Platform

- PostgreSQL
- Keycloak
- Temporal
- Valkey
- MinIO

### Automation

- Playwright + TypeScript

### AI

- Provider abstraction
- One production provider adapter
- Ollama optional for local development

### Deployment

- Docker
- Docker Compose for local development

Kubernetes, Appium, advanced visual regression, predictive analytics, and extensive CI/CD integrations remain later-phase work.

---

# 21. Definition of Architecture Done

Before Phase 1 feature development begins, the repository must have:

- documented stack
- documented module boundaries
- environment configuration strategy
- local Docker setup
- database migration strategy
- authentication strategy
- API contract strategy
- logging/telemetry strategy
- test execution isolation strategy
- AI provider abstraction
- artifact storage strategy
- coding standards
- branch/commit conventions

