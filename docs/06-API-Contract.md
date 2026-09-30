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

## 4. Dashboard & Reports

Implemented in Phase 1 Slice 8. Read-only descriptive analytics over
persisted Slices 1–7 data. All routes are project-scoped, require
membership (admin bypass), and never mutate state, call AI providers, or
contact Jira. Dashboard reads require `dashboard.read`; reports require
`reports.read`. Unknown/inaccessible projects return `403`.

Date range: optional `from`/`to` (ISO-8601; date-only values are UTC
calendar days). Defaults to the last 30 days; maximum 365 days;
`from <= to` is enforced (`400` otherwise). Timestamps stay UTC.

```http
GET /api/v1/projects/{projectId}/dashboard/summary?from=&to=
GET /api/v1/projects/{projectId}/dashboard/execution-trend?from=&to=&granularity=
GET /api/v1/projects/{projectId}/dashboard/failure-breakdown?from=&to=
GET /api/v1/projects/{projectId}/dashboard/defects?from=&to=
GET /api/v1/projects/{projectId}/dashboard/tickets?from=&to=
GET /api/v1/projects/{projectId}/reports/executions?from=&to=&status=&testCaseId=&classification=&page=&pageSize=
GET /api/v1/projects/{projectId}/reports/defects?from=&to=&status=&severity=&classification=&search=&page=&pageSize=
GET /api/v1/projects/{projectId}/reports/tickets?from=&to=&provider=&syncStatus=&page=&pageSize=
```

Semantics:

- KPIs: test-case totals (+ approved = latest version `Approved`);
  execution totals by `ExecutionStatus`; pass rate = `Passed ÷ terminal`
  where terminal = Passed/Failed/Cancelled/TimedOut/Error (Queued/Running
  excluded), `null` when no terminal executions (never `NaN`, never a
  misleading `0%`); defect totals by `DefectStatus` + high/critical count;
  ticket totals by `TicketSyncStatus` from internal records.
- Trend: daily UTC buckets (weekly when the range exceeds 62 days or
  `granularity=week`); missing days render as zeros server-side.
- Failure breakdown: authoritative deterministic
  `ExecutionTest.FailureClassification` over terminal tests only; `Unknown`
  appears only when such rows exist. AI advisory output is never consulted.
- Recent lists are bounded (8); activity reuses `audit_events` with a fixed
  safe-action whitelist (no metadata payloads).
- Reports paginate with the established `{items,totalCount,page,pageSize}`
  envelope (max page size 100). Invalid enum filters return `400` with field
  details. No secrets, logs, artifacts, or credentials appear in responses.

Dashboard values must come from persisted backend data, not hardcoded demo values.

### 4.1 Executive Analytics (Phase 2 Slice 12)

Deterministic historical analytics over existing tables (no new tables).
Same project scoping, UTC date-range rules, and permission model as above:
dashboard routes require `dashboard.read`, report/export routes require
`reports.read`. Null means insufficient data (never a misleading zero).

```http
GET /api/v1/projects/{projectId}/dashboard/executive-overview?from=&to=
GET /api/v1/projects/{projectId}/dashboard/flakiness-trend?from=&to=&granularity=
GET /api/v1/projects/{projectId}/dashboard/healing?from=&to=
GET /api/v1/projects/{projectId}/dashboard/durations?from=&to=
GET /api/v1/projects/{projectId}/dashboard/readiness?from=&to=
GET /api/v1/projects/{projectId}/reports/flakiness?from=&to=&search=&flakyOnly=&minExecutions=&module=&priority=&framework=&healedOnly=&sort=&descending=&page=&pageSize=
GET /api/v1/projects/{projectId}/reports/flakiness/export?from=&to=&search=&flakyOnly=&minExecutions=&module=&priority=&framework=&healedOnly=
```

Scales: `passRate`/`failRate` are 0-1 ratios (Slice 8 convention);
`flakinessIndex`, `flakinessRate`, `automationCoverage`, `releaseReadiness`,
component values, `defectsPer100Executions`, `healingSuccessRate` are 0-100.

Semantics:

- One logical execution = one Execution row (engine infra-retries reuse the
  execution, so retries never double-count). Verdicts: Passed/Failed;
  Cancelled excluded; TimedOut/Error reported as unstable (neither
  flaky-making nor flaky-blocking); Queued/Running excluded.
