using System.Security.Cryptography;
using System.Text;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Secrets;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoTestAi.Infrastructure.Secrets;

/// <summary>
/// Encrypted database-backed secret vault (Slice 3A §8). Values are sealed with
/// AES-256-GCM under an externally controlled KEK; only ciphertext, nonce, and
/// key version persist. The same class implements both capabilities, but DI
/// exposes <see cref="ISecretResolver"/> to execution code and
/// <see cref="ISecretStore"/> to management code — never both to one consumer.
/// Later backends (Vault/AWS/Azure) implement the same two interfaces without
/// touching ExecutionEngine.
/// </summary>
public sealed class EfSecretVault : ISecretStore, ISecretResolver
{
    private const string DevKekBase64 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="; // 32 zero bytes, dev only

    private readonly AutoTestAiDbContext _db;
    private readonly SecretVaultOptions _options;
    private readonly ILogger<EfSecretVault> _logger;

    public EfSecretVault(
        AutoTestAiDbContext db,
        IOptions<SecretVaultOptions> options,
        ILogger<EfSecretVault> logger)
    {
        _db = db;
        _options = options.Value;
        _logger = logger;
    }

    // ---------- ISecretResolver (execution path) ----------

    public async Task<bool> ExistsAsync(string secretReference, CancellationToken ct)
    {
        if (!SecretReference.TryParseSecretId(secretReference, out var secretId))
            return false;
        var row = await _db.EnvironmentSecrets.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == secretId, ct);
        return row is not null && !string.IsNullOrEmpty(row.EncryptedValue);
    }

    public async Task<string> ResolveAsync(string secretReference, CancellationToken ct)
    {
        if (!SecretReference.TryParseSecretId(secretReference, out var secretId))
            throw new NotFoundException("Unknown secret reference.");
        var row = await _db.EnvironmentSecrets.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == secretId, ct);
        if (row is null || string.IsNullOrEmpty(row.EncryptedValue) || string.IsNullOrEmpty(row.Nonce))
            throw new NotFoundException("The referenced secret is not available.");
        try
        {
            return Decrypt(row.EncryptedValue, row.Nonce);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Never surface key material or ciphertext; audit-friendly failure.
            _logger.LogWarning("Secret decryption failed for reference {SecretRef}.", secretReference);
            throw new NotFoundException("The referenced secret is not available.");
        }
    }

    // ---------- ISecretStore (management path) ----------

    public async Task<SecretMetadata> CreateAsync(
        Guid projectId, Guid environmentId, string name, string value, string? description, CancellationToken ct)
    {
        var existing = await _db.EnvironmentSecrets
            .FirstOrDefaultAsync(s => s.ProjectId == projectId && s.EnvironmentId == environmentId && s.Name == name, ct);
        if (existing is not null)
            throw new ConflictException($"A secret named '{name}' already exists for this environment.");
        var now = DateTimeOffset.UtcNow;
        var row = new EnvironmentSecret
        {
            ProjectId = projectId,
            EnvironmentId = environmentId,
            Name = name,
            Description = description,
            KeyVersion = _options.KeyVersion,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var (ciphertext, nonce) = Encrypt(value);
        row.EncryptedValue = ciphertext;
        row.Nonce = nonce;
        _db.EnvironmentSecrets.Add(row);
        await _db.SaveChangesAsync(ct);
        row.SecretReference = SecretReference.Create(row.Id);
        await _db.SaveChangesAsync(ct);
        return Map(row);
    }

    public async Task<SecretMetadata> UpdateAsync(
        Guid secretId, string? name, string? value, string? description, CancellationToken ct)
    {
        var row = await _db.EnvironmentSecrets.FirstOrDefaultAsync(s => s.Id == secretId, ct)
            ?? throw new NotFoundException("Secret not found.");
        if (name is not null && !string.Equals(row.Name, name, StringComparison.Ordinal))
        {
            var clash = await _db.EnvironmentSecrets.AnyAsync(
                s => s.Id != secretId && s.ProjectId == row.ProjectId && s.EnvironmentId == row.EnvironmentId && s.Name == name, ct);
            if (clash)
                throw new ConflictException($"A secret named '{name}' already exists for this environment.");
            row.Name = name;
        }
        if (value is not null)
        {
            var (ciphertext, nonce) = Encrypt(value);
            row.EncryptedValue = ciphertext;
            row.Nonce = nonce;
            row.KeyVersion = _options.KeyVersion;
        }
        if (description is not null)
            row.Description = description;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Map(row);
    }

    public async Task DeleteAsync(Guid secretId, CancellationToken ct)
    {
        var row = await _db.EnvironmentSecrets.FirstOrDefaultAsync(s => s.Id == secretId, ct)
            ?? throw new NotFoundException("Secret not found.");
        _db.EnvironmentSecrets.Remove(row);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<SecretMetadata?> GetMetadataAsync(Guid secretId, CancellationToken ct)
    {
        var row = await _db.EnvironmentSecrets.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == secretId, ct);
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<SecretMetadata>> ListMetadataAsync(Guid projectId, Guid? environmentId, CancellationToken ct)
    {
        var query = _db.EnvironmentSecrets.AsNoTracking().Where(s => s.ProjectId == projectId);
        if (environmentId.HasValue)
            query = query.Where(s => s.EnvironmentId == environmentId.Value);
        var rows = await query.OrderBy(s => s.Name).ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    // ---------- crypto ----------

    private byte[] Kek()
    {
        if (!string.IsNullOrWhiteSpace(_options.KekBase64))
        {
            var raw = Convert.FromBase64String(_options.KekBase64.Trim());
            if (raw.Length != 32)
                throw new InvalidOperationException("Secrets:KekBase64 must decode to 32 bytes (AES-256).");
            return raw;
        }
        // Explicit non-production guard: no silent insecure production provider.
        // IHostEnvironment would add a package dependency; the environment name
        // is read from the standard host variables instead.
        var envName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        if (string.Equals(envName, "Production", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Secrets:KekBase64 is required in production. Refusing to seal secrets with a development key.");
        _logger.LogWarning("Secrets:KekBase64 is not configured; using the non-production development key. Never use this outside local development/testing.");
        return Convert.FromBase64String(DevKekBase64);
    }

    private (string Ciphertext, string Nonce) Encrypt(string plaintext)
    {
        var key = Kek();
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        using (var aes = new AesGcm(key, AesGcm.TagByteSizes.MaxSize))
            aes.Encrypt(nonce, plainBytes, cipherBytes, tag);
        var combined = new byte[cipherBytes.Length + tag.Length];
        Buffer.BlockCopy(cipherBytes, 0, combined, 0, cipherBytes.Length);
        Buffer.BlockCopy(tag, 0, combined, cipherBytes.Length, tag.Length);
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(plainBytes);
        return (Convert.ToBase64String(combined), Convert.ToBase64String(nonce));
    }

    private string Decrypt(string ciphertextBase64, string nonceBase64)
    {
        var key = Kek();
        try
        {
            var combined = Convert.FromBase64String(ciphertextBase64);
            var nonce = Convert.FromBase64String(nonceBase64);
            const int tagSize = 16;
            if (combined.Length < tagSize)
                throw new CryptographicException("Ciphertext too short.");
            var cipherBytes = new byte[combined.Length - tagSize];
            var tag = new byte[tagSize];
            Buffer.BlockCopy(combined, 0, cipherBytes, 0, cipherBytes.Length);
            Buffer.BlockCopy(combined, cipherBytes.Length, tag, 0, tagSize);
            var plainBytes = new byte[cipherBytes.Length];
            using (var aes = new AesGcm(key, tagSize))
                aes.Decrypt(nonce, cipherBytes, tag, plainBytes);
            var result = Encoding.UTF8.GetString(plainBytes);
            CryptographicOperations.ZeroMemory(plainBytes);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static SecretMetadata Map(EnvironmentSecret row)
        => new(row.Id, row.ProjectId, row.EnvironmentId, row.Name, row.Description,
            row.SecretReference, !string.IsNullOrEmpty(row.EncryptedValue),
            row.RowVersion, row.CreatedAt, row.UpdatedAt);
}
