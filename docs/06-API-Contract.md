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

```http
POST /api/v1/projects/{projectId}/test-generation
```

Example request:

```json
{
  "title": "User login with valid credentials",
  "description": "Verify that a registered user can log in.",
  "targetUrl": "https://example.test",
  "framework": "playwright-typescript",
  "platform": "web",
  "requirements": ["Validate successful login", "Validate dashboard navigation"]
}
```

AI output must be treated as generated content requiring validation/review, not trusted executable code.

## 8. Execution

```http
GET  /api/v1/projects/{projectId}/executions
POST /api/v1/projects/{projectId}/executions
GET  /api/v1/executions/{executionId}
POST /api/v1/executions/{executionId}/cancel
GET  /api/v1/executions/{executionId}/tests
GET  /api/v1/execution-tests/{executionTestId}/logs
GET  /api/v1/execution-tests/{executionTestId}/artifacts
GET  /api/v1/execution-tests/{executionTestId}/failure-analysis
```

## 9. Bugs

```http
GET  /api/v1/projects/{projectId}/bugs
GET  /api/v1/bugs/{bugId}
POST /api/v1/projects/{projectId}/bugs
PUT  /api/v1/bugs/{bugId}
```

## 10. Tickets

MVP manual creation:

```http
POST /api/v1/bugs/{bugId}/tickets
GET  /api/v1/projects/{projectId}/tickets
GET  /api/v1/tickets/{ticketId}
```

Later synchronization:

```http
POST /api/v1/tickets/{ticketId}/sync
```

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

Events: `ExecutionStarted`, `ExecutionStatusChanged`, `ExecutionTestStarted`, `ExecutionLogReceived`, `ExecutionTestCompleted`, `FailureAnalysisCompleted`, `ExecutionCompleted`, `ExecutionFailed`.

The server authorizes access before a client subscribes to project/execution channels.
The hub requires authentication; `SubscribeToExecution` verifies the execution
exists, resolves its project, and enforces `executions.read` plus project
membership. Rejections surface as hub errors (`Execution not found.` /
`Forbidden: no access to this execution.`).
