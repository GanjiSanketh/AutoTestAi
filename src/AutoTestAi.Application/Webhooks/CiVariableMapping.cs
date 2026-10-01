namespace AutoTestAi.Application.Webhooks;

/// <summary>
/// Internal normalized CI event (Phase 3 Slice 3B). Carries only the safe,
/// allowlisted fields required for filtering, variable mapping, and audit.
/// Never the raw provider payload.
/// </summary>
public sealed record NormalizedCiEvent(
    string Provider,
    Guid IntegrationId,
    Guid ProjectId,
    string DeliveryId,
    string EventType,
    string? Branch,
    string? CommitSha,
    string? Repository,
    string? PullRequestNumber,
    string? BuildNumber,
    string? CiRunId,
    string? Actor,
    DateTimeOffset ReceivedAt,
    string PayloadHash);

/// <summary>
/// Safe variable-mapping sources: normalized CI fields that may become
/// execution variable values. Closed set — arbitrary payload fields can
/// never become variables.
/// </summary>
public static class CiVariableMapping
{
    public const string Branch = "BRANCH";
    public const string CommitSha = "COMMIT_SHA";
    public const string Repository = "REPOSITORY";
    public const string EventType = "EVENT_TYPE";
    public const string CiRunId = "CI_RUN_ID";
    public const string PullRequestNumber = "PULL_REQUEST_NUMBER";
    public const string BuildNumber = "BUILD_NUMBER";

    public static readonly IReadOnlyList<string> SupportedSources = new[]
    {
        Branch, CommitSha, Repository, EventType, CiRunId, PullRequestNumber, BuildNumber,
    };

    public const int MaxValueLength = 500;

    public static bool IsSupportedSource(string? source)
        => !string.IsNullOrWhiteSpace(source) &&
           SupportedSources.Contains(source.Trim(), StringComparer.Ordinal);

    /// <summary>Source field name → normalized event value.</summary>
    public static string? ResolveValue(string source, NormalizedCiEvent normalized)
        => source switch
        {
            Branch => normalized.Branch,
            CommitSha => normalized.CommitSha,
            Repository => normalized.Repository,
            EventType => normalized.EventType,
            CiRunId => normalized.CiRunId,
            PullRequestNumber => normalized.PullRequestNumber,
            BuildNumber => normalized.BuildNumber,
            _ => null,
        };

    /// <summary>
    /// Builds validated plain variable overrides from the configured mapping.
    /// Missing/empty normalized values are skipped (never injected as empty).
    /// Over-long values fail the delivery deterministically.
    /// </summary>
    public static Dictionary<string, string> BuildOverrides(
        IReadOnlyDictionary<string, string> mapping,
        NormalizedCiEvent normalized)
    {
        var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (target, source) in mapping)
        {
            if (!Variables.VariableModel.IsValidKey(target) || !IsSupportedSource(source))
                continue;
            var value = ResolveValue(source.Trim(), normalized);
            if (string.IsNullOrEmpty(value))
                continue;
            var trimmed = value.Trim();
            if (trimmed.Length == 0 || trimmed.Length > MaxValueLength)
                throw new Common.ValidationException(
                    $"Mapped CI value for '{target}' is invalid.",
                    new[] { new Common.FieldError($"variableMapping.{target}", $"Mapped values must be 1..{MaxValueLength} characters.") });
            overrides[target] = trimmed;
        }
        return overrides;
    }
}
