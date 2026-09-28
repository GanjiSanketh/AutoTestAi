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
projects → defects → tickets
projects → integrations
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
`id UUID PK`, `project_id FK`, `suite_id`, `status`, `trigger_type`, `environment_id`, `workflow_id`, `started_at`, `completed_at`, `created_by`, `created_at`.

### execution_tests
`id UUID PK`, `execution_id FK`, `test_case_id FK`, `test_case_version_id FK`, `status`, `worker_id`, `attempt`, timestamps, `duration_ms`, `error_type`, `error_message`.

### execution_logs
`id BIGSERIAL PK`, `execution_test_id FK`, `timestamp`, `level`, `message`, `metadata JSONB`.

### execution_artifacts
`id UUID PK`, `execution_test_id FK`, `artifact_type`, `storage_key`, `content_type`, `size_bytes`, `created_at`.

### failure_analyses
`id UUID PK`, `execution_test_id FK`, `classification`, `root_cause`, `evidence JSONB`, `confidence NUMERIC(5,4)`, `provider`, `model`, `created_at`.

### defects
`id UUID PK`, `project_id FK`, `execution_test_id`, `title`, `description`, `severity`, `status`, `root_cause_type`, `ai_confidence`, timestamps.

### tickets
`id UUID PK`, `project_id FK`, `defect_id`, `provider`, `external_ticket_id`, `external_url`, `title`, `status`, `sync_status`, timestamps; unique `(project_id,provider,external_ticket_id)`.

### integrations
`id UUID PK`, `project_id`, `provider`, `integration_type`, `configuration JSONB`, `secret_reference`, `status`, timestamps.

### audit_events
`id BIGSERIAL PK`, `actor_user_id`, `action`, `entity_type`, `entity_id`, `project_id`, `ip_address`, `user_agent`, `metadata JSONB`, `created_at`.

## 4. Important Indexes

Index project membership, project/test status, test versions, execution project/status, execution-test status, execution-log `(execution_test_id,timestamp)`, defects `(project_id,status)`, tickets `(project_id,sync_status)`, and audit `(project_id,created_at)`.

## 5. JSONB

Use JSONB for variable structures such as structured test steps, AI metadata, execution metadata, evidence and provider-specific integration configuration. Core queryable business fields remain relational.

## 6. Retention

Define configurable retention for execution logs and artifacts because these are high-volume data. Do not assume indefinite retention.