- Flaky test = >=1 Passed AND >=1 Failed in the window (always-failing is
  failure-prone, always-passing is not flaky). Test rate =
  100*min(P,F)/(P+F), null when <2 verdicts. Project index =
  100*flaky/eligible(>=2 verdicts), null when empty. Trend buckets are
  daily (weekly rollup past 62 days); a null bucket index means no data.
- Automation coverage = 100*(non-archived cases with latest version
  Approved)/(non-archived cases), null when empty.
- Release readiness (0-100, informational only, never an AI decision): 35%
  pass rate + 20% flakiness health (100-index) + 15% coverage + 15% defect
  health (max(0,100-25*open Critical/High)) + 15% completion health
  (terminal/total); unknown components excluded with renormalized weights;
  null when pass rate unknown. Bands: >=80 Ready, >=60 Caution, else
  NeedsAttention. Every component exposes value, weight, contribution,
  threshold, and detail.
- Defect density proxy = 100*(defects created in window)/(terminal
  executions), null when empty (creation-date based).
- Durations use valid samples only (non-null, >=0ms): count/avg/min/max/total
  server-side; p50/p90 over a capped (50000) sorted fetch; daily averages.
  No SLA targets exist in the domain: `slaConfigured` is false and
  open-defect aging (<7d/7-30d/>30d) is reported instead.
- Healing reuses Slice 11 rows: attempts/applied/failed, deterministic vs
  AI-assisted split, success = 100*applied/attempts, distinct tests and
  executions, daily trend, and a neutral healed-and-flaky co-occurrence
  count (no causal claims, no prediction).
- Flakiness report sorting allowlist: `testKey`, `title`, `executions`,
  `flakinessRate`, `lastRun` (nulls last, TestKey tiebreak); anything else
  returns `400`. Deleted test cases are skipped, never surfaced blind.
- CSV export (`text/csv`, attachment): same filters, TestKey order, max 5000
  rows, RFC-4180 quoting, safe columns only (no secrets, logs, or evidence).

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
the operator to check Jira before retrying. Ticket `origin` is `Manual`
for every Slice-7 ticket. Known limitations carried from Slice 7: no
bidirectional sync, webhooks, polling, or Azure DevOps.

### 10.1 Automated Ticketing (Phase 2 Slice 10)

Policy-controlled automatic Jira creation for eligible internal defects.
Slice 7 stays manual-only in behavior; automation is an additional
system path that reuses the same provider and idempotency, and manual
and automatic tickets converge on the same Synced record.

```http
GET /api/v1/projects/{projectId}/auto-ticket-policy
PUT /api/v1/projects/{projectId}/auto-ticket-policy
GET /api/v1/projects/{projectId}/auto-ticket-policy/status
POST /api/v1/projects/{projectId}/defects/{defectId}/ticket/automation/retry
```

Policy upsert (`settings.manage`):

```json
{
  "enabled": true,
  "integrationId": null,
  "severities": ["Critical", "High"],
  "defectStatuses": ["Open"],
  "classifications": ["ApplicationDefect"],
  "minimumConfidence": 0.7
}
```

