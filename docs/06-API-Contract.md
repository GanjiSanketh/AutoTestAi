# AutoTest AI — API Contract

**Base path:** `/api/v1`  
**Protocol:** HTTPS REST + SignalR  
**Format:** JSON

## 1. Rules

Version APIs explicitly. Use UUIDs. Return a consistent error envelope. Validate DTOs. Never expose secrets. Enforce authorization server-side. Paginate collections. Use idempotency for operations with external side effects.

## 2. Error Envelope

```json
{
  "error": {
    "code": "VALIDATION_ERROR",
    "message": "One or more validation errors occurred.",
    "details": [{"field":"title","message":"Title is required."}],
    "traceId": "..."
  }
}
```

## 3. Authentication

Keycloak/OIDC handles authentication. The API validates issuer, audience, signature, expiry and required claims. Authorization uses application roles/permissions and project membership.

```http
GET /api/v1/auth/me
```

Authenticated caller profile (safe fields only — never tokens or secrets):

```json
{
  "id": "uuid",
  "externalIdentityId": "keycloak-sub",
  "email": "user@example.com",
  "displayName": "Example User",
  "roles": ["tester"],
  "permissions": ["testcases.read"]
}
```

Status semantics: missing/invalid/expired token → `401`; authenticated caller
without the required permission or project membership → `403` (`FORBIDDEN`
envelope); authorization failures never return `500`. Unknown project ids
return `403` (not `404`) to avoid leaking project existence.

The `autotestai-web` realm client maps audience `autotestai-api` into access
tokens so API audience validation succeeds.

## 4. Dashboard

```http
GET /api/v1/dashboard/summary
GET /api/v1/dashboard/execution-trend
GET /api/v1/dashboard/bug-severity
GET /api/v1/dashboard/integration-status
```

Dashboard values must come from persisted backend data, not hardcoded demo values.

## 5. Projects

Implemented in Phase 1 Slice 2. List returns only accessible projects
(member projects, or all for platform admins), paginated
(`?page=&pageSize=&search=`, max page size 100).

```http
GET    /api/v1/projects
POST   /api/v1/projects                                    → 201
GET    /api/v1/projects/{projectId}
PUT    /api/v1/projects/{projectId}
DELETE /api/v1/projects/{projectId}                       → 204, soft delete (Archived)
GET    /api/v1/projects/{projectId}/members
POST   /api/v1/projects/{projectId}/members               → 201
PUT    /api/v1/projects/{projectId}/members/{userId}
DELETE /api/v1/projects/{projectId}/members/{userId}      → 204
GET    /api/v1/projects/{projectId}/environments
POST   /api/v1/projects/{projectId}/environments          → 201
PUT    /api/v1/environments/{environmentId}
DELETE /api/v1/environments/{environmentId}               → 204, soft delete (Archived)
GET    /api/v1/roles                                       (reference data for role assignment)
```

Rules: `projects.read` gates reads, `projects.manage` gates writes; every
project-scoped route additionally requires membership (admin bypass).
Inaccessible projects return `403`, never `404`. Project `key` is normalized
to upper-case, unique, and immutable after creation. `DELETE` archives instead
of physically deleting so execution history, defects and audit data survive.
Duplicate key/member → `409`; validation failures → `400` with field details.
Default environments must belong to the same project; deleting the default
clears it. Environments carry metadata only — no secrets (vault is a later
phase). Member lookup accepts user id or email; roles must come from the
`roles` table. Project operations emit `audit_events`
(`project.created/updated/archived`, `project.member_added`,
`project.member_role_updated`, `project.member_removed`,
`environment.created/updated/deleted`,
`project.default_environment_changed`).

## 6. Test Cases

Implemented in Phase 1 Slice 3. A test case is the logical identity;
a test case version is one immutable revision. List items never carry
`source_code` (loaded only via details/version endpoints).

```http
GET    /api/v1/projects/{projectId}/test-cases
POST   /api/v1/projects/{projectId}/test-cases          → 201, creates version 1
GET    /api/v1/test-cases/{testCaseId}
PUT    /api/v1/test-cases/{testCaseId}
DELETE /api/v1/test-cases/{testCaseId}                  → 204, soft delete (Archived)
GET    /api/v1/test-cases/{testCaseId}/versions         (newest first)
GET    /api/v1/test-cases/{testCaseId}/versions/{versionId}
POST   /api/v1/test-cases/{testCaseId}/review           → { versionId, reviewStatus }
```

List supports `?page=&pageSize=&search=&status=&priority=&framework=&platform=&reviewStatus=`
(max page size 100). Search covers key/title/module (case-insensitive).

