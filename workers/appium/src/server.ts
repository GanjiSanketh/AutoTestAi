/**
 * Mobile execution HTTP plane (Slice 3C-4A scaffold).
 *
 *   POST   /v1/assignments        submit an assignment (202 + background run)
 *   GET    /v1/assignments/:id    poll progress (steps, logs, terminal result)
 *   DELETE /v1/assignments/:id    best-effort abort
 *   GET    /health                orchestrator probe (no auth)
 *
 * Authentication: Bearer WORKER_API_TOKEN (timing-safe compare). The token is
 * server-side configuration shared with the API only — never browsers.
 *
 * Slice 3C-4A explicitly defers Appium driver creation: accepted assignments
 * validate the envelope, then complete immediately with a controlled
 * error/automation deferred result. This worker never reports success for
 * work it did not perform. No shell, no eval, no dynamic code loading.
 */
import { createServer, type IncomingMessage, type ServerResponse } from 'node:http';
import { timingSafeEqual } from 'node:crypto';
import type { MobileWorkerConfig } from './config.js';
import {
  WebdriverIoDriver,
  classifyDriverError,
  parseAppiumServerUrl,
  type IMobileDriver,
} from './driver.js';
import { isHttpAppUrl, stageAppBinary, type StagedAppBinary } from './appBinary.js';
import { redactStepValue } from './redaction.js';
import { validateMobileSteps } from './steps.js';
import type {
  MobileAssignment,
  MobileAssignmentProgress,
  MobileAssignmentStatus,
  MobileLog,
  MobileResult,
  MobileStepResult,
} from './types.js';

interface MobileAssignmentRecord {
  assignment: MobileAssignment;
  assignmentToken: string;
  status: MobileAssignmentStatus;
  currentStepOrder: number | null;
  stepResults: MobileStepResult[];
  logs: MobileLog[];
  result: MobileResult | null;
  /** Appium session id once established; null before creation or after close. Never logged. */
  appiumSessionId: string | null;
  abort: AbortController;
  startedAtUnixMs: number;
}

const MAX_LOGS = 2000;

