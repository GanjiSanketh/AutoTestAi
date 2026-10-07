using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Identity;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using System.Text.Json;

namespace AutoTestAi.Application.TestCases;

/// <summary>
/// Suite management use cases (Phase 4 Slice 9A).
/// Every method enforces server-side authorization through suite → project;
/// the store performs no authorization checks. Suites are project-scoped:
/// membership, execution history and reports never cross projects.
/// </summary>
public sealed class SuiteService : ISuiteService
{
    private readonly ISuiteStore _suites;
    private readonly ITestCaseStore _cases;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuthorizationService _authorization;
    private readonly IUserDirectory _users;
    private readonly IDateTimeProvider _clock;
    private readonly IAuditService _audit;

    public SuiteService(
        ISuiteStore suites,
        ITestCaseStore cases,
        ICurrentUserService currentUser,
        IAuthorizationService authorization,
        IUserDirectory users,
        IDateTimeProvider clock,
        IAuditService audit)
    {
        _suites = suites;
        _cases = cases;
        _currentUser = currentUser;
        _authorization = authorization;
        _users = users;
        _clock = clock;
        _audit = audit;
    }

    public async Task<CreateSuiteResult> CreateAsync(CreateSuiteCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await _authorization.RequireProjectAccessAsync(
            command.ProjectId, Permissions.TestCasesManage, cancellationToken);

        var errors = new List<FieldError>();

        var name = command.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
            errors.Add(new FieldError("name", "Suite name is required."));
        else if (name.Length > 200)
            errors.Add(new FieldError("name", "Suite name must be at most 200 characters."));

        var status = ParseStatus(command.Status, errors);

        if (command.Members is not null)
        {
            var seen = new HashSet<Guid>();
            for (int i = 0; i < command.Members.Count; i++)
            {
                var member = command.Members[i];
                if (member.TestCaseId == Guid.Empty)
                    errors.Add(new FieldError($"members[{i}].testCaseId", "Test case ID is required."));
                else if (!seen.Add(member.TestCaseId))
                    errors.Add(new FieldError($"members[{i}].testCaseId", "Duplicate test case in suite."));
                if (member.ExecutionOrder <= 0)
                    errors.Add(new FieldError($"members[{i}].executionOrder", "Execution order must be positive."));
            }
        }

        ValidationException.ThrowIfInvalid(errors);

        if (await _suites.ExistsWithNameAsync(command.ProjectId, name, null, cancellationToken))
            throw new ConflictException($"A suite with the name '{name}' already exists in this project.");

        // Resolve member test cases before creating anything so a bad member
        // never leaves a half-created suite behind.
        var resolvedMembers = new List<TestCase>();
        if (command.Members is not null)
        {
            foreach (var member in command.Members)
            {
                var testCase = await _cases.GetByIdAsync(member.TestCaseId, cancellationToken)
                    ?? throw new NotFoundException("Test case not found.");
                if (testCase.ProjectId != command.ProjectId)
                    throw new ForbiddenException("The test case does not belong to this project.");
                resolvedMembers.Add(testCase);
            }
        }

        var now = _clock.UtcNow;
        var suite = new TestSuite
        {
            ProjectId = command.ProjectId,
            Name = name,
            Description = BlankToNull(command.Description),
            Status = status ?? ProjectStatus.Active,
            CreatedBy = await ResolveAppUserIdAsync(cancellationToken),
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _suites.AddAsync(suite, cancellationToken);

        if (command.Members is not null)
        {
            for (int i = 0; i < command.Members.Count; i++)
                await _suites.AddMemberAsync(suite.Id, command.Members[i].TestCaseId, command.Members[i].ExecutionOrder, cancellationToken);
        }
        await _suites.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync("suite.created", "test_suite",
            suite.Id.ToString(), command.ProjectId,
            JsonSerializer.Serialize(new { suiteId = suite.Id, name = suite.Name, testCount = resolvedMembers.Count }),
            cancellationToken);

        return new CreateSuiteResult(suite.Id, command.ProjectId, suite.Name, suite.Name);
    }

    public async Task<PagedSuiteList> ListAsync(Guid projectId, SuiteListFilters filters, int page, int pageSize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filters);
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.TestCasesRead, ct);