Rules: no policy (or `enabled: false`) means no automation — the safe
default. Evaluation is deterministic (severity, defect status, failure
classification, optional confidence threshold); no AI decides. The
defect request path never calls Jira; a Pending automatic intent is
persisted and executed by the background service. Every execution —
first attempt, retry, background reconciliation, operator retry — first
re-evaluates the current policy, so disabling the policy stops future
Jira creation (operator retry against an ineligible policy is rejected
with `409`). Retryable Jira failures (timeout, 429, 5xx) schedule
bounded retries (max 5 attempts, exponential backoff; a server
`Retry-After` hint is honored but clamped to 1s–1h); validation (400),
auth (401), permission (403), not-found (404), and malformed responses
are permanent and never spin. Retry returns `202` when requeued and
idempotent `200` when a Synced ticket already exists. Reads require
`tickets.read`; retry requires `tickets.create`. Claim/lease semantics:
one Pending intent per defect per integration (unique filtered index);
execution requires a live database-backed claim (`ClaimToken` +
`ClaimExpiresAt`, compare-and-set via `RowVersion`), so two API
instances cannot execute Jira twice for the same intent; only the live
claimant's writes land and Synced never regresses; expired claims are
reclaimable after a crash. Claim tokens never appear in any response,
log, or audit payload. Audit events
`ticket.automation.skipped/requested/created/failed/retry_scheduled/
recovered/superseded` and `autoticket.policy_configured/updated` carry
safe metadata only with system (not human) actor attribution.
Crash consistency: (A) before intent persistence — nothing to recover,
next trigger retries; (B) after intent persistence — reconciliation
re-discovers the Pending row; (C) after claim, before Jira — lease
expires, another worker reclaims, exactly one executes at a time;
(D) during the Jira request (crash/timeout) — ambiguous, recorded on
recovery (`ticket.automation.recovered`) and retried bounded;
(E) after Jira accepts but before local persistence — the residual
window: a bounded retry may create a second external issue, and no
Jira-supported idempotent POST exists in the integrated API surface to
close it, so the limitation stays explicit and operators are told to
check Jira before manually retrying; (F) after local Synced persistence
— terminal, idempotent, all later triggers converge. Exactly-once
external creation is therefore NOT claimed; what IS guaranteed is
at-most-one concurrent executor, single authoritative internal Ticket,
bounded retries, no silent loss, and full auditability.

### 10.2 Self-Healing (Phase 2 Slice 11)

Deterministic-first execution-time locator recovery. Healing never
mutates stored tests: it retries the original action once with a
validated alternate locator, or preserves the original failure.

```http
GET /api/v1/projects/{projectId}/self-healing-policy
PUT /api/v1/projects/{projectId}/self-healing-policy
GET /api/v1/projects/{projectId}/self-healing-policy/status
GET /api/v1/projects/{projectId}/executions/{executionId}/healing
POST /api/v1/execution-grid/assignments/{assignmentRef}/healing/suggest
```

Policy upsert (`settings.manage`; reads require `executions.read`):

```json
{
  "enabled": true,
  "aiFallbackEnabled": false,
  "minDeterministicScore": 50,
  "minAiConfidence": 0.7,
  "allowedStrategies": ["css", "xpath", "role", "text", "testid"]
}
```

Rules: no policy (or `enabled: false`) means no healing -- the safe
default, with zero per-step overhead. Only locator-plausible failures
on healable Playwright actions qualify; assertion, navigation,
network/auth, environment, cancellation, and non-Playwright scopes
never heal. Deterministic candidates (test attributes, role, label,
text, stable id/name) are validated against the live DOM first:
exactly one visible, enabled, action-compatible match, otherwise
reject (ambiguity is never suppressed with `first()`/`nth()`). AI
fallback requires `aiFallbackEnabled` plus bounded redacted evidence
(max 4000 chars, no secrets/cookies/storage/full DOM) and returns locator
DATA only, schema-validated (`{candidates: [{strategy, value,
reason}]}`; code/JS/unsupported strategies rejected) and live-validated
identically -- confidence never overrides validation. One retry per
step; healed steps pass with original targets preserved in history,
failed healing preserves the original failure and flows into the
normal classification-to-defect-to-ticket pipeline (healed steps do
not create defects). Persistence is one fenced outcome row per
(execution test, step); stale leases are rejected (`409`). The worker
machine plane (`healing/suggest`) authenticates with the per-assignment
lease token (unknown refs return `404`, mismatches `401`) and resolves the
configured `IAiProvider` server-side -- the worker never holds provider
keys. Audit events `self-healing.policy_configured/updated`,
`self-healing.ai_suggested/ai_failed/ai_timeout`, and
`self-healing.recorded` carry safe metadata only.

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

Events: `ExecutionStarted`, `ExecutionStatusChanged`, `ExecutionTestStarted`, `ExecutionStepStarted`, `ExecutionStepCompleted`, `ExecutionLogReceived`, `ExecutionTestCompleted`, `FailureAnalysisCompleted`, `ExecutionCompleted`, `ExecutionFailed`, `ExecutionQueued`, `ExecutionAssigned`, `WorkerStatusChanged`, `SelfHealingApplied`, `SelfHealingFailed`.

