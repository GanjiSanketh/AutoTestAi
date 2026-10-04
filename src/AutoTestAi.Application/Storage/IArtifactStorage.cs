namespace AutoTestAi.Application.Storage;

/// <summary>
/// Artifact storage abstraction (screenshots, videos, traces, reports).
/// PostgreSQL stores only metadata/references; bytes live in MinIO/S3.
/// Implemented in Infrastructure; the interface lives here so Application
/// services can persist artifacts and mint download URLs without touching
/// storage SDKs or credentials.
/// </summary>
public interface IArtifactStorage
{
    bool IsConfigured { get; }
    Task UploadAsync(string storageKey, Stream content, string contentType, CancellationToken cancellationToken);
    Task<string> GetPresignedDownloadUrlAsync(string storageKey, int expirySeconds, CancellationToken cancellationToken);
    Task<bool> CheckConnectivityAsync(CancellationToken cancellationToken);
    /// <summary>
    /// Server-side bounded object read by server-controlled key (Slice 3C-4D-2).
    /// Never uses browser-facing presigned URLs. Enforces maxBytes while
    /// downloading; missing objects surface as NotFoundException; transport
    /// failures propagate; cancellation propagates; content is never logged.
    /// </summary>
    Task<byte[]> DownloadAsync(string storageKey, int maxBytes, CancellationToken cancellationToken);
}
