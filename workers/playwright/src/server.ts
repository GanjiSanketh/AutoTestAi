/**
 * Playwright execution HTTP plane (Slice 5 §12, §36).
 *
 *   POST   /v1/assignments        submit an assignment (202 + background run)
 *   GET    /v1/assignments/:id    poll progress (steps, logs, terminal result)
 *   DELETE /v1/assignments/:id    best-effort abort
 *   GET    /health                orchestrator probe (no auth)
 *
 * Authentication: Bearer WORKER_API_TOKEN (timing-safe compare). The token is
 * server-side configuration shared with the API only — never browsers.
 * Each assignment runs in an isolated browser/context; cleanup always runs,
 * even on step failure, timeout, abort, or upload problems.
 */
import { createServer, type IncomingMessage, type ServerResponse } from 'node:http';
import { timingSafeEqual } from 'node:crypto';
import { Browser, chromium, firefox, webkit } from 'playwright';
import type { WorkerConfig } from './config.js';
import { redactStepValue } from './redaction.js';
import {
  EngineAbort,
  PlaywrightPageAdapter,
  executeSteps,
  validateSteps,
} from './stepEngine.js';
import type {
  AssignmentProgress,
  AssignmentStatus,
  WorkerAssignment,
  WorkerClassification,
  WorkerLog,
  WorkerOutcomeStatus,
  WorkerResult,
  WorkerScreenshot,
  WorkerStepResult,
} from './types.js';

interface AssignmentRecord {
  assignment: WorkerAssignment;
  status: AssignmentStatus;
  currentStepOrder: number | null;
  stepResults: WorkerStepResult[];
  logs: WorkerLog[];
  result: WorkerResult | null;
  abort: AbortController;
  startedAtUnixMs: number;
}

const MAX_LOGS = 2000;

