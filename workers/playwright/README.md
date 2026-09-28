# Playwright Worker (Phase-0 skeleton)

Isolated web-execution worker for AutoTest AI (docs/04 §5, ADR-005).
The API never executes test code; this container is the only execution site.

## Phase-0 scope

- Startup + structured logs proving the worker environment boots.
- `GET /health` on `WORKER_HEALTH_PORT` (default 8090) for orchestrator probes.
- `runAssignment()` seam documenting the Phase-1 contract.
- No real test execution yet.

## Run locally

```bash
npm install
npm run build
API_BASE_URL=http://localhost:5193 npm start
curl http://localhost:8090/health
```

## Environment

| Variable | Default | Purpose |
|---|---|---|
| `WORKER_ID` | generated | Stable worker identity |
| `API_BASE_URL` | `http://localhost:5193` | Callback API |
| `TEMPORAL_ADDRESS` | `localhost:7233` | Workflow server (Phase 1 polling) |
| `TEMPORAL_TASK_QUEUE` | `autotestai-execution` | Queue to poll |
| `WORKER_HEALTH_PORT` | `8090` | Health endpoint port |

## Docker

```bash
docker build -t autotestai/playwright-worker:phase0 -f Dockerfile .
docker run --rm -p 8090:8090 -e API_BASE_URL=http://host.docker.internal:5193 autotestai/playwright-worker:phase0
```
