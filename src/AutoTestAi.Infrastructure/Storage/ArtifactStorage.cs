using AutoTestAi.Application.Storage;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace AutoTestAi.Infrastructure.Storage;

public sealed class MinioOptions
{
    public const string SectionName = "Minio";
    public string? Endpoint { get; set; }
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }
    public bool UseSsl { get; set; }
    public string Bucket { get; set; } = "autotestai-artifacts";
    public bool Configured =>
        !string.IsNullOrWhiteSpace(Endpoint) &&
        !string.IsNullOrWhiteSpace(AccessKey) &&
        !string.IsNullOrWhiteSpace(SecretKey);
}

public sealed class MinioArtifactStorage : IArtifactStorage
{
    /// <summary>Absolute ceiling for server-side object reads (Slice 3C-4D-2).</summary>
    public const int MaxDownloadBytes = 16 * 1024 * 1024;

    private readonly MinioOptions _options;
    private readonly Lazy<IMinioClient> _client;

    public bool IsConfigured => _options.Configured;

    public MinioArtifactStorage(IOptions<MinioOptions> options)
    {
        _options = options.Value;
        _client = new Lazy<IMinioClient>(() => new MinioClient()
            .WithEndpoint(_options.Endpoint)
            .WithCredentials(_options.AccessKey, _options.SecretKey)
            .WithSSL(_options.UseSsl)
            .Build());
    }

    private void ThrowIfNotConfigured()
    {
        if (!IsConfigured)
            throw new InvalidOperationException(
                "MinIO storage is not configured. Set the Minio section (endpoint/accessKey/secretKey).");
    }

    public async Task UploadAsync(string storageKey, Stream content, string contentType, CancellationToken cancellationToken)
    {
        ThrowIfNotConfigured();
        var args = new PutObjectArgs()
            .WithBucket(_options.Bucket)
            .WithObject(storageKey)
            .WithStreamData(content)
            .WithObjectSize(content.Length)
            .WithContentType(contentType);
        await _client.Value.PutObjectAsync(args, cancellationToken);
    }

    public async Task<bool> CheckConnectivityAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured) return false;
        try
        {
            var args = new BucketExistsArgs().WithBucket(_options.Bucket);
            await _client.Value.BucketExistsAsync(args, cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> GetPresignedDownloadUrlAsync(string storageKey, int expirySeconds, CancellationToken cancellationToken)
    {
        ThrowIfNotConfigured();
        var args = new PresignedGetObjectArgs()
            .WithBucket(_options.Bucket)
            .WithObject(storageKey)
            .WithExpiry(expirySeconds);
        return await _client.Value.PresignedGetObjectAsync(args);
    }

    /// <summary>
    /// Slice 3C-4D-2 server-side bounded read. The byte cap is enforced
    /// while streaming (never a post-hoc check on an unbounded buffer);
    /// missing objects map to the repository NotFound convention; content
    /// is never logged.
    /// </summary>
    public async Task<byte[]> DownloadAsync(string storageKey, int maxBytes, CancellationToken cancellationToken)
    {
        ThrowIfNotConfigured();
        if (string.IsNullOrWhiteSpace(storageKey))
            throw new ArgumentException("Storage key is required.", nameof(storageKey));
        var cap = Math.Clamp(maxBytes, 1, MaxDownloadBytes);
        using var destination = new MemoryStream(Math.Min(cap, 81920));
        var args = new GetObjectArgs()
            .WithBucket(_options.Bucket)
            .WithObject(storageKey)
            .WithCallbackStream(source => CopyCapped(source, destination, cap, storageKey, cancellationToken));
        try
        {
            await _client.Value.GetObjectAsync(args, cancellationToken);
        }
        catch (ObjectNotFoundException)
        {
            throw new Application.Common.NotFoundException("Stored object not found.");
        }
        return destination.ToArray();
    }

    private static void CopyCapped(Stream source, MemoryStream destination, int cap, string storageKey, CancellationToken ct)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int read;
            long total = 0;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                total += read;
                if (total > cap)
                    throw new InvalidOperationException($"Stored object exceeds the {cap}-byte download bound.");
                destination.Write(buffer, 0, read);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