Rules: `testcases.read` gates reads, `testcases.manage` gates writes; every
test-case route resolves test case → project and requires membership (admin
bypass). Unknown/inaccessible test cases return `403`, never `404`. Test keys
are normalized to upper-case, unique per project (duplicates across projects
allowed), and immutable after creation. `PUT` distinguishes logical metadata
(alway updatable) from versioned content (`sourceCode`/`structuredSteps`):
content edits allocate the next sequential version number server-side
(unique-violation retry, no client-chosen numbers) and leave history
untouched; metadata-only edits create no version. Versions are immutable —
there is no version-content update endpoint. `structured_steps` stays
structured JSONB (`[{order, action, target?, value?}]`, ≤500 steps).
Review lifecycle: Pending ↔ ChangesRequested ↔ Approved/Rejected with
`Approved → Pending` and `Rejected → Approved` rejected; same-state is a
no-op. `DELETE` archives instead of physically deleting so executions stay
reproducible via `test_case_version_id`. Duplicate key → `409`; validation
failures → `400` with field details. Test-case operations emit `audit_events`
(`testcase.created/updated/archived`, `testcase.version_created`,
`testcase.review_changed`). No endpoint executes test source code.

## 7. AI Generation

Implemented in Phase 1 Slice 4. Production-oriented generation behind the AI
gateway (ADR-003): the application depends only on `IAiProvider`; Ollama and
OpenAI adapters live in Infrastructure (HTTP, no vendor SDKs). Gemini is a
planned provider and is reported as unsupported until a real adapter lands —
support is never faked.

```http
POST /api/v1/projects/{projectId}/test-generation
GET  /api/v1/projects/{projectId}/ai-provider-status
```

Example request (project-scoped intent only — never provider/model/keys/prompts):

```json
{
  "title": "User login with valid credentials",
  "description": "Verify that a registered user can log in.",
  "targetUrl": "https://example.test",
  "framework": "playwright",
  "platform": "web",
  "module": "Authentication",
  "priority": "High",
  "additionalContext": "Use {{username}} / {{password}} placeholders.",
  "requirements": ["Validate successful login", "Validate dashboard navigation"]
}
```

Example response (normalized; `→ 200`):

```json
{
  "generationId": "uuid",
  "testCaseId": "uuid",
  "testKey": "AI-LOGIN-AB12CD",
  "versionId": "uuid",
  "versionNumber": 1,
  "status": "Succeeded",
  "title": "Successful user login",
  "framework": "playwright",
  "platform": "web",
  "structuredSteps": [{"order": 1, "action": "navigate", "target": "https://example.test", "value": null}],
  "sourceCode": "import { test } from '@playwright/test'; …",
  "assumptions": ["Username field uses #username."],
  "warnings": ["The selector was inferred and has not been verified."],
  "provider": "ollama",
  "model": "qwen3:8b",
  "promptVersion": "test-generation-v1",
  "latencyMs": 12345,
  "reviewStatus": "Pending"
}
```

Rules: `testcases.manage` + project membership gate generation (admin
bypass); anonymous → `401`, unauthorized → `403` (`FORBIDDEN`). Successful
generation creates a new test case (server-allocated `AI-*` key) with version 1
via `TestCaseService`: `sourceType = "ai"`, `reviewStatus = "Pending"`
(never auto-approved), existing `generation_provider` / `generation_model` /
`generation_latency_ms` columns populated, and the redacted normalized request
stored in `generation_request` (credentials masked as `[REDACTED]`). No new
tables. Audit events `test-generation.requested/completed/failed` carry safe
metadata only (provider, model, prompt version, latency, outcome).

`GET …/ai-provider-status` returns safe metadata only
(`provider/model/configured/detail/promptVersion`) for readers — never keys,
endpoints, or prompts. Validation failures → `400`; upstream rate limits →
`429` (plus a per-project generation budget); provider timeouts/unreachable →
`503`; malformed provider output → `502`; missing provider configuration →
`503` (`PROVIDER_NOT_CONFIGURED`); unknown/unimplemented provider → `500`
(`PROVIDER_NOT_SUPPORTED`). No endpoint executes generated code; failure
analysis (`AnalyzeFailureAsync`) stays unimplemented until Slice 6.

## 8. Execution

Implemented in Phase 1 Slice 5. Executions bind exactly one immutable
`TestCaseVersion` — never "latest". Only `Approved` versions may execute
(`409` otherwise); archived test cases and empty step lists are rejected.
History is immutable: reruns create new executions.

```http
POST /api/v1/projects/{projectId}/executions
GET  /api/v1/projects/{projectId}/executions?page=&pageSize=&status=&testCaseId=
GET  /api/v1/projects/{projectId}/executions/{executionId}
GET  /api/v1/projects/{projectId}/executions/{executionId}/steps
GET  /api/v1/projects/{projectId}/executions/{executionId}/logs?afterId=&take=
GET  /api/v1/projects/{projectId}/executions/{executionId}/artifacts
GET  /api/v1/projects/{projectId}/executions/{executionId}/artifacts/{artifactId}/download
POST /api/v1/projects/{projectId}/executions/{executionId}/cancel
```

