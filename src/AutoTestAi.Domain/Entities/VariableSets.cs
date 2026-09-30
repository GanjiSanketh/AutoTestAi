using AutoTestAi.Domain.Common;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Domain.Entities;

/// <summary>
/// Named variable collection scoped to Project, Environment, or Suite (Slice 3A).
/// Variables payload is JSON: key -&gt; { value } | { secretRef }.
/// Raw secret values are never stored here.
/// </summary>
public sealed class VariableSet : EntityBase
{
    public Guid ProjectId { get; set; }

    public VariableScopeType ScopeType { get; set; } = VariableScopeType.Project;

    /// <summary>Null for Project scope; EnvironmentId or SuiteId otherwise.</summary>
    public Guid? ScopeId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Raw JSON object of variable entries (validated at application layer).</summary>
    public string VariablesJson { get; set; } = "{}";

    /// <summary>Optimistic concurrency token.</summary>
    public byte[]? RowVersion { get; set; }
}

/// <summary>
/// Secret metadata (Slice 3A). The plaintext value is never stored in these
/// columns; the encrypted payload lives in <see cref="EncryptedValue"/> with
/// <see cref="Nonce"/> and <see cref="KeyVersion"/> (AES-256-GCM).
/// </summary>
public sealed class EnvironmentSecret : EntityBase
{
    public Guid ProjectId { get; set; }

    public Guid EnvironmentId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Opaque reference, e.g. env_secret:&lt;id&gt;. Safe to persist/emit.</summary>
    public string SecretReference { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>Base64 AES-GCM ciphertext of the secret value. Never logged.</summary>
    public string? EncryptedValue { get; set; }

    /// <summary>Base64 AES-GCM nonce.</summary>
    public string? Nonce { get; set; }

    /// <summary>Key version / key id for rotation.</summary>
    public string KeyVersion { get; set; } = "v1";

    public byte[]? RowVersion { get; set; }
}

/// <summary>
/// Per-execution override envelope (Slice 3A). Persisted at start so the
/// Temporal activity can reload it by execution id. Contains only plain
/// values and secret references — never plaintext secret values.
/// </summary>
public sealed class ExecutionVariables
{
    public Guid ExecutionId { get; set; }

    public Guid ProjectId { get; set; }

    public Guid? SuiteId { get; set; }

    public Guid? EnvironmentId { get; set; }

    /// <summary>JSON object of plain key-&gt;value overrides.</summary>
    public string VariableOverridesJson { get; set; } = "{}";

    /// <summary>JSON object of key-&gt;secretRef overrides.</summary>
    public string SecretRefOverridesJson { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
