using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.Reports;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Application.TestGeneration;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using AutoTestAi.Domain.TestCases;

namespace AutoTestAi.Application.Maintenance;

/// <summary>
/// Human-gated test-maintenance lifecycle (Phase 4 Slice 4, web locators
/// only). Scan aggregates persisted deterministic healing evidence into
/// Proposed rows; approval revalidates the pinned version/step and mints a
/// new Pending version through <see cref="ITestCaseService"/>; the existing
/// review remains the final execution gate. Reads need testcases.read;
/// scan/approve/reject need testcases.manage.
/// </summary>
public sealed class MaintenanceService : IMaintenanceService
{
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;
    private const int ScanEvidenceTake = 2000;
    private const int ScanVerdictTake = 2000;
    private const int ScanTestCap = 200;
    private const int ScanCandidateCap = 100;
    private const int EvidenceDetailTake = 50;
    private const int MaxRejectionLength = 500;
    private const int ScanWindowDays = 30;
    // Manual throttle on the existing per-project operation budget: scans are
    // bounded read-aggregate queries, so 30/minute admits interactive and
    // test use while still bounding tight loops. 429 when exceeded.
    private const int ScansPerMinute = 30;

    private static readonly IReadOnlySet<string> ProposalStatuses =
        new HashSet<string>(Enum.GetNames<MaintenanceProposalStatus>(), StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> KnownSignals =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MaintenanceDetector.SignalHealedLocator };

    private readonly IMaintenanceStore _store;
    private readonly ITestCaseService _cases;
    private readonly IAuthorizationService _authorization;
    private readonly IAuditService _audit;
    private readonly IReportQueryStore _reports;
    private readonly AiGenerationRateLimiter _rateLimiter;
    private readonly ICurrentUserService _currentUser;
    private readonly IUserDirectory _users;
    private readonly IDateTimeProvider _clock;

    public MaintenanceService(
        IMaintenanceStore store,
        ITestCaseService cases,
        IAuthorizationService authorization,
        IAuditService audit,
        IReportQueryStore reports,
        AiGenerationRateLimiter rateLimiter,
        ICurrentUserService currentUser,
        IUserDirectory users,
        IDateTimeProvider clock)
    {
        _store = store;
        _cases = cases;
        _authorization = authorization;
        _audit = audit;
        _reports = reports;
        _rateLimiter = rateLimiter;
        _currentUser = currentUser;
        _users = users;
        _clock = clock;
    }

    public async Task<MaintenanceScanResult> ScanAsync(Guid projectId, CancellationToken ct)
    {
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.TestCasesManage, ct);
        // Manual throttle on existing per-project operation-budget mechanics:
        // bounded synchronous scans, 429 when exceeded, no new infrastructure.
        _rateLimiter.CheckOperationOrThrow(projectId, "maintenance-scan", "maintenance",
            ScansPerMinute, "Maintenance scan rate limit exceeded for this project");

        var now = _clock.UtcNow;
        var since = now.AddDays(-ScanWindowDays);
        var evidence = await _store.ListHealingEvidenceAsync(projectId, since, ScanEvidenceTake, ct);
        var testIds = evidence.Select(r => r.TestCaseId).Distinct().OrderBy(id => id).Take(ScanTestCap).ToList();
        var versions = await _store.ListLatestVersionsAsync(projectId, testIds, ct);
        var excluded = new HashSet<Guid>(await _store.ListOpenAppBugTestCaseIdsAsync(projectId, ct));
        var verdicts = await _store.ListFailedVerdictsAsync(projectId, since, ScanVerdictTake, ct);

        var snapshots = new Dictionary<Guid, MaintenanceDetector.VersionSnapshot>();
        foreach (var row in versions)
        {
            if (!Enum.TryParse<ReviewStatus>(row.ReviewStatus, ignoreCase: true, out var review))
                continue;
            var steps = ParseSteps(row.StepsJson)
                .Select(s => new MaintenanceDetector.VersionStep(s.Order, s.Action, s.Target))
                .ToList();
            snapshots[row.TestCaseId] = new MaintenanceDetector.VersionSnapshot(
                row.VersionId, review, steps);
        }

