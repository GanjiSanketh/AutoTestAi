using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Application.TestGeneration;
using AutoTestAi.Domain.Defects;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using DomainFailureAnalysis = AutoTestAi.Domain.Entities.FailureAnalysis;

namespace AutoTestAi.Application.Defects;

/// <summary>
/// Human-owned defect lifecycle (Slice 6 §19-22). All relationships derive
/// from the referenced failed execution; the frontend supplies only human
/// content (title/description/severity). Every mutation is audited.
/// </summary>
public sealed class DefectService : IDefectService
{
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;
    private const int MaxTitleLength = 200;
    private const int MaxDescriptionLength = 4000;

    private static readonly IReadOnlySet<ExecutionTestStatus> FailedStatuses =
        new HashSet<ExecutionTestStatus>
        {
            ExecutionTestStatus.Failed,
            ExecutionTestStatus.Error,
            ExecutionTestStatus.TimedOut,
        };

    private readonly IDefectStore _store;
    private readonly IExecutionStore _executions;
    private readonly ITestCaseStore _cases;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuthorizationService _authorization;
    private readonly IUserDirectory _users;
    private readonly IDateTimeProvider _clock;
    private readonly IAuditService _audit;

    public DefectService(
        IDefectStore store,
        IExecutionStore executions,
        ITestCaseStore cases,
        ICurrentUserService currentUser,
        IAuthorizationService authorization,
        IUserDirectory users,
        IDateTimeProvider clock,
        IAuditService audit)
    {
        _store = store;
        _executions = executions;
        _cases = cases;
        _currentUser = currentUser;
        _authorization = authorization;
        _users = users;
        _clock = clock;
        _audit = audit;
    }

    public async Task<DefectDetailDto> CreateAsync(
        CreateDefectCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _authorization.RequireProjectAccessAsync(
            command.ProjectId, Permissions.BugsManage, cancellationToken);

        var errors = new List<FieldError>();
        var title = command.Title?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
            errors.Add(new FieldError("title", "Title is required."));
        else if (title.Length > MaxTitleLength)
            errors.Add(new FieldError("title", $"Title must be at most {MaxTitleLength} characters."));
        var description = string.IsNullOrWhiteSpace(command.Description) ? null : command.Description.Trim();
        if (description is not null && description.Length > MaxDescriptionLength)
            errors.Add(new FieldError("description", $"Description must be at most {MaxDescriptionLength} characters."));
        Severity severity = Severity.Medium;
        if (!string.IsNullOrWhiteSpace(command.Severity))
        {
            if (!Enum.TryParse<Severity>(command.Severity.Trim(), ignoreCase: true, out var parsed))
                errors.Add(new FieldError("severity", "Severity must be 'Critical', 'High', 'Medium' or 'Low'."));
            else severity = parsed;
        }
        if (command.ExecutionId == Guid.Empty)
            errors.Add(new FieldError("executionId", "Execution id is required."));
        ValidationException.ThrowIfInvalid(errors);

        // Authoritative relationships come from the execution, not the client.
        var execution = await _executions.GetExecutionByIdAsync(command.ExecutionId, cancellationToken);
        if (execution is null || execution.ProjectId != command.ProjectId)
            throw new ForbiddenException("The execution does not belong to this project.");
        var test = (await _executions.ListTestsByExecutionAsync(execution.Id, cancellationToken))
            .OrderBy(t => t.CreatedAt).FirstOrDefault()
            ?? throw new ConflictException("The execution has no test to file a defect against.");
        if (!FailedStatuses.Contains(test.Status))
            throw new ConflictException(
                $"Defects can only be filed against failed executions (current test status '{test.Status}').");

        DomainFailureAnalysis? analysis = null;
        if (command.FailureAnalysisId is not null)
        {
            analysis = await _executions.GetAnalysisByIdAsync(command.FailureAnalysisId.Value, cancellationToken);
            if (analysis is null || analysis.ExecutionTestId != test.Id)
                throw new ValidationException("The referenced analysis does not belong to this execution.",
                    new[] { new FieldError("failureAnalysisId", "Analysis must belong to the execution.") });
        }

        var now = _clock.UtcNow;
        var defect = new Defect
        {
            ProjectId = command.ProjectId,
            ExecutionTestId = test.Id,
            FailureAnalysisId = analysis?.Id,
            Title = title,
            Description = description,
            Severity = severity,
            Status = DefectStatus.Open,
            RootCauseType = test.FailureClassification == FailureClassification.Unknown
                ? null : test.FailureClassification,
            AiConfidence = analysis?.Confidence,
            CreatedBy = await ResolveAppUserIdAsync(cancellationToken),
            CreatedAt = now,
            UpdatedAt = now,
        };
        await _store.AddAsync(defect, cancellationToken);
        await _store.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            analysis is null ? "defect.created" : "defect.created_from_analysis",
            "defect", defect.Id.ToString(), defect.ProjectId,
            SafeMeta(defect, test, analysis), cancellationToken);

