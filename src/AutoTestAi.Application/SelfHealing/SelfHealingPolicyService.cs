using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.TestGeneration;
using AutoTestAi.Domain.Entities;

namespace AutoTestAi.Application.SelfHealing;

/// <summary>
/// Project-scoped self-healing policy administration (Phase 2 Slice 11).
/// Reads require executions.read; writes require settings.manage. The policy
/// never contains credentials; defaults are safe (disabled).
/// </summary>
public sealed class SelfHealingPolicyService : ISelfHealingPolicyService
{
    private static readonly IReadOnlySet<string> ValidStrategies =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "css", "xpath", "role", "text", "testid" };

    private readonly ISelfHealingPolicyStore _policies;
    private readonly ISelfHealingAttemptStore _attempts;
    private readonly TestExecution.IExecutionStore _executions;
    private readonly IAuthorizationService _authorization;
    private readonly ICurrentUserService _currentUser;
    private readonly IUserDirectory _users;
    private readonly IDateTimeProvider _clock;
    private readonly IAuditService _audit;

    public SelfHealingPolicyService(
        ISelfHealingPolicyStore policies,
        ISelfHealingAttemptStore attempts,
        TestExecution.IExecutionStore executions,
        IAuthorizationService authorization,
        ICurrentUserService currentUser,
        IUserDirectory users,
        IDateTimeProvider clock,
        IAuditService audit)
    {
        _policies = policies;
        _attempts = attempts;
        _executions = executions;
        _authorization = authorization;
        _currentUser = currentUser;
        _users = users;
        _clock = clock;
        _audit = audit;
    }

    public async Task<SelfHealingPolicyDto?> GetAsync(Guid projectId, CancellationToken ct)
    {
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.ExecutionsRead, ct);
        var row = await _policies.GetByProjectAsync(projectId, ct);
        return row is null ? null : Map(row);
    }

    public async Task<SelfHealingPolicyDto> UpsertAsync(UpsertSelfHealingPolicyCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _authorization.RequireProjectAccessAsync(command.ProjectId, Permissions.SettingsManage, ct);

        var strategies = NormalizeStrategies(command.AllowedStrategies);
        var errors = new List<FieldError>();
        if (command.MinDeterministicScore.HasValue &&
            (command.MinDeterministicScore.Value < 0 || command.MinDeterministicScore.Value > 100))
            errors.Add(new FieldError("minDeterministicScore", "Minimum deterministic score must be between 0 and 100."));
        if (command.MinAiConfidence.HasValue &&
            (command.MinAiConfidence.Value < 0 || command.MinAiConfidence.Value > 1))
            errors.Add(new FieldError("minAiConfidence", "Minimum AI confidence must be between 0 and 1."));
        if (command.AiFallbackEnabled && !command.Enabled)
            errors.Add(new FieldError("aiFallbackEnabled", "AI fallback requires healing to be enabled."));
        ValidationException.ThrowIfInvalid(errors);

        var now = _clock.UtcNow;
        var actor = await ResolveAppUserIdAsync(ct);
        var existing = await _policies.GetByProjectAsync(command.ProjectId, ct);
        var isNew = existing is null;
        var row = existing ?? new SelfHealingPolicy { ProjectId = command.ProjectId, CreatedAt = now };
        row.Enabled = command.Enabled;
        row.AiFallbackEnabled = command.Enabled && command.AiFallbackEnabled;
        row.MaxAttemptsPerStep = 1;
        row.MinDeterministicScore = command.MinDeterministicScore;
        row.MinAiConfidence = command.MinAiConfidence;
        row.AllowedStrategies = string.Join(",", strategies);
        row.UpdatedBy = actor;
        row.UpdatedAt = now;
        if (isNew)
            await _policies.AddAsync(row, ct);
        await _policies.SaveChangesAsync(ct);

        await _audit.RecordAsync(
            isNew ? "self-healing.policy_configured" : "self-healing.policy_updated",
            "self-healing-policy", row.Id.ToString(), command.ProjectId,
            SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
            {
                projectId = row.ProjectId,
                enabled = row.Enabled,
                aiFallbackEnabled = row.AiFallbackEnabled,
            })), ct);
        return Map(row);
    }

    public async Task<SelfHealingStatusDto> GetStatusAsync(Guid projectId, CancellationToken ct)
    {
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.ExecutionsRead, ct);
        var row = await _policies.GetByProjectAsync(projectId, ct);
        var counts = await _attempts.CountByProjectAsync(projectId, ct);
        if (row is null)
            return new SelfHealingStatusDto(projectId, false, false, false,
                counts.AttemptCount, counts.AppliedCount, counts.LastHealedAt);
        return new SelfHealingStatusDto(projectId, row.Enabled, true, row.AiFallbackEnabled,
            counts.AttemptCount, counts.AppliedCount, counts.LastHealedAt);
    }

    public async Task<IReadOnlyList<SelfHealingAttemptDto>> ListAttemptsAsync(
        Guid projectId, Guid executionId, CancellationToken ct)
    {
        // Project isolation: authorize the project scope first (unknown projects
        // yield 403, never existence-revealing 404), then prove the execution
        // belongs to it before returning anything.
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.ExecutionsRead, ct);
        var execution = await _executions.GetExecutionByIdAsync(executionId, ct);
        if (execution is null || execution.ProjectId != projectId)
            throw new NotFoundException("Execution not found.");
        return (await _attempts.ListByExecutionAsync(executionId, ct))
            .Where(a => a.ProjectId == projectId)
            .OrderBy(a => a.StepOrder)
            .Select(MapAttempt)
            .ToList();
    }

    internal static List<string> NormalizeStrategies(IReadOnlyList<string>? values)
    {
        var result = new List<string>();
        if (values is null)
            return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in values)
        {
            var value = raw?.Trim().ToLowerInvariant() ?? string.Empty;
            if (value.Length == 0 || !seen.Add(value))
                continue;
            if (!ValidStrategies.Contains(value))
                throw new ValidationException("Allowed strategies must be a subset of 'css', 'xpath', 'role', 'text', 'testid'.",
                    new[] { new FieldError("allowedStrategies", "Allowed strategies must be a subset of 'css', 'xpath', 'role', 'text', 'testid'.") });
            result.Add(value);
        }
        if (result.Count > 5)
            throw new ValidationException("Allowed strategies must contain at most 5 entries.",
                new[] { new FieldError("allowedStrategies", "Allowed strategies must contain at most 5 entries.") });
        return result;
    }

    private async Task<Guid?> ResolveAppUserIdAsync(CancellationToken ct)
        => string.IsNullOrWhiteSpace(_currentUser.ExternalIdentityId)
            ? null
            : await _users.FindAppUserIdAsync(_currentUser.ExternalIdentityId!, ct);

    internal static SelfHealingPolicyDto Map(SelfHealingPolicy row) => new(
        row.ProjectId, row.Enabled, row.AiFallbackEnabled, row.MaxAttemptsPerStep,
        row.MinDeterministicScore, row.MinAiConfidence,
        string.IsNullOrWhiteSpace(row.AllowedStrategies)
            ? Array.Empty<string>()
            : row.AllowedStrategies.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        row.UpdatedAt);

    internal static SelfHealingAttemptDto MapAttempt(SelfHealingAttempt row) => new(
        row.Id, row.ExecutionId, row.StepOrder, row.StepAction,
        row.OriginalStrategy, row.OriginalValue,
        row.RecoveredStrategy, row.RecoveredValue,
        row.HealingStrategy.ToString(), row.Status.ToString(),
        row.CandidateCount, row.WasApplied, row.IsAiAssisted, row.CreatedAt);
}