Start request (exact version only — no scripts, paths, or credentials accepted):

```json
{
  "testCaseVersionId": "uuid",
  "environmentId": "uuid (optional, must belong to the project)",
  "browser": "chromium",
  "idempotencyKey": "optional client key (repeat returns the original, 200)"
}
```

Start response (`→ 202`, `200` when an idempotency key repeats):

```json
{
  "executionId": "uuid",
  "executionTestId": "uuid",
  "projectId": "uuid",
  "testCaseId": "uuid",
  "testCaseVersionId": "uuid",
  "status": "Queued",
  "workflowId": "test-execution-<id>",
  "createdAt": "...",
  "duplicated": false
}
```

Rules: `executions.execute` gates start, `executions.cancel` gates cancel,
`executions.read` gates reads; every route additionally requires membership
(admin bypass). Unknown/inaccessible executions return `403`, never `404`
(genuine 404 only after the boundary, e.g. admins). Status lifecycle:
Queued → Running → Passed/Failed/Cancelled/TimedOut/Error; terminal states
are final. Cancel is idempotent: Queued cancels immediately; Running asks
Temporal to cancel and the workflow persists the terminal state; terminal
executions return their status unchanged. Validation failures → `400`;
unapproved/archived/empty versions → `409`; Temporal or storage unavailable →
`503`. Step values on password-like targets persist as `[REDACTED]`;
artifact download returns a short-lived server-minted presigned URL
(`{downloadUrl, expiresInSeconds}`) — storage credentials never reach clients.

Live events on `/hubs/execution` (authorized subscription per execution):
`ExecutionStarted`, `ExecutionStatusChanged`, `ExecutionTestStarted`,
`ExecutionStepStarted`, `ExecutionStepCompleted`, `ExecutionLogReceived`
(bounded batches), `ExecutionTestCompleted`, `ExecutionCompleted`,
`ExecutionFailed`. Payloads carry no secrets. REST remains authoritative;
SignalR is the live-update mechanism.

## 9. Bugs (Defects)

Implemented in Phase 1 Slice 6. Defects are internal human-owned records;
external ticketing (Jira/Azure DevOps) is a later slice. AI can never create
defects — creation is an explicit authenticated user action.

```http
GET  /api/v1/projects/{projectId}/defects?page=&pageSize=&status=&severity=&classification=&testCaseId=&search=
POST /api/v1/projects/{projectId}/defects
GET  /api/v1/projects/{projectId}/defects/{defectId}
PUT  /api/v1/projects/{projectId}/defects/{defectId}
POST /api/v1/projects/{projectId}/defects/{defectId}/status
```

Create request (relationships derive server-side from the execution):

```json
{
  "executionId": "uuid (must be a failed execution in this project)",
  "title": "Login returns 500",
  "description": "Staging login fails after deploy.",
  "severity": "High",
  "failureAnalysisId": "uuid (optional, must belong to the same execution)"
}
```

Rules: `bugs.manage` gates create/update/status; `bugs.read` gates reads;
membership enforced (admin bypass); unknown/inaccessible defects → `403`
(genuine 404 post-boundary). Executions must be Failed/Error/TimedOut
(`409` otherwise); unknown execution ids → `403`. Status lifecycle:
Open → InProgress/Resolved/Closed/Rejected, InProgress → Open/Resolved/Closed,
Resolved → Closed/Open, Closed/Rejected → Open; invalid transitions → `400`
with field details. Every create/update/status/severity change emits an
audit event (`defect.created[_from_analysis]/updated/status_changed/
severity_changed/reopened`).

## 10. Tickets

Implemented in Phase 1 Slice 7. Manual Jira creation only — the internal
defect is the system of record and Jira is an external creation target.
No automatic, AI-driven, or webhook/polling behavior exists.

```http
POST /api/v1/projects/{projectId}/defects/{defectId}/ticket
GET  /api/v1/projects/{projectId}/defects/{defectId}/ticket
GET  /api/v1/projects/{projectId}/integrations/jira/status
PUT  /api/v1/projects/{projectId}/integrations/jira
```

Create response (`→ 201` new, `→ 200` when the ticket already exists):

```json
{
  "id": "uuid",
  "projectId": "uuid",
  "defectId": "uuid",
  "integrationId": "uuid",
  "provider": "jira",
  "externalId": "10001",
  "externalKey": "ABC-123",
  "externalUrl": "https://jira.example.atlassian.net/browse/ABC-123",
  "title": "[AutoTestAI] Login returns 500",
  "syncStatus": "Synced",
  "createdBy": "uuid",
  "createdAt": "...",
  "updatedAt": "...",
  "alreadyExisted": false
}
```

