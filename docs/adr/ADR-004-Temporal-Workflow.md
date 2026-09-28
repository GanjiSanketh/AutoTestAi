# ADR-004 — Temporal for Durable Workflow Orchestration

**Status:** Accepted

## Decision

Use Temporal as the primary durable workflow orchestration mechanism for long-running test execution, retries, worker allocation, artifact collection and failure analysis. PostgreSQL remains the application system of record.

RabbitMQ is not the primary workflow engine for Phase 1.
