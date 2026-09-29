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
| Quality dashboard | dashboard + reports API + ECharts (Slice 8: project-scoped read-only `IDashboardService`/`IReportService` over EF aggregates; summary/trend/failure-breakdown/defect/ticket overviews; paginated execution/defect/ticket reports; pass rate = Passed ÷ terminal, null when empty; deterministic classification authoritative; ticket metrics from internal records, never Jira; no prediction, AI analytics, or exports) |
| Execution grid | grid_workers + grid_assignments (Slice 9: project-scoped read/write `IExecutionGridService`/`IGridScheduler` over EF aggregates; worker registration/heartbeat, capacity leases, lease renewal/expiry, stale worker reaping; deterministic least-loaded scheduling with capability matching; atomic claim via unique filtered index; global/project concurrency ceilings; no auto-scaling, AI scheduling, self-healing, or Phase 2+ features) |
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
