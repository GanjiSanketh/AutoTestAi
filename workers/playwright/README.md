# Playwright Worker (Slice 5 execution plane)

Isolated web-execution worker for AutoTest AI (docs/04 §5, ADR-005).
The API never executes test code; this container is the only execution site.

## What it does

- Serves a small authenticated HTTP API consumed by the backend worker client:
  - `POST /v1/assignments` — submit a structured-step assignment (202, background run)
  - `GET /v1/assignments/:id` — poll progress (steps, logs, terminal result)
  - `DELETE /v1/assignments/:id` — best-effort abort
  - `GET /health` — orchestrator probe (no auth)
- Executes the **structured TestStep contract only** (`order/action/target/value`)
  through a controlled interpreter (`src/stepEngine.ts`).
- Launches one isolated browser + context per assignment; cleanup always runs
  (step failure, timeout, abort, crash).
- Returns step results, redacted logs, and screenshots (base64); the backend
  uploads artifact bytes to MinIO and persists metadata.

## Security boundary (non-negotiable)

- Generated `sourceCode` is never read here, let alone executed.
- No `eval`, no `new Function`, no `child_process`, no dynamic imports.
- Targets resolve through explicit prefixes only
  (`css=`, `xpath=`, `role=`, `text=`, `testid=`, bare CSS). No JS evaluation.
- Unknown actions and missing targets fail as automation failures.
- Password-like values travel and persist as `[REDACTED]` (MVP: credential
  steps execute with masked placeholders; real secret injection needs a future
  vault design and is intentionally not implemented here).

## Supported actions

`navigate`, `click`, `fill`, `type`, `select`, `check`, `uncheck`, `press`,
`wait` (milliseconds, default 1000, max 30000), `assertVisible`,
`assertText` (substring), `assertValue` (exact), `screenshot`.

## Run locally

```bash
npm install
npm run build
npm test
API_BASE_URL=http://localhost:5193 WORKER_API_TOKEN=dev-token npm start
curl http://localhost:8090/health
```

A real browser is required for live runs (`npx playwright install chromium`).

## Environment

| Variable | Default | Purpose |
|---|---|---|
| `WORKER_ID` | generated | Stable worker identity |
| `API_BASE_URL` | `http://localhost:5193` | Callback API (reserved) |
| `TEMPORAL_ADDRESS` | `localhost:7233` | Workflow server (reserved) |
| `TEMPORAL_TASK_QUEUE` | `autotestai-execution` | Queue name (informational) |
| `WORKER_HEALTH_PORT` | `8090` | HTTP port |
| `WORKER_API_TOKEN` | empty (auth disabled, dev only) | Bearer token for `/v1/*` |
| `BROWSER` | `chromium` | `chromium` \| `firefox` \| `webkit` |
| `WORKER_EXECUTION_TIMEOUT_MS` | `300000` | Assignment backstop |
| `WORKER_STEP_TIMEOUT_MS` | `30000` | Per-step backstop |

## Tests

`npm test` runs the browser-free suite (locators, redaction, interpreter with
a fake page, HTTP contract/auth). Live browser validation requires installed
browsers and is reported explicitly, never assumed.

## Docker

```bash
docker build -t autotestai/playwright-worker:slice5 -f Dockerfile .
docker run --rm -p 8090:8090 \
  -e API_BASE_URL=http://host.docker.internal:5193 \
  -e WORKER_API_TOKEN=change-me \
  autotestai/playwright-worker:slice5
```
