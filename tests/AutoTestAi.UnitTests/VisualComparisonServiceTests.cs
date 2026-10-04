using AutoTestAi.Application.Mobile;
using AutoTestAi.Application.Storage;
using AutoTestAi.Application.Visual;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AutoTestAi.UnitTests;

/// <summary>Slice 3C-4D-2: visual comparison orchestration — baseline
/// resolution, project isolation, threshold semantics, skip-vs-mismatch
/// discipline, storage failure mapping, cancellation.</summary>
public sealed class VisualComparisonServiceTests
{
    private static readonly Guid ProjectA = Guid.NewGuid();

    private sealed class FakeBaselineStore : IVisualBaselineStore
    {
        public readonly Dictionary<Guid, VisualBaseline> Rows = new();
        public Task<VisualBaseline?> GetByIdAsync(Guid id, CancellationToken ct)
            => Task.FromResult(Rows.TryGetValue(id, out var b) ? b : null);
        public Task<VisualBaseline?> FindActiveAsync(Guid testCaseVersionId, int stepOrder, CancellationToken ct)
            => Task.FromResult(Rows.Values.FirstOrDefault(b =>
                b.TestCaseVersionId == testCaseVersionId && b.StepOrder == stepOrder &&
                b.Status == VisualBaselineStatus.Active));
        public Task<VisualBaseline?> FindCandidateAsync(Guid testCaseVersionId, int stepOrder, string sha256, CancellationToken ct)
            => Task.FromResult<VisualBaseline?>(null);
        public Task<IReadOnlyList<VisualBaseline>> ListAsync(Guid projectId, Guid? testCaseVersionId, VisualBaselineStatus? status, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<VisualBaseline>>(Array.Empty<VisualBaseline>());
        public Task AddAsync(VisualBaseline baseline, CancellationToken ct) { Rows[baseline.Id] = baseline; return Task.CompletedTask; }
        public Task DeleteAsync(VisualBaseline baseline, CancellationToken ct) => Task.CompletedTask;
        public Task SaveChangesAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeArtifacts : IArtifactStorage
    {
        public bool IsConfigured { get; set; } = true;
        public readonly Dictionary<string, byte[]> Objects = new();
        public readonly List<string> Downloads = new();
        public bool ThrowOnDownload { get; set; }
        public Task UploadAsync(string key, Stream content, string contentType, CancellationToken ct) => Task.CompletedTask;
        public Task<string> GetPresignedDownloadUrlAsync(string key, int expirySeconds, CancellationToken ct)
            => Task.FromResult($"https://artifacts.example/{key}");
        public Task<bool> CheckConnectivityAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<byte[]> DownloadAsync(string key, int maxBytes, CancellationToken ct)
        {
            Downloads.Add(key);
            if (ThrowOnDownload) throw new InvalidOperationException("Storage is down.");
            if (!Objects.TryGetValue(key, out var bytes))
                throw new AutoTestAi.Application.Common.NotFoundException("Stored object not found.");
            if (bytes.Length > maxBytes)
                throw new InvalidOperationException("Stored object exceeds the download bound.");
            return Task.FromResult(bytes);
        }
    }

    private static byte[] Solid(int width, int height, Rgba32 color)
    {
        using var image = new Image<Rgba32>(width, height);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
                accessor.GetRowSpan(y).Fill(color);
        });
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    private static readonly byte[] White10 = Solid(10, 10, new Rgba32(255, 255, 255, 255));

    private sealed record Harness(
        VisualComparisonService Service,
        FakeBaselineStore Baselines,
        FakeArtifacts Artifacts,
        Guid VersionId);

