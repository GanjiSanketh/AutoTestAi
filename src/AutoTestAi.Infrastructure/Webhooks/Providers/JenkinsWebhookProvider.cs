using System.Text.Json;

namespace AutoTestAi.Infrastructure.Webhooks.Providers;

using AutoTestAi.Application.Webhooks;

/// <summary>
/// Jenkins webhook adapter (Phase 3 Slice 3B). There is NO canonical
/// Jenkins→AutoTestAi HMAC protocol, so this adapter does NOT invent one:
/// authentication is a configured bearer-style token compared in constant
/// time (Authorization: Bearer &lt;token&gt;, or the "token" header / query
/// field accepted by common Jenkins HTTP plugins). Delivery identity is OUR
/// application convention — X-Jenkins-Delivery or Idempotency-Key when the
/// configured Jenkins client supplies it, otherwise a deterministic
/// best-effort hash that is NOT guaranteed to deduplicate provider retries.
/// </summary>
public sealed class JenkinsWebhookProvider : ICiWebhookProvider
{
    public const string DeliveryHeader = "X-Jenkins-Delivery";
    public const string IdempotencyHeader = "Idempotency-Key";
    public const string TokenHeader = "token";

    public string ProviderName => CiProviderNames.Jenkins;

    public Task<bool> AuthenticateAsync(
        byte[] rawBody, ICiWebhookHeaders headers, string? secret,
        CiIntegrationConfig config, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(secret))
            return Task.FromResult(false);
        var presented =
            ExtractBearer(headers.Get("Authorization")) ??
            headers.Get(TokenHeader);
        if (string.IsNullOrEmpty(presented))
            return Task.FromResult(false);
        return Task.FromResult(CiWebhookCrypto.FixedTimeEquals(presented.Trim(), secret));
    }

    public string? ExtractDeliveryId(ICiWebhookHeaders headers, string rawBodyPreview)
    {
        var explicitId = headers.Get(DeliveryHeader)?.Trim() ?? headers.Get(IdempotencyHeader)?.Trim();
        if (!string.IsNullOrEmpty(explicitId))
            return explicitId;
        // Best-effort fallback (documented limitation): stable fields hashed.
        // Semantically identical retries without a caller-supplied id may hash
        // differently and are NOT guaranteed to deduplicate.
        try
        {
            using var document = JsonDocument.Parse(rawBodyPreview);
            var root = document.RootElement;
            static string Part(JsonElement e, string name)
                => e.TryGetProperty(name, out var v) ? v.GetRawText() : string.Empty;
            var stable = string.Join("|", new[]
            {
                Part(root, "name"), Part(root, "job_name"), Part(root, "number"),
                Part(root, "build_number"), Part(root, "id"), Part(root, "url"),
                Part(root, "status"), Part(root, "result"),
            });
            if (string.IsNullOrWhiteSpace(stable.Replace("|", string.Empty)))
                return null;
            return "jenkins-fallback-" + CiWebhookCrypto.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(stable))[..32];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string? ExtractEventType(ICiWebhookHeaders headers, string rawBodyPreview)
    {
        // Configured job/build notification: honor an explicit event header,
        // otherwise derive from common payload status/result fields.
        var explicitEvent = headers.Get("X-Jenkins-Event")?.Trim();
        if (!string.IsNullOrEmpty(explicitEvent))
            return explicitEvent;
        try
        {
            using var document = JsonDocument.Parse(rawBodyPreview);
            var root = document.RootElement;
            if (root.TryGetProperty("event", out var e) && e.ValueKind == JsonValueKind.String)
                return e.GetString();
            if (root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String)
                return "build." + s.GetString();
            if (root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String)
                return "build." + r.GetString();
        }
        catch (JsonException)
        {
        }
        return "jenkins.notification";
    }

    public NormalizedCiEvent Normalize(
        Guid integrationId, Guid projectId, string deliveryId, string eventType,
        byte[] rawBody, DateTimeOffset receivedAt, string payloadHash)
    {
        string? branch = null, commit = null, repo = null, build = null, runId = null;
        try
        {
            using var document = JsonDocument.Parse(rawBody);
            var root = document.RootElement;
            static string? Str(JsonElement e, string name)
                => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            repo = Str(root, "job_name") ?? Str(root, "name");
            build = Str(root, "number") ?? Str(root, "build_number") ?? Str(root, "id");
            if (root.TryGetProperty("build", out var b) && b.ValueKind == JsonValueKind.Object)
                build ??= Str(b, "number") ?? Str(b, "id");
            if (root.TryGetProperty("scm", out var scm) && scm.ValueKind == JsonValueKind.Object)
            {
                branch = Str(scm, "branch");
                commit = Str(scm, "commit") ?? Str(scm, "revision");
                repo ??= Str(scm, "url");
            }
            branch ??= Str(root, "branch");
            commit ??= Str(root, "commit") ?? Str(root, "revision") ?? Str(root, "sha");
            runId = build;
        }
        catch (JsonException)
        {
        }
        return new NormalizedCiEvent(
            CiProviderNames.Jenkins, integrationId, projectId, deliveryId, eventType,
            branch, commit, repo, null, build, runId, null, receivedAt, payloadHash);
    }

    private static string? ExtractBearer(string? authorization)
    {
        if (string.IsNullOrWhiteSpace(authorization))
            return null;
        const string scheme = "Bearer ";
        return authorization.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
            ? authorization[scheme.Length..].Trim()
            : null;
    }
}
