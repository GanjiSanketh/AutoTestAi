# ADR-006 — Docker First, Kubernetes Later

**Status:** Accepted

## Decision

Use Docker/Docker Compose for Phase 1. Adopt k3s/Kubernetes when operational scale requires it.

## Consequences

Local development stays simple, workers remain reproducible and isolated, and later horizontal scaling does not force Kubernetes-specific coupling into the application.
