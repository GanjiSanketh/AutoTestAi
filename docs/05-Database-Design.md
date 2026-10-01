# AutoTest AI — Database Design

**Database:** PostgreSQL  
**Status:** Phase-1 logical design

## 1. Principles

PostgreSQL is the authoritative application database. Prefer UUIDs for externally exposed IDs. Store timestamps as `TIMESTAMPTZ`. Execution history is immutable after completion except for explicit enrichment. Secrets are never stored as plaintext in normal business tables. External provider IDs are stored with internal IDs.

## 2. Core Model

```text
users → project_members → projects
projects → test_cases → test_case_versions
projects → test_suites → suite_test_cases
projects → executions → execution_tests
execution_tests → execution_logs
execution_tests → execution_artifacts
execution_tests → failure_analyses
execution_tests → self_healing_attempts
projects → self_healing_policies
projects → defects → tickets
projects → integrations
projects → variable_sets (Slice 3A)
projects → environment_secrets (Slice 3A)
executions → execution_variables (Slice 3A)
users/projects → audit_events
```

## 3. Tables

### users
`id UUID PK`, `external_identity_id VARCHAR UNIQUE`, `email VARCHAR`, `display_name VARCHAR`, `is_active BOOLEAN`, `created_at`, `updated_at`.

### roles
`id UUID PK`, `name VARCHAR UNIQUE`, `description TEXT`.

### permissions
`id UUID PK`, `code VARCHAR UNIQUE`, `description TEXT`.

### user_roles
`user_id FK`, `role_id FK`, composite PK.

### projects
`id UUID PK`, `name`, `key UNIQUE`, `description`, `repository_url`, `target_url`, `default_environment_id`, `framework`, `platform`, `status`, `created_by`, `created_at`, `updated_at`.

### project_members
`project_id FK`, `user_id FK`, `role_id FK`, `created_at`, composite PK.

### environments
`id UUID PK`, `project_id FK`, `name`, `base_url`, `status`, timestamps. Secret values are referenced externally.

### test_cases
`id UUID PK`, `project_id FK`, `test_key`, `title`, `description`, `module`, `framework`, `platform`, `priority`, `status`, `source_type`, `created_by`, timestamps; unique `(project_id,test_key)`.

### test_case_versions
`id UUID PK`, `test_case_id FK`, `version_number`, `source_code`, `structured_steps JSONB`, `generation_request JSONB`, `generation_provider`, `generation_model`, `generation_latency_ms`, `review_status`, `created_by`, `created_at`; unique `(test_case_id,version_number)`.

### test_suites
`id UUID PK`, `project_id FK`, `name`, `description`, `status`, `created_by`, timestamps.

### suite_test_cases
`suite_id FK`, `test_case_id FK`, `execution_order`, composite PK.

### executions
`id UUID PK`, `project_id FK`, `suite_id`, `status`, `trigger_type`, `environment_id`, `workflow_id`, `idempotency_key NULL` (unique per project when set), `started_at`, `completed_at`, `created_by`, `created_at`.

### execution_tests
`id UUID PK`, `execution_id FK`, `test_case_id FK`, `test_case_version_id FK` (exact immutable version bound at creation — never re-resolved), `status`, `worker_id`, `attempt`, `framework`, `browser`, `failure_classification` (`Unknown` until terminal), timestamps, `duration_ms`, `error_type`, `error_message`.

### execution_step_results (Slice 5)
`id UUID PK`, `execution_test_id FK`, `step_order`, `action`, `target`, `status`, `started_at`, `completed_at`, `duration_ms`, `error_message`. Step `value` fields are intentionally NOT persisted (password-like values would leak); index `(execution_test_id,step_order)`.

### execution_logs
`id BIGSERIAL PK`, `execution_test_id FK`, `timestamp`, `level`, `message`, `metadata JSONB`. Bounded chronological reads via `(execution_test_id, id)` cursor.