    private static Harness Create(VisualBaseline? baseline = null, byte[]? baselineBytes = null)
    {
        var baselines = new FakeBaselineStore();
        var artifacts = new FakeArtifacts();
        var versionId = Guid.NewGuid();
        if (baseline is not null)
        {
            baseline.TestCaseVersionId = versionId;
            baselines.Rows[baseline.Id] = baseline;
            if (baselineBytes is not null)
                artifacts.Objects[baseline.StorageKey] = baselineBytes;
        }
        var service = new VisualComparisonService(
            baselines, artifacts, NullLogger<VisualComparisonService>.Instance);
        return new Harness(service, baselines, artifacts, versionId);
    }

    private static VisualBaseline ActiveBaseline(Guid project, string key = "projects/p/baseline.png", int? threshold = null)
        => new()
        {
            ProjectId = project,
            TestCaseId = Guid.NewGuid(),
            TestCaseVersionId = Guid.NewGuid(),
            StepOrder = 3,
            Status = VisualBaselineStatus.Active,
            StorageKey = key,
            Sha256 = new string('a', 64),
            Width = 10,
            Height = 10,
            ContentType = "image/png",
            MismatchThresholdBps = threshold,
        };

    [Fact]
    public async Task NoActiveBaseline_ReturnsNull()
    {
        var h = Create();
        var outcome = await h.Service.CompareCheckpointAsync(
            ProjectA, h.VersionId, 3, White10, CancellationToken.None);
        Assert.Null(outcome);
        Assert.Empty(h.Artifacts.Downloads);
    }

    [Fact]
    public async Task ProjectMismatch_ReturnsNull_WithoutReadingBytes()
    {
        var baseline = ActiveBaseline(Guid.NewGuid());
        var h = Create(baseline, White10);
        var outcome = await h.Service.CompareCheckpointAsync(
            ProjectA, h.VersionId, 3, White10, CancellationToken.None);
        Assert.Null(outcome);
        Assert.Empty(h.Artifacts.Downloads);
    }

    [Fact]
    public async Task ExplicitThreshold_Respected()
    {
        var h = Create(ActiveBaseline(ProjectA, threshold: 0), White10);
        var changed = (byte[])White10.Clone();
        using (var image = Image.Load<Rgba32>(changed))
        {
            image[0, 0] = new Rgba32(0, 0, 0, 255);
            using var stream = new MemoryStream();
            image.SaveAsPng(stream);
            changed = stream.ToArray();
        }
        var outcome = await h.Service.CompareCheckpointAsync(
            ProjectA, h.VersionId, 3, changed, CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.True(outcome!.IsMismatch);
        Assert.Equal(0, outcome.ThresholdBps);
        Assert.Equal(100, outcome.MismatchRateBps);
        Assert.NotNull(outcome.DiffPng);
    }

    [Fact]
    public async Task DefaultThreshold_IsTenBps()
    {
        // 100x100 with 1 differing pixel = exactly 1 bps: passes at default 10.
        using var image = new Image<Rgba32>(100, 100);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
                accessor.GetRowSpan(y).Fill(new Rgba32(0, 0, 0, 255));
        });
        image[0, 0] = new Rgba32(255, 255, 255, 255);
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        var oneBps = stream.ToArray();