Status response (safe — never secrets):

```json
{
  "provider": "jira",
  "configured": true,
  "enabled": true,
  "projectKey": "ABC",
  "baseUrl": "https://jira.example.atlassian.net",
  "issueType": "Bug"
}
```

Upsert request (`settings.manage`; `apiToken` optional on update to retain
the stored secret):

```json
{
  "baseUrl": "https://jira.example.atlassian.net",
  "projectKey": "ABC",
  "email": "qa@example.com",
  "apiToken": "secret-on-create-only",
  "issueType": "Bug",
  "priorityMapping": {"Critical": "Highest"},
  "enabled": true
}
```

Rules: create requires `tickets.create` + membership (admin bypass);
status read requires `tickets.read`; upsert requires `settings.manage`
(admin-only in the default role map). Cross-project defect paths return
`403`. Missing/disabled/incomplete Jira configuration → `409`; duplicate
success → idempotent `200` (`alreadyExisted: true`), never a second Jira
issue (unique filtered index backs the check). Jira validation → `400`;
Jira auth/permission → `502` (`JIRA_AUTH_FAILED`/`JIRA_FORBIDDEN`); rate
limits → `429`; Jira unreachable/timeout → `503`/`502` without leaking
provider bodies. Audit events `ticket.creation_requested/created/
creation_failed` and `integration.jira_configured/updated` carry safe
metadata only. Timeouts are ambiguous by nature — the failure message tells
the operator to check Jira before retrying. Known limitations: manual
creation only; no bidirectional sync, webhooks, polling, Azure DevOps, or
automatic ticketing.

## 11. Integrations

```http
GET    /api/v1/projects/{projectId}/integrations
POST   /api/v1/projects/{projectId}/integrations
PUT    /api/v1/integrations/{integrationId}
DELETE /api/v1/integrations/{integrationId}
POST   /api/v1/integrations/{integrationId}/test
```

## 12. SignalR

Hub: `/hubs/execution`

Events: `ExecutionStarted`, `ExecutionStatusChanged`, `ExecutionTestStarted`, `ExecutionStepStarted`, `ExecutionStepCompleted`, `ExecutionLogReceived`, `ExecutionTestCompleted`, `FailureAnalysisCompleted`, `ExecutionCompleted`, `ExecutionFailed`.

The server authorizes access before a client subscribes to project/execution channels.
The hub requires authentication; `SubscribeToExecution` verifies the execution
exists, resolves its project, and enforces `executions.read` plus project
membership. Rejections surface as hub errors (`Execution not found.` /
`Forbidden: no access to this execution.`).

## 13. Failure Analysis

Implemented in Phase 1 Slice 6. Advisory AI explanations of failed
executions over bounded redacted evidence. Analysis never mutates execution
history, never modifies tests, and never creates defects.

```http
POST /api/v1/projects/{projectId}/executions/{executionId}/failure-analysis
GET  /api/v1/projects/{projectId}/executions/{executionId}/failure-analysis
GET  /api/v1/projects/{projectId}/executions/{executionId}/failure-analysis/attempts
```

Example response (`→ 200`):

```json
{
  "id": "uuid",
  "executionId": "uuid",
  "attempt": 1,
  "status": "Completed",
  "classification": "TestFailure",
  "summary": "The heading assertion failed.",
  "probableCause": "Expected text differs from the rendered page.",
  "confidence": 0.75,
  "evidence": ["step 2 assertText failed"],
  "assumptions": ["Login page under test."],
  "warnings": ["Only one log line was available."],
  "recommendedAction": "Inspect the heading selector.",
  "isLikelyDefect": false,
  "provider": "stub",
  "model": "stub-1.0",
  "promptVersion": "failure-analysis-v1",
  "latencyMs": 12
}
```

Rules: triggering requires `executions.analyze`; reading requires
`executions.read`; membership enforced (admin bypass). Only Failed/Error/
TimedOut executions may be analyzed (`409` otherwise); concurrent Running
attempts are rejected with `409` (unique filtered index); retries create new
attempts without rerunning the test. Evidence is bounded by
`AI:FailureAnalysis` options (log lines/chars, error size, failed steps,
artifact refs) with explicit truncation markers, and redacted before any
provider sees it. Provider failures map like Slice 4 (`429`/`502`/`503`);
malformed output is rejected (attempt marked Failed); cancellation marks the
attempt Cancelled without touching the execution. Audit events
`failure-analysis.requested/completed/failed/cancelled` carry safe metadata
only; `FailureAnalysisCompleted` is published on success.
