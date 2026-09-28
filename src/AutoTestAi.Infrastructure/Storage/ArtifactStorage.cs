using AutoTestAi.Application.Storage;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;

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
}
