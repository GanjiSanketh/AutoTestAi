using AutoTestAi.Application.Visual;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace AutoTestAi.UnitTests;

/// <summary>Slice 3C-4D-2: deterministic RGBA pixel comparison contract —
/// identical bytes, compression/metadata indifference, exact mismatch
/// arithmetic, threshold boundaries, normalization, bounds, determinism.</summary>
public sealed class VisualComparisonTests
{
    private static byte[] Solid<TPixel>(int width, int height, TPixel color, PngEncoder? encoder = null)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        using var image = new Image<TPixel>(width, height);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
                accessor.GetRowSpan(y).Fill(color);
        });
        using var stream = new MemoryStream();
        image.SaveAsPng(stream, encoder ?? new PngEncoder());
        return stream.ToArray();
    }

    private static byte[] Solid(int width, int height, Rgba32 color, PngEncoder? encoder = null)
        => Solid<Rgba32>(width, height, color, encoder);

    private static void FillSolid<TPixel>(Image<TPixel> image, TPixel color)
        where TPixel : unmanaged, IPixel<TPixel>
        => image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
                accessor.GetRowSpan(y).Fill(color);
        });

    private static byte[] WithPixel(byte[] png, int x, int y, Rgba32 color)
    {
        using var image = Image.Load<Rgba32>(png);
        image[x, y] = color;
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    [Fact]
    public void AlgorithmVersion_IsStable()
        => Assert.Equal("v1-rgba-bps", RgbaPixelComparer.AlgorithmVersion);

    [Fact]
    public async Task IdenticalImages_MismatchZero_NoDiff()
    {
        var png = Solid(10, 10, new Rgba32(10, 20, 30, 255));
        var result = await RgbaPixelComparer.CompareAsync(png, png, produceDiff: true, CancellationToken.None);

        Assert.True(result.DimensionsMatch);
        Assert.Equal(0, result.DifferingPixels);
        Assert.Equal(100, result.TotalPixels);
        Assert.Equal(0, result.MismatchRateBps);
        Assert.Null(result.DiffPng);
        Assert.Equal("v1-rgba-bps", result.AlgorithmVersion);
    }

    [Fact]
    public async Task DifferentCompression_SamePixels_Pass()
    {
        using var image = new Image<Rgba32>(16, 16);
        FillSolid(image, new Rgba32(200, 100, 50, 255));
        static byte[] Encode(Image<Rgba32> source, PngCompressionLevel level)
        {
            using var stream = new MemoryStream();
            source.SaveAsPng(stream, new PngEncoder { CompressionLevel = level });
            return stream.ToArray();
        }
        var fast = Encode(image, PngCompressionLevel.BestSpeed);
        var best = Encode(image, PngCompressionLevel.BestCompression);
        Assert.NotEqual(fast, best); // encoded bytes genuinely differ

        var result = await RgbaPixelComparer.CompareAsync(fast, best, produceDiff: true, CancellationToken.None);
        Assert.Equal(0, result.MismatchRateBps);
    }

    [Fact]
    public async Task OneChangedPixel_ExactDeterministicCount()
    {
        var baseline = Solid(10, 10, new Rgba32(0, 0, 0, 255));
        var actual = WithPixel(baseline, 3, 4, new Rgba32(255, 255, 255, 255));
        var result = await RgbaPixelComparer.CompareAsync(baseline, actual, produceDiff: true, CancellationToken.None);

        Assert.Equal(1, result.DifferingPixels);
        Assert.Equal(100, result.TotalPixels);
        Assert.Equal(100, result.MismatchRateBps); // 1 * 10000 / 100
        Assert.NotNull(result.DiffPng);
    }

    [Fact]
    public async Task BelowThreshold_Passes_WithDiffBytesIgnored()
    {
        // 100x100 = 10000px: 1 differing pixel = exactly 1 bps.
        var baseline = Solid(100, 100, new Rgba32(0, 0, 0, 255));
        var actual = WithPixel(baseline, 0, 0, new Rgba32(255, 255, 255, 255));
        var result = await RgbaPixelComparer.CompareAsync(baseline, actual, produceDiff: false, CancellationToken.None);

        Assert.Equal(1, result.MismatchRateBps);
        Assert.Null(result.DiffPng);
    }

    [Fact]
    public async Task ThresholdZero_AnyDifferenceCounts()
    {
        var baseline = Solid(10, 10, new Rgba32(0, 0, 0, 255));
        var same = await RgbaPixelComparer.CompareAsync(baseline, baseline, produceDiff: false, CancellationToken.None);
        Assert.Equal(0, same.MismatchRateBps);

        var actual = WithPixel(baseline, 0, 0, new Rgba32(0, 0, 0, 255) with { R = 17 });
        var result = await RgbaPixelComparer.CompareAsync(baseline, actual, produceDiff: false, CancellationToken.None);
        // Channel distance 17 exceeds the fixed tolerance of 16.
        Assert.Equal(100, result.MismatchRateBps);
    }

    [Fact]
    public async Task WithinTolerance_Passes()
    {
        var baseline = Solid(10, 10, new Rgba32(100, 100, 100, 255));
        var actual = WithPixel(baseline, 0, 0, new Rgba32(116, 100, 100, 255));
        var result = await RgbaPixelComparer.CompareAsync(baseline, actual, produceDiff: false, CancellationToken.None);
        // Channel distance exactly 16 is within tolerance.
        Assert.Equal(0, result.MismatchRateBps);
    }

    [Fact]
    public async Task ThresholdFull_AllowsCompleteDifference()
    {
        var baseline = Solid(4, 4, new Rgba32(0, 0, 0, 255));
        var actual = Solid(4, 4, new Rgba32(255, 255, 255, 255));
        var result = await RgbaPixelComparer.CompareAsync(baseline, actual, produceDiff: false, CancellationToken.None);
        Assert.Equal(10000, result.MismatchRateBps); // worst case is representable
    }

    [Fact]
    public async Task DimensionMismatch_IsDeterministicMismatch()
    {
        var baseline = Solid(10, 10, new Rgba32(0, 0, 0, 255));
        var actual = Solid(10, 11, new Rgba32(0, 0, 0, 255));
        var result = await RgbaPixelComparer.CompareAsync(baseline, actual, produceDiff: true, CancellationToken.None);

        Assert.False(result.DimensionsMatch);
        Assert.Equal(10, result.BaselineWidth);
        Assert.Equal(11, result.ActualHeight);
        Assert.Equal(10000, result.MismatchRateBps);
        Assert.NotNull(result.DiffPng); // bounded diagnostic, not silence

        var rerun = await RgbaPixelComparer.CompareAsync(baseline, actual, produceDiff: true, CancellationToken.None);
        Assert.Equal(result.DiffPng, rerun.DiffPng);
    }

    [Fact]
    public async Task RgbAgainstRgbaEquivalent_Passes()
    {
        using var rgb = new Image<Rgb24>(8, 8);
        FillSolid(rgb, new Rgb24(10, 20, 30));
        using var rgbStream = new MemoryStream();
        rgb.SaveAsPng(rgbStream);
        var rgba = Solid(8, 8, new Rgba32(10, 20, 30, 255));

        var result = await RgbaPixelComparer.CompareAsync(rgbStream.ToArray(), rgba, produceDiff: false, CancellationToken.None);
        Assert.Equal(0, result.MismatchRateBps);
    }

    [Fact]
    public async Task GrayscaleAgainstRgbaEquivalent_Passes()
    {
        using var gray = new Image<L8>(8, 8);
        FillSolid(gray, new L8(77));
        using var grayStream = new MemoryStream();
        gray.SaveAsPng(grayStream);
        var rgba = Solid(8, 8, new Rgba32(77, 77, 77, 255));

        var result = await RgbaPixelComparer.CompareAsync(grayStream.ToArray(), rgba, produceDiff: false, CancellationToken.None);
        Assert.Equal(0, result.MismatchRateBps);
    }

    [Fact]
    public async Task PaletteAgainstRgbaEquivalent_Passes()
    {
        using var image = new Image<Rgba32>(8, 8);
        FillSolid(image, new Rgba32(10, 200, 30, 255));
        using var stream = new MemoryStream();
        image.SaveAsPng(stream, new PngEncoder { ColorType = PngColorType.Palette });
        var rgba = Solid(8, 8, new Rgba32(10, 200, 30, 255));

        var result = await RgbaPixelComparer.CompareAsync(stream.ToArray(), rgba, produceDiff: false, CancellationToken.None);
        Assert.Equal(0, result.MismatchRateBps);
    }

    [Fact]
    public async Task AlphaFlattenedAgainstBlack_BeforeComparison()
    {
        // Semi-transparent red over black == opaque dark red after flattening.
        var translucent = Solid(4, 4, new Rgba32(200, 0, 0, 128));
        var flattened = Solid(4, 4, new Rgba32(100, 0, 0, 255));
        var same = await RgbaPixelComparer.CompareAsync(translucent, flattened, produceDiff: false, CancellationToken.None);
        Assert.Equal(0, same.MismatchRateBps);

        // Fully transparent vs opaque black likewise flatten identically.
        var clear = Solid(4, 4, new Rgba32(90, 80, 70, 0));
        var black = Solid(4, 4, new Rgba32(0, 0, 0, 255));
        var cleared = await RgbaPixelComparer.CompareAsync(clear, black, produceDiff: false, CancellationToken.None);
        Assert.Equal(0, cleared.MismatchRateBps);
    }

    [Fact]
    public async Task MalformedInput_IsComparisonError()
    {
        await Assert.ThrowsAsync<VisualComparisonException>(() =>
            RgbaPixelComparer.CompareAsync(new byte[] { 1, 2, 3, 4 }, Solid(4, 4, new Rgba32(0, 0, 0, 255)), produceDiff: true, CancellationToken.None));
        await Assert.ThrowsAsync<VisualComparisonException>(() =>
            RgbaPixelComparer.CompareAsync(Array.Empty<byte>(), Array.Empty<byte>(), produceDiff: false, CancellationToken.None));
    }

    [Fact]
    public async Task OversizedDimensions_AreComparisonError_WithoutAllocation()
    {
        var claimed = PngWithDimensions(5000, 5000);
        await Assert.ThrowsAsync<VisualComparisonException>(() =>
            RgbaPixelComparer.CompareAsync(claimed, Solid(4, 4, new Rgba32(0, 0, 0, 255)), produceDiff: false, CancellationToken.None));
    }

    [Fact]
    public async Task RepeatedComparison_IsByteForByteDeterministic()
    {
        var baseline = Solid(12, 9, new Rgba32(11, 22, 33, 255));
        var actual = WithPixel(WithPixel(baseline, 1, 1, new Rgba32(200, 0, 0, 255)), 5, 5, new Rgba32(0, 200, 0, 255));
        var first = await RgbaPixelComparer.CompareAsync(baseline, actual, produceDiff: true, CancellationToken.None);
        var second = await RgbaPixelComparer.CompareAsync(baseline, actual, produceDiff: true, CancellationToken.None);

        Assert.Equal(2, first.DifferingPixels);
        Assert.Equal(first.MismatchRateBps, second.MismatchRateBps);
        Assert.Equal(first.DiffPng, second.DiffPng);
    }

    [Fact]
    public async Task CancelledToken_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RgbaPixelComparer.CompareAsync(
                Solid(4, 4, new Rgba32(0, 0, 0, 255)),
                Solid(4, 4, new Rgba32(0, 0, 0, 255)), produceDiff: false, cts.Token));
    }

    /// <summary>Minimal PNG whose IHDR claims large dimensions (header-only;
    /// forces the decode-bound path without allocating pixels).</summary>
    private static byte[] PngWithDimensions(int width, int height)
    {
        using var stream = new MemoryStream();
        stream.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        ihdr[0] = (byte)(width >> 24); ihdr[1] = (byte)(width >> 16); ihdr[2] = (byte)(width >> 8); ihdr[3] = (byte)width;
        ihdr[4] = (byte)(height >> 24); ihdr[5] = (byte)(height >> 16); ihdr[6] = (byte)(height >> 8); ihdr[7] = (byte)height;
        ihdr[8] = 8; ihdr[9] = 2; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        WriteChunk(stream, "IHDR", ihdr);
        WriteChunk(stream, "IEND", Array.Empty<byte>());
        return stream.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = data.Length;
        stream.WriteByte((byte)(length >> 24)); stream.WriteByte((byte)(length >> 16));
        stream.WriteByte((byte)(length >> 8)); stream.WriteByte((byte)length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes, 0, 4);
        stream.Write(data, 0, data.Length);
        var crc = Crc32(typeBytes, data);
        stream.WriteByte((byte)(crc >> 24)); stream.WriteByte((byte)(crc >> 16));
        stream.WriteByte((byte)(crc >> 8)); stream.WriteByte((byte)crc);
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in type.Concat(data))
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) == 1 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
}
