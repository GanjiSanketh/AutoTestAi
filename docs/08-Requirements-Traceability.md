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
| AI test generation | AI Gateway + provider adapters |
| Test execution | Temporal + isolated Playwright worker |
| Live execution | SignalR |
| Failure analysis | failure_analyses + AI analysis |
| Defect management | defects |
| Jira | tickets + integrations |
| Quality dashboard | dashboard API + ECharts |
| Audit | audit_events |

## 3. NFR Traceability

| NFR | Implementation |
|---|---|
| Parallel execution | Horizontally scalable worker pool |
| AI latency | Provider/model metrics and configuration |
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
