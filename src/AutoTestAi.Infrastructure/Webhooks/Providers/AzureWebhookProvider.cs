using System.Text;
using System.Text.Json;

namespace AutoTestAi.Infrastructure.Webhooks.Providers;

using AutoTestAi.Application.Webhooks;

/// <summary>
/// Azure Pipelines service-hook adapter (Phase 3 Slice 3B). Azure DevOps
/// service hooks provide NO native HMAC signature, so this adapter does NOT
/// claim one: authentication is HTTP Basic with a configured username
/// (non-secret configuration) and password (Slice 3A secret reference),
/// validated over the required HTTPS deployment. Delivery identity prefers
/// notificationId, falling back to subscriptionId:notificationId.
/// </summary>
public sealed class AzureWebhookProvider : ICiWebhookProvider
{
    public string ProviderName => CiProviderNames.Azure;

    public Task<bool> AuthenticateAsync(
        byte[] rawBody, ICiWebhookHeaders headers, string? secret,
        CiIntegrationConfig config, CancellationToken ct)
    {
        // Both halves required: configured username + resolvable password.
        if (string.IsNullOrEmpty(secret) || string.IsNullOrWhiteSpace(config.Username))
            return Task.FromResult(false);
        var presented = ParseBasic(headers.Get("Authorization"));
        if (presented is null)
            return Task.FromResult(false);
        var (username, password) = presented.Value;
        if (!string.Equals(username, config.Username.Trim(), StringComparison.Ordinal))
            return Task.FromResult(false);
        return Task.FromResult(CiWebhookCrypto.FixedTimeEquals(password, secret));
    }

    public string? ExtractDeliveryId(ICiWebhookHeaders headers, string rawBodyPreview)
    {
        try
        {
            using var document = JsonDocument.Parse(rawBodyPreview);
            var root = document.RootElement;
            static string? Str(JsonElement e, string name)
                => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var notificationId = Str(root, "notificationId");
            var subscriptionId = Str(root, "subscriptionId");
            if (!string.IsNullOrWhiteSpace(notificationId))
                return string.IsNullOrWhiteSpace(subscriptionId)
                    ? notificationId.Trim()
                    : $"{subscriptionId.Trim()}:{notificationId.Trim()}";
        }
        catch (JsonException)
        {
        }
        return null;
    }

    public string? ExtractEventType(ICiWebhookHeaders headers, string rawBodyPreview)
    {
        try
        {
            using var document = JsonDocument.Parse(rawBodyPreview);
            var root = document.RootElement;
            if (root.TryGetProperty("eventType", out var e) && e.ValueKind == JsonValueKind.String)
                return e.GetString()?.Trim();
        }
        catch (JsonException)
        {
        }
        return null;
    }

    public NormalizedCiEvent Normalize(
        Guid integrationId, Guid projectId, string deliveryId, string eventType,
        byte[] rawBody, DateTimeOffset receivedAt, string payloadHash)
    {
        string? branch = null, commit = null, repo = null, build = null, runId = null, actor = null;
        try
        {
            using var document = JsonDocument.Parse(rawBody);
            var root = document.RootElement;
            static string? Str(JsonElement e, string name)
                => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (root.TryGetProperty("resource", out var resource) && resource.ValueKind == JsonValueKind.Object)
            {
                if (resource.TryGetProperty("repository", out var repository) && repository.ValueKind == JsonValueKind.Object)
                    repo = Str(repository, "name") ?? Str(repository, "remoteUrl");
                repo ??= Str(resource, "repository");
                branch = CleanRef(Str(resource, "sourceBranch") ?? Str(resource, "refName"));
                commit = Str(resource, "sourceVersion") ?? Str(resource, "commitId");
                if (resource.TryGetProperty("id", out var id))
                    build = id.ValueKind == JsonValueKind.String ? id.GetString() : id.GetRawText().Trim('"');
                if (resource.TryGetProperty("requestedFor", out var requestedFor) &&
                    requestedFor.ValueKind == JsonValueKind.Object)
                    actor = Str(requestedFor, "displayName") ?? Str(requestedFor, "uniqueName");
            }
            runId = build;
        }
        catch (JsonException)
        {
        }
        return new NormalizedCiEvent(
            CiProviderNames.Azure, integrationId, projectId, deliveryId, eventType,
            branch, commit, repo, null, build, runId, actor, receivedAt, payloadHash);
    }

    private static (string Username, string Password)? ParseBasic(string? authorization)
    {
        if (string.IsNullOrWhiteSpace(authorization))
            return null;
        const string scheme = "Basic ";
        if (!authorization.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[scheme.Length..].Trim()));
            var separator = decoded.IndexOf(':');
            if (separator <= 0)
                return null;
            return (decoded[..separator], decoded[(separator + 1)..]);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? CleanRef(string? gitRef)
    {
        if (string.IsNullOrEmpty(gitRef))
            return null;
        return gitRef.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? gitRef["refs/heads/".Length..]
            : gitRef;
    }
}
