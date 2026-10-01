using System.Text.Json;

namespace AutoTestAi.Infrastructure.Webhooks.Providers;

using AutoTestAi.Application.Webhooks;

/// <summary>
/// GitHub Actions webhook adapter (Phase 3 Slice 3B). Verified model
/// (docs.github.com): HMAC-SHA256 over the exact raw body in
/// X-Hub-Signature-256 ("sha256=&lt;hex&gt;"), delivery identity from
/// X-GitHub-Delivery, event from X-GitHub-Event.
/// </summary>
public sealed class GithubWebhookProvider : ICiWebhookProvider
{
    public const string DeliveryHeader = "X-GitHub-Delivery";
    public const string EventHeader = "X-GitHub-Event";
    public const string SignatureHeader = "X-Hub-Signature-256";

    public string ProviderName => CiProviderNames.GitHub;

    public Task<bool> AuthenticateAsync(
        byte[] rawBody, ICiWebhookHeaders headers, string? secret,
        CiIntegrationConfig config, CancellationToken ct)
    {
        // Fail closed: unsigned GitHub operation is not supported.
        if (string.IsNullOrEmpty(secret))
            return Task.FromResult(false);
        var signature = headers.Get(SignatureHeader);
        if (!CiWebhookCrypto.TryParseSha256HexSignature(signature, out var expected))
            return Task.FromResult(false);
        var actual = CiWebhookCrypto.HmacSha256(System.Text.Encoding.UTF8.GetBytes(secret), rawBody);
        return Task.FromResult(CiWebhookCrypto.FixedTimeEquals(actual, expected));
    }

    public string? ExtractDeliveryId(ICiWebhookHeaders headers, string rawBodyPreview)
        => headers.Get(DeliveryHeader)?.Trim();

    public string? ExtractEventType(ICiWebhookHeaders headers, string rawBodyPreview)
        => headers.Get(EventHeader)?.Trim();

    public NormalizedCiEvent Normalize(
        Guid integrationId, Guid projectId, string deliveryId, string eventType,
        byte[] rawBody, DateTimeOffset receivedAt, string payloadHash)
    {
        string? branch = null, commit = null, repo = null, pr = null, actor = null, runId = null;
        try
        {
            using var document = JsonDocument.Parse(rawBody);
            var root = document.RootElement;
            static string? Str(JsonElement e, string name)
                => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (root.TryGetProperty("repository", out var repository) && repository.ValueKind == JsonValueKind.Object)
                repo = Str(repository, "full_name") ?? Str(repository, "name");
            if (root.TryGetProperty("sender", out var sender) && sender.ValueKind == JsonValueKind.Object)
                actor = Str(sender, "login");
            if (string.Equals(eventType, "push", StringComparison.OrdinalIgnoreCase))
            {
                var gitRef = Str(root, "ref") ?? string.Empty;
                branch = gitRef.StartsWith("refs/heads/", StringComparison.Ordinal)
                    ? gitRef["refs/heads/".Length..]
                    : gitRef.Length > 0 ? gitRef : null;
                commit = Str(root, "after");
            }
            else if (root.TryGetProperty("pull_request", out var pull) && pull.ValueKind == JsonValueKind.Object)
            {
                pr = pull.TryGetProperty("number", out var n) && n.ValueKind == JsonValueKind.Number
                    ? n.GetRawText() : Str(pull, "number");
                if (pull.TryGetProperty("head", out var head) && head.ValueKind == JsonValueKind.Object)
                {
                    commit = Str(head, "sha");
                    branch = Str(head, "ref");
                    if (head.TryGetProperty("repo", out var headRepo) && headRepo.ValueKind == JsonValueKind.Object)
                        repo ??= Str(headRepo, "full_name");
                }
            }
            if (root.TryGetProperty("workflow_run", out var run) && run.ValueKind == JsonValueKind.Object)
            {
                runId = run.TryGetProperty("id", out var id) ? id.GetRawText().Trim('"') : null;
                commit ??= Str(run, "head_sha");
                branch ??= Str(run, "head_branch");
            }
        }
        catch (JsonException)
        {
            // Defensive: authentication already succeeded; partial fields are acceptable.
        }
        return new NormalizedCiEvent(
            CiProviderNames.GitHub, integrationId, projectId, deliveryId, eventType,
            branch, commit, repo, pr, null, runId, actor, receivedAt, payloadHash);
    }
}