        var failedByKey = new Dictionary<(Guid, Guid), (HashSet<Guid> Executions, bool AppEnv)>();
        foreach (var verdict in verdicts)
        {
            if (verdict.TestCaseVersionId is null) continue;
            var key = (verdict.TestCaseId, verdict.TestCaseVersionId.Value);
            if (!failedByKey.TryGetValue(key, out var bucket))
                failedByKey[key] = bucket = (new HashSet<Guid>(), false);
            if (verdict.Classification is FailureClassification.AutomationFailure or FailureClassification.TestFailure)
                bucket.Executions.Add(verdict.ExecutionId);
            if (verdict.Classification is FailureClassification.ApplicationDefect or FailureClassification.EnvironmentFailure)
                failedByKey[key] = (bucket.Executions, true);
        }
        var failedInfo = failedByKey.ToDictionary(
            kvp => kvp.Key,
            kvp => new MaintenanceDetector.FailedVerdictInfo(kvp.Value.Executions.Count, kvp.Value.AppEnv));

        var detectorRows = evidence.Select(r => new MaintenanceDetector.EvidenceRow(
            r.AttemptId, r.ProjectId, r.TestCaseId, r.TestCaseVersionId,
            r.StepOrder, r.StepAction ?? string.Empty,
            r.OriginalStrategy, r.OriginalValue, r.RecoveredStrategy, r.RecoveredValue,
            r.IsAiAssisted, r.WasApplied, r.Status,
            r.HealingStrategy,
            r.ExecutionId, r.ExecutionStatus, r.Classification, r.CreatedAt)).ToList();

        var bands = await LoadForecastBandsAsync(projectId, since, now,
            detectorRows.Select(r => r.TestCaseId).Distinct().ToList(), ct);
        var detection = MaintenanceDetector.Detect(
            projectId, now, detectorRows, snapshots, excluded, failedInfo, bands);

        var created = 0;
        var existing = 0;
        foreach (var candidate in detection.Candidates.Take(ScanCandidateCap))
        {
            var open = await _store.FindOpenAsync(candidate.TestCaseId, candidate.TestCaseVersionId,
                candidate.StepOrder, candidate.ProposedStrategy, candidate.ProposedValue, ct);
            if (open is not null && open.ProjectId == projectId)
            {
                existing++;
                continue;
            }
            var proposal = new MaintenanceProposal
            {
                ProjectId = projectId,
                TestCaseId = candidate.TestCaseId,
                TestCaseVersionId = candidate.TestCaseVersionId,
                StepOrder = candidate.StepOrder,
                StepAction = candidate.StepAction,
                OriginalStrategy = candidate.OriginalStrategy,
                OriginalValue = candidate.OriginalValue,
                ProposedStrategy = candidate.ProposedStrategy,
                ProposedValue = candidate.ProposedValue,
                HealingStrategy = candidate.HealingStrategy,
                SignalType = MaintenanceDetector.SignalHealedLocator,
                Confidence = candidate.Confidence,
                OccurrenceCount = candidate.OccurrenceCount,
                Status = MaintenanceProposalStatus.Proposed,
                ProposedBy = null,
                CreatedAt = now,
                UpdatedAt = now,
            };
            await _store.AddAsync(proposal, ct);
            try
            {
                await _store.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (IsUniqueViolation(ex))
            {
                // Concurrent scan converged on the same open proposal.
                existing++;
                continue;
            }
            created++;
            await _audit.RecordAsync("maintenance.proposed", "maintenance_proposal",
                proposal.Id.ToString(), projectId,
                SafeMeta(proposalId: proposal.Id, signal: proposal.SignalType,
                    confidence: proposal.Confidence, occurrenceCount: proposal.OccurrenceCount,
                    testCaseId: proposal.TestCaseId, versionId: proposal.TestCaseVersionId,
                    stepOrder: proposal.StepOrder), ct);
        }

        await _audit.RecordAsync("maintenance.scan", "maintenance_scan",
            null, projectId,
            SafeScanMeta(detection.Candidates.Count, created, existing, detection.SkippedGroups), ct);
        return new MaintenanceScanResult(
            detection.Candidates.Count, created, existing, detection.SkippedGroups, now);
    }