### execution_artifacts
`id UUID PK`, `execution_test_id FK`, `artifact_type`, `storage_key`, `file_name`, `step_order NULL`, `content_type`, `size_bytes`, `created_at`. Bytes live in MinIO/S3 under deterministic keys (`projects/{project}/executions/{execution}/tests/{test}/step-{order}-{file}`); downloads use short-lived server-minted presigned URLs.

### failure_analyses (Slice 6)
`id UUID PK`, `execution_test_id FK`, `attempt` (1-based; retries append rows), `status` (Running/Completed/Failed/Cancelled), `classification`, `summary` (≤500), `root_cause` (probable cause, ≤2000), `evidence JSONB` (bounded redacted snapshot), `assumptions`/`warnings` (`text[]`), `recommended_action` (≤500), `is_likely_defect`, `confidence`, `provider`, `model`, `prompt_version`, `latency_ms`, input/output/total tokens, `error_message`, `created_at`. Unique `(execution_test_id,attempt)`; unique filtered `execution_test_id WHERE status='Running'` (one active analysis). Analysis rows are advisory and immutable once terminal.

### defects
`id UUID PK`, `project_id FK`, `execution_test_id` (failed execution this defect was filed against), `failure_analysis_id NULL` (advisory analysis linked at creation, if any), `title`, `description`, `severity`, `status`, `root_cause_type`, `ai_confidence` (copied from the linked analysis, if any), `created_by`, timestamps.

### tickets
`id UUID PK`, `project_id FK`, `defect_id`, `integration_id NULL` (Slice-7 Jira row; NULL for pre-Slice-7 rows), `provider`, `external_ticket_id`, `external_key NULL` (e.g. ABC-123), `external_url`, `title`, `status`, `sync_status` (Pending/Synced/Failed), `origin` (Manual/Automatic, Slice 10; pre-Slice-10 rows read as Manual), `attempt_count` (Slice 10 automation bookkeeping), `next_attempt_at NULL` (Slice 10 retry schedule; NULL = no retry scheduled), `claim_token NULL` + `claim_expires_at NULL` (Slice 10 cross-instance automation lease; NULL = unclaimed), `row_version` (Slice 10 optimistic-concurrency token for claim/finish compare-and-set), `created_by NULL`, `last_error NULL` (safe diagnostic, ≤2000), timestamps; unique `(project_id,provider,external_ticket_id)`; unique filtered `(defect_id,integration_id) WHERE sync_status='Synced'` (one successful Jira ticket per defect per integration; Failed rows stay retryable); unique filtered `(defect_id,integration_id) WHERE sync_status='Pending'` (Slice 10: at most one Pending automation intent per defect per integration across API instances); index `(defect_id)`; index `(project_id,sync_status,next_attempt_at)` (Slice 10 reconciliation).

### auto_ticket_policies (Slice 10)
`id UUID PK`, `project_id FK UNIQUE` (one policy per project; absence means automation disabled), `enabled`, `integration_id NULL` (pinned Jira integration; NULL resolves to the project default), `severities` (CSV ≤200), `defect_statuses` (CSV ≤200), `classifications` (CSV ≤200), `minimum_confidence NULL` (0–1; NULL disables the filter), `updated_by NULL`, timestamps. No secrets: the policy references an integration row and never carries credentials.

### self_healing_policies (Slice 11)
`id UUID PK`, `project_id FK UNIQUE` (one policy per project; absence means healing disabled), `enabled`, `ai_fallback_enabled` (requires `enabled`), `max_attempts_per_step` (always 1 in Slice 11), `min_deterministic_score NULL` (0–100; NULL = conservative default), `min_ai_confidence NULL` (0–1; advisory only, NULL disables the filter), `allowed_strategies` (CSV ≤200; empty = safe default set), `updated_by NULL`, timestamps. No secrets, DOM, or credentials.