export function createMobileWorkerServer(
  config: MobileWorkerConfig,
  deps?: { createDriver?: () => IMobileDriver },
): {
  listen: (port?: number) => Promise<void>;
  close: () => Promise<void>;
  /** Active (queued/running) assignment count for grid heartbeats. */
  activeAssignments: () => number;
  /** Grid-directed drain: stop accepting new work, finish in-flight work. */
  setDraining: (draining: boolean) => void;
} {
  const assignments = new Map<string, MobileAssignmentRecord>();
  let shuttingDown = false;
  let draining = false;
  // One driver per server: session handles stay addressable for shutdown
  // cleanup even after individual runs settle.
  const driver = (deps?.createDriver ?? (() => new WebdriverIoDriver()))();

  function activeCount(): number {
    let count = 0;
    for (const record of assignments.values()) {
      if (record.status === 'queued' || record.status === 'running') count += 1;
    }
    return count;
  }

  function log(level: MobileLog['level'], message: string): void {
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

  function progressOf(record: MobileAssignmentRecord): MobileAssignmentProgress {
    return {
      assignmentId: record.assignment.assignmentId,
      status: record.status,
      currentStepOrder: record.currentStepOrder,
      stepResults: record.stepResults,
      logs: record.logs,
      result: record.result,
      appiumSessionId: record.appiumSessionId,
    };
  }

  function pushLog(record: MobileAssignmentRecord, level: MobileLog['level'], message: string): void {
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

  /**
   * Mobile runtime (Slice 3C-4B-1): create a real Appium session, then hold
   * a controlled "session established" state until cancellation, timeout,
   * or shutdown. Steps are validated but NOT executed here — the action
   * engine slice owns step execution. Terminal outcomes only arise from
   * cancellation, timeout, or session failure; success is never reported
   * for work not performed.
   */
  async function runMobile(record: MobileAssignmentRecord): Promise<void> {
    const { assignment } = record;
    const startedAt = Date.now();
    // Yield so the 202 acceptance is sent while the record is still queued,
    // preserving the submit-then-poll contract for control-plane clients.
    await Promise.resolve();
    const timeout = setTimeout(
      () => record.abort.abort(new Error('Execution timeout.')),
      Math.min(assignment.timeouts.executionMs, config.executionTimeoutMs),
    );
    let sessionId: string | null = null;
    let stagedApp: StagedAppBinary | null = null;
    const finish = (
      status: MobileResult['status'],
      classification: MobileResult['classification'],
      errorType: string | null,
      errorMessage: string | null,
    ): void => {
      record.status = status;
      record.result = {
        status,
        classification,
        errorType,
        errorMessage,
        durationMs: Date.now() - startedAt,
        stepResults: record.stepResults,
        logs: record.logs,
        screenshots: [],
        appiumSessionId: sessionId,
      };
    };
    const closeSession = async (): Promise<void> => {
      if (stagedApp !== null) {
        const staged = stagedApp;
        stagedApp = null;
        await staged.cleanup();
      }
      if (sessionId === null) return;
      const closing = sessionId;
      sessionId = null;
      record.appiumSessionId = null;
      try {
        await driver.deleteSession(closing);
        pushLog(record, 'info', 'appium session closed');
      } catch (error) {
        pushLog(
          record,
          'warning',
          `appium session cleanup failed: ${error instanceof Error ? error.message.slice(0, 200) : 'unknown'}`,
        );
      }
    };
    record.status = 'running';
    pushLog(record, 'info', `worker ${config.workerId} accepted assignment ${assignment.assignmentId}`);
    try {
      if (record.abort.signal.aborted) throw new Error('Cancelled via API.');
      let endpoint;
      try {
        endpoint = parseAppiumServerUrl(config.appiumServerUrl);
      } catch (error) {
        const classified = classifyDriverError(error);
        finish('error', classified.kind, 'InvalidEndpoint', classified.message);
        pushLog(record, 'error', `assignment failed: ${classified.message}`);
        return;
      }
      try {
        // Install/Reinstall: stage the server-minted binary to a
        // worker-controlled temp path. Preinstalled skips this entirely.
        // Effective capabilities carry the temp path, never the URL.
        let effectiveCapabilities = assignment.capabilities;
        if (isHttpAppUrl(assignment.capabilities.app)) {
          try {
            stagedApp = await stageAppBinary(assignment.capabilities.app!, record.abort.signal);
          } catch (error) {
            const classified = classifyDriverError(error);
            finish('error', classified.kind, 'AppDownloadFailed', classified.message);
            pushLog(record, 'error', `assignment failed: ${classified.message}`);
            return;
          }
          effectiveCapabilities = { ...assignment.capabilities, app: stagedApp.path };
        }
        const handle = await driver.createSession(effectiveCapabilities, {
          endpoint,
          newCommandTimeoutMs: Math.min(
            assignment.timeouts.executionMs,
            config.executionTimeoutMs,
          ),
          signal: record.abort.signal,
        });
        sessionId = handle.sessionId;
      } catch (error) {
        const classified = classifyDriverError(error);
        finish('error', classified.kind, 'SessionCreationFailed', classified.message);
        pushLog(record, 'error', `assignment failed: ${classified.message}`);
        await closeSession();
        return;
      }
      record.appiumSessionId = sessionId;
      pushLog(record, 'info', 'appium session established; awaiting execution slice');
      // Deferred: hold the established session until cancellation, timeout,
      // or shutdown. No steps run in this checkpoint.
      await new Promise<void>((_, reject) => {
        if (record.abort.signal.aborted) {
          reject(new Error('Cancelled via API.'));
          return;
        }
        record.abort.signal.addEventListener('abort', () => reject(new Error('Cancelled via API.')), {
          once: true,
        });
      });
      throw new Error('Unreachable: abort always settles the wait above.');
    } catch (error) {
      const cancelled =
        error instanceof Error &&
        (record.abort.signal.aborted || /cancelled via api/i.test(error.message));
      const timedOut =
        !cancelled &&
        error instanceof Error &&
        (/execution timeout/i.test(error.message) || /timed out/i.test(error.message));
      await closeSession();
      if (cancelled) {
        finish('cancelled', 'unknown', 'Cancelled', 'Cancelled via API.');
      } else if (timedOut) {
        finish('timedOut', 'environment', 'TimeoutError', 'Mobile execution timed out.');
      } else {
        const classified = classifyDriverError(error);
        finish('error', classified.kind, 'WorkerError', classified.message);
      }
      pushLog(record, 'error', `assignment ${record.status}: ${record.result?.errorMessage ?? 'unknown'}`);
    } finally {
      clearTimeout(timeout);
    }
  }

  function contractProblems(body: unknown): string[] {
    if (!body || typeof body !== 'object') return ['Assignment must be a JSON object.'];
    const assignment = body as Partial<MobileAssignment>;
    const problems: string[] = [];
    if (!assignment.assignmentId) problems.push('assignmentId is required.');
    if (!assignment.executionId) problems.push('executionId is required.');
    if ((assignment.framework ?? '').toLowerCase() !== 'appium') {
      problems.push("framework must be 'appium'.");
    }
    if (!assignment.platform || typeof assignment.platform !== 'string' || assignment.platform.trim().length === 0) {
      problems.push('platform is required.');
    }
    problems.push(...validateMobileSteps((assignment.steps ?? []) as MobileAssignment['steps']));
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
        const assignment = body as MobileAssignment;
        if (!assignment.assignmentToken) {
          sendJson(res, 400, { error: 'Assignment token is required.' });
          return;
        }
        if (assignments.has(assignment.assignmentId)) {
          sendJson(res, 200, progressOf(assignments.get(assignment.assignmentId)!));
          return;
        }
        const record: MobileAssignmentRecord = {
          assignment,
          assignmentToken: assignment.assignmentToken,
          status: 'queued',
          currentStepOrder: null,
          stepResults: [],
          logs: [],
          result: null,
          appiumSessionId: null,
          abort: new AbortController(),
          startedAtUnixMs: Date.now(),
        };
        assignments.set(assignment.assignmentId, record);
        void runMobile(record);
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
            `appium worker listening (auth ${config.apiToken ? 'enabled' : 'DISABLED — set WORKER_API_TOKEN'})`,
          );
          resolve();
        });
      }),
    close: () =>
      new Promise<void>((resolve) => {
        shuttingDown = true;
        const open = [...assignments.values()].filter((r) => r.appiumSessionId !== null);
        for (const record of assignments.values()) {
          if (record.status === 'queued' || record.status === 'running') {
            record.abort.abort(new Error('Worker shutting down.'));
          }
        }
        void Promise.allSettled(open.map((r) => driver.deleteSession(r.appiumSessionId!))).then(() => {
          server.close(() => resolve());
        });
      }),
    activeAssignments: () => activeCount(),
    setDraining: (value: boolean) => {
      draining = value;
      if (value) log('info', 'grid directed drain: no new assignments accepted');
    },
  };
}
