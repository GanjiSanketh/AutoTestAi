namespace AutoTestAi.Application.Visual;

/// <summary>Visual comparison failure: undecodable, oversized, or otherwise
/// uncomparable input. Never a verdict — callers treat this as
/// comparison-error (warning-only), never as a visual mismatch.</summary>
public sealed class VisualComparisonException : Exception
{
    public VisualComparisonException(string message) : base(message) { }
}

/// <summary>Internal deterministic comparison result (Slice 3C-4D-2).</summary>
public sealed record PixelComparisonResult(
    bool DimensionsMatch,
    int BaselineWidth,
    int BaselineHeight,
    int ActualWidth,
    int ActualHeight,
    long DifferingPixels,
    long TotalPixels,
    int MismatchRateBps,
    string AlgorithmVersion,
    byte[]? DiffPng);
