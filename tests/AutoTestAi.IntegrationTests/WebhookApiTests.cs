using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoTestAi.Application.Webhooks;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using AutoTestAi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AutoTestAi.IntegrationTests;

/// <summary>
/// Slice 3B: CI/CD webhook ingress, management, delivery history, retry, and
/// end-to-end fan-out through the real TestExecutionService with a fake
/// workflow coordinator. No live provider accounts — deterministic vectors.
/// </summary>
public sealed class WebhookApiTests : IClassFixture<Slice1ApiFactory>
{
    private static readonly Guid AdminRoleId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TesterRoleId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly Slice1ApiFactory _factory;
    private readonly Guid _projectA = Guid.NewGuid();
    private readonly Guid _projectB = Guid.NewGuid();
    private readonly SemaphoreSlim _seedLock = new(1, 1);
    private bool _seeded;
    private Guid _envA = Guid.Empty;
    private Guid _suiteA = Guid.Empty;

    public WebhookApiTests(Slice1ApiFactory factory) => _factory = factory;

    private static HttpClient ClientFor(Slice1ApiFactory factory, string sub, string[] roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Create(sub, roles));
        return client;
    }

    private static string GithubSignature(string secret, byte[] body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();
    }

    private async Task SeedOnceAsync()
    {
        await _seedLock.WaitAsync();
        try
        {
            if (_seeded) return;
            var (pa, pb) = (_projectA, _projectB);
            await _factory.SeedAsync(db =>
            {
                if (!db.Roles.Any())
                {
                    db.Roles.AddRange(
                        new Role { Id = AdminRoleId, Name = "admin" },
                        new Role { Id = TesterRoleId, Name = "tester" });
                }
                var admin = new User { ExternalIdentityId = "ex-wh-admin", Email = "admin@x", DisplayName = "Admin" };
                var tester = new User { ExternalIdentityId = "ex-wh-tester", Email = "tester@x", DisplayName = "Tester" };
                var outsider = new User { ExternalIdentityId = "ex-wh-outsider", Email = "outsider@x", DisplayName = "Outsider" };
                db.Users.AddRange(admin, tester, outsider);
                db.Projects.AddRange(
                    new Project { Id = pa, Name = "Webhook Alpha", Key = "WHA" },
                    new Project { Id = pb, Name = "Webhook Beta", Key = "WHB" });
                db.ProjectMembers.AddRange(
                    new ProjectMember { ProjectId = pa, UserId = admin.Id, RoleId = AdminRoleId },
                    new ProjectMember { ProjectId = pa, UserId = tester.Id, RoleId = TesterRoleId });
                var envA = new TestEnvironment { ProjectId = pa, Name = "QA", BaseUrl = "https://qa.example.com" };
                db.Environments.Add(envA);
                var suite = new TestSuite { ProjectId = pa, Name = "Smoke" };
                db.TestSuites.Add(suite);
                var case1 = new TestCase
                {
                    ProjectId = pa, TestKey = "WH-001", Title = "Webhook case 1",
                    Framework = "playwright", Platform = "web",
                    Priority = Priority.Medium, Status = TestCaseStatus.Active, SourceType = "manual",
                };
                var case2 = new TestCase
                {
                    ProjectId = pa, TestKey = "WH-002", Title = "Webhook case 2",
                    Framework = "playwright", Platform = "web",
                    Priority = Priority.Medium, Status = TestCaseStatus.Active, SourceType = "manual",
                };
                db.TestCases.AddRange(case1, case2);
                db.TestCaseVersions.AddRange(
                    new TestCaseVersion
                    {
                        TestCaseId = case1.Id, VersionNumber = 1, SourceCode = "// v1",
                        StructuredSteps = JsonDocument.Parse(
                            """[{"order":1,"action":"navigate","target":"https://qa.example.com"}]"""),
                        ReviewStatus = ReviewStatus.Approved,
                    },
                    new TestCaseVersion
                    {
                        TestCaseId = case2.Id, VersionNumber = 1, SourceCode = "// v1",
                        StructuredSteps = JsonDocument.Parse(
                            """[{"order":1,"action":"navigate","target":"https://qa.example.com"}]"""),
                        ReviewStatus = ReviewStatus.Approved,
                    });
                db.SuiteTestCases.AddRange(
                    new SuiteTestCase { SuiteId = suite.Id, TestCaseId = case2.Id, ExecutionOrder = 2 },
                    new SuiteTestCase { SuiteId = suite.Id, TestCaseId = case1.Id, ExecutionOrder = 1 });
                _envA = envA.Id;
                _suiteA = suite.Id;
                return Task.CompletedTask;
            });
            _seeded = true;
        }
        finally { _seedLock.Release(); }
    }

    private async Task<Guid> UpsertGithubAsync(HttpClient admin, string secret = "gh-secret-1", bool enabled = true)
    {
        var response = await admin.PutAsJsonAsync($"/api/v1/projects/{_projectA}/integrations/cicd", new
        {
            provider = "github",
            enabled,
            defaultSuiteId = _suiteA,
            defaultEnvironmentId = _envA,
            eventAllowlist = new[] { "push" },
            branchAllowlist = new[] { "main" },
            repositoryAllowlist = new string[] { },
            variableMapping = new Dictionary<string, string> { ["BRANCH"] = "BRANCH" },
            webhookSecret = secret,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(body);
        Assert.True(document.RootElement.GetProperty("hasSecret").GetBoolean());
        var url = document.RootElement.GetProperty("webhookUrl").GetString() ?? string.Empty;
        Assert.Contains($"/api/v1/webhooks/github/{_projectA}/", url, StringComparison.Ordinal);
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private static HttpRequestMessage GithubPost(
        Guid projectId, Guid integrationId, string deliveryId, string eventType, string secret, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/webhooks/github/{projectId}/{integrationId}")
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-Hub-Signature-256", GithubSignature(secret, body));
        request.Headers.Add("X-GitHub-Delivery", deliveryId);
        request.Headers.Add("X-GitHub-Event", eventType);
        return request;
    }

    [Fact]
    public async Task Upsert_RequiresSettingsManage()
    {
        await SeedOnceAsync();
        var tester = ClientFor(_factory, "ex-wh-tester", ["tester"]);
        var response = await tester.PutAsJsonAsync($"/api/v1/projects/{_projectA}/integrations/cicd", new
        {
            provider = "github",
            enabled = true,
        });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Github_ValidDelivery_AcceptsDuplicatesAndRejectsForged()
    {
        await SeedOnceAsync();
        var admin = ClientFor(_factory, "ex-wh-admin", ["admin"]);
        var integrationId = await UpsertGithubAsync(admin);
        var anonymous = _factory.CreateClient();
        const string payload = """{"ref":"refs/heads/main","after":"abc123","repository":{"full_name":"octo/repo"}}""";

        var first = await anonymous.SendAsync(GithubPost(_projectA, integrationId, "delivery-1", "push", "gh-secret-1", payload));
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        var duplicate = await anonymous.SendAsync(GithubPost(_projectA, integrationId, "delivery-1", "push", "gh-secret-1", payload));
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        var duplicateBody = await duplicate.Content.ReadAsStringAsync();
        Assert.Contains("duplicate", duplicateBody, StringComparison.OrdinalIgnoreCase);

        var forged = await anonymous.SendAsync(GithubPost(_projectA, integrationId, "delivery-2", "push", "wrong-secret", payload));
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);

        // Exactly one durable delivery row exists for the accepted delivery.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AutoTestAiDbContext>();
        var rows = await db.WebhookDeliveries
            .Where(d => d.IntegrationId == integrationId)
            .ToListAsync();
        Assert.Single(rows);
        Assert.Equal("delivery-1", rows[0].DeliveryId);
        Assert.Equal(WebhookProcessingStatus.Accepted, rows[0].ProcessingStatus);
        Assert.DoesNotContain("gh-secret-1", rows[0].NormalizedMetadataJson ?? string.Empty, StringComparison.Ordinal);

        // Drive the (background-disabled) processor: fan-out of 2, TriggerType Ci.
        var processing = scope.ServiceProvider.GetRequiredService<IWebhookProcessingService>();
        await processing.ProcessAsync(rows[0].Id, CancellationToken.None);
        await db.Entry(rows[0]).ReloadAsync();
        Assert.Equal(WebhookProcessingStatus.Triggered, rows[0].ProcessingStatus);
        Assert.Equal(2, rows[0].TriggeredCount);
        Assert.NotNull(rows[0].ExecutionId);
        var executions = await db.Executions.Where(e => e.ProjectId == _projectA).ToListAsync();
        Assert.Equal(2, executions.Count);
        Assert.All(executions, e =>
        {
            Assert.Equal(TriggerType.Ci, e.TriggerType);
            Assert.Equal(_suiteA, e.SuiteId);
            Assert.Equal(_envA, e.EnvironmentId);
        });
        Assert.Equal(2, _factory.WorkflowCoordinator.Started.Count(s => s.ProjectId == _projectA));
    }

    [Fact]
    public async Task Github_UnknownOrCrossProjectIntegration_IsNotFound()
    {
        await SeedOnceAsync();
        var admin = ClientFor(_factory, "ex-wh-admin", ["admin"]);
        var integrationId = await UpsertGithubAsync(admin, secret: "gh-secret-2");
        var anonymous = _factory.CreateClient();
        const string payload = """{"ref":"refs/heads/main"}""";

        // Wrong project for a real integration.
        var crossProject = await anonymous.SendAsync(
            GithubPost(_projectB, integrationId, "delivery-x", "push", "gh-secret-2", payload));
        Assert.Equal(HttpStatusCode.NotFound, crossProject.StatusCode);

        // Unknown integration id (valid HMAC shape, secret unknown => 404 before auth).
        var unknown = await anonymous.SendAsync(
            GithubPost(_projectA, Guid.NewGuid(), "delivery-y", "push", "gh-secret-2", payload));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Github_DisabledIntegration_IsNotFound()
    {
        await SeedOnceAsync();
        var admin = ClientFor(_factory, "ex-wh-admin", ["admin"]);
        var integrationId = await UpsertGithubAsync(admin, secret: "gh-secret-3", enabled: false);
        var anonymous = _factory.CreateClient();
        var response = await anonymous.SendAsync(
            GithubPost(_projectA, integrationId, "delivery-disabled", "push", "gh-secret-3", """{"ref":"refs/heads/main"}"""));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Github_OversizedBody_IsRejected()
    {
        await SeedOnceAsync();
        var admin = ClientFor(_factory, "ex-wh-admin", ["admin"]);
        var integrationId = await UpsertGithubAsync(admin, secret: "gh-secret-4");
        var anonymous = _factory.CreateClient();
        var big = new string('x', 1024 * 1024 + 16);
        var body = Encoding.UTF8.GetBytes($"{{\"ref\":\"refs/heads/main\",\"blob\":\"{big}\"}}");
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/webhooks/github/{_projectA}/{integrationId}")
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-Hub-Signature-256", GithubSignature("gh-secret-4", body));
        request.Headers.Add("X-GitHub-Delivery", "delivery-big");
        request.Headers.Add("X-GitHub-Event", "push");
        var response = await anonymous.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Gitlab_TokenFlow_AcceptsAndRejects()
    {
        await SeedOnceAsync();
        var admin = ClientFor(_factory, "ex-wh-admin", ["admin"]);
        var upsert = await admin.PutAsJsonAsync($"/api/v1/projects/{_projectA}/integrations/cicd", new
        {
            provider = "gitlab",
            enabled = true,
            defaultSuiteId = _suiteA,
            defaultEnvironmentId = _envA,
            eventAllowlist = new[] { "Push Hook" },
            webhookSecret = "gl-secret-1",
        });
        Assert.Equal(HttpStatusCode.OK, upsert.StatusCode);
        using var document = JsonDocument.Parse(await upsert.Content.ReadAsStringAsync());
        var integrationId = document.RootElement.GetProperty("id").GetGuid();

        static HttpRequestMessage GitlabPost(Guid projectId, Guid integrationId, string token)
        {
            var body = Encoding.UTF8.GetBytes("""{"ref":"refs/heads/master","checkout_sha":"abc"}""");
            var request = new HttpRequestMessage(HttpMethod.Post,
                $"/api/v1/webhooks/gitlab/{projectId}/{integrationId}")
            {
                Content = new ByteArrayContent(body),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Add("X-Gitlab-Token", token);
            request.Headers.Add("X-Gitlab-Event-UUID", "gl-delivery-1");
            request.Headers.Add("X-Gitlab-Event", "Push Hook");
            return request;
        }

        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Accepted,
            (await anonymous.SendAsync(GitlabPost(_projectA, integrationId, "gl-secret-1"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.SendAsync(GitlabPost(_projectA, integrationId, "wrong"))).StatusCode);
    }

    [Fact]
    public async Task JenkinsAndAzure_FlowsAuthenticate()
    {
        await SeedOnceAsync();
        var admin = ClientFor(_factory, "ex-wh-admin", ["admin"]);

        var jenkins = await admin.PutAsJsonAsync($"/api/v1/projects/{_projectA}/integrations/cicd", new
        {
            provider = "jenkins",
            enabled = true,
            defaultSuiteId = _suiteA,
            defaultEnvironmentId = _envA,
            webhookSecret = "jenkins-token-1",
        });
        Assert.Equal(HttpStatusCode.OK, jenkins.StatusCode);
        var jenkinsId = JsonDocument.Parse(await jenkins.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        static HttpRequestMessage JenkinsPost(Guid projectId, Guid integrationId, string? bearer)
        {
            var request = new HttpRequestMessage(HttpMethod.Post,
                $"/api/v1/webhooks/jenkins/{projectId}/{integrationId}")
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("""{"name":"job","number":"7"}""")),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            if (bearer is not null)
                request.Headers.Add("Authorization", $"Bearer {bearer}");
            request.Headers.Add("X-Jenkins-Delivery", "jenkins-1");
            return request;
        }

        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Accepted,
            (await anonymous.SendAsync(JenkinsPost(_projectA, jenkinsId, "jenkins-token-1"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.SendAsync(JenkinsPost(_projectA, jenkinsId, "wrong"))).StatusCode);

        var azure = await admin.PutAsJsonAsync($"/api/v1/projects/{_projectA}/integrations/cicd", new
        {
            provider = "azure",
            enabled = true,
            defaultSuiteId = _suiteA,
            defaultEnvironmentId = _envA,
            username = "hookuser",
            webhookSecret = "azure-pass-1",
        });
        Assert.Equal(HttpStatusCode.OK, azure.StatusCode);
        var azureId = JsonDocument.Parse(await azure.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        Assert.DoesNotContain("azure-pass-1", await azure.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        static HttpRequestMessage AzurePost(Guid projectId, Guid integrationId, string user, string password)
        {
            var body = Encoding.UTF8.GetBytes("""{"notificationId":"n-9","subscriptionId":"s-9","eventType":"git.push"}""");
            var request = new HttpRequestMessage(HttpMethod.Post,
                $"/api/v1/webhooks/azure/{projectId}/{integrationId}")
            {
                Content = new ByteArrayContent(body),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Add("Authorization", "Basic " + Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{user}:{password}")));
            return request;
        }

        Assert.Equal(HttpStatusCode.Accepted,
            (await anonymous.SendAsync(AzurePost(_projectA, azureId, "hookuser", "azure-pass-1"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.SendAsync(AzurePost(_projectA, azureId, "hookuser", "wrong"))).StatusCode);
    }

    [Fact]
    public async Task Deliveries_ListedWithoutSecrets_AndFailedCanRetry()
    {
        await SeedOnceAsync();
        var admin = ClientFor(_factory, "ex-wh-admin", ["admin"]);
        var tester = ClientFor(_factory, "ex-wh-tester", ["tester"]);
        // Over-long branch trips deterministic variable-mapping failure (Failed, retryable).
        var upsert = await admin.PutAsJsonAsync($"/api/v1/projects/{_projectA}/integrations/cicd", new
        {
            provider = "github",
            enabled = true,
            defaultSuiteId = _suiteA,
            defaultEnvironmentId = _envA,
            eventAllowlist = new[] { "push" },
            variableMapping = new Dictionary<string, string> { ["BRANCH"] = "BRANCH" },
            webhookSecret = "gh-secret-5",
        });
        Assert.Equal(HttpStatusCode.OK, upsert.StatusCode);
        var integrationId = JsonDocument.Parse(await upsert.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        var anonymous = _factory.CreateClient();
        var longBranch = new string('b', 600);
        var accepted = await anonymous.SendAsync(GithubPost(_projectA, integrationId, "delivery-retry",
            "push", "gh-secret-5", $"{{\"ref\":\"refs/heads/{longBranch}\",\"after\":\"abc\"}}"));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var deliveryId = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync())
            .RootElement.GetProperty("deliveryId").GetGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var processing = scope.ServiceProvider.GetRequiredService<IWebhookProcessingService>();
            await processing.ProcessAsync(deliveryId, CancellationToken.None);
        }
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AutoTestAiDbContext>();
            var row = await db.WebhookDeliveries.FirstAsync(d => d.Id == deliveryId);
            Assert.Equal(WebhookProcessingStatus.Failed, row.ProcessingStatus);
            Assert.Equal("invalid_variable_mapping", row.FailureReason);
        }

        var list = await tester.GetAsync($"/api/v1/projects/{_projectA}/integrations/cicd/github/deliveries");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listBody = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain("gh-secret-5", listBody, StringComparison.Ordinal);
        Assert.Contains("invalid_variable_mapping", listBody, StringComparison.Ordinal);

        var retry = await admin.PostAsync(
            $"/api/v1/projects/{_projectA}/integrations/cicd/github/deliveries/{deliveryId}/retry", null);
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);

        var testerRetry = await tester.PostAsync(
            $"/api/v1/projects/{_projectA}/integrations/cicd/github/deliveries/{deliveryId}/retry", null);
        Assert.Equal(HttpStatusCode.Forbidden, testerRetry.StatusCode);
    }
}
