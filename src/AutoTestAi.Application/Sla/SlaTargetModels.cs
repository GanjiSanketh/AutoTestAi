using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Application.Sla;

/// <summary>Persisted project SLA targets.</summary>
public sealed record SlaTargetDto(
    Guid ProjectId,
    int CriticalDays,
    int HighDays,
    int MediumDays,
    int LowDays,
    Guid? UpdatedBy,
    DateTimeOffset UpdatedAt);

/// <summary>Breach counts for one severity at a single evaluation instant.</summary>
public sealed record SlaSeverityBreachDto(
    string Severity,
    int ThresholdDays,
    int Eligible,
    int Breached);

/// <summary>
/// Current open-defect breach evaluation (Phase 4 Slice 13). Current-status
/// only: eligible defects are open or in-progress defects whose severity has
/// a configured target; breached means elapsed age strictly greater than the
/// threshold. Compliance is 0–100 (null when nothing is eligible — never a
/// fabricated 100%). This is not historical time-to-resolution compliance.
/// </summary>
public sealed record SlaComplianceDto(
    DateTimeOffset EvaluatedAt,
    int Eligible,
    int Compliant,
    int Breached,
    double? CompliancePercent,
    IReadOnlyList<SlaSeverityBreachDto> BySeverity,
    int UnconfiguredSeverityCount);

/// <summary>
/// Targets read surface. Targets is null when the project never configured
/// thresholds (no defaults are fabricated); compliance is null in the same
/// case and never implies historical resolution-time compliance.
/// </summary>
public sealed record SlaTargetsResponse(
    SlaTargetDto? Targets,
    SlaComplianceDto? Compliance);

/// <summary>Upsert input; every severity threshold is required (1–365 days).</summary>
public sealed record UpsertSlaTargetsCommand(
    Guid ProjectId,
    int? CriticalDays,
    int? HighDays,
    int? MediumDays,
    int? LowDays);

/// <summary>
/// Project-scoped SLA target configuration and current breach evaluation
/// (Phase 4 Slice 13). Reads require reports.read; writes require
/// settings.manage. Evaluation is read-only and deterministic.
/// </summary>
public interface ISlaTargetService
{
    Task<SlaTargetsResponse> GetAsync(Guid projectId, CancellationToken ct);

    Task<SlaTargetDto> UpsertAsync(UpsertSlaTargetsCommand command, CancellationToken ct);
}

/// <summary>One severity slice of the breach evaluation (store-level row).</summary>
public sealed record SlaSeverityCount(
    string Severity,
    int ThresholdDays,
    int Eligible,
    int Breached);
