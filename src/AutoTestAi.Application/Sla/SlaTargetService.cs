using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.Reports;
using AutoTestAi.Application.TestGeneration;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Application.Sla;

/// <summary>
/// Project-scoped SLA target configuration and current open-defect breach
/// evaluation (Phase 4 Slice 13). Reads require reports.read; writes require
/// settings.manage. Thresholds bound the maximum age of currently open or
/// in-progress defects per severity; evaluation never infers resolution
/// times and never mutates defects.
/// </summary>
public sealed class SlaTargetService : ISlaTargetService
{
    private const int MinDays = 1;
    private const int MaxDays = 365;

    private static readonly IReadOnlyList<string> Severities = new[]
    {
        nameof(Severity.Critical), nameof(Severity.High), nameof(Severity.Medium), nameof(Severity.Low),
    };

    private readonly ISlaTargetStore _targets;
    private readonly IReportQueryStore _reports;
    private readonly IAuthorizationService _authorization;
    private readonly ICurrentUserService _currentUser;
    private readonly IUserDirectory _users;
    private readonly IDateTimeProvider _clock;
    private readonly IAuditService _audit;

    public SlaTargetService(
        ISlaTargetStore targets,
        IReportQueryStore reports,
        IAuthorizationService authorization,
        ICurrentUserService currentUser,
        IUserDirectory users,
        IDateTimeProvider clock,
        IAuditService audit)
    {
        _targets = targets;
        _reports = reports;
        _authorization = authorization;
        _currentUser = currentUser;
        _users = users;
        _clock = clock;
        _audit = audit;
    }

    public async Task<SlaTargetsResponse> GetAsync(Guid projectId, CancellationToken ct)
    {
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.ReportsRead, ct);
        var row = await _targets.GetByProjectAsync(projectId, ct);
        if (row is null)
            return new SlaTargetsResponse(null, null);
        var dto = Map(row);
        var compliance = await EvaluateAsync(projectId, ThresholdsOf(row), ct);
        return new SlaTargetsResponse(dto, compliance);
    }

    public async Task<SlaTargetDto> UpsertAsync(UpsertSlaTargetsCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _authorization.RequireProjectAccessAsync(command.ProjectId, Permissions.SettingsManage, ct);

        var errors = new List<FieldError>();
        var critical = Day(command.CriticalDays, "criticalDays", errors);
        var high = Day(command.HighDays, "highDays", errors);
        var medium = Day(command.MediumDays, "mediumDays", errors);
        var low = Day(command.LowDays, "lowDays", errors);
        ValidationException.ThrowIfInvalid(errors);

        var now = _clock.UtcNow;
        var existing = await _targets.GetByProjectAsync(command.ProjectId, ct);
        var isNew = existing is null;
        var row = existing ?? new SlaTargetPolicy { ProjectId = command.ProjectId, CreatedAt = now };
        row.CriticalDays = critical!.Value;
        row.HighDays = high!.Value;
        row.MediumDays = medium!.Value;
        row.LowDays = low!.Value;
        row.UpdatedBy = await ResolveAppUserIdAsync(ct);
        row.UpdatedAt = now;
        if (isNew)
            await _targets.AddAsync(row, ct);
        await _targets.SaveChangesAsync(ct);

        await _audit.RecordAsync(
            isNew ? "sla.target_configured" : "sla.target_updated",
            "sla-targets", row.Id.ToString(), command.ProjectId,
            SafeMeta(row), ct);
        return Map(row);
    }

    private async Task<SlaComplianceDto> EvaluateAsync(
        Guid projectId, IReadOnlyDictionary<string, int> thresholds, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var slices = await _reports.GetSlaBreachCountsAsync(projectId, thresholds, now, ct);
        var eligible = 0;
        var breached = 0;
        var unconfigured = 0;
        var bySeverity = new List<SlaSeverityBreachDto>();
        foreach (var severity in Severities)
        {
            var slice = slices.FirstOrDefault(s => string.Equals(s.Severity, severity, StringComparison.Ordinal));
            if (slice is null)
            {
                unconfigured++;
                continue;
            }
            eligible += slice.Eligible;
            breached += slice.Breached;
            bySeverity.Add(new SlaSeverityBreachDto(slice.Severity, slice.ThresholdDays, slice.Eligible, slice.Breached));
        }
        var compliant = eligible - breached;
        return new SlaComplianceDto(
            now,
            eligible,
            compliant,
            breached,
            eligible == 0 ? null : Math.Round(100.0 * compliant / eligible, 1),
            bySeverity,
            unconfigured);
    }

    private static IReadOnlyDictionary<string, int> ThresholdsOf(SlaTargetPolicy row)
        => new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [nameof(Severity.Critical)] = row.CriticalDays,
            [nameof(Severity.High)] = row.HighDays,
            [nameof(Severity.Medium)] = row.MediumDays,
            [nameof(Severity.Low)] = row.LowDays,
        };

    private static int? Day(int? value, string field, List<FieldError> errors)
    {
        if (!value.HasValue)
            errors.Add(new FieldError(field, "A day threshold is required for every severity."));
        else if (value.Value < MinDays || value.Value > MaxDays)
            errors.Add(new FieldError(field, $"Day threshold must be between {MinDays} and {MaxDays}."));
        return errors.Count == 0 ? value : null;
    }

    private static SlaTargetDto Map(SlaTargetPolicy row)
        => new(row.ProjectId, row.CriticalDays, row.HighDays, row.MediumDays, row.LowDays,
            row.UpdatedBy, row.UpdatedAt);

    private async Task<Guid?> ResolveAppUserIdAsync(CancellationToken cancellationToken)
        => string.IsNullOrWhiteSpace(_currentUser.ExternalIdentityId)
            ? null
            : await _users.FindAppUserIdAsync(_currentUser.ExternalIdentityId!, cancellationToken);

    private static string SafeMeta(SlaTargetPolicy row)
        => SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
        {
            projectId = row.ProjectId,
            criticalDays = row.CriticalDays,
            highDays = row.HighDays,
            mediumDays = row.MediumDays,
            lowDays = row.LowDays,
        }));
}
