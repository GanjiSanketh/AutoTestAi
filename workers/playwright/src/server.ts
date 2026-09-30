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
import { ALLOWED_STRATEGIES, DEFAULT_POLICY, type SelfHealingPolicy } from './selfHealing.js';
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
  WorkerHealingPolicy,
  WorkerLog,
  WorkerOutcomeStatus,
  WorkerResult,
  WorkerScreenshot,
  WorkerStepResult,
} from './types.js';

interface AssignmentRecord {
  assignment: WorkerAssignment;
  assignmentToken: string;
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
  /** Active (queued/running) assignment count for grid heartbeats. */
  activeAssignments: () => number;
  /** Grid-directed drain: stop accepting new work, finish in-flight work. */
  setDraining: (draining: boolean) => void;
} {
  const assignments = new Map<string, AssignmentRecord>();
  let shuttingDown = false;
  let draining = false;

  function activeCount(): number {
    let count = 0;
    for (const record of assignments.values()) {
      if (record.status === 'queued' || record.status === 'running') count += 1;
    }
    return count;
  }

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

  /**
   * Slice 11 AI fallback: the worker never holds provider keys and never
   * embeds vendor SDKs. When the assignment policy enables AI fallback, raw
   * bounded redacted evidence is POSTed to the control-plane healing-suggest
   * endpoint, which resolves the configured IAiProvider server-side. Auth is
   * the per-assignment lease token (fencing: only the live lease holder can
   * request candidates). Any failure here is a controlled healing failure.
   */
  function suggestAiFor(
    assignment: WorkerAssignment,
  ): ((envelope: {
    action: string;
    originalTarget: string;
    domFragment: string;
    attributes: string[];
    nearbyText: string[];
  }) => Promise<string>) | undefined {
    const policy = normalizeHealingPolicy(assignment.healing);
    if (!policy.enabled || !policy.aiFallbackEnabled) return undefined;
    const baseUrl = (config.apiBaseUrl ?? '').trim().replace(/\/+$/, '');
    if (!baseUrl) return undefined;
    const assignmentId = assignment.assignmentId;
    const token = assignment.assignmentToken;
    return async (envelope) => {
      const body = JSON.stringify({
        action: envelope.action,
        originalTarget: envelope.originalTarget,
        domFragment: envelope.domFragment.slice(0, 4000),
        attributes: envelope.attributes.slice(0, 20),
        nearbyText: envelope.nearbyText.slice(0, 20),
      });
      const response = await fetch(
        `${baseUrl}/api/v1/execution-grid/assignments/${encodeURIComponent(assignmentId)}/healing/suggest`,
        {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            Authorization: `Bearer ${token}`,
          },
          body,
          signal: AbortSignal.timeout(config.healingAiTimeoutMs ?? 15000),
        },
      );
      if (!response.ok) {
        throw new Error(`Healing-suggest endpoint returned HTTP ${response.status}.`);
      }
      const payload = (await response.json()) as { candidates?: unknown };
      // Return the raw contract; the engine schema-validates before use.
      return JSON.stringify({ candidates: payload.candidates ?? [] });
    };
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
          healing: {
            policy: normalizeHealingPolicy(assignment.healing),
            suggestAi: suggestAiFor(assignment),
            aiTimeoutMs: config.healingAiTimeoutMs ?? 15000,
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
          healingAttempts: run.healingAttempts,
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
    // Slice 11: normalize (never trust) the healing policy. Unknown fields are
    // dropped; invalid values fall back to safe defaults (disabled).
    try {
      assignment.healing = normalizeHealingPolicy(assignment.healing ?? null) as WorkerHealingPolicy;
    } catch {
      assignment.healing = { ...DEFAULT_POLICY };
    }
    return problems;
  }

  /**
   * Slice 11: coerces an untrusted healing policy to safe values. Healing is
   * disabled unless explicitly enabled; AI fallback stays off unless both the
   * policy enables it and a suggest provider is wired. One-heal retry is
   * enforced by the engine regardless of the supplied maxAttemptsPerStep.
   */
  function normalizeHealingPolicy(raw: unknown): SelfHealingPolicy {
    if (!raw || typeof raw !== 'object') return { ...DEFAULT_POLICY };
    const input = raw as Partial<WorkerHealingPolicy>;
    const enabled = input.enabled === true;
    const strategies = Array.isArray(input.allowedStrategies)
      ? input.allowedStrategies
          .filter((s): s is string => typeof s === 'string')
          .map((s) => s.trim().toLowerCase())
          .filter((s) => (ALLOWED_STRATEGIES as readonly string[]).includes(s))
      : undefined;
    const minScore =
      typeof input.minDeterministicScore === 'number' && Number.isFinite(input.minDeterministicScore)
        ? Math.min(100, Math.max(0, Math.floor(input.minDeterministicScore)))
        : undefined;
    const minConfidence =
      typeof input.minAiConfidence === 'number' && Number.isFinite(input.minAiConfidence)
        ? Math.min(1, Math.max(0, input.minAiConfidence))
        : null;
    return {
      enabled,
      aiFallbackEnabled: enabled && input.aiFallbackEnabled === true,
      maxAttemptsPerStep: 1,
      ...(minScore !== undefined ? { minDeterministicScore: minScore } : {}),
      ...(minConfidence !== null ? { minAiConfidence: minConfidence } : {}),
      ...(strategies && strategies.length > 0 ? { allowedStrategies: strategies } : {}),
    };
  }

  const server = createServer((req, res) => {
    void (async () => {
      const url = new URL(req.url ?? '/', 'http://localhost');
      if (req.method === 'GET' && url.pathname === '/health') {
        const drained = shuttingDown || draining;
        sendJson(res, drained ? 503 : 200, {
          status: drained ? 'draining' : 'healthy',
          workerId: config.workerId,
          workerKey: config.workerKey,
          taskQueue: config.taskQueue,
          capacity: config.capacity,
          activeAssignments: activeCount(),
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
        if (shuttingDown || draining) {
          sendJson(res, 503, { error: 'Worker is draining.' });
          return;
        }
        if (activeCount() >= Math.max(1, config.capacity)) {
          sendJson(res, 429, { error: 'Worker is at capacity.' });
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
        if (!assignment.assignmentToken) {
          sendJson(res, 400, { error: 'Assignment token is required.' });
          return;
        }
        if (assignments.has(assignment.assignmentId)) {
          sendJson(res, 200, progressOf(assignments.get(assignment.assignmentId)!));
          return;
        }
        const record: AssignmentRecord = {
          assignment,
          assignmentToken: assignment.assignmentToken,
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
    activeAssignments: () => activeCount(),
    setDraining: (value: boolean) => {
      draining = value;
      if (value) log('info', 'grid directed drain: no new assignments accepted');
    },
  };
}
