using System.Security.Cryptography;
using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.Storage;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Application.TestGeneration;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Application.Mobile;

/// <summary>
/// Project-scoped visual baseline lifecycle (Phase 3 Slice 3C-4D-1).
/// Reference metadata plus server-generated object-storage keys only;
/// image bytes travel caller → storage, never through logs, audit, or
/// DTOs. Candidate creation is idempotent on (version, step, sha256);
/// approval atomically demotes the previous Active row. Comparison
/// logic belongs to 3C-4D-2; execution never consults this service.
/// </summary>
public sealed class VisualBaselineService : IVisualBaselineService
{
    private const int MaxImageBytes = 8 * 1024 * 1024;
    private const int MaxDimensionPx = 8192;
    private const int DownloadExpirySeconds = 900;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly IVisualBaselineStore _store;
    private readonly ITestCaseStore _cases;
    private readonly IArtifactStorage _artifacts;
    private readonly IAuthorizationService _authorization;
    private readonly ICurrentUserService _currentUser;
    private readonly IUserDirectory _users;
    private readonly IDateTimeProvider _clock;
    private readonly IAuditService _audit;

    public VisualBaselineService(
        IVisualBaselineStore store,
        ITestCaseStore cases,
        IArtifactStorage artifacts,
        IAuthorizationService authorization,
        ICurrentUserService currentUser,
        IUserDirectory users,
        IDateTimeProvider clock,
        IAuditService audit)
    {
        _store = store;
        _cases = cases;
        _artifacts = artifacts;
        _authorization = authorization;
        _currentUser = currentUser;
        _users = users;
        _clock = clock;
        _audit = audit;
    }