        return await MapDetailAsync(defect, cancellationToken);
    }

    public async Task<PagedResult<DefectListItemDto>> ListAsync(
        Guid projectId, int page, int pageSize, DefectFilters filters, CancellationToken cancellationToken)
    {
        await _authorization.RequireProjectAccessAsync(
            projectId, Permissions.BugsRead, cancellationToken);

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize);
        var status = ParseEnum<DefectStatus>(filters.Status, "status",
            "Status must be 'Open', 'InProgress', 'Resolved', 'Closed' or 'Rejected'.");
        var severity = ParseEnum<Severity>(filters.Severity, "severity",
            "Severity must be 'Critical', 'High', 'Medium' or 'Low'.");
        var classification = ParseClassification(filters.Classification);

        var search = string.IsNullOrWhiteSpace(filters.Search) ? null : filters.Search.Trim();
        var totalCount = await _store.CountAsync(
            projectId, status, severity, classification, filters.TestCaseId, search, cancellationToken);
        var rows = await _store.ListAsync(
            projectId, status, severity, classification, filters.TestCaseId, search,
            (page - 1) * pageSize, pageSize, cancellationToken);

        var items = new List<DefectListItemDto>();
        foreach (var defect in rows)
            items.Add(await MapListItemAsync(defect, cancellationToken));
        return new PagedResult<DefectListItemDto>(items, totalCount, page, pageSize);
    }

    public async Task<DefectDetailDto> GetAsync(Guid defectId, CancellationToken cancellationToken)
    {
        var defect = await RequireDefectAsync(defectId, Permissions.BugsRead, cancellationToken);
        return await MapDetailAsync(defect, cancellationToken);
    }

    public async Task<DefectDetailDto> UpdateAsync(
        Guid defectId, UpdateDefectCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var defect = await RequireDefectAsync(defectId, Permissions.BugsManage, cancellationToken);

        var errors = new List<FieldError>();
        var title = command.Title?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
            errors.Add(new FieldError("title", "Title is required."));
        else if (title.Length > MaxTitleLength)
            errors.Add(new FieldError("title", $"Title must be at most {MaxTitleLength} characters."));
        var description = string.IsNullOrWhiteSpace(command.Description) ? null : command.Description.Trim();
        if (description is not null && description.Length > MaxDescriptionLength)
            errors.Add(new FieldError("description", $"Description must be at most {MaxDescriptionLength} characters."));
        Severity severity = defect.Severity;
        var severityChanged = false;
        if (!string.IsNullOrWhiteSpace(command.Severity))
        {
            if (!Enum.TryParse<Severity>(command.Severity.Trim(), ignoreCase: true, out var parsed))
                errors.Add(new FieldError("severity", "Severity must be 'Critical', 'High', 'Medium' or 'Low'."));
            else if (parsed != defect.Severity)
            {
                severityChanged = true;
                severity = parsed;
            }
        }
        ValidationException.ThrowIfInvalid(errors);

        var fromSeverity = defect.Severity;
        defect.Title = title;
        defect.Description = description;
        defect.Severity = severity;
        defect.UpdatedAt = _clock.UtcNow;
        await _store.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync("defect.updated", "defect",
            defect.Id.ToString(), defect.ProjectId,
            SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new { title })), cancellationToken);
        if (severityChanged)
            await _audit.RecordAsync("defect.severity_changed", "defect",
                defect.Id.ToString(), defect.ProjectId,
                JsonSerializer.Serialize(new { from = fromSeverity.ToString(), to = severity.ToString() }),
                cancellationToken);

        return await MapDetailAsync(defect, cancellationToken);
    }

    public async Task<DefectDetailDto> ChangeStatusAsync(
        Guid defectId, ChangeDefectStatusCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var defect = await RequireDefectAsync(defectId, Permissions.BugsManage, cancellationToken);

        if (string.IsNullOrWhiteSpace(command.Status) ||
            !Enum.TryParse<DefectStatus>(command.Status.Trim(), ignoreCase: true, out var target))
            throw new ValidationException("Status is invalid.",
                new[] { new FieldError("status", "Status must be 'Open', 'InProgress', 'Resolved', 'Closed' or 'Rejected'.") });

        if (target != defect.Status)
        {
            if (!DefectTransitions.IsValidTransition(defect.Status, target))
                throw new ValidationException(
                    $"Cannot transition defect status from '{defect.Status}' to '{target}'.",
                    new[] { new FieldError("status", "Defect status transition is not allowed.") });
            var from = defect.Status;
            defect.Status = target;
            defect.UpdatedAt = _clock.UtcNow;
            await _store.SaveChangesAsync(cancellationToken);
            await _audit.RecordAsync(
                target == DefectStatus.Open && from != DefectStatus.Open ? "defect.reopened" : "defect.status_changed",
                "defect", defect.Id.ToString(), defect.ProjectId,
                JsonSerializer.Serialize(new { from = from.ToString(), to = target.ToString() }),
                cancellationToken);
        }

        return await MapDetailAsync(defect, cancellationToken);
    }

    // ---------- helpers ----------

    /// <summary>
    /// Slice-1/3 convention: unknown ids authorize against the id itself as an
    /// opaque scope (403 for outsiders, genuine 404 for authorized callers).
    /// </summary>
    private async Task<Defect> RequireDefectAsync(
        Guid defectId, string permission, CancellationToken cancellationToken)
    {
        var defect = await _store.GetByIdAsync(defectId, cancellationToken);
        await _authorization.RequireProjectAccessAsync(
            defect?.ProjectId ?? defectId, permission, cancellationToken);
        return defect ?? throw new NotFoundException("Defect not found.");
    }

    private async Task<Guid?> ResolveAppUserIdAsync(CancellationToken cancellationToken)
        => string.IsNullOrWhiteSpace(_currentUser.ExternalIdentityId)
            ? null
            : await _users.FindAppUserIdAsync(_currentUser.ExternalIdentityId!, cancellationToken);

    private static string? ParseEnum<T>(string? value, string field, string message) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (Enum.TryParse<T>(value.Trim(), ignoreCase: true, out var parsed))
            return parsed.ToString();
        throw new ValidationException(message, new[] { new FieldError(field, message) });
    }

    private static string? ParseClassification(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (Enum.TryParse<FailureClassification>(value.Trim(), ignoreCase: true, out var parsed))
            return parsed.ToString();
        throw new ValidationException("Classification filter is invalid.",
            new[] { new FieldError("classification", "Classification must be a valid failure classification.") });
    }

    private async Task<DefectListItemDto> MapListItemAsync(Defect defect, CancellationToken ct)
    {
        var (testKey, executionId) = await ResolveTestInfoAsync(defect, ct);
        return new DefectListItemDto(
            defect.Id, defect.ProjectId, defect.Title,
            defect.Severity.ToString(), defect.Status.ToString(),
            defect.RootCauseType?.ToString(),
            executionId, testKey, defect.CreatedAt, defect.UpdatedAt);
    }

    private async Task<DefectDetailDto> MapDetailAsync(Defect defect, CancellationToken ct)
    {
        Guid? executionId = null;
        Guid? testCaseId = null;
        string? testKey = null;
        string? testTitle = null;
        Guid? versionId = null;
        int? versionNumber = null;
        ExecutionTest? test = null;
        if (defect.ExecutionTestId is not null)
        {
            test = await _executions.GetExecutionTestByIdAsync(defect.ExecutionTestId.Value, ct);
            if (test is not null)
            {
                var execution = await _executions.GetExecutionByIdAsync(test.ExecutionId, ct);
                executionId = execution?.Id;
                testCaseId = test.TestCaseId;
                var testCase = await _cases.GetByIdAsync(test.TestCaseId, ct);
                testKey = testCase?.TestKey;
                testTitle = testCase?.Title;
                versionId = test.TestCaseVersionId;
                if (versionId is not null)
                    versionNumber = (await _cases.GetVersionByIdAsync(versionId.Value, ct))?.VersionNumber;
            }
        }
        DefectAnalysisDto? analysis = null;
        if (defect.FailureAnalysisId is not null)
        {
            var row = await _executions.GetAnalysisByIdAsync(defect.FailureAnalysisId.Value, ct);
            if (row is not null)
                analysis = new DefectAnalysisDto(
                    row.Id, row.Attempt, row.Status.ToString(), row.Classification.ToString(),
                    row.Summary, row.Confidence, row.Provider, row.Model);
        }
        return new DefectDetailDto(
            defect.Id, defect.ProjectId, defect.Title, defect.Description,
            defect.Severity.ToString(), defect.Status.ToString(),
            defect.RootCauseType?.ToString(),
            executionId, defect.ExecutionTestId, testCaseId, testKey, testTitle,
            versionId, versionNumber,
            defect.FailureAnalysisId, analysis,
            defect.AiConfidence, defect.CreatedBy, defect.CreatedAt, defect.UpdatedAt);
    }

    private async Task<(string? TestKey, Guid? ExecutionId)> ResolveTestInfoAsync(
        Defect defect, CancellationToken ct)
    {
        if (defect.ExecutionTestId is null) return (null, null);
        var test = await _executions.GetExecutionTestByIdAsync(defect.ExecutionTestId.Value, ct);
        if (test is null) return (null, null);
        var testCase = await _cases.GetByIdAsync(test.TestCaseId, ct);
        return (testCase?.TestKey, test.ExecutionId);
    }

    private static string SafeMeta(Defect defect, ExecutionTest test, DomainFailureAnalysis? analysis)
        => SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
        {
            title = defect.Title,
            severity = defect.Severity.ToString(),
            executionTestId = test.Id,
            testCaseId = test.TestCaseId,
            analysisId = analysis?.Id,
        }));
}