        using var baselineImage = new Image<Rgba32>(100, 100);
        baselineImage.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
                accessor.GetRowSpan(y).Fill(new Rgba32(0, 0, 0, 255));
        });
        using var baselineStream = new MemoryStream();
        baselineImage.SaveAsPng(baselineStream);

        var h = Create(ActiveBaseline(ProjectA), baselineStream.ToArray());
        var outcome = await h.Service.CompareCheckpointAsync(
            ProjectA, h.VersionId, 3, oneBps, CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.False(outcome!.IsMismatch);
        Assert.Equal(10, outcome.ThresholdBps);
        Assert.Equal(1, outcome.MismatchRateBps);
    }

    [Fact]
    public async Task OutOfRangeThreshold_SkipsComparison()
    {
        foreach (var threshold in new int?[] { -1, 10001 })
        {
            var h = Create(ActiveBaseline(ProjectA, threshold: threshold), White10);
            var outcome = await h.Service.CompareCheckpointAsync(
                ProjectA, h.VersionId, 3, White10, CancellationToken.None);
            Assert.Null(outcome);
        }
    }

    [Fact]
    public async Task UnconfiguredStorage_SkipsComparison()
    {
        var h = Create(ActiveBaseline(ProjectA), White10);
        h.Artifacts.IsConfigured = false;
        var outcome = await h.Service.CompareCheckpointAsync(
            ProjectA, h.VersionId, 3, White10, CancellationToken.None);
        Assert.Null(outcome);
        Assert.Empty(h.Artifacts.Downloads);
    }

    [Fact]
    public async Task MissingBaselineObject_SkipsComparison()
    {
        var h = Create(ActiveBaseline(ProjectA)); // no bytes stored
        var outcome = await h.Service.CompareCheckpointAsync(
            ProjectA, h.VersionId, 3, White10, CancellationToken.None);
        Assert.Null(outcome);
    }

    [Fact]
    public async Task StorageFailure_SkipsComparison()
    {
        var h = Create(ActiveBaseline(ProjectA), White10);
        h.Artifacts.ThrowOnDownload = true;
        var outcome = await h.Service.CompareCheckpointAsync(
            ProjectA, h.VersionId, 3, White10, CancellationToken.None);
        Assert.Null(outcome);
    }

    [Fact]
    public async Task CorruptBaseline_SkipsComparison()
    {
        var h = Create(ActiveBaseline(ProjectA), new byte[] { 1, 2, 3, 4 });
        var outcome = await h.Service.CompareCheckpointAsync(
            ProjectA, h.VersionId, 3, White10, CancellationToken.None);
        Assert.Null(outcome);
    }

    [Fact]
    public async Task CorruptActual_SkipsComparison()
    {
        var h = Create(ActiveBaseline(ProjectA), White10);
        var outcome = await h.Service.CompareCheckpointAsync(
            ProjectA, h.VersionId, 3, new byte[] { 9, 9, 9 }, CancellationToken.None);
        Assert.Null(outcome);
    }

    [Fact]
    public async Task OversizedActual_SkipsWithoutDownload()
    {
        var h = Create(ActiveBaseline(ProjectA), White10);
        var outcome = await h.Service.CompareCheckpointAsync(
            ProjectA, h.VersionId, 3, new byte[4 * 1024 * 1024 + 1], CancellationToken.None);
        Assert.Null(outcome);
        Assert.Empty(h.Artifacts.Downloads);
    }

    [Fact]
    public async Task Match_PopulatesOutcome_WithoutDiff()
    {
        var h = Create(ActiveBaseline(ProjectA), White10);
        var outcome = await h.Service.CompareCheckpointAsync(
            ProjectA, h.VersionId, 3, White10, CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.False(outcome!.IsMismatch);
        Assert.False(outcome.DimensionMismatch);
        Assert.Equal(0, outcome.MismatchRateBps);
        Assert.Equal("v1-rgba-bps", outcome.AlgorithmVersion);
        Assert.Equal(10, outcome.ActualWidth);
        Assert.Null(outcome.DiffPng);
        Assert.True(outcome.DurationMs >= 0);
    }

    [Fact]
    public async Task DimensionMismatch_IsMismatchRegardlessOfThreshold()
    {
        var h = Create(ActiveBaseline(ProjectA, threshold: 10000), White10);
        var tall = Solid(10, 11, new Rgba32(255, 255, 255, 255));
        var outcome = await h.Service.CompareCheckpointAsync(
            ProjectA, h.VersionId, 3, tall, CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.True(outcome!.IsMismatch);
        Assert.True(outcome.DimensionMismatch);
        Assert.NotNull(outcome.DiffPng);
    }

    [Fact]
    public async Task CancelledToken_Propagates()
    {
        var h = Create(ActiveBaseline(ProjectA), White10);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            h.Service.CompareCheckpointAsync(ProjectA, h.VersionId, 3, White10, cts.Token));
    }
}
