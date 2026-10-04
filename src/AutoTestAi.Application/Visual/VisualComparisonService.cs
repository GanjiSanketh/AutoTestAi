using AutoTestAi.Application.Mobile;
using AutoTestAi.Application.Storage;
using Microsoft.Extensions.Logging;

namespace AutoTestAi.Application.Visual;

/// <summary>Performed-comparison outcome (Slice 3C-4D-2). Null (not an
/// outcome) means skipped/error: the caller leaves the verdict unchanged.</summary>
public sealed record VisualComparisonOutcome(
    Guid BaselineId,
    string BaselineSha256,
    int BaselineWidth,
    int BaselineHeight,
    int ActualWidth,
    int ActualHeight,
    bool DimensionMismatch,
    int MismatchRateBps,
    int ThresholdBps,
    bool IsMismatch,
    string AlgorithmVersion,
    long DurationMs,
    byte[]? DiffPng);

/// <summary>
/// Server-side visual comparison orchestration (Slice 3C-4D-2). Resolves
/// the Active baseline, downloads both images through bounded server-side
/// reads, and compares deterministically. Returns null for every
/// skipped/error path (missing baseline, project mismatch, storage or
/// decode failure) so callers leave the execution verdict unchanged;
/// only a performed comparison can report a mismatch. Cancellation
/// propagates; nothing here retries, persists, or emits audit events
/// (the engine owns persistence, fencing, and audit).
/// </summary>
public interface IVisualComparisonService
{
    Task<VisualComparisonOutcome?> CompareCheckpointAsync(
        Guid projectId,
        Guid testCaseVersionId,
        int stepOrder,
        byte[] actualPng,
        CancellationToken ct);
}

public sealed class VisualComparisonService : IVisualComparisonService
{
    /// <summary>Default mismatch tolerance: 10 bps = 0.1%.</summary>
    public const int DefaultThresholdBps = 10;

    private const int MaxBaselineBytes = 8 * 1024 * 1024;
    private const int MaxActualBytes = 4 * 1024 * 1024;

    private readonly IVisualBaselineStore _baselines;
    private readonly IArtifactStorage _artifacts;
    private readonly ILogger<VisualComparisonService> _logger;

    public VisualComparisonService(
        IVisualBaselineStore baselines,
        IArtifactStorage artifacts,
        ILogger<VisualComparisonService> logger)
    {
        _baselines = baselines;
        _artifacts = artifacts;
        _logger = logger;
    }

    public async Task<VisualComparisonOutcome?> CompareCheckpointAsync(
        Guid projectId,
        Guid testCaseVersionId,
        int stepOrder,
        byte[] actualPng,
        CancellationToken ct)
    {
        var baseline = await _baselines.FindActiveAsync(testCaseVersionId, stepOrder, ct);
        if (baseline is null)
            return null; // no baseline: check skipped, verdict unchanged
        if (baseline.ProjectId != projectId)
        {
            // Safety failure: never compare across projects, never expose contents.
            _logger.LogWarning("Visual comparison skipped for test case version {VersionId}: baseline project mismatch.",
                testCaseVersionId);
            return null;
        }
        var threshold = baseline.MismatchThresholdBps ?? DefaultThresholdBps;
        if (threshold < 0 || threshold > 10000)
        {
            _logger.LogWarning("Visual comparison skipped for baseline {BaselineId}: stored threshold out of range.",
                baseline.Id);
            return null;
        }
        if (actualPng is null || actualPng.Length == 0 || actualPng.Length > MaxActualBytes)
        {
            _logger.LogWarning("Visual comparison skipped for baseline {BaselineId}: actual image missing or oversized.",
                baseline.Id);
            return null;
        }
        if (!_artifacts.IsConfigured)
        {
            _logger.LogWarning("Visual comparison skipped for baseline {BaselineId}: artifact storage is not configured.",
                baseline.Id);
            return null;
        }

        byte[] baselineBytes;
        try
        {
            baselineBytes = await _artifacts.DownloadAsync(baseline.StorageKey, MaxBaselineBytes, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Missing/corrupt/unreachable baseline object: comparison error,
            // warning-only. Never a visual mismatch, never a retry trigger.
            _logger.LogWarning(ex, "Visual comparison skipped for baseline {BaselineId}: baseline object unreadable.",
                baseline.Id);
            return null;
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        PixelComparisonResult compared;
        try
        {
            compared = await RgbaPixelComparer.CompareAsync(baselineBytes, actualPng, produceDiff: true, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Visual comparison skipped for baseline {BaselineId}: comparison failed.",
                baseline.Id);
            return null;
        }

        var isMismatch = !compared.DimensionsMatch || compared.MismatchRateBps > threshold;
        return new VisualComparisonOutcome(
            baseline.Id, baseline.Sha256,
            compared.BaselineWidth, compared.BaselineHeight,
            compared.ActualWidth, compared.ActualHeight,
            !compared.DimensionsMatch,
            compared.MismatchRateBps, threshold,
            isMismatch,
            RgbaPixelComparer.AlgorithmVersion,
            started.ElapsedMilliseconds,
            compared.DiffPng);
    }
}
