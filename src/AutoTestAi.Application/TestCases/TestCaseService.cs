using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Identity;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using AutoTestAi.Domain.TestCases;

namespace AutoTestAi.Application.TestCases;

/// <summary>
/// Test repository use cases. Every method enforces server-side authorization
/// through test case → project; the store performs no authorization checks.
/// Versions are immutable: content edits allocate a new sequential version.
/// </summary>
public sealed class TestCaseService : ITestCaseService
{
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;
    private const int MaxTitleLength = 200;
    private const int MaxDescriptionLength = 4000;
    private const int MaxModuleLength = 100;
    private const int MaxFrameworkLength = 100;
    private const int MaxPlatformLength = 100;
    private const int MaxSourceLength = 200_000;

    private readonly ITestCaseStore _store;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuthorizationService _authorization;
    private readonly IUserDirectory _users;
    private readonly IDateTimeProvider _clock;
    private readonly IAuditService _audit;

    public TestCaseService(
        ITestCaseStore store,
        ICurrentUserService currentUser,
        IAuthorizationService authorization,
        IUserDirectory users,
        IDateTimeProvider clock,
        IAuditService audit)
    {
        _store = store;
        _currentUser = currentUser;
        _authorization = authorization;
        _users = users;
        _clock = clock;
        _audit = audit;
    }

    public async Task<PagedResult<TestCaseListItemDto>> ListAsync(
        Guid projectId, int page, int pageSize, TestCaseFilters filters, CancellationToken cancellationToken)
    {
        await _authorization.RequireProjectAccessAsync(
            projectId, Permissions.TestCasesRead, cancellationToken);

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize);
        var statusFilter = ValidateFilters(filters);

        var totalCount = await _store.CountAsync(
            projectId, NormalizedSearch(filters.Search), statusFilter, cancellationToken);
        var cases = await _store.ListAsync(
            projectId, NormalizedSearch(filters.Search), statusFilter,
            (page - 1) * pageSize, pageSize, cancellationToken);
        var latest = await _store.GetLatestVersionsAsync(
            cases.Select(t => t.Id).ToList(), cancellationToken);

