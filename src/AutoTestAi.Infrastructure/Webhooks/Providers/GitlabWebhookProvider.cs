using System.Text.Json;

namespace AutoTestAi.Infrastructure.Webhooks.Providers;

using AutoTestAi.Application.Webhooks;

/// <summary>
/// GitLab CI webhook adapter (Phase 3 Slice 3B). Approved primary mechanism
/// (docs.gitlab.com): plaintext X-Gitlab-Token comparison against the
/// configured secret. Delivery identity from X-Gitlab-Event-UUID, event from
/// X-Gitlab-Event. The optional HMAC signing-token mechanism is deferred.
/// </summary>
public sealed class GitlabWebhookProvider : ICiWebhookProvider
{
    public const string TokenHeader = "X-Gitlab-Token";
    public const string EventUuidHeader = "X-Gitlab-Event-UUID";
    public const string EventHeader = "X-Gitlab-Event";

    public string ProviderName => CiProviderNames.GitLab;

    public Task<bool> AuthenticateAsync(
        byte[] rawBody, ICiWebhookHeaders headers, string? secret,
        CiIntegrationConfig config, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(secret))
            return Task.FromResult(false);
        var token = headers.Get(TokenHeader);
        if (string.IsNullOrEmpty(token))
            return Task.FromResult(false);
        return Task.FromResult(CiWebhookCrypto.FixedTimeEquals(token.Trim(), secret));
    }

    public string? ExtractDeliveryId(ICiWebhookHeaders headers, string rawBodyPreview)
        => headers.Get(EventUuidHeader)?.Trim();

    public string? ExtractEventType(ICiWebhookHeaders headers, string rawBodyPreview)
        => headers.Get(EventHeader)?.Trim();

    public NormalizedCiEvent Normalize(
        Guid integrationId, Guid projectId, string deliveryId, string eventType,
        byte[] rawBody, DateTimeOffset receivedAt, string payloadHash)
    {
        string? branch = null, commit = null, repo = null, pr = null, actor = null, build = null;
        try
        {
            using var document = JsonDocument.Parse(rawBody);
            var root = document.RootElement;
            static string? Str(JsonElement e, string name)
                => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (root.TryGetProperty("project", out var project) && project.ValueKind == JsonValueKind.Object)
                repo = Str(project, "path_with_namespace") ?? Str(project, "name");
            if (root.TryGetProperty("user_username", out var user) && user.ValueKind == JsonValueKind.String)
                actor = user.GetString();
            var gitRef = Str(root, "ref");
            if (!string.IsNullOrEmpty(gitRef) && gitRef.StartsWith("refs/heads/", StringComparison.Ordinal))
                branch = gitRef["refs/heads/".Length..];
            else if (root.TryGetProperty("object_attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
            {
                branch ??= Str(attrs, "target_branch") ?? Str(attrs, "source_branch");
                if (attrs.TryGetProperty("last_commit", out var last) && last.ValueKind == JsonValueKind.Object)
                    commit ??= Str(last, "id");
                pr = attrs.TryGetProperty("iid", out var iid) ? iid.GetRawText().Trim('"') : null;
                build = attrs.TryGetProperty("id", out var pipelineId) ? pipelineId.GetRawText().Trim('"') : null;
            }
            commit ??= Str(root, "checkout_sha") ?? Str(root, "after");
            if (root.TryGetProperty("build_id", out var buildId))
                build ??= buildId.GetRawText().Trim('"');
        }
        catch (JsonException)
        {
        }
        return new NormalizedCiEvent(
            CiProviderNames.GitLab, integrationId, projectId, deliveryId, eventType,
            branch, commit, repo, pr, build, build, actor, receivedAt, payloadHash);
    }
}
