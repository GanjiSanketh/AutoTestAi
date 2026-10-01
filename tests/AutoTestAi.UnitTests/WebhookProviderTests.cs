using System.Text;
using AutoTestAi.Application.Webhooks;
using AutoTestAi.Infrastructure.Webhooks.Providers;

namespace AutoTestAi.UnitTests;

/// <summary>Slice 3B: provider-specific webhook authentication and normalization.
/// No live provider accounts — deterministic vectors only.</summary>
public sealed class WebhookProviderTests
{
    private sealed class Headers : ICiWebhookHeaders
    {
        private readonly Dictionary<string, string> _values;
        public Headers(Dictionary<string, string> values) => _values = values;
        public string? Get(string name)
        {
            foreach (var (key, value) in _values)
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                    return value;
            return null;
        }
    }

    private static string GithubSignature(string secret, byte[] body)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();
    }

    // ---------- shared crypto ----------

    [Fact]
    public void Crypto_RejectsMalformedSignatures()
    {
        Assert.False(CiWebhookCrypto.TryParseSha256HexSignature(null, out _));
        Assert.False(CiWebhookCrypto.TryParseSha256HexSignature("sha1=abc", out _));
        Assert.False(CiWebhookCrypto.TryParseSha256HexSignature("sha256=xyz", out _));
        Assert.False(CiWebhookCrypto.TryParseSha256HexSignature("sha256=abcd", out _));
        Assert.True(CiWebhookCrypto.TryParseSha256HexSignature("sha256=" + new string('a', 64), out var sig));
        Assert.Equal(32, sig.Length);
    }

    [Fact]
    public void Crypto_FixedTimeEquals_LengthMismatchIsFalse()
    {
        Assert.False(CiWebhookCrypto.FixedTimeEquals("abc", "abcd"));
        Assert.False(CiWebhookCrypto.FixedTimeEquals(null, "abc"));
        Assert.True(CiWebhookCrypto.FixedTimeEquals("secret-token", "secret-token"));
        Assert.False(CiWebhookCrypto.FixedTimeEquals("secret-token", "secret-tokem"));
    }

    // ---------- GitHub ----------

    [Fact]
    public async Task Github_ValidHmacAuthenticates()
    {
        var provider = new GithubWebhookProvider();
        var body = Encoding.UTF8.GetBytes("""{"ref":"refs/heads/main"}""");
        var headers = new Headers(new Dictionary<string, string>
        {
            ["X-Hub-Signature-256"] = GithubSignature("s3cret", body),
            ["X-GitHub-Delivery"] = "72d3162e-cc78-11e3-81ab-4c9367dc0958",
            ["X-GitHub-Event"] = "push",
        });
        Assert.True(await provider.AuthenticateAsync(body, headers, "s3cret", CiIntegrationConfig.Empty, CancellationToken.None));
        Assert.Equal("72d3162e-cc78-11e3-81ab-4c9367dc0958", provider.ExtractDeliveryId(headers, string.Empty));
        Assert.Equal("push", provider.ExtractEventType(headers, string.Empty));
    }

    [Fact]
    public async Task Github_InvalidHmacIsRejected()
    {
        var provider = new GithubWebhookProvider();
        var body = Encoding.UTF8.GetBytes("""{"ref":"refs/heads/main"}""");
        var headers = new Headers(new Dictionary<string, string>
        {
            ["X-Hub-Signature-256"] = GithubSignature("wrong", body),
        });
        Assert.False(await provider.AuthenticateAsync(body, headers, "s3cret", CiIntegrationConfig.Empty, CancellationToken.None));
        Assert.False(await provider.AuthenticateAsync(body, new Headers(new Dictionary<string, string>()), "s3cret", CiIntegrationConfig.Empty, CancellationToken.None));
        Assert.False(await provider.AuthenticateAsync(body, headers, null, CiIntegrationConfig.Empty, CancellationToken.None));
        Assert.False(await provider.AuthenticateAsync(body, headers, string.Empty, CiIntegrationConfig.Empty, CancellationToken.None));
    }

    [Fact]
    public void Github_NormalizesPushAndPullRequest()
    {
        var provider = new GithubWebhookProvider();
        var push = Encoding.UTF8.GetBytes("""{"ref":"refs/heads/main","after":"abc123","repository":{"full_name":"octo/repo"},"sender":{"login":"octocat"}}""");
        var pushEvent = provider.Normalize(Guid.NewGuid(), Guid.NewGuid(), "d1", "push", push, DateTimeOffset.UtcNow, "hash");
        Assert.Equal("main", pushEvent.Branch);
        Assert.Equal("abc123", pushEvent.CommitSha);
        Assert.Equal("octo/repo", pushEvent.Repository);
        Assert.Equal("octocat", pushEvent.Actor);

        var pr = Encoding.UTF8.GetBytes("""{"pull_request":{"number":42,"head":{"sha":"def456","ref":"feature/x","repo":{"full_name":"octo/repo"}}}}""");
        var prEvent = provider.Normalize(Guid.NewGuid(), Guid.NewGuid(), "d2", "pull_request", pr, DateTimeOffset.UtcNow, "hash");
        Assert.Equal("42", prEvent.PullRequestNumber);
        Assert.Equal("def456", prEvent.CommitSha);
        Assert.Equal("feature/x", prEvent.Branch);
    }

    // ---------- GitLab ----------

    [Fact]
    public async Task Gitlab_ValidTokenAuthenticates()
    {
        var provider = new GitlabWebhookProvider();
        var headers = new Headers(new Dictionary<string, string>
        {
            ["X-Gitlab-Token"] = "my-secret-token",
            ["X-Gitlab-Event-UUID"] = "13792a34-cac6-4fda-95a8-c58e00a3954e",
            ["X-Gitlab-Event"] = "Push Hook",
        });
        Assert.True(await provider.AuthenticateAsync(Array.Empty<byte>(), headers, "my-secret-token", CiIntegrationConfig.Empty, CancellationToken.None));
        Assert.Equal("13792a34-cac6-4fda-95a8-c58e00a3954e", provider.ExtractDeliveryId(headers, string.Empty));
        Assert.Equal("Push Hook", provider.ExtractEventType(headers, string.Empty));
    }

    [Fact]
    public async Task Gitlab_InvalidTokenIsRejected()
    {
        var provider = new GitlabWebhookProvider();
        var bad = new Headers(new Dictionary<string, string> { ["X-Gitlab-Token"] = "wrong" });
        var missing = new Headers(new Dictionary<string, string>());
        Assert.False(await provider.AuthenticateAsync(Array.Empty<byte>(), bad, "my-secret-token", CiIntegrationConfig.Empty, CancellationToken.None));
        Assert.False(await provider.AuthenticateAsync(Array.Empty<byte>(), missing, "my-secret-token", CiIntegrationConfig.Empty, CancellationToken.None));
        Assert.False(await provider.AuthenticateAsync(Array.Empty<byte>(), bad, null, CiIntegrationConfig.Empty, CancellationToken.None));
    }

    [Fact]
    public void Gitlab_NormalizesPushHook()
    {
        var provider = new GitlabWebhookProvider();
        var body = Encoding.UTF8.GetBytes("""{"ref":"refs/heads/master","checkout_sha":"f15b32","project":{"path_with_namespace":"group/proj"},"user_username":"alice"}""");
        var normalized = provider.Normalize(Guid.NewGuid(), Guid.NewGuid(), "d", "Push Hook", body, DateTimeOffset.UtcNow, "hash");
        Assert.Equal("master", normalized.Branch);
        Assert.Equal("f15b32", normalized.CommitSha);
        Assert.Equal("group/proj", normalized.Repository);
        Assert.Equal("alice", normalized.Actor);
    }

    // ---------- Jenkins ----------

    [Fact]
    public async Task Jenkins_BearerTokenAuthenticates()
    {
        var provider = new JenkinsWebhookProvider();
        var bearer = new Headers(new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer job-token-123",
            ["X-Jenkins-Delivery"] = "jenkins-delivery-1",
        });
        var legacy = new Headers(new Dictionary<string, string> { ["token"] = "job-token-123" });
        Assert.True(await provider.AuthenticateAsync(Array.Empty<byte>(), bearer, "job-token-123", CiIntegrationConfig.Empty, CancellationToken.None));
        Assert.True(await provider.AuthenticateAsync(Array.Empty<byte>(), legacy, "job-token-123", CiIntegrationConfig.Empty, CancellationToken.None));
        Assert.False(await provider.AuthenticateAsync(Array.Empty<byte>(), bearer, "other", CiIntegrationConfig.Empty, CancellationToken.None));
        Assert.False(await provider.AuthenticateAsync(Array.Empty<byte>(), new Headers(new Dictionary<string, string>()), "job-token-123", CiIntegrationConfig.Empty, CancellationToken.None));
        Assert.False(await provider.AuthenticateAsync(Array.Empty<byte>(), bearer, null, CiIntegrationConfig.Empty, CancellationToken.None));
        Assert.Equal("jenkins-delivery-1", provider.ExtractDeliveryId(bearer, "{}"));
    }

    [Fact]
    public void Jenkins_FallbackDeliveryIdIsDeterministic()
    {
        var provider = new JenkinsWebhookProvider();
        var headers = new Headers(new Dictionary<string, string>());
        var first = provider.ExtractDeliveryId(headers, """{"name":"job","number":"42","result":"SUCCESS"}""");
        var second = provider.ExtractDeliveryId(headers, """{"name":"job","number":"42","result":"SUCCESS"}""");
        Assert.NotNull(first);
        Assert.StartsWith("jenkins-fallback-", first, StringComparison.Ordinal);
        Assert.Equal(first, second);
        Assert.Null(provider.ExtractDeliveryId(headers, "not-json"));
    }

    // ---------- Azure ----------

    private static string Basic(string user, string password)
        => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));

    [Fact]
    public async Task Azure_BasicAuthenticates()
    {
        var provider = new AzureWebhookProvider();
        var config = CiIntegrationConfig.Empty with { Username = "hookuser" };
        var headers = new Headers(new Dictionary<string, string> { ["Authorization"] = Basic("hookuser", "p@ss") });
        Assert.True(await provider.AuthenticateAsync(Array.Empty<byte>(), headers, "p@ss", config, CancellationToken.None));
        Assert.False(await provider.AuthenticateAsync(Array.Empty<byte>(), headers, "wrong", config, CancellationToken.None));
        var wrongUser = new Headers(new Dictionary<string, string> { ["Authorization"] = Basic("other", "p@ss") });
        Assert.False(await provider.AuthenticateAsync(Array.Empty<byte>(), wrongUser, "p@ss", config, CancellationToken.None));
        Assert.False(await provider.AuthenticateAsync(Array.Empty<byte>(), headers, "p@ss", CiIntegrationConfig.Empty, CancellationToken.None));
        Assert.False(await provider.AuthenticateAsync(Array.Empty<byte>(), headers, null, config, CancellationToken.None));
    }

    [Fact]
    public void Azure_ExtractsNotificationIdentity()
    {
        var provider = new AzureWebhookProvider();
        var headers = new Headers(new Dictionary<string, string>());
        Assert.Equal("n-1", provider.ExtractDeliveryId(headers, """{"notificationId":"n-1","eventType":"git.push"}"""));
        Assert.Equal("s-9:n-1", provider.ExtractDeliveryId(headers, """{"notificationId":"n-1","subscriptionId":"s-9","eventType":"git.push"}"""));
        Assert.Equal("git.push", provider.ExtractEventType(headers, """{"notificationId":"n-1","eventType":"git.push"}"""));
        Assert.Null(provider.ExtractDeliveryId(headers, """{"eventType":"git.push"}"""));
    }

    [Fact]
    public void Azure_NormalizesBuildCompleted()
    {
        var provider = new AzureWebhookProvider();
        var body = Encoding.UTF8.GetBytes("""{"resource":{"sourceBranch":"refs/heads/main","sourceVersion":"abc","id":"99","repository":{"name":"repo"}}}""");
        var normalized = provider.Normalize(Guid.NewGuid(), Guid.NewGuid(), "d", "build.complete", body, DateTimeOffset.UtcNow, "hash");
        Assert.Equal("main", normalized.Branch);
        Assert.Equal("abc", normalized.CommitSha);
        Assert.Equal("99", normalized.BuildNumber);
    }
}