export function createWorkerServer(config: WorkerConfig): {
  listen: (port?: number) => Promise<void>;
  close: () => Promise<void>;
} {
  const assignments = new Map<string, AssignmentRecord>();
  let shuttingDown = false;

  function log(level: WorkerLog['level'], message: string): void {
    console.log(JSON.stringify({ level, msg: message, workerId: config.workerId }));
  }

  function authorized(req: IncomingMessage): boolean {
    if (!config.apiToken) return true;
    const header = req.headers.authorization ?? '';
    const presented = header.startsWith('Bearer ') ? header.slice(7) : '';
    const expected = Buffer.from(config.apiToken);
    const actual = Buffer.from(presented);
    return (
      expected.length === actual.length &&
      expected.length > 0 &&
      timingSafeEqual(expected, actual)
    );
  }

  function readJson(req: IncomingMessage): Promise<unknown> {
    return new Promise((resolve, reject) => {
      const chunks: Buffer[] = [];
      req.on('data', (chunk: Buffer) => {
        chunks.push(chunk);
        if (chunks.reduce((n, c) => n + c.length, 0) > 4 * 1024 * 1024) {
          reject(new Error('Request body too large.'));
          req.destroy();
        }
      });
      req.on('end', () => {
        try {
          resolve(JSON.parse(Buffer.concat(chunks).toString('utf8')));
        } catch {
          reject(new Error('Request body must be JSON.'));
        }
      });
      req.on('error', reject);
    });
  }

  function sendJson(res: ServerResponse, status: number, body: unknown): void {
    res.writeHead(status, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify(body));
  }

  function progressOf(record: AssignmentRecord): AssignmentProgress {
    return {
      assignmentId: record.assignment.assignmentId,
      status: record.status,
      currentStepOrder: record.currentStepOrder,
      stepResults: record.stepResults,
      logs: record.logs,
      result: record.result,
    };
  }

  function pushLog(record: AssignmentRecord, level: WorkerLog['level'], message: string): void {
    record.logs.push({
      seq: record.logs.length + 1,
      timestampUnixMs: Date.now(),
      level,
      message: message.slice(0, 4000),
    });
    if (record.logs.length > MAX_LOGS) {
      record.logs.splice(0, record.logs.length - MAX_LOGS);
    }
  }

  async function launchBrowser(): Promise<Browser> {
    const launcher =
      config.browser === 'firefox' ? firefox : config.browser === 'webkit' ? webkit : chromium;
    return launcher.launch({ headless: true });
  }

  async function runInBackground(record: AssignmentRecord): Promise<void> {
    const { assignment } = record;
    const startedAt = Date.now();
    const timeout = setTimeout(
      () => record.abort.abort(new Error('Execution timeout.')),
      Math.min(assignment.timeouts.executionMs, config.executionTimeoutMs),
    );
    record.status = 'running';
    pushLog(record, 'info', `worker ${config.workerId} started assignment ${assignment.assignmentId}`);

    let browser: Browser | null = null;
    try {
      browser = await launchBrowser();
      const context = await browser.newContext({ viewport: { width: 1280, height: 800 } });
      try {
        const page = await context.newPage();
        const adapter = new PlaywrightPageAdapter(page);
        const run = await executeSteps(adapter, assignment.steps, {
          stepTimeoutMs: Math.min(assignment.timeouts.stepMs, config.stepTimeoutMs),
          signal: record.abort.signal,
          screenshotOnFailure: assignment.screenshotOnFailure,
          callbacks: {
            onStepStart: (order) => {
              record.currentStepOrder = order;
            },
            onLog: (level, message) => pushLog(record, level, message),
          },
        });
        const screenshots: WorkerScreenshot[] = [...run.screenshots];
        if (assignment.screenshotOnFinish && !run.failed) {
          try {
            const png = await adapter.captureScreenshot();
            screenshots.push({
              stepOrder: null,
              fileName: 'finish.png',
              contentType: 'image/png',
              base64Content: png.toString('base64'),
            });
          } catch {
            pushLog(record, 'warning', 'finish screenshot unavailable');
          }
        }
        record.stepResults = run.stepResults;
        record.status = run.failed ? 'failed' : 'passed';
        record.result = {
          status: record.status,
          classification: run.failed ? run.classification : 'unknown',
          errorType: run.errorType ?? null,
          errorMessage: run.errorMessage ?? null,
          durationMs: Date.now() - startedAt,
          stepResults: run.stepResults,
          logs: record.logs,
          screenshots,
        };
        pushLog(
          record,
          run.failed ? 'error' : 'info',
          run.failed
            ? `assignment failed: ${run.errorMessage ?? 'unknown'}`
            : `assignment passed in ${record.result.durationMs}ms`,
        );
      } finally {
        await context.close().catch(() => undefined);
      }
    } catch (error) {
      const aborted =
        error instanceof EngineAbort ||
        (error instanceof Error && record.abort.signal.aborted);
      const timedOut =
        !aborted &&
        (error instanceof Error
          ? /timeout/i.test(error.message)
          : false);
      const outcome: WorkerOutcomeStatus = aborted ? 'cancelled' : timedOut ? 'timedOut' : 'error';
      const classification: WorkerClassification = aborted
        ? 'unknown'
        : timedOut
          ? 'environment'
          : 'automation';
      record.status = outcome;
      record.result = {
        status: outcome,
        classification,
        errorType: aborted ? 'Cancelled' : timedOut ? 'TimeoutError' : 'WorkerError',
        errorMessage:
          error instanceof Error ? error.message.slice(0, 4000) : 'Worker failed unexpectedly.',
        durationMs: Date.now() - startedAt,
        stepResults: record.stepResults,
        logs: record.logs,
        screenshots: [],
      };
      pushLog(record, 'error', `assignment ${outcome}: ${record.result.errorMessage}`);
    } finally {
      clearTimeout(timeout);
      if (browser) await browser.close().catch(() => undefined);
    }
  }

  function contractProblems(body: unknown): string[] {
    if (!body || typeof body !== 'object') return ['Assignment must be a JSON object.'];
    const assignment = body as Partial<WorkerAssignment>;
    const problems: string[] = [];
    if (!assignment.assignmentId) problems.push('assignmentId is required.');
    if (!assignment.executionId) problems.push('executionId is required.');
    if (!['chromium', 'firefox', 'webkit'].includes((assignment.browser ?? '').toLowerCase())) {
      problems.push("browser must be 'chromium', 'firefox' or 'webkit'.");
    }
    problems.push(...validateSteps((assignment.steps ?? []) as WorkerAssignment['steps']));
    // Redact password-like values at the boundary (defense in depth; the API
    // already redacts before dispatch).
    for (const step of assignment.steps ?? []) {
      step.value = redactStepValue(step.action, step.target, step.value) ?? step.value;
    }
    return problems;
  }

  const server = createServer((req, res) => {
    void (async () => {
      const url = new URL(req.url ?? '/', 'http://localhost');
      if (req.method === 'GET' && url.pathname === '/health') {
        sendJson(res, shuttingDown ? 503 : 200, {
          status: shuttingDown ? 'draining' : 'healthy',
          workerId: config.workerId,
          taskQueue: config.taskQueue,
        });
        return;
      }
      if (!req.url?.startsWith('/v1/assignments')) {
        sendJson(res, 404, { error: 'Not found.' });
        return;
      }
      if (!authorized(req)) {
        sendJson(res, 401, { error: 'Unauthorized.' });
        return;
      }

      if (req.method === 'POST' && url.pathname === '/v1/assignments') {
        if (shuttingDown) {
          sendJson(res, 503, { error: 'Worker is draining.' });
          return;
        }
        let body: unknown;
        try {
          body = await readJson(req);
        } catch (error) {
          sendJson(res, 400, { error: error instanceof Error ? error.message : 'Bad request.' });
          return;
        }
        const problems = contractProblems(body);
        if (problems.length > 0) {
          sendJson(res, 400, { error: 'Invalid assignment.', details: problems });
          return;
        }
        const assignment = body as WorkerAssignment;
        if (assignments.has(assignment.assignmentId)) {
          sendJson(res, 200, progressOf(assignments.get(assignment.assignmentId)!));
          return;
        }
        const record: AssignmentRecord = {
          assignment,
          status: 'queued',
          currentStepOrder: null,
          stepResults: [],
          logs: [],
          result: null,
          abort: new AbortController(),
          startedAtUnixMs: Date.now(),
        };
        assignments.set(assignment.assignmentId, record);
        void runInBackground(record);
        sendJson(res, 202, progressOf(record));
        return;
      }

      const match = /^\/v1\/assignments\/([^/]+)$/.exec(url.pathname);
      if (!match) {
        sendJson(res, 404, { error: 'Not found.' });
        return;
      }
      const record = assignments.get(match[1]!);
      if (!record) {
        sendJson(res, 404, { error: 'Assignment not found.' });
        return;
      }
      if (req.method === 'GET') {
        sendJson(res, 200, progressOf(record));
        return;
      }
      if (req.method === 'DELETE') {
        if (record.status === 'queued' || record.status === 'running') {
          record.abort.abort(new Error('Cancelled via API.'));
        }
        sendJson(res, 200, progressOf(record));
        return;
      }
      sendJson(res, 405, { error: 'Method not allowed.' });
    })().catch((error: unknown) => {
      log('error', `request failed: ${error instanceof Error ? error.message : 'unknown'}`);
      if (!res.headersSent) sendJson(res, 500, { error: 'Internal worker error.' });
    });
  });

  return {
    listen: (port?: number) =>
      new Promise<void>((resolve) => {
        server.listen(port ?? config.healthPort, () => {
          log(
            'info',
            `playwright worker listening (auth ${config.apiToken ? 'enabled' : 'DISABLED — set WORKER_API_TOKEN'})`,
          );
          resolve();
        });
      }),
    close: () =>
      new Promise<void>((resolve) => {
        shuttingDown = true;
        server.close(() => resolve());
      }),
  };
}