### self_healing_attempts (Slice 11)
`id UUID PK`, `project_id FK`, `execution_id FK`, `execution_test_id FK`, `test_case_id`, `test_case_version_id NULL` (snapshot reference — versions are never mutated by healing), `step_order`, `step_action` (≤200), `original_strategy` (≤50), `original_value` (bounded, redacted), `recovered_strategy` (≤50), `recovered_value` (bounded, redacted), `healing_strategy` (None/TestAttribute/Role/Label/Text/Structural/Ai), `status` (Applied/Failed persisted; granular candidate lifecycle lives in execution logs), `candidate_count`, `was_applied`, `is_ai_assisted`, `error_message` (≤2000, redacted), `assignment_id NULL` (lease that owned the run; NULL = legacy lease-free worker), timestamps. Unique `(execution_test_id,step_order)` (one authoritative outcome per step; concurrent workers converge); indexes on `project_id`, `execution_id`, `test_case_version_id`, `created_at`. Never stores raw DOM, screenshots per candidate, full prompts, or secrets.

### integrations
`id UUID PK`, `project_id`, `provider`, `integration_type`, `configuration JSONB`, `secret_reference`, `status`, timestamps. Slice 7 Jira shape: `configuration = {baseUrl, projectKey, email, issueType, priorityMapping, appBaseUrl?}` (no secrets — the API token lives in `secret_reference` server-side only); unique filtered `(project_id,provider) WHERE project_id IS NOT NULL` (one Jira row per project). Slice 3B CI/CD shape: `integration_type='cicd'`, `provider ∈ {github, gitlab, jenkins, azure}` (one row per provider per project via the same unique index), `configuration = {defaultSuiteId?, defaultEnvironmentId?, eventAllowlist[], branchAllowlist[], repositoryAllowlist[], variableMapping{}, username?, secretMapping{}}`; `secret_reference` holds ONLY an opaque Slice 3A reference (`env_secret:<id>`, provisioned as `CI_WEBHOOK_<PROVIDER>` in the default environment) — never plaintext.

### webhook_deliveries (Slice 3B)
`id UUID PK`, `integration_id FK`, `project_id FK`, `provider` (≤50), `delivery_id` (≤200, provider-scoped: GitHub delivery UUID, GitLab event UUID, Azure notification id, Jenkins caller id or derived hash), `event_type` (≤200), `received_at`, `payload_hash` (SHA-256 hex of the raw body; the raw body itself is never persisted), `verification_status` (Pending/Verified/Failed), `processing_status` (Received/Accepted/Triggered/Failed/Rejected/Duplicate/Ignored), `normalized_metadata_json NULL` (redacted branch/commit/repo/actor/filter outcome only), `execution_id NULL` (first fanned-out execution), `triggered_count`, `failure_reason NULL` (≤500, safe codes only), `processed_at NULL`, `claim_token NULL` + `claim_expires_at NULL` (crash-recovery lease), `row_version` (optimistic concurrency), timestamps. Unique `IX_webhook_deliveries_Integration_Delivery (integration_id,delivery_id)` (authoritative duplicate boundary); indexes on `project_id`, `integration_id`, `processing_status`, `received_at`, `execution_id`. No secrets, signatures, headers, or payloads. Retention default 90 days (purge job deferred — documented).

### grid_workers (Slice 9)
`id UUID PK`, `worker_key VARCHAR(100) UNIQUE`, `display_name`, `worker_type`, `framework`, `browsers text[]`, `version`, `status`, `capacity`, `active_assignment_count`, `last_heartbeat_at`, `credential_hash`, `credential_salt`, `base_url`, `row_version` (concurrency token), `created_at`, `updated_at`. Index on `status`, `last_heartbeat_at`; unique on `worker_key`.

### grid_assignments (Slice 9)
`id UUID PK`, `execution_id`, `execution_test_id`, `worker_id`, `status` (Pending/Claimed/Running/Completed/Released/Expired/Cancelled), `attempt`, `acquired_at`, `expires_at`, `last_renewed_at`, `worker_assignment_ref`, `created_at`, `updated_at`. Index on `execution_id`, `execution_test_id`, `worker_id`, `status`, `expires_at`; unique filtered `(execution_test_id) WHERE status IN ('Claimed','Running')` (one active lease per execution test).

