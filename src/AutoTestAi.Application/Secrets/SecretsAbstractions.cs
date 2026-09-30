namespace AutoTestAi.Application.Secrets;

/// <summary>
/// Opaque secret pointer (Slice 3A). Only this string may be persisted,
/// logged (as an identifier), or sent over SignalR/Temporal. The plaintext
/// value is never embedded in a reference.
/// Format: env_secret:&lt;guid&gt; (v1). Future backends may add vault: prefixes.
/// </summary>
public static class SecretReference
{
    public const string Prefix = "env_secret:";
    public const int MaxLength = 256;

    public static bool IsValid(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return false;
        var trimmed = reference.Trim();
        if (trimmed.Length > MaxLength) return false;
        if (!trimmed.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        return Guid.TryParse(trimmed[Prefix.Length..], out _);
    }

    public static string Create(Guid secretId)
        => $"{Prefix}{secretId:D}";

    public static bool TryParseSecretId(string? reference, out Guid secretId)
    {
        secretId = Guid.Empty;
        if (!IsValid(reference)) return false;
        return Guid.TryParse(reference!.Trim()[Prefix.Length..], out secretId);
    }
}

/// <summary>
/// Read-only secret capability (Slice 3A §7). Execution code may depend only
/// on this interface — never on <see cref="ISecretStore"/>.
/// </summary>
public interface ISecretResolver
{
    Task<bool> ExistsAsync(string secretReference, CancellationToken ct);
    Task<string> ResolveAsync(string secretReference, CancellationToken ct);
}

/// <summary>
/// Administrative secret-management capability (Slice 3A §7). Never injected
/// into ExecutionEngine, grid dispatch, workflow activities, or publishers.
/// </summary>
public interface ISecretStore
{
    Task<SecretMetadata> CreateAsync(Guid projectId, Guid environmentId, string name, string value, string? description, CancellationToken ct);
    Task<SecretMetadata> UpdateAsync(Guid secretId, string? name, string? value, string? description, CancellationToken ct);
    Task DeleteAsync(Guid secretId, CancellationToken ct);
    Task<SecretMetadata?> GetMetadataAsync(Guid secretId, CancellationToken ct);
    Task<IReadOnlyList<SecretMetadata>> ListMetadataAsync(Guid projectId, Guid? environmentId, CancellationToken ct);
}

/// <summary>Secret metadata DTO. Never carries the plaintext value.</summary>
public sealed record SecretMetadata(
    Guid Id,
    Guid ProjectId,
    Guid EnvironmentId,
    string Name,
    string? Description,
    string SecretReference,
    bool HasValue,
    byte[]? RowVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
