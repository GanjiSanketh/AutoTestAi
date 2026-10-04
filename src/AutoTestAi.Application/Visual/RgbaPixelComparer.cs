using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace AutoTestAi.Application.Visual;

/// <summary>
/// Pure deterministic RGBA pixel comparer (Slice 3C-4D-2, v1-rgba-bps).
/// Decodes PNGs to canonical RGBA (so compression/metadata differences
/// never mismatch), flattens alpha against black, and counts pixels whose
/// maximum channel distance exceeds the fixed tolerance. Integer math
/// only: no floating point enters the verdict path.
/// </summary>
public static class RgbaPixelComparer
{
    public const string AlgorithmVersion = "v1-rgba-bps";

    /// <summary>Maximum per-channel distance (0-255) still considered equal.</summary>
    public const int PerPixelTolerance = 16;

    /// <summary>Maximum decodable dimension per side; beyond is a comparison error.</summary>
    public const int MaxDimensionPx = 4096;

    /// <summary>Maximum decodable pixel count (~16.7M, ≈64MB RGBA).</summary>
    public const long MaxPixelCount = 4096L * 4096L;

    /// <summary>Diagnostic diff clamp per side for dimension mismatches.</summary>
    public const int MaxDiagnosticDimensionPx = 512;

    /// <summary>
    /// Compares two PNG byte strings. Throws VisualComparisonException for
    /// undecodable, oversized, or empty input (comparison error, never a
    /// mismatch verdict). Cancellation is honored during decode and scan.
    /// </summary>
    public static async Task<PixelComparisonResult> CompareAsync(
        byte[] baselinePng, byte[] actualPng, bool produceDiff, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(baselinePng);
        ArgumentNullException.ThrowIfNull(actualPng);
        if (baselinePng.Length == 0 || actualPng.Length == 0)
            throw new VisualComparisonException("Comparison input image is empty.");

        using var baseline = await DecodeAsync(baselinePng, "baseline", ct);
        using var actual = await DecodeAsync(actualPng, "actual", ct);

        if (baseline.Width != actual.Width || baseline.Height != actual.Height)
        {
            byte[]? diagnostic = null;
            if (produceDiff)
                diagnostic = EncodeDiagnostic(actual.Width, actual.Height, baseline.Width, baseline.Height);
            return new PixelComparisonResult(
                false,
                baseline.Width, baseline.Height,
                actual.Width, actual.Height,
                -1, 0, 10000,
                AlgorithmVersion,
                diagnostic);
        }

        long differing = 0;
        var total = (long)actual.Width * actual.Height;
        var baselineAccess = baseline.Frames.RootFrame;
        var actualAccess = actual.Frames.RootFrame;
        for (var y = 0; y < actual.Height; y++)
        {
            if ((y & 63) == 0)
                ct.ThrowIfCancellationRequested();
            var brow = baselineAccess.DangerousGetPixelRowMemory(y).Span;
            var arow = actualAccess.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < actual.Width; x++)
            {
                var b = Flatten(brow[x]);
                var a = Flatten(arow[x]);
                var distance = Math.Max(
                    Math.Max(Math.Abs(b.R - a.R), Math.Abs(b.G - a.G)),
                    Math.Abs(b.B - a.B));
                if (distance > PerPixelTolerance)
                    differing++;
            }
        }

        var rateBps = total == 0 ? 10000 : (int)(differing * 10000 / total);
        byte[]? diff = null;
        if (produceDiff && differing > 0)
            diff = EncodeHighlight(actual, baseline);

        return new PixelComparisonResult(
            true,
            baseline.Width, baseline.Height,
            actual.Width, actual.Height,
            differing, total, Math.Clamp(rateBps, 0, 10000),
            AlgorithmVersion,
            diff);
    }

    private static async Task<Image<Rgba32>> DecodeAsync(byte[] png, string role, CancellationToken ct)
    {
        ImageInfo info;
        try
        {
            info = await Image.IdentifyAsync(new MemoryStream(png, writable: false), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new VisualComparisonException($"Comparison {role} image is not decodable.");
        }
        if (info.Width < 1 || info.Height < 1 ||
            info.Width > MaxDimensionPx || info.Height > MaxDimensionPx ||
            (long)info.Width * info.Height > MaxPixelCount)
            throw new VisualComparisonException($"Comparison {role} image exceeds safe decode bounds.");
        try
        {
            return await Image.LoadAsync<Rgba32>(new MemoryStream(png, writable: false), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new VisualComparisonException($"Comparison {role} image failed to decode.");
        }
    }

    /// <summary>Porter-Duff over black with truncating integer math (deterministic).</summary>
    private static (int R, int G, int B) Flatten(Rgba32 pixel)
        => ((pixel.R * pixel.A) / 255, (pixel.G * pixel.A) / 255, (pixel.B * pixel.A) / 255);

    /// <summary>
    /// Difference-highlight diff at actual dimensions: unchanged pixels as
    /// neutral grayscale, differing pixels pure red. Deterministic for
    /// identical inputs (no timestamps, no metadata written).
    /// </summary>
    private static byte[] EncodeHighlight(Image<Rgba32> actual, Image<Rgba32> baseline)
    {
        using var diff = new Image<Rgba32>(actual.Width, actual.Height);
        var bFrame = baseline.Frames.RootFrame;
        var aFrame = actual.Frames.RootFrame;
        var dFrame = diff.Frames.RootFrame;
        for (var y = 0; y < actual.Height; y++)
        {
            var brow = bFrame.DangerousGetPixelRowMemory(y).Span;
            var arow = aFrame.DangerousGetPixelRowMemory(y).Span;
            var drow = dFrame.DangerousGetPixelRowMemory(y).Span;
            for (var x = 0; x < actual.Width; x++)
            {
                var b = Flatten(brow[x]);
                var a = Flatten(arow[x]);
                var distance = Math.Max(
                    Math.Max(Math.Abs(b.R - a.R), Math.Abs(b.G - a.G)),
                    Math.Abs(b.B - a.B));
                if (distance > PerPixelTolerance)
                {
                    drow[x] = new Rgba32(255, 0, 0, 255);
                }
                else
                {
                    var gray = (byte)((a.R + a.G + a.B) / 3);
                    drow[x] = new Rgba32(gray, gray, gray, 255);
                }
            }
        }
        using var stream = new MemoryStream();
        diff.SaveAsPng(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Bounded deterministic diagnostic for dimension mismatches (no pixel
    /// correspondence exists): solid red at clamped minimum dimensions.
    /// The verdict message and audit carry the exact dimensions.
    /// </summary>
    private static byte[] EncodeDiagnostic(int actualWidth, int actualHeight, int baselineWidth, int baselineHeight)
    {
        static int ClampSide(int a, int b) => Math.Clamp(Math.Min(Math.Max(a, 1), Math.Max(b, 1)), 1, MaxDiagnosticDimensionPx);
        using var image = new Image<Rgba32>(ClampSide(actualWidth, baselineWidth), ClampSide(actualHeight, baselineHeight));
        for (var y = 0; y < image.Height; y++)
        {
            var row = image.Frames.RootFrame.DangerousGetPixelRowMemory(y).Span;
            row.Fill(new Rgba32(255, 0, 0, 255));
        }
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }
}