### audit_events
`id BIGSERIAL PK`, `actor_user_id`, `action`, `entity_type`, `entity_id`, `project_id`, `ip_address`, `user_agent`, `metadata JSONB`, `created_at`.

### variable_sets (Slice 3A)
`id UUID PK`, `project_id FK`, `scope_type` (Project/Environment/Suite, string), `scope_id NULL` (NULL for Project scope; environment/suite id otherwise), `name` (≤200), `variables_json TEXT` (key → `{value}` | `{secretRef}`; raw secrets rejected at the application layer), `row_version` (optimistic concurrency), timestamps. Unique `project_id WHERE scope_type='Project'` (one project set); unique `(project_id,scope_type,scope_id) WHERE scope_id IS NOT NULL` (one set per environment/suite).

### environment_secrets (Slice 3A)
`id UUID PK`, `project_id FK`, `environment_id FK`, `name` (≤200, unique per environment), `secret_reference` (opaque `env_secret:<id>`, ≤256), `description NULL`, `encrypted_value NULL` (base64 AES-256-GCM ciphertext — never plaintext), `nonce NULL` (base64), `key_version` (≤50), `row_version`, timestamps. No plaintext secret column exists.

### execution_variables (Slice 3A)
`execution_id UUID PK`, `project_id`, `suite_id NULL`, `environment_id NULL`, `variable_overrides_json TEXT` (flat key→value), `secret_ref_overrides_json TEXT` (flat key→secretRef; raw secrets rejected), `created_at`. Refs only — secret values never persist here.

## 4. Important Indexes

Index project membership, project/test status, test versions, execution project/status, execution-test status, execution-log `(execution_test_id,timestamp)`, defects `(project_id,status)`, tickets `(project_id,sync_status)`, grid_workers `(status)`, `grid_workers (last_heartbeat_at)`, `grid_assignments (execution_test_id)` filtered unique, `grid_assignments (worker_id, expires_at)`, and audit `(project_id,created_at)`.

Slice 8 reporting adds no tables and no indexes: dashboard/report
aggregates reuse these source-of-truth tables and existing indexes with
bounded UTC date ranges (default 30 days, max 365).

Slice 9 grid uses the tables above; queries are bounded by validated
UTC date ranges (default 30 days, max 365). Concurrency is enforced by
unique filtered indexes and optimistic concurrency tokens (`row_version`).

Slice 11 healing adds the two tables above (one coherent migration).
Concurrency is enforced by the unique `(execution_test_id,step_order)`
index plus a pre-insert existence check (converge, never duplicate);
stale workers are fenced by `assignment_id` against the live lease.
Evidence retention is bounded by construction: only redacted locator
pairs and counts persist (no DOM, no screenshots per candidate).

Slice 12 adds no tables: analytics compute from the tables above.
One index-only migration adds `IX_executions_Project_Created`
(`executions(project_id,created_at)`) for every window range predicate
(Slice 8 history plus Slice 12 verdict/duration aggregates) and
`IX_healing_Project_Created`
(`self_healing_attempts(project_id,created_at)`) for healing window
queries. No other indexes were justified: defects, test cases, and
execution-test joins reuse existing indexes.

Slice 3A adds `variable_sets`, `environment_secrets`, and
`execution_variables` (one additive migration, no changes to historical
rows; `executions.environment_id` stays nullable for legacy
environment-less executions).

Slice 3B adds `webhook_deliveries` (one additive migration: table + unique
`(integration_id,delivery_id)` + five secondary indexes). No existing
table is altered; Jira rows and Slice 3A secret tables are untouched.

## 5. JSONB

Use JSONB for variable structures such as structured test steps, AI metadata, execution metadata, evidence and provider-specific integration configuration. Core queryable business fields remain relational.

## 6. Retention

Define configurable retention for execution logs and artifacts because these are high-volume data. Do not assume indefinite retention.