        return new PagedResult<TestCaseListItemDto>(
            cases.Select(t => MapListItem(t, latest.GetValueOrDefault(t.Id))).ToList(),
            totalCount, page, pageSize);
    }

    public async Task<TestCaseDto> GetByIdAsync(Guid testCaseId, CancellationToken cancellationToken)
    {
        var testCase = await RequireTestCaseAsync(testCaseId, Permissions.TestCasesRead, cancellationToken);
        var latest = await LatestAsync(testCaseId, cancellationToken);
        return MapDetails(testCase, latest);
    }

    public async Task<TestCaseDto> CreateAsync(CreateTestCaseCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _authorization.RequireProjectAccessAsync(
            command.ProjectId, Permissions.TestCasesManage, cancellationToken);

        var errors = ValidateMetadata(
            command.Title, command.Description, command.Module,
            command.Framework, command.Platform, command.Priority, command.Status, command.SourceType,
            out var priority, out var status, out var sourceType);
        var key = TestCaseKey.Normalize(command.TestKey);
        if (key.Length == 0) errors.Add(new FieldError("testKey", "Test key is required."));
        else if (key.Length > TestCaseKey.MaxLength) errors.Add(new FieldError("testKey", $"Test key must be at most {TestCaseKey.MaxLength} characters."));
        else if (!TestCaseKey.IsValidFormat(key)) errors.Add(new FieldError("testKey", "Test key must start with a letter and contain only letters, digits, '_' or '-'."));
        ValidateContent(command.SourceCode, command.StructuredSteps, errors);
        ValidateGenerationMetadata(command, errors);
        ValidationException.ThrowIfInvalid(errors);

        if (await _store.GetByKeyAsync(command.ProjectId, key, cancellationToken) is not null)
            throw new ConflictException($"Test key '{key}' is already in use in this project.");

        var now = _clock.UtcNow;
        var testCase = new TestCase
        {
            ProjectId = command.ProjectId,
            TestKey = key,
            Title = command.Title.Trim(),
            Description = BlankToNull(command.Description),
            Module = BlankToNull(command.Module),
            Framework = BlankToNull(command.Framework),
            Platform = BlankToNull(command.Platform),
            Priority = priority!.Value,
            Status = status!.Value,
            SourceType = sourceType,
            CreatedBy = await ResolveAppUserIdAsync(cancellationToken),
            CreatedAt = now,
            UpdatedAt = now,
        };
        await _store.AddTestCaseAsync(testCase, cancellationToken);

        var version = BuildVersion(
            testCase.Id, 1, command.SourceCode, command.StructuredSteps, null, testCase.CreatedBy);
        version.CreatedAt = now;
        // AI provenance (Slice 4 §16): provider/model/latency + redacted request.
        version.GenerationProvider = BlankToNull(command.GenerationProvider);
        version.GenerationModel = BlankToNull(command.GenerationModel);
        version.GenerationLatencyMs = command.GenerationLatencyMs;
        version.GenerationRequest = command.GenerationRequest;
        await _store.AddVersionAsync(version, cancellationToken);
        await _store.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync("testcase.created", "test_case", testCase.Id.ToString(),
            testCase.ProjectId, JsonSerializer.Serialize(new { testKey = key }), cancellationToken);

        return MapDetails(testCase, version);
    }

    public async Task<TestCaseDto> UpdateAsync(
        Guid testCaseId, UpdateTestCaseCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var testCase = await RequireTestCaseAsync(testCaseId, Permissions.TestCasesManage, cancellationToken);

        var errors = ValidateMetadata(
            command.Title, command.Description, command.Module,
            command.Framework, command.Platform, command.Priority, command.Status, command.SourceType,
            out var priority, out var status, out var sourceType);
        if (command.HasSourceCode) ValidateSourceCode(command.SourceCode, errors);
        JsonDocument? newSteps = null;
        if (command.HasStructuredSteps)
            newSteps = ValidateSteps(command.StructuredSteps, errors);
        ValidationException.ThrowIfInvalid(errors);

        testCase.Title = command.Title.Trim();
        testCase.Description = BlankToNull(command.Description);
        testCase.Module = BlankToNull(command.Module);
        testCase.Framework = BlankToNull(command.Framework);
        testCase.Platform = BlankToNull(command.Platform);
        testCase.Priority = priority!.Value;
        testCase.Status = status!.Value;
        testCase.SourceType = sourceType;
        testCase.UpdatedAt = _clock.UtcNow;

        TestCaseVersion? latest = await LatestAsync(testCaseId, cancellationToken);
        var contentChanged = ContentChanged(latest, command, newSteps);
        TestCaseVersion? created = null;
        if (contentChanged)
        {
            var creator = await ResolveAppUserIdAsync(cancellationToken);
            created = await _store.AddNextVersionAsync(testCaseId, next => BuildVersion(
                testCaseId,
                next,
                command.HasSourceCode ? command.SourceCode : latest?.SourceCode,
                null,
                command.HasStructuredSteps ? newSteps : latest?.StructuredSteps,
                creator), cancellationToken);
            await _audit.RecordAsync("testcase.version_created", "test_case_version",
                created.Id.ToString(), testCase.ProjectId,
                JsonSerializer.Serialize(new { versionNumber = created.VersionNumber }), cancellationToken);
        }

        await _store.SaveChangesAsync(cancellationToken);
        await _audit.RecordAsync("testcase.updated", "test_case", testCase.Id.ToString(),
            testCase.ProjectId, JsonSerializer.Serialize(new { testKey = testCase.TestKey }), cancellationToken);

        return MapDetails(testCase, created ?? latest);
    }

    public async Task ArchiveAsync(Guid testCaseId, CancellationToken cancellationToken)
    {
        var testCase = await RequireTestCaseAsync(testCaseId, Permissions.TestCasesManage, cancellationToken);
        if (testCase.Status != TestCaseStatus.Archived)
        {
            testCase.Status = TestCaseStatus.Archived;
            testCase.UpdatedAt = _clock.UtcNow;
            await _store.SaveChangesAsync(cancellationToken);
            await _audit.RecordAsync("testcase.archived", "test_case", testCase.Id.ToString(),
                testCase.ProjectId, null, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<TestCaseVersionDto>> ListVersionsAsync(
        Guid testCaseId, CancellationToken cancellationToken)
    {
        var testCase = await RequireTestCaseAsync(testCaseId, Permissions.TestCasesRead, cancellationToken);
        var versions = await _store.ListVersionsAsync(testCase.Id, cancellationToken);
        return versions.Select(MapVersion).ToList();
    }

    public async Task<TestCaseVersionDto> GetVersionAsync(
        Guid testCaseId, Guid versionId, CancellationToken cancellationToken)
    {
        var testCase = await RequireTestCaseAsync(testCaseId, Permissions.TestCasesRead, cancellationToken);
        var version = await _store.GetVersionByIdAsync(versionId, cancellationToken);
        if (version is null || version.TestCaseId != testCase.Id)
            throw new NotFoundException("Test case version not found.");
        return MapVersion(version);
    }

    public async Task<TestCaseVersionDto> ReviewAsync(
        Guid testCaseId, ReviewTestCaseCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var testCase = await RequireTestCaseAsync(testCaseId, Permissions.TestCasesManage, cancellationToken);

        var version = await _store.GetVersionByIdAsync(command.VersionId, cancellationToken);
        if (version is null || version.TestCaseId != testCase.Id)
            throw new NotFoundException("Test case version not found.");

        var errors = new List<FieldError>();
        ReviewStatus? target = null;
        if (string.IsNullOrWhiteSpace(command.ReviewStatus))
            errors.Add(new FieldError("reviewStatus", "Review status is required."));
        else if (!Enum.TryParse<ReviewStatus>(command.ReviewStatus.Trim(), ignoreCase: true, out var parsed))
            errors.Add(new FieldError("reviewStatus", "Review status must be 'Pending', 'Approved', 'ChangesRequested' or 'Rejected'."));
        else target = parsed;
        ValidationException.ThrowIfInvalid(errors);

        if (target!.Value != version.ReviewStatus)
        {
            if (!ReviewStatusTransitions.IsValidTransition(version.ReviewStatus, target.Value))
                throw new ValidationException(
                    $"Cannot transition review status from '{version.ReviewStatus}' to '{target.Value}'.",
                    new[] { new FieldError("reviewStatus", "Review status transition is not allowed.") });
            var from = version.ReviewStatus;
            version.ReviewStatus = target.Value;
            await _store.SaveChangesAsync(cancellationToken);
            await _audit.RecordAsync("testcase.review_changed", "test_case_version",
                version.Id.ToString(), testCase.ProjectId,
                JsonSerializer.Serialize(new { from = from.ToString(), to = target.Value.ToString() }),
                cancellationToken);
        }

        return MapVersion(version);
    }

    // ---------- helpers ----------

    /// <summary>
    /// Resolves the test case while preserving the Slice-1 convention: unknown
    /// ids authorize against the id itself as an opaque scope, so non-admins
    /// get 403 (never existence-revealing 404); admins get a true 404.
    /// </summary>
    private async Task<TestCase> RequireTestCaseAsync(
        Guid testCaseId, string permission, CancellationToken cancellationToken)
    {
        var testCase = await _store.GetByIdAsync(testCaseId, cancellationToken);
        await _authorization.RequireProjectAccessAsync(
            testCase?.ProjectId ?? testCaseId, permission, cancellationToken);
        return testCase ?? throw new NotFoundException("Test case not found.");
    }

    private async Task<TestCaseVersion?> LatestAsync(Guid testCaseId, CancellationToken cancellationToken)
        => (await _store.GetLatestVersionsAsync(new[] { testCaseId }, cancellationToken))
            .GetValueOrDefault(testCaseId);

    private async Task<Guid?> ResolveAppUserIdAsync(CancellationToken cancellationToken)
        => string.IsNullOrWhiteSpace(_currentUser.ExternalIdentityId)
            ? null
            : await _users.FindAppUserIdAsync(_currentUser.ExternalIdentityId!, cancellationToken);

    private static string? NormalizedSearch(string? search)
        => string.IsNullOrWhiteSpace(search) ? null : search.Trim();

    private static TestCaseStatusFilter ValidateFilters(TestCaseFilters filters)
    {
        var errors = new List<FieldError>();
        string? status = null, priority = null, review = null;
        if (!string.IsNullOrWhiteSpace(filters.Status))
        {
            if (Enum.TryParse<TestCaseStatus>(filters.Status.Trim(), ignoreCase: true, out var s)) status = s.ToString();
            else errors.Add(new FieldError("status", "Status must be 'Draft', 'Active', 'Deprecated' or 'Archived'."));
        }
        if (!string.IsNullOrWhiteSpace(filters.Priority))
        {
            if (Enum.TryParse<Priority>(filters.Priority.Trim(), ignoreCase: true, out var p)) priority = p.ToString();
            else errors.Add(new FieldError("priority", "Priority must be 'Critical', 'High', 'Medium' or 'Low'."));
        }
        if (!string.IsNullOrWhiteSpace(filters.ReviewStatus))
        {
            if (Enum.TryParse<ReviewStatus>(filters.ReviewStatus.Trim(), ignoreCase: true, out var r)) review = r.ToString();
            else errors.Add(new FieldError("reviewStatus", "Review status must be 'Pending', 'Approved', 'ChangesRequested' or 'Rejected'."));
        }
        ValidationException.ThrowIfInvalid(errors);
        return new TestCaseStatusFilter(
            status,
            priority,
            string.IsNullOrWhiteSpace(filters.Framework) ? null : filters.Framework.Trim(),
            string.IsNullOrWhiteSpace(filters.Platform) ? null : filters.Platform.Trim(),
            review);
    }

    private static List<FieldError> ValidateMetadata(
        string? title, string? description, string? module,
        string? framework, string? platform, string? priority, string? status, string? sourceType,
        out Priority? parsedPriority, out TestCaseStatus? parsedStatus, out string parsedSourceType)
    {
        var errors = new List<FieldError>();
        if (string.IsNullOrWhiteSpace(title)) errors.Add(new FieldError("title", "Title is required."));
        else if (title.Trim().Length > MaxTitleLength) errors.Add(new FieldError("title", $"Title must be at most {MaxTitleLength} characters."));
        if (description is not null && description.Length > MaxDescriptionLength) errors.Add(new FieldError("description", $"Description must be at most {MaxDescriptionLength} characters."));
        if (module is not null && module.Trim().Length > MaxModuleLength) errors.Add(new FieldError("module", $"Module must be at most {MaxModuleLength} characters."));
        if (framework is not null && framework.Trim().Length > MaxFrameworkLength) errors.Add(new FieldError("framework", $"Framework must be at most {MaxFrameworkLength} characters."));
        if (platform is not null && platform.Trim().Length > MaxPlatformLength) errors.Add(new FieldError("platform", $"Platform must be at most {MaxPlatformLength} characters."));

        parsedPriority = null;
        if (string.IsNullOrWhiteSpace(priority)) parsedPriority = Priority.Medium;
        else if (Enum.TryParse<Priority>(priority.Trim(), ignoreCase: true, out var p)) parsedPriority = p;
        else errors.Add(new FieldError("priority", "Priority must be 'Critical', 'High', 'Medium' or 'Low'."));

        parsedStatus = null;
        if (string.IsNullOrWhiteSpace(status)) parsedStatus = TestCaseStatus.Draft;
        else if (Enum.TryParse<TestCaseStatus>(status.Trim(), ignoreCase: true, out var s)) parsedStatus = s;
        else errors.Add(new FieldError("status", "Status must be 'Draft', 'Active', 'Deprecated' or 'Archived'."));

        parsedSourceType = TestCaseSourceTypes.Manual;
        if (!string.IsNullOrWhiteSpace(sourceType))
        {
            if (TestCaseSourceTypes.IsSupported(sourceType)) parsedSourceType = TestCaseSourceTypes.Normalize(sourceType);
            else errors.Add(new FieldError("sourceType", "Source type must be 'manual', 'ai' or 'imported'."));
        }
        return errors;
    }

    private static void ValidateContent(string? sourceCode, JsonElement? structuredSteps, List<FieldError> errors)
    {
        ValidateSourceCode(sourceCode, errors);
        ValidateSteps(structuredSteps, errors);
    }

    private static void ValidateSourceCode(string? sourceCode, List<FieldError> errors)
    {
        if (sourceCode is not null && sourceCode.Length > MaxSourceLength)
            errors.Add(new FieldError("sourceCode", $"Source code must be at most {MaxSourceLength} characters."));
    }

    /// <summary>AI provenance metadata (Slice 4 §16): shaped, bounded, never secrets.</summary>
    private static void ValidateGenerationMetadata(CreateTestCaseCommand command, List<FieldError> errors)
    {
        if (command.GenerationProvider is not null && command.GenerationProvider.Trim().Length > 100)
            errors.Add(new FieldError("generationProvider", "Generation provider must be at most 100 characters."));
        if (command.GenerationModel is not null && command.GenerationModel.Trim().Length > 200)
            errors.Add(new FieldError("generationModel", "Generation model must be at most 200 characters."));
        if (command.GenerationLatencyMs is not null && command.GenerationLatencyMs < 0)
            errors.Add(new FieldError("generationLatencyMs", "Generation latency must not be negative."));
        if (command.GenerationRequest is not null &&
            command.GenerationRequest.RootElement.ValueKind != JsonValueKind.Object)
            errors.Add(new FieldError("generationRequest", "Generation request must be a JSON object."));
    }

    private static JsonDocument? ValidateSteps(JsonElement? structuredSteps, List<FieldError> errors)
    {
        if (structuredSteps is null) return null;
        foreach (var problem in TestStep.Validate(structuredSteps))
            errors.Add(new FieldError("structuredSteps", problem));
        return errors.Count == 0 ? JsonDocument.Parse(structuredSteps.Value.GetRawText()) : null;
    }

    /// <summary>A new version is allocated only when versioned content differs.</summary>
    private static bool ContentChanged(
        TestCaseVersion? latest, UpdateTestCaseCommand command, JsonDocument? newSteps)
    {
        var oldCode = (latest?.SourceCode ?? string.Empty).Trim();
        var newCode = (command.HasSourceCode ? command.SourceCode ?? string.Empty : latest?.SourceCode ?? string.Empty).Trim();
        if (command.HasSourceCode && !string.Equals(oldCode, newCode, StringComparison.Ordinal))
            return true;

        if (!command.HasStructuredSteps) return false;
        var oldSteps = latest?.StructuredSteps?.RootElement;
        var newElement = newSteps?.RootElement;
        if (oldSteps is null && newElement is null) return false;
        if (oldSteps is null || newElement is null) return true;
        return !TestStep.ContentEquals(oldSteps, newElement);
    }

    private TestCaseVersion BuildVersion(
        Guid testCaseId, int versionNumber, string? sourceCode, JsonElement? stepsElement,
        JsonDocument? stepsDocument, Guid? creator)
    {
        JsonDocument? steps = stepsDocument;
        if (steps is null && stepsElement.HasValue)
            steps = JsonDocument.Parse(stepsElement.Value.GetRawText());
        return new TestCaseVersion
        {
            TestCaseId = testCaseId,
            VersionNumber = versionNumber,
            SourceCode = string.IsNullOrWhiteSpace(sourceCode) ? null : sourceCode,
            StructuredSteps = steps,
            ReviewStatus = ReviewStatus.Pending,
            CreatedBy = creator,
            CreatedAt = _clock.UtcNow,
        };
    }

    private static string? BlankToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static TestCaseListItemDto MapListItem(TestCase testCase, TestCaseVersion? latest) => new(
        testCase.Id, testCase.ProjectId, testCase.TestKey, testCase.Title,
        testCase.Module, testCase.Framework, testCase.Platform,
        testCase.Priority.ToString(), testCase.Status.ToString(),
        latest?.VersionNumber ?? 0,
        (latest?.ReviewStatus ?? ReviewStatus.Pending).ToString(),
        testCase.UpdatedAt);

    private static TestCaseDto MapDetails(TestCase testCase, TestCaseVersion? latest) => new(
        testCase.Id, testCase.ProjectId, testCase.TestKey, testCase.Title,
        testCase.Description, testCase.Module, testCase.Framework, testCase.Platform,
        testCase.Priority.ToString(), testCase.Status.ToString(), testCase.SourceType,
        latest?.VersionNumber ?? 0,
        (latest?.ReviewStatus ?? ReviewStatus.Pending).ToString(),
        testCase.CreatedBy, testCase.CreatedAt, testCase.UpdatedAt);

    private static TestCaseVersionDto MapVersion(TestCaseVersion version) => new(
        version.Id, version.TestCaseId, version.VersionNumber, version.SourceCode,
        TestStep.Parse(version.StructuredSteps?.RootElement).Select(s =>
            new TestStepDto(s.Order, s.Action, s.Target, s.Value)).ToList(),
        version.GenerationProvider, version.GenerationModel, version.GenerationLatencyMs,
        version.ReviewStatus.ToString(), version.CreatedBy, version.CreatedAt);
}