Slice 11 reuses the existing hub: the granular `self-healing.*`
lifecycle (`started`, `candidate_generated/rejected/validated`,
`applied`, `failed`, `skipped`) streams as execution logs, while
`SelfHealingApplied` supplements step completion for applied
recoveries. `ExecutionStepCompleted` step payloads carry additive
`healed`, `recoveredTarget`, `healingStrategy`, and `aiAssisted` fields.

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
# #   1 4 .   E x e c u t i o n   G r i d   ( P h a s e   2 ,   S l i c e   9 )  
  
 P r o j e c t - i n d e p e n d e n t   g r i d   m a n a g e m e n t ;   r e q u i r e s   ` s e t t i n g s . m a n a g e ` .  
  
 # # #   W o r k e r   R e g i s t r a t i o n   ( m a c h i n e   a u t h )  
  
 ` ` ` h t t p  
 P O S T   / a p i / v 1 / e x e c u t i o n - g r i d / w o r k e r s / r e g i s t e r  
 ` ` `  
  
 R e q u e s t   ( p r o v i s i o n i n g   t o k e n   r e q u i r e d ) :  
 ` ` ` j s o n  
 {  
     " w o r k e r K e y " :   " w o r k e r - a b c 1 2 3 " ,  
     " d i s p l a y N a m e " :   " C I   W o r k e r   1 " ,  
     " w o r k e r T y p e " :   " p l a y w r i g h t " ,  
     " f r a m e w o r k " :   " p l a y w r i g h t " ,  
     " b r o w s e r s " :   [ " c h r o m i u m " ,   " f i r e f o x " ] ,  
     " v e r s i o n " :   " p h a s e 2 - s l i c e 9 " ,  
     " c a p a c i t y " :   4 ,  
     " b a s e U r l " :   " h t t p : / / w o r k e r - h o s t : 8 0 9 0 " ,  
     " p r o v i s i o n i n g T o k e n " :   " g r i d - p r o v i s i o n i n g - s e c r e t "  
 }  
 ` ` `  
  
 R e s p o n s e   ( ` 2 0 0 ` ) :  
 ` ` ` j s o n  
 {  
     " w o r k e r I d " :   " u u i d " ,  
     " c r e d e n t i a l " :   " o n e - t i m e - s e c r e t " ,  
     " h e a r t b e a t I n t e r v a l S e c o n d s " :   3 0 ,  
     " l e a s e D u r a t i o n S e c o n d s " :   3 0 0  
 }  
 ` ` `  
  
 # # #   W o r k e r   H e a r t b e a t   ( m a c h i n e   a u t h )  
  
 ` ` ` h t t p  
 P O S T   / a p i / v 1 / e x e c u t i o n - g r i d / w o r k e r s / { w o r k e r I d } / h e a r t b e a t  
 ` ` `  
  
 R e q u e s t   ( B e a r e r   t o k e n   =   c r e d e n t i a l   f r o m   r e g i s t r a t i o n ) :  
 ` ` ` j s o n  
 {   " c a p a c i t y " :   4 ,   " a c t i v e A s s i g n m e n t C o u n t " :   1 ,   " v e r s i o n " :   " p h a s e 2 - s l i c e 9 "   }  
 ` ` `  
  
 R e s p o n s e   ( ` 2 0 0 ` ) :  
 ` ` ` j s o n  
 {  
     " w o r k e r I d " :   " u u i d " ,  
     " e f f e c t i v e S t a t u s " :   " A v a i l a b l e " ,  
     " d r a i n i n g " :   f a l s e ,  
     " s e r v e r T i m e U n i x M s " :   1 6 9 9 9 9 9 9 9 9 0 0 0 ,  
     " h e a r t b e a t I n t e r v a l S e c o n d s " :   3 0  
 }  
 ` ` `  
  
 # # #   G r i d   S t a t u s   &   M a n a g e m e n t   ( s e t t i n g s . m a n a g e )  
  
 ` ` ` h t t p  
 G E T     / a p i / v 1 / e x e c u t i o n - g r i d / s t a t u s  
 G E T     / a p i / v 1 / e x e c u t i o n - g r i d / w o r k e r s  
 G E T     / a p i / v 1 / e x e c u t i o n - g r i d / w o r k e r s / { w o r k e r I d }  
 P O S T   / a p i / v 1 / e x e c u t i o n - g r i d / w o r k e r s / { w o r k e r I d } / d r a i n  
 P O S T   / a p i / v 1 / e x e c u t i o n - g r i d / w o r k e r s / { w o r k e r I d } / d i s a b l e  
 P O S T   / a p i / v 1 / e x e c u t i o n - g r i d / w o r k e r s / { w o r k e r I d } / e n a b l e  
 ` ` `  
  
 ` G E T   / s t a t u s `   r e t u r n s :  
 ` ` ` j s o n  
 {  
     " t o t a l W o r k e r s " :   4 ,  
     " a v a i l a b l e W o r k e r s " :   2 ,  
     " b u s y W o r k e r s " :   1 ,  
     " d r a i n i n g W o r k e r s " :   0 ,  
     " u n h e a l t h y W o r k e r s " :   0 ,  
     " o f f l i n e W o r k e r s " :   1 ,  
     " d i s a b l e d W o r k e r s " :   0 ,  
     " t o t a l C a p a c i t y " :   1 6 ,  
     " a c t i v e A s s i g n m e n t s " :   3 ,  
     " a v a i l a b l e S l o t s " :   1 3 ,  
     " q u e u e d E x e c u t i o n s " :   2 ,  
     " a c t i v e L e a s e s " :   3 ,  
     " e x p i r e d L e a s e s L a s t H o u r " :   0 ,  
     " w o r k e r s " :   [ . . . ]  
 }  
 ` ` `  
  
 ` d r a i n `   s t o p s   n e w   a s s i g n m e n t s ;   r u n n i n g   w o r k   m a y   c o m p l e t e .  
 ` d i s a b l e `   i m m e d i a t e l y   s t o p s   n e w   w o r k ;   r u n n i n g   w o r k   i s   n o t   i n t e r r u p t e d   b u t   n o   n e w   l e a s e s   a r e   g r a n t e d .  
 ` e n a b l e `   r e t u r n s   a   d i s a b l e d / d r a i n i n g   w o r k e r   t o   s e r v i c e .  
  
 # # #   S i g n a l R   E v e n t s   ( P h a s e   2   a d d i t i o n s )  
  
 H u b :   ` / h u b s / e x e c u t i o n `   ( s a m e   a s   S l i c e   5 )  
  
 N e w   e v e n t s :  
 -   ` E x e c u t i o n Q u e u e d `   � �    e x e c u t i o n   i s   w a i t i n g   f o r   g r i d   c a p a c i t y  
 -   ` E x e c u t i o n A s s i g n e d `   � �    a   g r i d   l e a s e   w a s   c l a i m e d   o n   a   w o r k e r  
 -   ` W o r k e r S t a t u s C h a n g e d `   � �    w o r k e r   l i f e c y c l e / h e a l t h   c h a n g e d  
  
 P a y l o a d s   c a r r y   i d e n t i f i e r s   o n l y ;   n o   c r e d e n t i a l s   o r   s e c r e t s .  
  
 # # #   C o n c u r r e n c y   S e m a n t i c s  
  
 -   G l o b a l   m a x   a c t i v e   s t r e a m s :   c o n f i g u r a b l e   ( d e f a u l t   1 0 0 ) .  
 -   P e r - p r o j e c t   m a x   a c t i v e   s t r e a m s :   c o n f i g u r a b l e   ( d e f a u l t   2 5 ) .  
 -   O n e   a c t i v e   l e a s e   p e r   e x e c u t i o n   t e s t   ( u n i q u e   f i l t e r e d   i n d e x ) .  
 -   W o r k e r   c a p a c i t y   e n f o r c e d   s e r v e r - s i d e   ( c o n c u r r e n c y   t o k e n   o n   ` g r i d _ w o r k e r s ` ) .  
 -   S t a l e   w o r k e r s   ( h e a r t b e a t   t i m e o u t )   e x c l u d e d   f r o m   s c h e d u l i n g ;   l e a s e s   e x p i r e d   a n d   e x e c u t i o n s   r e q u e u e d .  
 -   D i s a b l e d   w o r k e r s   n e v e r   r e c e i v e   w o r k .  
  
 S e c u r i t y :   w o r k e r   c r e d e n t i a l s   n e v e r   r e t u r n e d   a f t e r   r e g i s t r a t i o n ;   n e v e r   l o g g e d ;   n e v e r   i n   a u d i t .   M a c h i n e   a u t h   o n l y   o n   w o r k e r - p l a n e   r o u t e s .  
 