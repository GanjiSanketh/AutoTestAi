using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.IntegrationTests;

/// <summary>
/// STEP 14: project boundary — 401 anonymous, 403 outsider/unpermitted,
/// 202 authorized member. Proves UUIDs cannot be swapped across projects.
/// </summary>
public sealed class ProjectAccessTests : IClassFixture<Slice1ApiFactory>
{
    private readonly Slice1ApiFactory _factory;
    private readonly Guid _projectId = Guid.NewGuid();
    private bool _seeded;
    private readonly SemaphoreSlim _seedLock = new(1, 1);

    public ProjectAccessTests(Slice1ApiFactory factory) => _factory = factory;

    private async Task SeedOnceAsync()
    {
        await _seedLock.WaitAsync();
        try
        {
            if (_seeded) return;
            _seeded = true;
            var projectId = _projectId;
            await _factory.SeedAsync(db =>
            {
                var member = new User
                {
                    ExternalIdentityId = "slice1-member",
                    Email = "member@example.com",
                    DisplayName = "Member",
                };
                var outsider = new User
                {
                    ExternalIdentityId = "slice1-outsider",
                    Email = "outsider@example.com",
                    DisplayName = "Outsider",
                };
                var viewer = new User
                {
                    ExternalIdentityId = "slice1-viewer",
                    Email = "viewer@example.com",
                    DisplayName = "Viewer",
                };
                db.Users.AddRange(member, outsider, viewer);
                db.Projects.Add(new Project { Id = projectId, Name = "Slice", Key = "SLICE1" });
                db.ProjectMembers.Add(new ProjectMember
                {
                    ProjectId = projectId,
                    UserId = member.Id,
                    RoleId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                });
                db.ProjectMembers.Add(new ProjectMember
                {
                    ProjectId = projectId,
                    UserId = viewer.Id,
                    RoleId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                });
                return Task.CompletedTask;
            });
        }
        finally
        {
            _seedLock.Release();
        }
    }

    private HttpClient Client(string? token)
    {
        var client = _factory.CreateClient();
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private sealed record ErrorPayload(ErrorDetail Error);
    private sealed record ErrorDetail(string Code, string Message);

    [Fact]
    public async Task StartExecution_WithoutToken_Returns401()
    {
        await SeedOnceAsync();
        var response = await Client(null)
            .PostAsJsonAsync($"/api/v1/projects/{_projectId}/executions", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task StartExecution_MemberWithPermission_Returns202()
    {
        // Slice 5: starting an execution binds one exact APPROVED version.
        Guid versionId = Guid.Empty;
        await SeedOnceAsync();
        await _factory.SeedAsync(db =>
        {
            var testCase = new TestCase
            {
                ProjectId = _projectId, TestKey = "RUN-001", Title = "Runnable",
                Priority = Priority.High, Status = TestCaseStatus.Active, SourceType = "manual",
            };
            db.TestCases.Add(testCase);
            var version = new TestCaseVersion
            {
                TestCaseId = testCase.Id, VersionNumber = 1, SourceCode = "// v1",
                StructuredSteps = System.Text.Json.JsonDocument.Parse(
                    """[{"order":1,"action":"navigate","target":"https://example.test"}]"""),
                ReviewStatus = ReviewStatus.Approved,
            };
            db.TestCaseVersions.Add(version);
            versionId = version.Id;
            return Task.CompletedTask;
        });
        var token = TestTokens.Create("slice1-member", ["tester"]);
        var response = await Client(token)
            .PostAsJsonAsync($"/api/v1/projects/{_projectId}/executions", new { testCaseVersionId = versionId });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task StartExecution_Outsider_Returns403Forbidden()
    {
        await SeedOnceAsync();
        var token = TestTokens.Create("slice1-outsider", ["tester"]);
        var response = await Client(token)
            .PostAsJsonAsync($"/api/v1/projects/{_projectId}/executions", new { });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorPayload>();
        Assert.Equal("FORBIDDEN", body?.Error.Code);
    }

    [Fact]
    public async Task StartExecution_MemberWithoutExecutePermission_Returns403()
    {
        await SeedOnceAsync();
        var token = TestTokens.Create("slice1-viewer", ["viewer"]);
        var response = await Client(token)
            .PostAsJsonAsync($"/api/v1/projects/{_projectId}/executions", new { });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task StartExecution_UnknownProject_Returns403_Not404()
    {
        // Membership deny must not reveal whether the project exists.
        await SeedOnceAsync();
        var token = TestTokens.Create("slice1-outsider", ["tester"]);
        var response = await Client(token)
            .PostAsJsonAsync($"/api/v1/projects/{Guid.NewGuid()}/executions", new { });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
