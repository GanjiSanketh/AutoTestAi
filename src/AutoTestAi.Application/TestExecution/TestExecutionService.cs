using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.ExecutionGrid;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.Projects;
using AutoTestAi.Application.Secrets;
using AutoTestAi.Application.Storage;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Application.TestGeneration;
using AutoTestAi.Application.Variables;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using AutoTestAi.Domain.Executions;
using AutoTestAi.Domain.TestCases;

namespace AutoTestAi.Application.TestExecution;

/// <summary>
/// Execution control-plane service (Slice 5). Enforces the approval gate and
/// binds exactly one immutable TestCaseVersion per execution — never "latest".
/// Historical executions are immutable; reruns create new records.
/// </summary>
public sealed class TestExecutionService : ITestExecutionService
{
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;
    private const int MaxIdempotencyKeyLength = 100;
    private static readonly IReadOnlySet<string> SupportedBrowsers =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "chromium", "firefox", "webkit" };

    private readonly IExecutionStore _store;
    private readonly ITestCaseStore _cases;
    private readonly IProjectStore _projects;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuthorizationService _authorization;
    private readonly IUserDirectory _users;
    private readonly IDateTimeProvider _clock;
    private readonly IAuditService _audit;
    private readonly IExecutionEventPublisher _events;
    private readonly IExecutionWorkflowCoordinator _workflows;
    private readonly IArtifactStorage _artifacts;
    private readonly IGridLeaseManager? _leases;
    private readonly IGridAssignmentStore? _gridAssignments;
    private readonly IExecutionVariablesStore? _variables;
    private readonly ITestSuiteLookup? _suites;
    private readonly ISecretResolver? _secrets;

    public TestExecutionService(
        IExecutionStore store,
        ITestCaseStore cases,
        IProjectStore projects,
        ICurrentUserService currentUser,
        IAuthorizationService authorization,
        IUserDirectory users,
        IDateTimeProvider clock,
        IAuditService audit,
        IExecutionEventPublisher events,
        IExecutionWorkflowCoordinator workflows,
        IArtifactStorage artifacts,
        IGridLeaseManager? leases = null,
        IGridAssignmentStore? gridAssignments = null,
        IExecutionVariablesStore? variables = null,
        ITestSuiteLookup? suites = null,
        ISecretResolver? secrets = null)
    {
        _store = store;
        _cases = cases;
        _projects = projects;
        _currentUser = currentUser;
        _authorization = authorization;
        _users = users;
        _clock = clock;
        _audit = audit;
        _events = events;
        _workflows = workflows;
        _artifacts = artifacts;
        _leases = leases;
        _gridAssignments = gridAssignments;
        _variables = variables;
        _suites = suites;
        _secrets = secrets;
    }

    public async Task<StartExecutionResultDto> StartAsync(
        StartExecutionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Authorize first so existence/probe behavior never leaks across projects
        // (preserves the Slice-1 convention: unknown projects yield 403, not 404).
        await _authorization.RequireProjectAccessAsync(
            command.ProjectId, Permissions.ExecutionsExecute, cancellationToken);

        var normalized = NormalizeAndValidate(command);

        // Repeat submissions with the same key return the original execution.
        if (normalized.IdempotencyKey is not null)
        {
            var existing = await _store.FindByIdempotencyKeyAsync(
                normalized.ProjectId, normalized.IdempotencyKey, cancellationToken);
            if (existing is not null)
            {
                var existingTest = (await _store.ListTestsByExecutionAsync(existing.Id, cancellationToken))
                    .OrderBy(t => t.CreatedAt).FirstOrDefault();
                if (existingTest?.TestCaseVersionId is not null)
                    return MapStart(existing, existingTest, existingTest.TestCaseVersionId.Value, duplicated: true);
            }
        }

        // Approval gate: resolve the EXACT version and prove it may execute.
        var version = await _cases.GetVersionByIdAsync(normalized.TestCaseVersionId, cancellationToken)
            ?? throw new NotFoundException("Test case version not found.");
        var testCase = await _cases.GetByIdAsync(version.TestCaseId, cancellationToken)
            ?? throw new NotFoundException("Test case not found.");
        if (testCase.ProjectId != normalized.ProjectId)
            throw new ForbiddenException("The test case version does not belong to this project.");
        if (testCase.Status == TestCaseStatus.Archived)
            throw new ConflictException("Archived test cases cannot be executed.");
        if (version.ReviewStatus != ReviewStatus.Approved)
            throw new ConflictException(
                $"Test case version {version.VersionNumber} has review status '{version.ReviewStatus}' and cannot be executed. Only Approved versions may execute.");

        // Slice 3A: environment-aware execution. Legacy Phase 2 executions may
        // carry a null EnvironmentId (environment-less) and keep working.
        // New normalized executions resolve to a concrete environment:
        // explicit id first, then the project's Active default. Overrides
        // always require an environment — they are rejected otherwise.
        var hasOverrides = (normalized.VariableOverrides?.Count ?? 0) > 0
            || (normalized.SecretRefOverrides?.Count ?? 0) > 0;
        Guid? resolvedEnvironmentId = null;
        if (normalized.EnvironmentId is not null)
        {
            var environment = await _projects.GetEnvironmentByIdAsync(
                normalized.EnvironmentId.Value, cancellationToken);
            if (environment is null || environment.ProjectId != normalized.ProjectId)
                throw new ValidationException("The specified environment does not belong to this project.",
                    new[] { new FieldError("environmentId", "Environment must belong to the project.") });
            resolvedEnvironmentId = environment.Id;
        }
        else
        {
            var project = await _projects.GetByIdAsync(normalized.ProjectId, cancellationToken);
            var defaultEnvId = project?.DefaultEnvironmentId;
            if (defaultEnvId.HasValue)
            {
                var defaultEnv = await _projects.GetEnvironmentByIdAsync(defaultEnvId.Value, cancellationToken);
                if (defaultEnv is not null
                    && defaultEnv.ProjectId == normalized.ProjectId
                    && defaultEnv.Status == Domain.Enums.ProjectStatus.Active)
                    resolvedEnvironmentId = defaultEnv.Id;
            }
            if (resolvedEnvironmentId is null && hasOverrides)
                throw new ValidationException("An environment is required when variable overrides are supplied.",
                    new[] { new FieldError("environmentId", "Provide an environment or configure the project default environment.") });
            // Otherwise: legacy environment-less execution (null preserved).
        }

        Guid? resolvedSuiteId = null;
        if (normalized.SuiteId is not null)
        {
            if (_suites is null)
                throw new ValidationException("Suite executions are not supported by this configuration.",
                    new[] { new FieldError("suiteId", "Suite lookup is unavailable.") });
            var suite = await _suites.GetSuiteByIdAsync(normalized.SuiteId.Value, cancellationToken);
            if (suite is null || suite.ProjectId != normalized.ProjectId)
                throw new ValidationException("The specified suite does not belong to this project.",
                    new[] { new FieldError("suiteId", "Suite must belong to the project.") });
            resolvedSuiteId = suite.Id;
        }

        if (resolvedEnvironmentId.HasValue)
        {
            // Validates override shapes and rejects raw secrets in secretRef
            // overrides. The normalized command never carries secret values.
            _ = ExecutionCommandFactory.Create(
                normalized.ProjectId,
                normalized.TestCaseVersionId,
                resolvedEnvironmentId.Value,
                resolvedSuiteId,
                normalized.Browser ?? "chromium",
                normalized.VariableOverrides,
                normalized.SecretRefOverrides,
                normalized.IdempotencyKey);
            if (_secrets is not null && normalized.SecretRefOverrides is not null)
            {
                foreach (var secretRef in normalized.SecretRefOverrides.Values)
                {
                    if (!await _secrets.ExistsAsync(secretRef, cancellationToken))
                        throw new ValidationException("A secret reference override is unknown.",
                            new[] { new FieldError("secretRefOverrides", "Each override must reference an existing secret.") });
                }
            }
        }
        else if (hasOverrides)
        {
            throw new ValidationException("An environment is required when variable overrides are supplied.",
                new[] { new FieldError("environmentId", "Provide an environment or configure the project default environment.") });
        }

        var steps = TestStep.Parse(version.StructuredSteps?.RootElement);
        if (steps.Count == 0)
            throw new ConflictException("The approved version has no executable structured steps.");

        if (!_workflows.IsConfigured)
            throw new InvalidOperationException(
                "Execution infrastructure is not configured. Temporal must be configured to start executions.");

        var now = _clock.UtcNow;
        var execution = new Execution
        {
            ProjectId = normalized.ProjectId,
            TriggerType = TriggerType.Manual,
            EnvironmentId = resolvedEnvironmentId,
            Status = ExecutionStatus.Queued,
            IdempotencyKey = normalized.IdempotencyKey,
            CreatedBy = await ResolveAppUserIdAsync(cancellationToken),
            CreatedAt = now,
            UpdatedAt = now,
        };
        await _store.AddExecutionAsync(execution, cancellationToken);

        var executionTest = new ExecutionTest
        {
            ExecutionId = execution.Id,
            TestCaseId = testCase.Id,
            TestCaseVersionId = version.Id,
            Status = ExecutionTestStatus.Queued,
            Framework = string.IsNullOrWhiteSpace(testCase.Framework) ? "playwright" : testCase.Framework.Trim(),
            Browser = normalized.Browser,
            Attempt = 1,
            FailureClassification = FailureClassification.Unknown,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await _store.AddExecutionTestAsync(executionTest, cancellationToken);
        await _store.SaveChangesAsync(cancellationToken);

        // Slice 3A: persist the override envelope (refs only, never values) so
        // the Temporal activity can reload it by execution id. Workflow args
        // and history never carry secret values.
        if (_variables is not null && (resolvedEnvironmentId.HasValue || resolvedSuiteId.HasValue || hasOverrides))
        {
            await _variables.SaveAsync(new Domain.Entities.ExecutionVariables
            {
                ExecutionId = execution.Id,
                ProjectId = execution.ProjectId,
                SuiteId = resolvedSuiteId,
                EnvironmentId = resolvedEnvironmentId,
                VariableOverridesJson = JsonSerializer.Serialize(
                    normalized.VariableOverrides ?? new Dictionary<string, string>()),
                SecretRefOverridesJson = JsonSerializer.Serialize(
                    normalized.SecretRefOverrides ?? new Dictionary<string, string>()),
            }, cancellationToken);
        }

        await _audit.RecordAsync("execution.requested", "execution",
            execution.Id.ToString(), execution.ProjectId,
            SafeMetadata(execution, testCase.Id, version.Id, "requested"), cancellationToken);

        try
        {
            execution.WorkflowId = await _workflows.StartAsync(execution.Id, execution.ProjectId, cancellationToken);
            await _store.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not (UnauthorizedAccessException or ForbiddenException))
        {
            // Fail safely: never leave a Queued execution that can never start.
            execution.Status = ExecutionStatus.Error;
            execution.CompletedAt = _clock.UtcNow;
            executionTest.Status = ExecutionTestStatus.Error;
            executionTest.ErrorType = nameof(ExecutionStatus.Error);
            executionTest.ErrorMessage = "The execution workflow could not be started.";
            executionTest.FailureClassification = FailureClassification.EnvironmentFailure;
            await _store.SaveChangesAsync(cancellationToken);
            await _audit.RecordAsync("execution.failed", "execution",
                execution.Id.ToString(), execution.ProjectId,
                SafeMetadata(execution, testCase.Id, version.Id, "start-failed"), cancellationToken);
            throw;
        }

        await _events.PublishAsync(execution.Id, ExecutionEvents.ExecutionStatusChanged,
            new { executionId = execution.Id, status = execution.Status.ToString() }, cancellationToken);

        return MapStart(execution, executionTest, version.Id, duplicated: false);
    }

    public async Task<PagedResult<ExecutionListItemDto>> ListAsync(
        Guid projectId, int page, int pageSize, ExecutionFilters filters, CancellationToken cancellationToken)
    {
        await _authorization.RequireProjectAccessAsync(
            projectId, Permissions.ExecutionsRead, cancellationToken);

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize);
        var status = ValidateStatusFilter(filters);

        var totalCount = await _store.CountAsync(projectId, status, filters.TestCaseId, cancellationToken);
        var rows = await _store.ListAsync(
            projectId, status, filters.TestCaseId, (page - 1) * pageSize, pageSize, cancellationToken);

        return new PagedResult<ExecutionListItemDto>(
            rows.Select(MapListItem).ToList(), totalCount, page, pageSize);
    }

    public async Task<ExecutionDetailDto> GetAsync(Guid executionId, CancellationToken cancellationToken)
    {
        var execution = await RequireExecutionAsync(executionId, Permissions.ExecutionsRead, cancellationToken);
        return await MapDetailAsync(execution, cancellationToken);
    }

    public async Task<IReadOnlyList<ExecutionStepDto>> ListStepsAsync(
        Guid executionId, CancellationToken cancellationToken)
    {
        var test = await RequireSingleTestAsync(executionId, Permissions.ExecutionsRead, cancellationToken);
        return (await _store.ListStepResultsAsync(test.Id, cancellationToken))
            .OrderBy(s => s.StepOrder).Select(MapStep).ToList();
    }

    public async Task<IReadOnlyList<ExecutionLogDto>> ListLogsAsync(
        Guid executionId, long? afterId, int take, CancellationToken cancellationToken)
    {
        var test = await RequireSingleTestAsync(executionId, Permissions.ExecutionsRead, cancellationToken);
        take = Math.Clamp(take <= 0 ? 100 : take, 1, 500);
        return (await _store.ListLogsAsync(test.Id, afterId, take, cancellationToken))
            .Select(l => new ExecutionLogDto(l.Id, l.Timestamp, l.Level, l.Message)).ToList();
    }

    public async Task<IReadOnlyList<ExecutionArtifactDto>> ListArtifactsAsync(
        Guid executionId, CancellationToken cancellationToken)
    {
        var test = await RequireSingleTestAsync(executionId, Permissions.ExecutionsRead, cancellationToken);
        return (await _store.ListArtifactsAsync(test.Id, cancellationToken))
            .Select(a => new ExecutionArtifactDto(
                a.Id, a.ArtifactType, a.FileName, a.StepOrder, a.ContentType, a.SizeBytes, a.CreatedAt)).ToList();
    }

    public async Task<ArtifactDownloadDto> GetArtifactDownloadUrlAsync(
        Guid executionId, Guid artifactId, CancellationToken cancellationToken)
    {
        var test = await RequireSingleTestAsync(executionId, Permissions.ExecutionsRead, cancellationToken);
        var artifact = await _store.GetArtifactByIdAsync(artifactId, cancellationToken);
        if (artifact is null || artifact.ExecutionTestId != test.Id)
            throw new NotFoundException("Execution artifact not found.");
        var url = await _artifacts.GetPresignedDownloadUrlAsync(
            artifact.StorageKey, 900, cancellationToken);
        return new ArtifactDownloadDto(url, 900);
    }

    public async Task<CancelExecutionResultDto> CancelAsync(
        Guid executionId, CancellationToken cancellationToken)
    {
        var execution = await RequireExecutionAsync(executionId, Permissions.ExecutionsCancel, cancellationToken);
        var test = (await _store.ListTestsByExecutionAsync(execution.Id, cancellationToken))
            .OrderBy(t => t.CreatedAt).FirstOrDefault()
            ?? throw new NotFoundException("Execution test not found.");

        if (ExecutionTransitions.IsTerminal(execution.Status))
            return new CancelExecutionResultDto(execution.Id, execution.Status.ToString(), false);

        if (execution.Status == ExecutionStatus.Queued)
        {
            // Revoked before start: the workflow's prepare step reconciles this
            // if a start is already racing (fail-safe, §60).
            var now = _clock.UtcNow;
            execution.Status = ExecutionStatus.Cancelled;
            execution.CompletedAt = now;
            execution.UpdatedAt = now;
            test.Status = ExecutionTestStatus.Cancelled;
            test.UpdatedAt = now;
            await _store.SaveChangesAsync(cancellationToken);
            if (_leases is not null)
            {
                var activeAssignment = await _gridAssignments.FindActiveByTestAsync(test.Id, cancellationToken);
                if (activeAssignment is not null)
                    await _leases.ReleaseAssignmentAsync(test.Id, activeAssignment.Id, activeAssignment.AssignmentToken, test.Status.ToString(), cancellationToken);
            }
            await _audit.RecordAsync("execution.cancelled", "execution",
                execution.Id.ToString(), execution.ProjectId,
                SafeMetadata(execution, test.TestCaseId, test.TestCaseVersionId, "cancelled-before-start"),
                cancellationToken);
            await _events.PublishAsync(execution.Id, ExecutionEvents.ExecutionStatusChanged,
                new { executionId = execution.Id, status = execution.Status.ToString() }, cancellationToken);
            return new CancelExecutionResultDto(execution.Id, execution.Status.ToString(), true);
        }

        // Running: ask Temporal to cancel; the workflow compensation persists
        // the terminal Cancelled state (never set it here while work continues).
        if (string.IsNullOrWhiteSpace(execution.WorkflowId))
            throw new ConflictException("The execution has no workflow to cancel.");
        var accepted = await _workflows.CancelAsync(execution.WorkflowId, cancellationToken);
        if (!accepted)
        {
            // Workflow is already gone: reconcile terminal state now.
            var now = _clock.UtcNow;
            if (ExecutionTransitions.IsValidTransition(execution.Status, ExecutionStatus.Cancelled))
            {
                execution.Status = ExecutionStatus.Cancelled;
                execution.CompletedAt = now;
                execution.UpdatedAt = now;
            }
            if (ExecutionTransitions.IsValidTestTransition(test.Status, ExecutionTestStatus.Cancelled))
            {
                test.Status = ExecutionTestStatus.Cancelled;
                test.UpdatedAt = now;
            }
            await _store.SaveChangesAsync(cancellationToken);
            await _audit.RecordAsync("execution.cancelled", "execution",
                execution.Id.ToString(), execution.ProjectId,
                SafeMetadata(execution, test.TestCaseId, test.TestCaseVersionId, "cancelled-reconciled"),
                cancellationToken);
            await _events.PublishAsync(execution.Id, ExecutionEvents.ExecutionStatusChanged,
                new { executionId = execution.Id, status = execution.Status.ToString() }, cancellationToken);
        }
        return new CancelExecutionResultDto(execution.Id, execution.Status.ToString(), true);
    }

    // ---------- helpers ----------

    /// <summary>
    /// Slice-1/3 convention: unknown ids authorize against the id itself as an
    /// opaque scope, so non-admins get 403 (never existence-revealing 404).
    /// </summary>
    private async Task<Execution> RequireExecutionAsync(
        Guid executionId, string permission, CancellationToken cancellationToken)
    {
        var execution = await _store.GetExecutionByIdAsync(executionId, cancellationToken);
        await _authorization.RequireProjectAccessAsync(
            execution?.ProjectId ?? executionId, permission, cancellationToken);
        return execution ?? throw new NotFoundException("Execution not found.");
    }

    private async Task<ExecutionTest> RequireSingleTestAsync(
        Guid executionId, string permission, CancellationToken cancellationToken)
    {
        var execution = await RequireExecutionAsync(executionId, permission, cancellationToken);
        return (await _store.ListTestsByExecutionAsync(execution.Id, cancellationToken))
            .OrderBy(t => t.CreatedAt).FirstOrDefault()
            ?? throw new NotFoundException("Execution test not found.");
    }

    private async Task<Guid?> ResolveAppUserIdAsync(CancellationToken cancellationToken)
        => string.IsNullOrWhiteSpace(_currentUser.ExternalIdentityId)
            ? null
            : await _users.FindAppUserIdAsync(_currentUser.ExternalIdentityId!, cancellationToken);

    private static StartExecutionCommand NormalizeAndValidate(StartExecutionCommand command)
    {
        var errors = new List<FieldError>();
        if (command.ProjectId == Guid.Empty)
            errors.Add(new FieldError("projectId", "Project id is required."));
        if (command.TestCaseVersionId == Guid.Empty)
            errors.Add(new FieldError("testCaseVersionId", "Test case version id is required."));
        var browser = (command.Browser ?? "chromium").Trim();
        if (!SupportedBrowsers.Contains(browser))
            errors.Add(new FieldError("browser", "Browser must be 'chromium', 'firefox' or 'webkit'."));
        string? key = string.IsNullOrWhiteSpace(command.IdempotencyKey) ? null : command.IdempotencyKey.Trim();
        if (key is not null && key.Length > MaxIdempotencyKeyLength)
            errors.Add(new FieldError("idempotencyKey", $"Idempotency key must be at most {MaxIdempotencyKeyLength} characters."));
        ValidationException.ThrowIfInvalid(errors);
        return command with { Browser = browser.ToLowerInvariant(), IdempotencyKey = key };
    }

    private static string? ValidateStatusFilter(ExecutionFilters filters)
    {
        if (string.IsNullOrWhiteSpace(filters.Status)) return null;
        if (Enum.TryParse<ExecutionStatus>(filters.Status.Trim(), ignoreCase: true, out var parsed))
            return parsed.ToString();
        throw new ValidationException("Status filter is invalid.",
            new[] { new FieldError("status", "Status must be 'Queued', 'Running', 'Passed', 'Failed', 'Cancelled', 'TimedOut' or 'Error'.") });
    }

    private static StartExecutionResultDto MapStart(
        Execution execution, ExecutionTest test, Guid versionId, bool duplicated)
        => new(execution.Id, test.Id,
            execution.ProjectId, test.TestCaseId, versionId,
            execution.Status.ToString(), execution.WorkflowId, execution.CreatedAt, duplicated);

    private static ExecutionListItemDto MapListItem(ExecutionListRow row) => new(
        row.Execution.Id, row.Execution.ProjectId,
        row.Execution.Status.ToString(), row.Execution.TriggerType.ToString(),
        row.Test.TestCaseId, row.TestKey, row.TestTitle, row.TestCaseVersionNumber,
        row.Test.Browser, row.Test.FailureClassification.ToString(), row.Test.DurationMs,
        row.Execution.StartedAt, row.Execution.CompletedAt, row.Execution.CreatedAt);

    private async Task<ExecutionDetailDto> MapDetailAsync(Execution execution, CancellationToken ct)
    {
        var test = (await _store.ListTestsByExecutionAsync(execution.Id, ct))
            .OrderBy(t => t.CreatedAt).FirstOrDefault()
            ?? throw new NotFoundException("Execution test not found.");
        var testCase = await _cases.GetByIdAsync(test.TestCaseId, ct);
        TestCaseVersion? version = null;
        if (test.TestCaseVersionId is not null)
            version = await _cases.GetVersionByIdAsync(test.TestCaseVersionId.Value, ct);
        var steps = (await _store.ListStepResultsAsync(test.Id, ct))
            .OrderBy(s => s.StepOrder).Select(MapStep).ToList();
        Guid? workerId = null;
        string? assignmentStatus = null;
        if (_gridAssignments is not null)
        {
            try
            {
                var lease = await _gridAssignments.FindActiveByTestAsync(test.Id, ct);
                workerId = lease?.WorkerId;
                assignmentStatus = lease?.Status.ToString();
            }
            catch
            {
                // Grid enrichment is supplementary; detail always wins.
            }
        }
        return new ExecutionDetailDto(
            execution.Id, execution.ProjectId,
            execution.Status.ToString(), execution.TriggerType.ToString(),
            execution.EnvironmentId, execution.WorkflowId,
            execution.StartedAt, execution.CompletedAt,
            execution.CreatedBy, execution.CreatedAt,
            new ExecutionTestDetailDto(
                test.Id, test.TestCaseId,
                testCase?.TestKey ?? "(unknown)",
                testCase?.Title ?? "(unknown)",
                testCase?.SourceType,
                test.TestCaseVersionId ?? Guid.Empty,
                version?.VersionNumber ?? 0,
                (version?.ReviewStatus ?? ReviewStatus.Pending).ToString(),
                test.Status.ToString(), test.Framework, test.Browser,
                test.FailureClassification.ToString(), test.Attempt,
                test.DurationMs, test.ErrorType, test.ErrorMessage,
                steps, test.CreatedAt, test.UpdatedAt),
            workerId, assignmentStatus);
    }

    private static ExecutionStepDto MapStep(ExecutionStepResult row) => new(
        row.StepOrder, row.Action, row.Target, row.Status.ToString(),
        row.StartedAt, row.CompletedAt, row.DurationMs, row.ErrorMessage);

    private static string SafeMetadata(
        Execution execution, Guid testCaseId, Guid? versionId, string outcome)
        => SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
        {
            executionId = execution.Id,
            testCaseId,
            testCaseVersionId = versionId,
            status = execution.Status.ToString(),
            workflowId = execution.WorkflowId,
            outcome,
        }));
}