    public async Task<PagedResult<MaintenanceProposalDto>> ListAsync(
        Guid projectId, MaintenanceProposalFilters filters,
        int page, int pageSize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filters);
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.TestCasesRead, ct);
        MaintenanceProposalStatus? status = null;
        if (!string.IsNullOrWhiteSpace(filters.Status))
        {
            if (!Enum.TryParse<MaintenanceProposalStatus>(filters.Status.Trim(), ignoreCase: true, out var parsed))
                throw new ValidationException("Status filter is invalid.",
                    new[] { new FieldError("status", "Status must be Proposed, Rejected, Applied or Superseded.") });
            status = parsed;
        }
        string? signal = null;
        if (!string.IsNullOrWhiteSpace(filters.Signal))
        {
            if (!KnownSignals.Contains(filters.Signal.Trim()))
                throw new ValidationException("Signal filter is invalid.",
                    new[] { new FieldError("signal", "Signal must be a known maintenance signal.") });
            signal = filters.Signal.Trim();
        }
        var (skip, take, pageNumber, size) = Paginate(page, pageSize);
        var search = string.IsNullOrWhiteSpace(filters.Search) ? null : filters.Search.Trim();
        var total = await _store.CountAsync(projectId, status, signal, search, ct);
        var rows = await _store.ListAsync(projectId, status, signal, search, skip, take, ct);
        return new PagedResult<MaintenanceProposalDto>(rows.Select(MapRow).ToList(), total, pageNumber, size);
    }

    public async Task<MaintenanceProposalDetailDto> GetAsync(
        Guid projectId, Guid proposalId, CancellationToken ct)
    {
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.TestCasesRead, ct);
        var proposal = await RequireProposalAsync(projectId, proposalId, ct);
        var testCase = await _cases.GetByIdAsync(proposal.TestCaseId, ct);
        if (testCase.ProjectId != projectId)
            throw new NotFoundException("Maintenance proposal not found.");
        var version = await _cases.GetVersionAsync(proposal.TestCaseId, proposal.TestCaseVersionId, ct);

        var now = _clock.UtcNow;
        var since = now.AddDays(-ScanWindowDays);
        var evidence = (await _store.ListHealingEvidenceAsync(projectId, since, ScanEvidenceTake, ct))
            .Where(r => r.TestCaseId == proposal.TestCaseId &&
                r.TestCaseVersionId == proposal.TestCaseVersionId &&
                r.StepOrder == proposal.StepOrder &&
                string.Equals(r.OriginalStrategy, proposal.OriginalStrategy, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.OriginalValue ?? string.Empty, proposal.OriginalValue ?? string.Empty, StringComparison.Ordinal) &&
                string.Equals(r.RecoveredStrategy, proposal.ProposedStrategy, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.RecoveredValue ?? string.Empty, proposal.ProposedValue ?? string.Empty, StringComparison.Ordinal))
            .OrderByDescending(r => r.CreatedAt)
            .Take(EvidenceDetailTake)
            .ToList();
        var executionIds = evidence.Select(r => r.ExecutionId).Distinct().OrderBy(id => id).Take(10).ToList();
        var attemptIds = evidence.Select(r => r.AttemptId).Distinct().OrderBy(id => id).Take(10).ToList();
        var applied = evidence.Count(r => r.Status == SelfHealingStatus.Applied && r.WasApplied);
        var denominator = evidence.Count(r => r.Status is SelfHealingStatus.Applied or SelfHealingStatus.Failed);
        var failedCount = (await _store.ListFailedVerdictsAsync(projectId, since, ScanVerdictTake, ct))
            .Where(v => v.TestCaseId == proposal.TestCaseId &&
                v.TestCaseVersionId == proposal.TestCaseVersionId &&
                (v.Classification == FailureClassification.AutomationFailure ||
                    v.Classification == FailureClassification.TestFailure))
            .Select(v => v.ExecutionId)
            .Distinct()
            .Count();
        var bands = await LoadForecastBandsAsync(projectId, since, now,
            new List<Guid> { proposal.TestCaseId }, ct);
        bands.TryGetValue(proposal.TestCaseId, out var band);

        int? createdNumber = null;
        if (proposal.CreatedVersionId is not null)
        {
            var createdVersion = await _cases.GetVersionAsync(
                proposal.TestCaseId, proposal.CreatedVersionId.Value, ct);
            createdNumber = createdVersion.VersionNumber;
        }
        var dto = MapDetail(proposal, testCase.TestKey, testCase.Title, version.VersionNumber, createdNumber);
        return new MaintenanceProposalDetailDto(dto, new MaintenanceEvidenceDto(
            proposal.OccurrenceCount, failedCount,
            denominator == 0 ? 0.0 : (double)applied / denominator,
            band, ConfidenceFactors(proposal.Confidence, proposal.OccurrenceCount, failedCount, band,
                denominator == 0 ? 0.0 : (double)applied / denominator),
            executionIds, attemptIds,
            evidence.Count == 0 ? proposal.CreatedAt : evidence.Min(r => r.CreatedAt),
            evidence.Count == 0 ? proposal.CreatedAt : evidence.Max(r => r.CreatedAt)));
    }

    public async Task<MaintenanceApproveResult> ApproveAsync(
        Guid projectId, Guid proposalId, CancellationToken ct)
    {
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.TestCasesManage, ct);
        var proposal = await RequireProposalAsync(projectId, proposalId, ct);

        // Idempotent re-approval: an Applied proposal returns its stored outcome.
        if (proposal.Status == MaintenanceProposalStatus.Applied)
        {
            if (proposal.CreatedVersionId is null)
                throw new ConflictException("Maintenance proposal is in an inconsistent applied state.");
            var appliedVersion = await _cases.GetVersionAsync(proposal.TestCaseId, proposal.CreatedVersionId.Value, ct);
            return new MaintenanceApproveResult(proposal.Id, proposal.Status.ToString(),
                appliedVersion.Id, appliedVersion.VersionNumber);
        }
        if (proposal.Status != MaintenanceProposalStatus.Proposed)
            throw new ConflictException($"Only Proposed maintenance proposals can be approved (now {proposal.Status}).");

        var testCase = await _cases.GetByIdAsync(proposal.TestCaseId, ct);
        if (testCase.ProjectId != projectId)
            throw new NotFoundException("Maintenance proposal not found.");
        var source = await _cases.GetVersionAsync(proposal.TestCaseId, proposal.TestCaseVersionId, ct);
        var versions = await _cases.ListVersionsAsync(proposal.TestCaseId, ct);
        var latest = versions.OrderByDescending(v => v.VersionNumber).FirstOrDefault();
        if (latest is null || latest.Id != source.Id)
            return await MarkStaleAsync(proposal, projectId, ct);
        if (!string.Equals(source.ReviewStatus, ReviewStatus.Approved.ToString(), StringComparison.OrdinalIgnoreCase))
            return await MarkStaleAsync(proposal, projectId, ct);

        var step = source.StructuredSteps.FirstOrDefault(s => s.Order == proposal.StepOrder);
        if (step is null)
            return await MarkStaleAsync(proposal, projectId, ct);
        if (!string.Equals(step.Action?.Trim(), proposal.StepAction?.Trim(), StringComparison.OrdinalIgnoreCase))
            return await MarkStaleAsync(proposal, projectId, ct);
        var parsed = MaintenanceDetector.ParseTarget(step.Target);
        if (parsed is null ||
            !string.Equals(parsed.Value.Strategy, proposal.OriginalStrategy, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(parsed.Value.Value, proposal.OriginalValue ?? string.Empty, StringComparison.Ordinal))
            return await MarkStaleAsync(proposal, projectId, ct);
        if (string.IsNullOrWhiteSpace(proposal.ProposedStrategy) ||
            !MaintenanceDetector.WebStrategies.Contains(proposal.ProposedStrategy.Trim()) ||
            string.IsNullOrEmpty(proposal.ProposedValue) ||
            proposal.ProposedValue.Length > TestStep.MaxTargetLength - 32)
            return await MarkStaleAsync(proposal, projectId, ct);
        var newTarget = MaintenanceDetector.FormatTarget(proposal.ProposedStrategy, proposal.ProposedValue);
        var reparsed = MaintenanceDetector.ParseTarget(newTarget);
        if (reparsed is null ||
            !string.Equals(reparsed.Value.Strategy, proposal.ProposedStrategy.Trim(), StringComparison.OrdinalIgnoreCase))
            return await MarkStaleAsync(proposal, projectId, ct);

        var stepsElement = BuildStepsWithReplacedTarget(source.StructuredSteps, proposal.StepOrder, newTarget);
        var now = _clock.UtcNow;
        var updated = await _cases.UpdateAsync(proposal.TestCaseId, new UpdateTestCaseCommand(
            testCase.Title, testCase.Description, testCase.Module, testCase.Framework,
            testCase.Platform, testCase.Priority, testCase.Status, testCase.SourceType,
            null, stepsElement, false, true), ct);
        var created = (await _cases.ListVersionsAsync(proposal.TestCaseId, ct))
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefault();
        if (created is null || created.VersionNumber <= source.VersionNumber)
            throw new ConflictException("Maintenance approval did not create a new test version; reload and retry.");

        proposal.Status = MaintenanceProposalStatus.Applied;
        proposal.CreatedVersionId = created.Id;
        proposal.ReviewedBy = await ResolveAppUserIdAsync(ct);
        proposal.ReviewedAt = now;
        proposal.UpdatedAt = now;
        proposal.RowVersion++;
        try
        {
            await _store.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsConcurrencyConflict(ex))
        {
            // A concurrent approval won: converge on its stored outcome.
            var current = await _store.GetByIdAsync(proposal.Id, ct);
            if (current is not null && current.ProjectId == projectId &&
                current.Status == MaintenanceProposalStatus.Applied && current.CreatedVersionId is not null)
            {
                var winner = await _cases.GetVersionAsync(current.TestCaseId, current.CreatedVersionId.Value, ct);
                return new MaintenanceApproveResult(current.Id, current.Status.ToString(),
                    winner.Id, winner.VersionNumber);
            }
            throw new ConflictException("Maintenance proposal was modified concurrently; reload and retry.");
        }

        await _audit.RecordAsync("maintenance.approved", "maintenance_proposal",
            proposal.Id.ToString(), projectId,
            SafeDecisionMeta(proposal.Id, proposal.TestCaseId, created.Id), ct);
        await _audit.RecordAsync("maintenance.applied", "maintenance_proposal",
            proposal.Id.ToString(), projectId,
            SafeDecisionMeta(proposal.Id, proposal.TestCaseId, created.Id), ct);
        return new MaintenanceApproveResult(proposal.Id, proposal.Status.ToString(),
            created.Id, created.VersionNumber);
    }

    public async Task<MaintenanceProposalDto> RejectAsync(
        Guid projectId, Guid proposalId, string? reason, CancellationToken ct)
    {
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.TestCasesManage, ct);
        var proposal = await RequireProposalAsync(projectId, proposalId, ct);
        var trimmed = reason?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            throw new ValidationException("Rejection reason is required.",
                new[] { new FieldError("reason", "Rejection reason is required.") });
        if (trimmed.Length > MaxRejectionLength)
            throw new ValidationException("Rejection reason is too long.",
                new[] { new FieldError("reason", $"Rejection reason must be at most {MaxRejectionLength} characters.") });
        if (proposal.Status != MaintenanceProposalStatus.Proposed)
            throw new ConflictException($"Only Proposed maintenance proposals can be rejected (now {proposal.Status}).");

        proposal.Status = MaintenanceProposalStatus.Rejected;
        proposal.RejectionReason = trimmed;
        proposal.ReviewedBy = await ResolveAppUserIdAsync(ct);
        proposal.ReviewedAt = _clock.UtcNow;
        proposal.UpdatedAt = proposal.ReviewedAt.Value;
        proposal.RowVersion++;
        try
        {
            await _store.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsConcurrencyConflict(ex))
        {
            throw new ConflictException("Maintenance proposal was modified concurrently; reload and retry.");
        }

        await _audit.RecordAsync("maintenance.rejected", "maintenance_proposal",
            proposal.Id.ToString(), projectId,
            SafeDecisionMeta(proposal.Id, proposal.TestCaseId, null), ct);
        return await MapAsync(proposal, ct);
    }

    // ---------- helpers ----------

    private async Task<MaintenanceProposal> RequireProposalAsync(Guid projectId, Guid proposalId, CancellationToken ct)
    {
        var proposal = await _store.GetByIdAsync(proposalId, ct);
        if (proposal is null || proposal.ProjectId != projectId)
            throw new NotFoundException("Maintenance proposal not found.");
        return proposal;
    }

    private async Task<MaintenanceApproveResult> MarkStaleAsync(
        MaintenanceProposal proposal, Guid projectId, CancellationToken ct)
    {
        proposal.Status = MaintenanceProposalStatus.Superseded;
        proposal.UpdatedAt = _clock.UtcNow;
        proposal.RowVersion++;
        await _store.SaveChangesAsync(ct);
        throw new ConflictException(
            "The maintenance proposal is stale because the test definition changed. No version was created. Run a new maintenance scan.");
    }

    private async Task<Dictionary<Guid, string?>> LoadForecastBandsAsync(
        Guid projectId, DateTimeOffset since, DateTimeOffset now,
        IReadOnlyList<Guid> testCaseIds, CancellationToken ct)
    {
        var bands = new Dictionary<Guid, string?>();
        if (testCaseIds.Count == 0) return bands;
        var range = new Reports.ReportDateRange(since, now);
        var outcomes = (await _reports.GetTestOutcomeRowsAsync(projectId, range, ct))
            .Where(o => testCaseIds.Contains(o.TestCaseId))
            .ToDictionary(o => o.TestCaseId, o => o);
        var verdicts = await _reports.GetTestRecentVerdictsAsync(
            projectId, range, testCaseIds, Reports.FlakinessForecast.MaxVerdictsPerTest, ct);
        foreach (var group in verdicts.GroupBy(v => v.TestCaseId))
        {
            outcomes.TryGetValue(group.Key, out var outcome);
            var ordered = group.OrderByDescending(v => v.CreatedAt).Select(v => v.Passed).ToList();
            var forecast = Reports.FlakinessForecast.Forecast(
                ordered, outcome?.Passed ?? ordered.Count(v => v), outcome?.Failed ?? ordered.Count(v => !v));
            bands[group.Key] = forecast.RiskBand;
        }
        return bands;
    }

    private static IReadOnlyList<TestStep> ParseSteps(string? stepsJson)
    {
        if (string.IsNullOrWhiteSpace(stepsJson)) return Array.Empty<TestStep>();
        try
        {
            using var document = JsonDocument.Parse(stepsJson);
            return TestStep.Parse(document.RootElement);
        }
        catch (JsonException)
        {
            return Array.Empty<TestStep>();
        }
    }

    /// <summary>
    /// Rebuilds the step array with exactly one target replaced, preserving
    /// order/action/value of every step. Lowercase keys match the validated
    /// TestStep contract; UpdateAsync revalidates the result.
    /// </summary>
    private static JsonElement BuildStepsWithReplacedTarget(
        IReadOnlyList<TestStepDto> steps, int stepOrder, string newTarget)
    {
        var payload = steps
            .OrderBy(s => s.Order)
            .Select(s => new
            {
                order = s.Order,
                action = s.Action,
                target = s.Order == stepOrder ? newTarget : s.Target,
                value = s.Value,
            })
            .ToList();
        return JsonSerializer.SerializeToElement(payload);
    }

    private async Task<Guid?> ResolveAppUserIdAsync(CancellationToken ct)
        => string.IsNullOrWhiteSpace(_currentUser.ExternalIdentityId)
            ? null
            : await _users.FindAppUserIdAsync(_currentUser.ExternalIdentityId!, ct);

    private async Task<MaintenanceProposalDto> MapAsync(MaintenanceProposal proposal, CancellationToken ct)
    {
        var testCase = await _cases.GetByIdAsync(proposal.TestCaseId, ct);
        var version = await _cases.GetVersionAsync(proposal.TestCaseId, proposal.TestCaseVersionId, ct);
        int? createdNumber = null;
        if (proposal.CreatedVersionId is not null)
        {
            var created = await _cases.GetVersionAsync(proposal.TestCaseId, proposal.CreatedVersionId.Value, ct);
            createdNumber = created.VersionNumber;
        }
        return MapDetail(proposal, testCase.TestKey, testCase.Title, version.VersionNumber, createdNumber);
    }

    private static MaintenanceProposalDto MapRow(MaintenanceProposalRow row)
        => new(row.Id, row.ProjectId, row.TestCaseId, row.TestKey, row.TestTitle,
            row.TestCaseVersionId, row.TestCaseVersionNumber,
            row.StepOrder, row.StepAction, row.OriginalStrategy, row.OriginalValue,
            row.ProposedStrategy, row.ProposedValue, row.HealingStrategy, row.SignalType,
            row.Confidence, row.OccurrenceCount, row.Status,
            row.ReviewedBy, row.ReviewedAt, row.RejectionReason, row.CreatedVersionId,
            null, row.CreatedAt, row.UpdatedAt);

    private static MaintenanceProposalDto MapDetail(
        MaintenanceProposal proposal, string testKey, string testTitle,
        int versionNumber, int? createdVersionNumber)
        => new(proposal.Id, proposal.ProjectId, proposal.TestCaseId, testKey, testTitle,
            proposal.TestCaseVersionId, versionNumber,
            proposal.StepOrder, proposal.StepAction, proposal.OriginalStrategy, proposal.OriginalValue,
            proposal.ProposedStrategy, proposal.ProposedValue, proposal.HealingStrategy.ToString(),
            proposal.SignalType, proposal.Confidence, proposal.OccurrenceCount,
            proposal.Status.ToString(), proposal.ReviewedBy, proposal.ReviewedAt,
            proposal.RejectionReason, proposal.CreatedVersionId, createdVersionNumber,
            proposal.CreatedAt, proposal.UpdatedAt);

    private static IReadOnlyList<string> ConfidenceFactors(
        int confidence, int occurrences, int failedCount, string? band, double ratio)
    {
        var factors = new List<string> { $"{occurrences} successful recoveries of the same locator" };
        if (failedCount >= MaintenanceDetector.MinFailedCorroboration)
            factors.Add($"{failedCount} related automation failures");
        if (string.Equals(band, Reports.FlakinessForecast.BandHigh, StringComparison.OrdinalIgnoreCase))
            factors.Add("flakiness forecast High");
        if (ratio >= MaintenanceDetector.MinHealingSuccessRatio)
            factors.Add($"healing success ratio {ratio:P0} on this step");
        return factors;
    }

    private static (int Skip, int Take, int Page, int Size) Paginate(int page, int pageSize)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize);
        return ((page - 1) * pageSize, pageSize, page, pageSize);
    }

    private static string SafeMeta(Guid proposalId, string signal, int confidence, int occurrenceCount,
        Guid testCaseId, Guid versionId, int stepOrder)
        => SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
        {
            proposalId,
            signal,
            confidence,
            occurrenceCount,
            testCaseId,
            testCaseVersionId = versionId,
            stepOrder,
        }));

    private static string SafeScanMeta(int candidates, int created, int existing, int skipped)
        => SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
        {
            candidateCount = candidates,
            createdCount = created,
            existingCount = existing,
            skippedCount = skipped,
        }));

    private static string SafeDecisionMeta(Guid proposalId, Guid testCaseId, Guid? createdVersionId)
        => SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
        {
            proposalId,
            testCaseId,
            createdVersionId,
        }));

    private static bool IsUniqueViolation(Exception ex)
    {
        var typeName = ex.GetType().FullName ?? string.Empty;
        if (typeName.Contains("DbUpdateException", StringComparison.Ordinal))
            return true;
        if (ex.InnerException is not null && IsUniqueViolation(ex.InnerException))
            return true;
        return ex.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("unique", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsConcurrencyConflict(Exception ex)
    {
        var typeName = ex.GetType().FullName ?? string.Empty;
        if (typeName.Contains("DbUpdateConcurrencyException", StringComparison.Ordinal))
            return true;
        return ex.InnerException is not null && IsConcurrencyConflict(ex.InnerException);
    }
}
