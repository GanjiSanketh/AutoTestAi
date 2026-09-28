# ADR-003 — Provider-Independent AI Architecture

**Status:** Accepted

## Decision

All AI access goes through an internal AI gateway/provider abstraction. Adapters can include Ollama, OpenAI and Gemini. Business modules must not directly depend on provider SDKs.

## Consequence

Local AI remains useful for development/private deployments without making Ollama mandatory in production.