        var (skip, take, pageNumber, size) = Paginate(page, pageSize);
        var totalCount = await _suites.CountAsync(projectId, NormalizedSearch(filters.Search), filters.Status, ct);
        var result = await _suites.ListAsync(projectId, NormalizedSearch(filters.Search), filters.Status, skip, take, ct);
        return new PagedSuiteList(result, totalCount, pageNumber, size);
    }

    public async Task<SuiteDetailDto?> GetByIdAsync(Guid suiteId, CancellationToken ct)
    {
        await RequireSuiteAsync(suiteId, Permissions.TestCasesRead, ct);
        return await _suites.GetByIdWithMembersAsync(suiteId, ct);
    }

    public async Task<SuiteDetailDto?> UpdateAsync(Guid suiteId, UpdateSuiteCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var suite = await RequireSuiteAsync(suiteId, Permissions.TestCasesManage, ct);

        var errors = new List<FieldError>();

        var name = command.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
            errors.Add(new FieldError("name", "Suite name is required."));
        else if (name.Length > 200)
            errors.Add(new FieldError("name", "Suite name must be at most 200 characters."));

        var status = ParseStatus(command.Status, errors);

        ValidationException.ThrowIfInvalid(errors);

        if (await _suites.ExistsWithNameAsync(suite.ProjectId, name, suiteId, ct))
            throw new ConflictException($"A suite with the name '{name}' already exists in this project.");

        suite.Name = name;
        suite.Description = BlankToNull(command.Description);
        if (status.HasValue)
            suite.Status = status.Value;
        suite.UpdatedAt = _clock.UtcNow;

        await _suites.SaveChangesAsync(ct);

        await _audit.RecordAsync("suite.updated", "test_suite",
            suite.Id.ToString(), suite.ProjectId,
            JsonSerializer.Serialize(new { suiteId = suite.Id, name = suite.Name }),
            ct);

        return await _suites.GetByIdWithMembersAsync(suiteId, ct);
    }

    public async Task ArchiveAsync(Guid suiteId, CancellationToken ct)
    {
        var suite = await RequireSuiteAsync(suiteId, Permissions.TestCasesManage, ct);

        suite.Status = ProjectStatus.Archived;
        suite.UpdatedAt = _clock.UtcNow;

        await _suites.SaveChangesAsync(ct);

        await _audit.RecordAsync("suite.archived", "test_suite",
            suite.Id.ToString(), suite.ProjectId,
            JsonSerializer.Serialize(new { suiteId = suite.Id }),
            ct);
    }

    public async Task<SuiteMemberResult> AddTestCaseAsync(Guid suiteId, CreateSuiteMemberCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var suite = await RequireSuiteAsync(suiteId, Permissions.TestCasesManage, ct);
        RequireMutable(suite);

        if (command.ExecutionOrder <= 0)
            ValidationException.ThrowIfInvalid([new FieldError("executionOrder", "Execution order must be positive.")]);

        var testCase = await _cases.GetByIdAsync(command.TestCaseId, ct)
            ?? throw new NotFoundException("Test case not found.");
        if (testCase.ProjectId != suite.ProjectId)
            throw new ForbiddenException("The test case does not belong to this project.");

        var existingMembers = await _suites.GetMembersAsync(suiteId, ct);
        if (existingMembers.Any(m => m.TestCaseId == command.TestCaseId))
            throw new ConflictException("Test case is already a member of this suite.");

        await _suites.AddMemberAsync(suiteId, command.TestCaseId, command.ExecutionOrder, ct);
        await _suites.SaveChangesAsync(ct);

        await _audit.RecordAsync("suite.test_added", "test_suite",
            suiteId.ToString(), suite.ProjectId,
            JsonSerializer.Serialize(new { suiteId, testCaseId = command.TestCaseId, executionOrder = command.ExecutionOrder }),
            ct);

        return new SuiteMemberResult(suiteId, command.TestCaseId, command.ExecutionOrder);
    }

    public async Task RemoveTestCaseAsync(Guid suiteId, Guid testCaseId, CancellationToken ct)
    {
        var suite = await RequireSuiteAsync(suiteId, Permissions.TestCasesManage, ct);
        RequireMutable(suite);

        if (!await _suites.RemoveMemberAsync(suiteId, testCaseId, ct))
            throw new NotFoundException("Test case is not a member of this suite.");
        await _suites.SaveChangesAsync(ct);

        await _audit.RecordAsync("suite.test_removed", "test_suite",
            suiteId.ToString(), suite.ProjectId,
            JsonSerializer.Serialize(new { suiteId, testCaseId }),
            ct);
    }

    public async Task ReorderAsync(Guid suiteId, ReorderSuiteMembersCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var suite = await RequireSuiteAsync(suiteId, Permissions.TestCasesManage, ct);
        RequireMutable(suite);

        var errors = new List<FieldError>();
        var orders = new Dictionary<Guid, int>();
        for (int i = 0; i < command.Members.Count; i++)
        {
            var item = command.Members[i];
            if (item.TestCaseId == Guid.Empty)
                errors.Add(new FieldError($"members[{i}].testCaseId", "Test case ID is required."));
            else if (!orders.TryAdd(item.TestCaseId, item.ExecutionOrder))
                errors.Add(new FieldError($"members[{i}].testCaseId", "Duplicate test case in suite."));
            if (item.ExecutionOrder <= 0)
                errors.Add(new FieldError($"members[{i}].executionOrder", "Execution order must be positive."));
        }
        if (orders.Values.Distinct().Count() != orders.Count)
            errors.Add(new FieldError("members", "Execution orders must be unique."));
        ValidationException.ThrowIfInvalid(errors);

        // Reorder never adds or drops members: the payload must cover exactly
        // the current membership set, so historical execution rows keep their
        // meaning and unknown IDs cannot inject cross-project rows.
        var existing = await _suites.GetMembersAsync(suiteId, ct);
        var existingIds = new HashSet<Guid>(existing.Select(m => m.TestCaseId));
        if (!existingIds.SetEquals(orders.Keys))
            throw new ValidationException("Reorder must cover exactly the current suite members.",
                [new FieldError("members", "Reorder must include every current member exactly once.")]);

        if (orders.Count == 0)
            return;

        await _suites.UpdateMemberOrdersAsync(suiteId, orders, ct);
        await _suites.SaveChangesAsync(ct);

        await _audit.RecordAsync("suite.test_reordered", "test_suite",
            suiteId.ToString(), suite.ProjectId,
            JsonSerializer.Serialize(new { suiteId, memberCount = orders.Count }),
            ct);
    }

    public async Task<PagedSuiteExecutionsResponse> GetExecutionHistoryAsync(Guid suiteId, SuiteExecutionHistoryFilters filters, int page, int pageSize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filters);

        await RequireSuiteAsync(suiteId, Permissions.ExecutionsRead, ct);

        var (status, trigger) = ValidateHistoryFilters(filters);
        var (skip, take, pageNumber, size) = Paginate(page, pageSize);
        var totalCount = await _suites.GetExecutionCountAsync(suiteId, status, trigger, ct);
        var executions = await _suites.GetExecutionHistoryAsync(suiteId, status, trigger, skip, take, ct);

        var items = executions.Select(e => new SuiteExecutionSummaryDto(
            e.ExecutionId,
            e.Status,
            e.TriggerType,
            e.CreatedAt,
            e.StartedAt,
            e.CompletedAt,
            e.TestCount,
            e.PassedCount,
            e.FailedCount)).ToList();

        return new PagedSuiteExecutionsResponse(items, totalCount, pageNumber, size);
    }

    public async Task<SuiteReportDto?> GetReportAsync(Guid suiteId, SuiteReportFilters filters, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filters);

        await RequireSuiteAsync(suiteId, Permissions.ReportsRead, ct);

        if (filters.From.HasValue && filters.To.HasValue && filters.From.Value > filters.To.Value)
            throw new ValidationException("Invalid report range.",
                [new FieldError("from", "Report 'from' must not be after 'to'.")]);

        return await _suites.GetReportDataAsync(suiteId, filters.From, filters.To, ct);
    }

    // ---------- helpers ----------

    /// <summary>
    /// Loads the suite while preserving the Slice-1 convention: unknown ids
    /// authorize against the id itself as an opaque scope, so non-admins get
    /// 403 (never existence-revealing 404); admins get a true 404.
    /// </summary>
    private async Task<TestSuite> RequireSuiteAsync(Guid suiteId, string permission, CancellationToken ct)
    {
        var suite = await _suites.GetByIdRawAsync(suiteId, ct);
        await _authorization.RequireProjectAccessAsync(
            suite?.ProjectId ?? suiteId, permission, ct);
        return suite ?? throw new NotFoundException("Test suite not found.");
    }

    private static void RequireMutable(TestSuite suite)
    {
        if (suite.Status == ProjectStatus.Archived)
            throw new ConflictException("Archived suites cannot be modified.");
    }

    private static ProjectStatus? ParseStatus(string? status, List<FieldError> errors)
    {
        if (string.IsNullOrWhiteSpace(status))
            return null;
        if (Enum.TryParse<ProjectStatus>(status.Trim(), true, out var parsed) &&
            (parsed == ProjectStatus.Active || parsed == ProjectStatus.Archived))
            return parsed;
        errors.Add(new FieldError("status", "Status must be 'Active' or 'Archived'."));
        return null;
    }

    private static (ExecutionStatus? Status, TriggerType? Trigger) ValidateHistoryFilters(SuiteExecutionHistoryFilters filters)
    {
        ExecutionStatus? status = null;
        TriggerType? trigger = null;
        if (!string.IsNullOrWhiteSpace(filters.Status))
        {
            if (Enum.TryParse<ExecutionStatus>(filters.Status.Trim(), true, out var parsed))
                status = parsed;
            else
                throw new ValidationException("Status filter is invalid.",
                    [new FieldError("status", "Status must be 'Queued', 'Running', 'Passed', 'Failed', 'Cancelled', 'TimedOut' or 'Error'.")]);
        }
        if (!string.IsNullOrWhiteSpace(filters.TriggerType))
        {
            if (Enum.TryParse<TriggerType>(filters.TriggerType.Trim(), true, out var parsed))
                trigger = parsed;
            else
                throw new ValidationException("Trigger filter is invalid.",
                    [new FieldError("triggerType", "Trigger type must be 'Manual', 'Schedule' or 'Ci'.")]);
        }
        return (status, trigger);
    }

    private async Task<Guid?> ResolveAppUserIdAsync(CancellationToken cancellationToken)
        => string.IsNullOrWhiteSpace(_currentUser.ExternalIdentityId)
            ? null
            : await _users.FindAppUserIdAsync(_currentUser.ExternalIdentityId!, cancellationToken);

    private static string? NormalizedSearch(string? search)
        => string.IsNullOrWhiteSpace(search) ? null : search.Trim();

    private static string? BlankToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static (int Skip, int Take, int Page, int Size) Paginate(int page, int pageSize)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize <= 0 ? 25 : pageSize, 1, 100);
        return ((page - 1) * pageSize, pageSize, page, pageSize);
    }
}
