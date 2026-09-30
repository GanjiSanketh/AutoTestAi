using AutoTestAi.Application.Common;
using AutoTestAi.Application.Secrets;

namespace AutoTestAi.Infrastructure.Secrets;

/// <summary>Fail-closed secret seams when no database is configured.</summary>
public sealed class UnavailableSecretResolver : ISecretResolver
{
    public Task<bool> ExistsAsync(string secretReference, CancellationToken ct)
        => Task.FromResult(false);
    public Task<string> ResolveAsync(string secretReference, CancellationToken ct)
        => throw new NotFoundException("Secret storage is not configured.");
}

public sealed class UnavailableSecretStore : ISecretStore
{
    private static Exception Unavailable() => new InvalidOperationException("Secret storage is not configured.");
    public Task<SecretMetadata> CreateAsync(Guid projectId, Guid environmentId, string name, string value, string? description, CancellationToken ct) => throw Unavailable();
    public Task<SecretMetadata> UpdateAsync(Guid secretId, string? name, string? value, string? description, CancellationToken ct) => throw Unavailable();
    public Task DeleteAsync(Guid secretId, CancellationToken ct) => throw Unavailable();
    public Task<SecretMetadata?> GetMetadataAsync(Guid secretId, CancellationToken ct) => Task.FromResult<SecretMetadata?>(null);
    public Task<IReadOnlyList<SecretMetadata>> ListMetadataAsync(Guid projectId, Guid? environmentId, CancellationToken ct) => Task.FromResult<IReadOnlyList<SecretMetadata>>(Array.Empty<SecretMetadata>());
}
