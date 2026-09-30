using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AutoTestAi.Application.Secrets;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using AutoTestAi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AutoTestAi.IntegrationTests;

/// <summary>Slice 3A API: variable sets, secret metadata, environment-aware
/// execution, project isolation, concurrency, and audit redaction.</summary>
public sealed class Slice3AApiTests : IClassFixture<Slice1ApiFactory>
{
    private static readonly Guid QaLeadRoleId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TesterRoleId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ViewerRoleId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly Slice1ApiFactory _factory;
    private readonly Guid _projectA = Guid.NewGuid();
    private readonly Guid _projectB = Guid.NewGuid();
    private readonly SemaphoreSlim _seedLock = new(1, 1);
    private bool _seeded;
    private Guid _envA = Guid.Empty;
    private Guid _envB = Guid.Empty;
    private Guid _approvedVersionA = Guid.Empty;
    private Guid _approvedVersionB = Guid.Empty;

    public Slice3AApiTests(Slice1ApiFactory factory) => _factory = factory;

    private static HttpClient ClientFor(Slice1ApiFactory factory, string sub, string[] roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TestTokens.Create(sub, roles));
        return client;
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
                        new Role { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), Name = "admin" },
                        new Role { Id = QaLeadRoleId, Name = "qa-lead" },
                        new Role { Id = TesterRoleId, Name = "tester" },
                        new Role { Id = ViewerRoleId, Name = "viewer" });
                }
                var lead = new User { ExternalIdentityId = "ex-3a-lead", Email = "lead3a@x", DisplayName = "Lead" };
                var tester = new User { ExternalIdentityId = "ex-3a-tester", Email = "tester3a@x", DisplayName = "Tester" };
                var viewer = new User { ExternalIdentityId = "ex-3a-viewer", Email = "viewer3a@x", DisplayName = "Viewer" };
                db.Users.AddRange(lead, tester, viewer);
                db.Projects.AddRange(
                    new Project { Id = pa, Name = "Vars Alpha", Key = "VAA" },
                    new Project { Id = pb, Name = "Vars Beta", Key = "VAB" });
                db.ProjectMembers.AddRange(
                    new ProjectMember { ProjectId = pa, UserId = lead.Id, RoleId = QaLeadRoleId },
                    new ProjectMember { ProjectId = pa, UserId = tester.Id, RoleId = TesterRoleId },
                    new ProjectMember { ProjectId = pa, UserId = viewer.Id, RoleId = ViewerRoleId });
                var envA = new TestEnvironment { ProjectId = pa, Name = "QA", BaseUrl = "https://qa.example.com" };
                var envB = new TestEnvironment { ProjectId = pb, Name = "Beta", BaseUrl = "https://beta.example.com" };
                db.Environments.AddRange(envA, envB);
                var testCase = new TestCase
                {
                    ProjectId = pa, TestKey = "VARS-001", Title = "Vars case",
                    Framework = "playwright", Platform = "web",
                    Priority = Priority.Medium, Status = TestCaseStatus.Active, SourceType = "manual",
                };
                db.TestCases.Add(testCase);
                var approved = new TestCaseVersion
                {
                    TestCaseId = testCase.Id, VersionNumber = 1, SourceCode = "// v1",
                    StructuredSteps = JsonDocument.Parse(
                        """[{"order":1,"action":"navigate","target":"${{ BASE_URL }}/login"}]"""),
                    ReviewStatus = ReviewStatus.Approved,
                };
                db.TestCaseVersions.Add(approved);
                var caseB = new TestCase
                {
                    ProjectId = pb, TestKey = "VARS-002", Title = "Vars case B",
                    Framework = "playwright", Platform = "web",
                    Priority = Priority.Medium, Status = TestCaseStatus.Active, SourceType = "manual",
                };
                db.TestCases.Add(caseB);
                var approvedB = new TestCaseVersion
                {
                    TestCaseId = caseB.Id, VersionNumber = 1, SourceCode = "// b-v1",
                    StructuredSteps = JsonDocument.Parse(
                        """[{"order":1,"action":"navigate","target":"https://beta.example.com"}]"""),
                    ReviewStatus = ReviewStatus.Approved,
                };
                db.TestCaseVersions.Add(approvedB);
                _envA = envA.Id;
                _envB = envB.Id;
                _approvedVersionA = approved.Id;
                _approvedVersionB = approvedB.Id;
                return Task.CompletedTask;
            });
            _seeded = true;
        }
        finally { _seedLock.Release(); }
    }

    // ---------- variable sets ----------

    [Fact]
    public async Task VariableSet_CRUD_RoundTrips()
    {
        await SeedOnceAsync();
        var client = ClientFor(_factory, "ex-3a-lead", ["qa-lead"]);

        var create = await client.PostAsJsonAsync($"/api/v1/projects/{_projectA}/variable-sets", new
        {
            scopeType = "Project",
            name = "defaults",
            variables = new Dictionary<string, object>
            {
                ["BASE_URL"] = new { value = "https://qa.example.com" },
            },
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var setId = created.GetProperty("id").GetString()!;

        var get = await client.GetAsync($"/api/v1/variable-sets/{setId}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        var list = await client.GetAsync($"/api/v1/projects/{_projectA}/variable-sets");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);

        var update = await client.PutAsJsonAsync($"/api/v1/variable-sets/{setId}", new
        {
            name = "defaults",
            variables = new Dictionary<string, object>
            {
                ["BASE_URL"] = new { value = "https://qa2.example.com" },
            },
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var delete = await client.DeleteAsync($"/api/v1/variable-sets/{setId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [Fact]
    public async Task VariableSet_RejectsRawStringShape()
    {
        await SeedOnceAsync();
        var client = ClientFor(_factory, "ex-3a-lead", ["qa-lead"]);
        var response = await client.PostAsJsonAsync($"/api/v1/projects/{_projectA}/variable-sets", new
        {
            scopeType = "Project",
            name = "bad",
            variables = new Dictionary<string, object> { ["API_TOKEN"] = "actual-secret" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task VariableSet_RejectsCrossProjectScope()
    {
        await SeedOnceAsync();
        var client = ClientFor(_factory, "ex-3a-lead", ["qa-lead"]);
        var response = await client.PostAsJsonAsync($"/api/v1/projects/{_projectA}/variable-sets", new
        {
            scopeType = "Environment",
            scopeId = _envB,
            name = "cross",
            variables = new Dictionary<string, object>(),
        });
        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden,
            $"unexpected {response.StatusCode}");
    }

    [Fact]
    public async Task VariableSet_TesterCannotManage_ViewerCanRead()
    {
        await SeedOnceAsync();
        var tester = ClientFor(_factory, "ex-3a-tester", ["tester"]);
        var denied = await tester.PostAsJsonAsync($"/api/v1/projects/{_projectA}/variable-sets", new
        {
            scopeType = "Project",
            name = "nope",
            variables = new Dictionary<string, object>(),
        });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var viewer = ClientFor(_factory, "ex-3a-viewer", ["viewer"]);
        var list = await viewer.GetAsync($"/api/v1/projects/{_projectA}/variable-sets");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }

    [Fact]
    public async Task VariableSet_StaleRowVersion_Conflicts()
    {
        await SeedOnceAsync();
        var client = ClientFor(_factory, "ex-3a-lead", ["qa-lead"]);
        var create = await client.PostAsJsonAsync($"/api/v1/projects/{_projectA}/variable-sets", new
        {
            scopeType = "Environment",
            scopeId = _envA,
            name = "env-vars",
            variables = new Dictionary<string, object>
            {
                ["BASE_URL"] = new { value = "https://qa.example.com" },
            },
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var setId = created.GetProperty("id").GetString()!;

        // Force a server-side row version so the stale token below mismatches.
        await _factory.SeedAsync(db =>
        {
            var row = db.VariableSets.First(s => s.Id == Guid.Parse(setId));
            row.RowVersion = new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 };
            return Task.CompletedTask;
        });

        var stale = await client.PutAsJsonAsync($"/api/v1/variable-sets/{setId}", new
        {
            name = "env-vars",
            variables = new Dictionary<string, object>(),
            rowVersion = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }),
        });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        await client.DeleteAsync($"/api/v1/variable-sets/{setId}");
    }

    // ---------- secrets ----------

    [Fact]
    public async Task Secret_CreateListExistsUpdateDelete_NeverReturnsValue()
    {
        await SeedOnceAsync();
        var client = ClientFor(_factory, "ex-3a-lead", ["qa-lead"]);
        const string value = "3a-integration-secret-abc";

        var create = await client.PostAsJsonAsync($"/api/v1/projects/{_projectA}/secrets", new
        {
            environmentId = _envA,
            name = "API_TOKEN",
            value,
            description = "token",
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var createdBody = await create.Content.ReadAsStringAsync();
        Assert.DoesNotContain(value, createdBody);
        var created = JsonDocument.Parse(createdBody).RootElement;
        var secretId = created.GetProperty("id").GetString()!;
        Assert.True(created.GetProperty("hasValue").GetBoolean());
        var secretRef = created.GetProperty("secretReference").GetString()!;
        Assert.StartsWith("env_secret:", secretRef);

        var listBody = await (await client.GetAsync(
            $"/api/v1/projects/{_projectA}/secrets?environmentId={_envA}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(value, listBody);

        var exists = await client.GetAsync($"/api/v1/secrets/{secretId}/exists");
        Assert.Equal(HttpStatusCode.OK, exists.StatusCode);

        // Ciphertext at rest differs from plaintext.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AutoTestAiDbContext>();
            var row = await db.EnvironmentSecrets.FirstAsync(s => s.Id == Guid.Parse(secretId));
            Assert.NotNull(row.EncryptedValue);
            Assert.DoesNotContain(value, row.EncryptedValue!);
            Assert.NotNull(row.Nonce);
        }

        // Resolver round-trips the value server-side only.
        using (var scope = _factory.Services.CreateScope())
        {
            var resolver = scope.ServiceProvider.GetRequiredService<ISecretResolver>();
            Assert.Equal(value, await resolver.ResolveAsync(secretRef, CancellationToken.None));
        }

        var update = await client.PutAsJsonAsync($"/api/v1/secrets/{secretId}", new
        {
            value = "3a-rotated-secret-xyz",
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.DoesNotContain("3a-rotated-secret-xyz", await update.Content.ReadAsStringAsync());

        var delete = await client.DeleteAsync($"/api/v1/secrets/{secretId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [Fact]
    public async Task Secret_TesterCannotCreate_CrossProjectEnvRejected()
    {
        await SeedOnceAsync();
        var tester = ClientFor(_factory, "ex-3a-tester", ["tester"]);
        var denied = await tester.PostAsJsonAsync($"/api/v1/projects/{_projectA}/secrets", new
        {
            environmentId = _envA,
            name = "X",
            value = "v",
        });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var lead = ClientFor(_factory, "ex-3a-lead", ["qa-lead"]);
        var cross = await lead.PostAsJsonAsync($"/api/v1/projects/{_projectA}/secrets", new
        {
            environmentId = _envB,
            name = "X",
            value = "v",
        });
        Assert.True(cross.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden,
            $"unexpected {cross.StatusCode}");
    }

    // ---------- environment-aware execution ----------

    [Fact]
    public async Task Execution_UsesDefaultEnvironment_WhenNoneSupplied()
    {
        await SeedOnceAsync();
        var admin = ClientFor(_factory, "ex-3a-admin", ["admin"]);
        var setDefault = await admin.PutAsJsonAsync($"/api/v1/projects/{_projectA}", new
        {
            name = "Vars Alpha",
            defaultEnvironmentId = _envA,
        });
        Assert.True(setDefault.IsSuccessStatusCode, await setDefault.Content.ReadAsStringAsync());

        var lead = ClientFor(_factory, "ex-3a-lead", ["qa-lead"]);
        var start = await lead.PostAsJsonAsync($"/api/v1/projects/{_projectA}/executions", new
        {
            testCaseVersionId = _approvedVersionA,
            browser = "chromium",
        });
        Assert.True(start.IsSuccessStatusCode, await start.Content.ReadAsStringAsync());

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AutoTestAiDbContext>();
            var executions = await db.Executions
                .Where(e => e.ProjectId == _projectA)
                .OrderByDescending(e => e.CreatedAt).ToListAsync();
            Assert.NotEmpty(executions);
            Assert.Equal(_envA, executions[0].EnvironmentId);
            var envelope = await db.ExecutionVariables
                .FirstOrDefaultAsync(v => v.ExecutionId == executions[0].Id);
            Assert.NotNull(envelope);
            Assert.Equal(_envA, envelope!.EnvironmentId);
            Assert.DoesNotContain("secret", envelope.SecretRefOverridesJson, StringComparison.OrdinalIgnoreCase);
        }

        // Clear the default for legacy tests below.
        await admin.PutAsJsonAsync($"/api/v1/projects/{_projectA}", new { name = "Vars Alpha" });
    }

    [Fact]
    public async Task Execution_OverridesRequireEnvironment_AndRawSecretsRejected()
    {
        await SeedOnceAsync();
        var lead = ClientFor(_factory, "ex-3a-lead", ["qa-lead"]);

        var missing = await lead.PostAsJsonAsync($"/api/v1/projects/{_projectA}/executions", new
        {
            testCaseVersionId = _approvedVersionA,
            variableOverrides = new Dictionary<string, string> { ["BASE_URL"] = "https://x.example.com" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var raw = await lead.PostAsJsonAsync($"/api/v1/projects/{_projectA}/executions", new
        {
            testCaseVersionId = _approvedVersionA,
            environmentId = _envA,
            secretRefOverrides = new Dictionary<string, string> { ["API_TOKEN"] = "plaintext-leak" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, raw.StatusCode);
    }

    [Fact]
    public async Task Execution_LegacyNullEnvironment_Preserved()
    {
        await SeedOnceAsync();
        // Project B has no default environment and no overrides: the start
        // preserves the legacy environment-less (null EnvironmentId) behavior.
        // Admin bypasses membership; the version belongs to project B.
        var admin = ClientFor(_factory, "ex-3a-admin", ["admin"]);
        var start = await admin.PostAsJsonAsync($"/api/v1/projects/{_projectB}/executions", new
        {
            testCaseVersionId = _approvedVersionB,
            browser = "chromium",
        });
        Assert.True(start.IsSuccessStatusCode, await start.Content.ReadAsStringAsync());
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AutoTestAiDbContext>();
            var execution = await db.Executions
                .Where(e => e.ProjectId == _projectB)
                .OrderByDescending(e => e.CreatedAt).FirstAsync();
            Assert.Null(execution.EnvironmentId);
        }
    }

    [Fact]
    public async Task Audit_NeverContainsSecretValues()
    {
        await SeedOnceAsync();
        var client = ClientFor(_factory, "ex-3a-lead", ["qa-lead"]);
        const string value = "3a-audit-probe-secret";
        await client.PostAsJsonAsync($"/api/v1/projects/{_projectA}/secrets", new
        {
            environmentId = _envA,
            name = "AUDIT_PROBE",
            value,
        });
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AutoTestAiDbContext>();
            var metas = await db.AuditEvents
                .Where(a => a.ProjectId == _projectA)
                .Select(a => a.MetadataJson)
                .ToListAsync();
            foreach (var meta in metas)
                Assert.DoesNotContain(value, meta ?? string.Empty);
        }
        // Cleanup probe.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AutoTestAiDbContext>();
            var probe = await db.EnvironmentSecrets
                .FirstOrDefaultAsync(s => s.ProjectId == _projectA && s.Name == "AUDIT_PROBE");
            if (probe is not null) { db.EnvironmentSecrets.Remove(probe); await db.SaveChangesAsync(); }
        }
    }
}
