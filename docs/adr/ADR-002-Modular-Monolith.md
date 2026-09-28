# ADR-002 — Modular Monolith for Phase 1

**Status:** Accepted

## Decision

Build the ASP.NET Core backend as a modular monolith. Execution workers remain isolated processes/containers.

## Rationale

This reduces operational complexity and speeds MVP delivery while preserving module boundaries for future extraction if justified.

Do not introduce microservices merely for architectural fashion.