    public async Task<VisualBaselineDto> ProposeAsync(ProposeBaselineCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _authorization.RequireProjectAccessAsync(command.ProjectId, Permissions.SettingsManage, ct);

        var errors = new List<FieldError>();
        if (command.StepOrder < 1)
            errors.Add(new FieldError("stepOrder", "Step order must be at least 1."));
        if (command.PngBytes is null || command.PngBytes.Length == 0)
            errors.Add(new FieldError("image", "Reference image bytes are required."));
        else if (command.PngBytes.Length > MaxImageBytes)
            errors.Add(new FieldError("image", $"Reference image must be at most {MaxImageBytes} bytes."));
        else if (!IsPng(command.PngBytes))
            errors.Add(new FieldError("image", "Reference image must be a PNG image."));
        if (command.Width < 1 || command.Width > MaxDimensionPx)
            errors.Add(new FieldError("width", $"Image width must be between 1 and {MaxDimensionPx} pixels."));
        if (command.Height < 1 || command.Height > MaxDimensionPx)
            errors.Add(new FieldError("height", $"Image height must be between 1 and {MaxDimensionPx} pixels."));
        ValidationException.ThrowIfInvalid(errors);

        var version = await _cases.GetVersionByIdAsync(command.TestCaseVersionId, ct);
        var testCase = version is null ? null : await _cases.GetByIdAsync(version.TestCaseId, ct);
        if (version is null || testCase is null || testCase.ProjectId != command.ProjectId)
            throw new NotFoundException("Test case version not found.");

        var sha256 = Convert.ToHexString(SHA256.HashData(command.PngBytes)).ToLowerInvariant();
        var existing = await _store.FindCandidateAsync(command.TestCaseVersionId, command.StepOrder, sha256, ct);
        if (existing is not null && existing.ProjectId == command.ProjectId)
            return Map(existing); // idempotent: same bytes proposed twice converge

        if (!_artifacts.IsConfigured)
            throw new ConflictException("Artifact storage is not configured for baseline images.");

        var actor = await ResolveAppUserIdAsync(ct);
        var now = _clock.UtcNow;
        var key = $"projects/{command.ProjectId}/visual-baselines/{command.TestCaseVersionId:N}/step-{command.StepOrder:000}-{sha256[..16]}.png";
        using (var stream = new MemoryStream(command.PngBytes, writable: false))
            await _artifacts.UploadAsync(key, stream, "image/png", ct);

        var baseline = new VisualBaseline
        {
            ProjectId = command.ProjectId,
            TestCaseId = testCase.Id,
            TestCaseVersionId = version.Id,
            StepOrder = command.StepOrder,
            Status = VisualBaselineStatus.Candidate,
            StorageKey = key,
            Sha256 = sha256,
            Width = command.Width,
            Height = command.Height,
            ContentType = "image/png",
            CreatedBy = actor,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await _store.AddAsync(baseline, ct);
        await _store.SaveChangesAsync(ct);

        await _audit.RecordAsync("visual.baseline_proposed", "visual_baseline",
            baseline.Id.ToString(), command.ProjectId, SafeMeta(baseline), ct);
        return Map(baseline);
    }

    public async Task<IReadOnlyList<VisualBaselineDto>> ListAsync(
        Guid projectId, Guid? testCaseVersionId, string? status, CancellationToken ct)
    {
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.ExecutionsRead, ct);
        VisualBaselineStatus? parsed = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<VisualBaselineStatus>(status.Trim(), ignoreCase: true, out var value))
                throw new ValidationException("Baseline status is invalid.",
                    new[] { new FieldError("status", "Status must be Candidate, Active, or Superseded.") });
            parsed = value;
        }
        if (testCaseVersionId.HasValue)
        {
            var version = await _cases.GetVersionByIdAsync(testCaseVersionId.Value, ct);
            var testCase = version is null ? null : await _cases.GetByIdAsync(version.TestCaseId, ct);
            if (version is null || testCase is null || testCase.ProjectId != projectId)
                throw new NotFoundException("Test case version not found.");
        }
        return (await _store.ListAsync(projectId, testCaseVersionId, parsed, ct)).Select(Map).ToList();
    }

    public async Task<VisualBaselineDto> GetAsync(Guid projectId, Guid baselineId, CancellationToken ct)
    {
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.ExecutionsRead, ct);
        var baseline = await _store.GetByIdAsync(baselineId, ct);
        if (baseline is null || baseline.ProjectId != projectId)
            throw new NotFoundException("Visual baseline not found.");
        return Map(baseline);
    }

    public async Task<VisualBaselineDto> ApproveAsync(ApproveBaselineCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _authorization.RequireProjectAccessAsync(command.ProjectId, Permissions.SettingsManage, ct);
        var baseline = await _store.GetByIdAsync(command.BaselineId, ct);
        if (baseline is null || baseline.ProjectId != command.ProjectId)
            throw new NotFoundException("Visual baseline not found.");
        if (baseline.Status != VisualBaselineStatus.Candidate)
            throw new ConflictException($"Only Candidate baselines can be approved (now {baseline.Status}).");

        // Service-level guard for the filtered unique invariant (backstop
        // for stores without partial-index support): exactly one Active row
        // per (version, step) survives this save.
        var now = _clock.UtcNow;
        var previous = await _store.FindActiveAsync(baseline.TestCaseVersionId, baseline.StepOrder, ct);
        if (previous is not null && previous.ProjectId == command.ProjectId && previous.Id != baseline.Id)
        {
            previous.Status = VisualBaselineStatus.Superseded;
            previous.UpdatedAt = now;
        }
        baseline.Status = VisualBaselineStatus.Active;
        baseline.ApprovedBy = await ResolveAppUserIdAsync(ct);
        baseline.ApprovedAt = now;
        baseline.UpdatedAt = now;
        try
        {
            await _store.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            throw new ConflictException("A baseline was approved concurrently; reload and retry.");
        }

        await _audit.RecordAsync("visual.baseline_approved", "visual_baseline",
            baseline.Id.ToString(), command.ProjectId, SafeMeta(baseline), ct);
        return Map(baseline);
    }

    public async Task RejectAsync(Guid projectId, Guid baselineId, CancellationToken ct)
    {
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.SettingsManage, ct);
        var baseline = await _store.GetByIdAsync(baselineId, ct);
        if (baseline is null || baseline.ProjectId != projectId)
            throw new NotFoundException("Visual baseline not found.");
        if (baseline.Status != VisualBaselineStatus.Candidate)
            throw new ConflictException($"Only Candidate baselines can be rejected (now {baseline.Status}).");
        await _store.DeleteAsync(baseline, ct);
        await _store.SaveChangesAsync(ct);

        await _audit.RecordAsync("visual.baseline_rejected", "visual_baseline",
            baseline.Id.ToString(), projectId, SafeMeta(baseline), ct);
    }

    public async Task<string> GetDownloadUrlAsync(Guid projectId, Guid baselineId, CancellationToken ct)
    {
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.ExecutionsRead, ct);
        var baseline = await _store.GetByIdAsync(baselineId, ct);
        if (baseline is null || baseline.ProjectId != projectId)
            throw new NotFoundException("Visual baseline not found.");
        return await _artifacts.GetPresignedDownloadUrlAsync(
            baseline.StorageKey, DownloadExpirySeconds, ct);
    }

    private async Task<Guid?> ResolveAppUserIdAsync(CancellationToken ct)
        => string.IsNullOrWhiteSpace(_currentUser.ExternalIdentityId)
            ? null
            : await _users.FindAppUserIdAsync(_currentUser.ExternalIdentityId!, ct);

    private static VisualBaselineDto Map(VisualBaseline baseline) => new(
        baseline.Id, baseline.ProjectId, baseline.TestCaseId, baseline.TestCaseVersionId,
        baseline.StepOrder, baseline.Status.ToString(), baseline.StorageKey, baseline.Sha256,
        baseline.Width, baseline.Height, baseline.ContentType, baseline.MismatchThresholdBps,
        baseline.CreatedBy, baseline.ApprovedBy, baseline.CreatedAt, baseline.ApprovedAt, baseline.UpdatedAt);

    private static bool IsPng(byte[] bytes)
        => bytes.Length >= PngSignature.Length &&
            PngSignature.SequenceEqual(bytes.Take(PngSignature.Length));

    private static string SafeMeta(VisualBaseline baseline)
        => SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
        {
            baselineId = baseline.Id,
            testCaseVersionId = baseline.TestCaseVersionId,
            stepOrder = baseline.StepOrder,
            status = baseline.Status.ToString(),
            sha256 = baseline.Sha256,
            width = baseline.Width,
            height = baseline.Height,
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
}
