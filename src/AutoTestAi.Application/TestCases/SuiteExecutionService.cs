using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using System.Text.Json;

namespace AutoTestAi.Application.TestCases;

/// <summary>
/// Manual suite execution orchestration (Phase 4 Slice 9A).
/// Reuses the existing execution pipeline: every suite member fans out to
/// <see cref="ITestExecutionService.StartAsSystemAsync"/> with the exact
/// latest Approved <see cref="TestCaseVersion"/> bound upfront, so the
/// engine's approval gate, assignment fencing, retry behavior and audit
/// trail are unchanged. All members are validated BEFORE any execution is
/// created — a suite is never partially started.
/// </summary>
public sealed class SuiteExecutionService : ISuiteExecutionService
{
    /// <summary>
    /// Suite-level idempotency keys must leave room for the per-member suffix
    /// ("-" + test case guid) inside the 100-char execution key column.
    /// </summary>
    private const int MaxSuiteIdempotencyKeyLength = 60;

    private readonly ISuiteStore _suites;
    private readonly ITestCaseStore _cases;
    private readonly ITestExecutionService _executions;
    private readonly IAuthorizationService _authorization;
    private readonly IDateTimeProvider _clock;
    private readonly IAuditService _audit;

    public SuiteExecutionService(
        ISuiteStore suites,
        ITestCaseStore cases,
        ITestExecutionService executions,
        IAuthorizationService authorization,
        IDateTimeProvider clock,
        IAuditService audit)
    {
        _suites = suites;
        _cases = cases;
        _executions = executions;
        _authorization = authorization;
        _clock = clock;
        _audit = audit;
    }

    public async Task<ExecuteSuiteResult> ExecuteAsync(Guid projectId, Guid suiteId, ExecuteSuiteCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (projectId == Guid.Empty)
            throw new ValidationException("Project id is required.",
                [new FieldError("projectId", "Project id is required.")]);

        // Authorize once for the project before touching suite state.
        await _authorization.RequireProjectAccessAsync(
            projectId, Permissions.TestCasesManage, cancellationToken);

        var idempotencyKey = string.IsNullOrWhiteSpace(command.IdempotencyKey)
            ? null
            : command.IdempotencyKey.Trim();
        if (idempotencyKey is not null && idempotencyKey.Length > MaxSuiteIdempotencyKeyLength)
            throw new ValidationException("Idempotency key is too long.",
                [new FieldError("idempotencyKey", $"Idempotency key must be at most {MaxSuiteIdempotencyKeyLength} characters.")]);

        // Suite identity comes from the route; a body suite id that disagrees
        // is rejected rather than silently preferred either way.
        if (command.SuiteId != Guid.Empty && command.SuiteId != suiteId)
            throw new ValidationException("Suite id mismatch.",
                [new FieldError("suiteId", "The suite id in the body must match the route.")]);

        var suite = await _suites.GetByIdWithMembersAsync(suiteId, cancellationToken);
        if (suite is null)
            throw new NotFoundException("Test suite not found.");

        if (suite.ProjectId != projectId)
            throw new ForbiddenException("The suite does not belong to this project.");

        if (!string.Equals(suite.Status, ProjectStatus.Active.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new ConflictException("Only active suites can be executed.");

        // Deterministic empty-suite behavior: reject, never create
        // meaningless execution state.
        var members = suite.Members
            .OrderBy(m => m.ExecutionOrder)
            .ThenBy(m => m.TestCaseId)
            .ToList();
        if (members.Count == 0)
            throw new ConflictException("The suite has no test cases.");

        // Resolve latest versions in one batch, then prove every member may
        // execute BEFORE creating anything (no partial fan-out).
        var latestVersions = await _cases.GetLatestVersionsAsync(
            members.Select(m => m.TestCaseId).ToList(), cancellationToken);

        var bound = new List<(SuiteMemberDto Member, TestCaseVersion Version)>(members.Count);
        foreach (var member in members)
        {
            if (!latestVersions.TryGetValue(member.TestCaseId, out var version))
                throw new ValidationException($"Test case {member.TestKey} has no versions.",
                    [new FieldError("version", $"Test case {member.TestKey} has no versions.")]);

            if (version.ReviewStatus != ReviewStatus.Approved)
                throw new ValidationException($"Test case {member.TestKey} has no approved version (current: {version.ReviewStatus}).",
                    [new FieldError("reviewStatus", $"Test case {member.TestKey} has no approved version (current: {version.ReviewStatus}).")]);

            var testCase = await _cases.GetByIdAsync(member.TestCaseId, cancellationToken)
                ?? throw new ValidationException($"Test case {member.TestKey} no longer exists.",
                    [new FieldError("testCaseId", $"Test case {member.TestKey} no longer exists.")]);
            if (testCase.ProjectId != projectId)
                throw new ForbiddenException("A suite member does not belong to this project.");
            if (testCase.Status == TestCaseStatus.Archived)
                throw new ConflictException($"Test case {member.TestKey} is archived and cannot be executed.");

            bound.Add((member, version));
        }

        // Suite-level fast path: a previous manual run with this key returns
        // its head execution. Per-member keys below make retries converge
        // even without this hit (StartAsSystemAsync returns Duplicated).
        if (idempotencyKey is not null)
        {
            var existing = await _suites.GetExecutionByIdempotencyKeyAsync(projectId, idempotencyKey, cancellationToken);
            if (existing is not null)
            {
                return new ExecuteSuiteResult(
                    existing.Id,
                    suiteId,
                    members.Count,
                    existing.Status.ToString(),
                    existing.CreatedAt);
            }
        }

        // Fan out through the existing execution seam (engine, grid
        // scheduler, assignment fencing, retries all unchanged). The exact
        // Approved version id is bound here and never re-resolved later.
        var baseKey = idempotencyKey ?? Guid.NewGuid().ToString("N");
        var executionIds = new List<Guid>(bound.Count);
        for (var i = 0; i < bound.Count; i++)
        {
            var (member, version) = bound[i];
            var startCommand = new StartExecutionCommand(
                projectId,
                version.Id,
                null, // EnvironmentId - uses project default
                "chromium",
                $"{baseKey}-{member.TestCaseId:N}",
                suiteId,
                null, // VariableOverrides
                null, // SecretRefOverrides
                TriggerType.Manual,
                null, // MobileDevicePoolId
                null); // MobileAppId

            var started = await _executions.StartAsSystemAsync(startCommand, cancellationToken);
            executionIds.Add(started.ExecutionId);
        }

        await _audit.RecordAsync("suite.executed", "test_suite",
            suite.Id.ToString(), projectId,
            JsonSerializer.Serialize(new
            {
                suiteId = suite.Id,
                suiteName = suite.Name,
                testCount = members.Count,
                triggerType = "manual",
                executionIds,
            }), cancellationToken);

        return new ExecuteSuiteResult(
            executionIds[0],
            suiteId,
            members.Count,
            ExecutionStatus.Queued.ToString(),
            _clock.UtcNow);
    }
}
