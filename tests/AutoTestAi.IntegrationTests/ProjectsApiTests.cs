using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.IntegrationTests;

/// <summary>
/// Slice 2: Projects module — auth matrix, IDOR boundary, CRUD, members,
/// environments, validation and conflict behavior (docs/06 §5).
/// </summary>
public sealed class ProjectsApiTests : IClassFixture<Slice1ApiFactory>
{
    private static readonly Guid AdminRoleId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid QaLeadRoleId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TesterRoleId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ViewerRoleId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private readonly Slice1ApiFactory _factory;
    private readonly Guid _projectA = Guid.NewGuid();
    private readonly Guid _projectB = Guid.NewGuid();
    private readonly Guid _envA = Guid.NewGuid();
    private readonly Guid _envB = Guid.NewGuid();
    // xUnit creates one test-class instance per test (fresh _projectA/_projectB
    // guids), so every test seeds its own consistent dataset — same pattern as
    // the Slice 1 ProjectAccessTests. Fixed role rows are inserted idempotently.
    private readonly SemaphoreSlim _seedLock = new(1, 1);

    public ProjectsApiTests(Slice1ApiFactory factory) => _factory = factory;

    private async Task SeedOnceAsync()
    {
        await _seedLock.WaitAsync();
        try
        {
            var (pa, pb, ea, eb) = (_projectA, _projectB, _envA, _envB);
            await _factory.SeedAsync(db =>
            {
                if (!db.Roles.Any())
                {
                    db.Roles.AddRange(
                        new Role { Id = AdminRoleId, Name = "admin" },
                        new Role { Id = QaLeadRoleId, Name = "qa-lead" },
                        new Role { Id = TesterRoleId, Name = "tester" },
                        new Role { Id = ViewerRoleId, Name = "viewer" });
                }

                var admin = new User { ExternalIdentityId = "s2-admin", Email = "a@x", DisplayName = "Admin" };
                var mgr = new User { ExternalIdentityId = "s2-manager", Email = "m@x", DisplayName = "Manager" };
                var member = new User { ExternalIdentityId = "s2-member", Email = "t@x", DisplayName = "Member" };
                var viewer = new User { ExternalIdentityId = "s2-viewer", Email = "v@x", DisplayName = "Viewer" };
                var outsider = new User { ExternalIdentityId = "s2-outsider", Email = "o@x", DisplayName = "Outsider" };
                var newbie = new User { ExternalIdentityId = "s2-newbie", Email = "n@x", DisplayName = "Newbie" };
                db.Users.AddRange(admin, mgr, member, viewer, outsider, newbie);

                db.Projects.AddRange(
                    new Project { Id = pa, Name = "Project Alpha", Key = "PROJA", DefaultEnvironmentId = ea },
                    new Project { Id = pb, Name = "Project Beta", Key = "PROJB", DefaultEnvironmentId = eb });

                db.ProjectMembers.AddRange(
                    new ProjectMember { ProjectId = pa, UserId = mgr.Id, RoleId = QaLeadRoleId },
                    new ProjectMember { ProjectId = pa, UserId = member.Id, RoleId = TesterRoleId },
                    new ProjectMember { ProjectId = pa, UserId = viewer.Id, RoleId = ViewerRoleId });

                db.Environments.AddRange(
                    new TestEnvironment { Id = ea, ProjectId = pa, Name = "QA", BaseUrl = "https://qa.alpha.example" },
                    new TestEnvironment { Id = eb, ProjectId = pb, Name = "QA", BaseUrl = "https://qa.beta.example" });
                return Task.CompletedTask;
            });
        }
        finally
        {
            _seedLock.Release();
        }
    }

    private HttpClient Client(string? sub, string[]? roles = null)
    {
        var client = _factory.CreateClient();
        if (sub is not null)
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", TestTokens.Create(sub, roles ?? ["tester"]));
        return client;
    }

    private static string Admin() => TestTokens.Create("s2-admin", ["admin"]);
    private static string Manager() => TestTokens.Create("s2-manager", ["qa-lead"]);
    private static string Member() => TestTokens.Create("s2-member", ["tester"]);
    private static string Viewer() => TestTokens.Create("s2-viewer", ["viewer"]);
    private static string Outsider() => TestTokens.Create("s2-outsider", ["tester"]);

    private sealed record PagePayload(IReadOnlyList<ProjectPayload> Items, int TotalCount, int Page, int PageSize);
    private sealed record ProjectPayload(
        Guid Id, string Name, string Key, string? Description, string? RepositoryUrl, string? TargetUrl,
        string? Framework, string? Platform, string Status, Guid? DefaultEnvironmentId,
        EnvSummaryPayload? DefaultEnvironment, int MemberCount, Guid? CreatedBy,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    private sealed record EnvSummaryPayload(Guid Id, string Name, string? BaseUrl, string Status);
    private sealed record MemberPayload(
        Guid UserId, string Email, string DisplayName, Guid RoleId, string RoleName, DateTimeOffset CreatedAt);
    private sealed record EnvPayload(
        Guid Id, Guid ProjectId, string Name, string? BaseUrl, string Status, bool IsDefault,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    private sealed record ErrorPayload(ErrorDetail Error);
    private sealed record ErrorDetail(string Code, string Message);

    // ---------- auth matrix ----------

    [Fact]
    public async Task List_Anonymous_Returns401()
    {
        await SeedOnceAsync();
        var response = await Client(null).GetAsync("/api/v1/projects");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Details_Anonymous_Returns401()
    {
        await SeedOnceAsync();
        var response = await Client(null).GetAsync($"/api/v1/projects/{_projectA}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_OutsiderSeesNothing_MemberSeesOwn_AdminSeesAll()
    {
        await SeedOnceAsync();
        var outsider = await Client("s2-outsider").GetFromJsonAsync<PagePayload>("/api/v1/projects");
        Assert.NotNull(outsider);
        Assert.Empty(outsider!.Items);

        var member = await Client("s2-member").GetFromJsonAsync<PagePayload>("/api/v1/projects");
        Assert.NotNull(member);
        Assert.Contains(member!.Items, p => p.Id == _projectA);
        Assert.DoesNotContain(member.Items, p => p.Id == _projectB);

        var admin = await Client("s2-admin", ["admin"]).GetFromJsonAsync<PagePayload>("/api/v1/projects");
        Assert.NotNull(admin);
        Assert.Contains(admin!.Items, p => p.Id == _projectA);
        Assert.Contains(admin.Items, p => p.Id == _projectB);
    }

    [Fact]
    public async Task List_Search_FiltersByNameOrKey()
    {
        await SeedOnceAsync();
        var client = Client("s2-admin", ["admin"]);
        var result = await client.GetFromJsonAsync<PagePayload>("/api/v1/projects?search=PROJA");
        Assert.NotNull(result);
        Assert.Contains(result!.Items, p => p.Id == _projectA);
        Assert.DoesNotContain(result.Items, p => p.Id == _projectB);
    }

    [Fact]
    public async Task List_Pagination_Respected()
    {
        await SeedOnceAsync();
        var result = await Client("s2-admin", ["admin"])
            .GetFromJsonAsync<PagePayload>("/api/v1/projects?page=1&pageSize=1");
        Assert.NotNull(result);
        Assert.Single(result!.Items);
        Assert.True(result.TotalCount >= 2);
        Assert.Equal(1, result.Page);
        Assert.Equal(1, result.PageSize);
    }

    // ---------- IDOR boundary ----------

    [Theory]
    [InlineData("details")]
    [InlineData("members")]
    [InlineData("environments")]
    public async Task CrossProjectReads_AsNonMember_Return403(string segment)
    {
        await SeedOnceAsync();
        var response = segment == "details"
            ? await Client("s2-member").GetAsync($"/api/v1/projects/{_projectB}")
            : await Client("s2-member").GetAsync($"/api/v1/projects/{_projectB}/{segment}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorPayload>();
        Assert.Equal("FORBIDDEN", body?.Error.Code);
    }

    [Fact]
    public async Task CrossProjectWrites_AsNonMember_Return403()
    {
        await SeedOnceAsync();
        var client = Client("s2-member");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(
            $"/api/v1/projects/{_projectB}", new { name = "Hacked" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync(
            $"/api/v1/projects/{_projectB}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(
            $"/api/v1/projects/{_projectB}/members", new { userId = Guid.NewGuid(), roleId = TesterRoleId })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(
            $"/api/v1/projects/{_projectB}/environments", new { name = "Evil" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(
            $"/api/v1/environments/{_envB}", new { name = "Evil" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync(
            $"/api/v1/environments/{_envB}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(
            $"/api/v1/projects/{_projectB}/executions", new { })).StatusCode);
    }

    // ---------- create ----------

    [Fact]
    public async Task Create_AsManager_Returns201_AndGrantsMembership()
    {
        await SeedOnceAsync();
        var create = await Client("s2-manager", ["qa-lead"]).PostAsJsonAsync("/api/v1/projects", new
        {
            name = "Slice Two",
            key = "sl2-new",
            description = "Created by test",
            repositoryUrl = "https://git.example/sl2",
            targetUrl = "https://sl2.example",
            framework = "playwright",
            platform = "web",
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var project = await create.Content.ReadFromJsonAsync<ProjectPayload>();
        Assert.NotNull(project);
        Assert.Equal("SL2-NEW", project!.Key); // normalized to predictable format
        Assert.Equal(1, project.MemberCount);

        var members = await Client("s2-manager", ["qa-lead"])
            .GetFromJsonAsync<List<MemberPayload>>($"/api/v1/projects/{project.Id}/members");
        Assert.NotNull(members);
        var creator = await _factory.FindUserAsync("s2-manager");
        Assert.Contains(members!, m => m.UserId == creator!.Id);
    }

    [Fact]
    public async Task Create_AsViewer_Returns403()
    {
        await SeedOnceAsync();
        var response = await Client("s2-viewer", ["viewer"])
            .PostAsJsonAsync("/api/v1/projects", new { name = "Nope", key = "NOPE1" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Create_DuplicateKey_Returns409()
    {
        await SeedOnceAsync();
        var response = await Client("s2-manager", ["qa-lead"])
            .PostAsJsonAsync("/api/v1/projects", new { name = "Dup", key = "PROJA" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorPayload>();
        Assert.Equal("CONFLICT", body?.Error.Code);
    }

    [Fact]
    public async Task Create_InvalidBody_Returns400_WithFieldDetails()
    {
        await SeedOnceAsync();
        var response = await Client("s2-manager", ["qa-lead"])
            .PostAsJsonAsync("/api/v1/projects", new
            {
                name = "",
                key = "bad key!",
                targetUrl = "not-a-url",
                status = "Bogus",
            });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonDocument>();
        Assert.NotNull(doc);
        var details = doc!.RootElement.GetProperty("error").GetProperty("details");
        var fields = details.EnumerateArray()
            .Select(e => e.GetProperty("field").GetString()).ToHashSet();
        Assert.Contains("name", fields);
        Assert.Contains("key", fields);
        Assert.Contains("targetUrl", fields);
    }

    // ---------- details / update / archive ----------

    [Fact]
    public async Task Details_AsMember_ReturnsProject_WithDefaultEnvironment()
    {
        await SeedOnceAsync();
        var project = await Client("s2-member")
            .GetFromJsonAsync<ProjectPayload>($"/api/v1/projects/{_projectA}");
        Assert.NotNull(project);
        Assert.Equal("PROJA", project!.Key);
        Assert.Equal(_envA, project.DefaultEnvironmentId);
        Assert.NotNull(project.DefaultEnvironment);
        Assert.Equal("QA", project.DefaultEnvironment!.Name);
        Assert.Equal(3, project.MemberCount);
    }

    [Fact]
    public async Task Update_AsManager_Persists_AndKeepsKeyImmutable()
    {
        await SeedOnceAsync();
        var response = await Client("s2-manager", ["qa-lead"]).PutAsJsonAsync(
            $"/api/v1/projects/{_projectA}",
            new { name = "Alpha Renamed", description = "Updated", platform = "web", framework = "playwright" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var project = await response.Content.ReadFromJsonAsync<ProjectPayload>();
        Assert.Equal("Alpha Renamed", project!.Name);
        Assert.Equal("PROJA", project.Key); // immutable: no key field accepted
    }

    [Fact]
    public async Task Update_AsViewer_Returns403()
    {
        await SeedOnceAsync();
        var response = await Client("s2-viewer", ["viewer"]).PutAsJsonAsync(
            $"/api/v1/projects/{_projectA}", new { name = "Hacked" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Update_UnknownProject_AsAdmin_Returns404()
    {
        await SeedOnceAsync();
        var response = await Client("s2-admin", ["admin"]).PutAsJsonAsync(
            $"/api/v1/projects/{Guid.NewGuid()}", new { name = "Ghost" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_DefaultEnvironment_FromOtherProject_Returns400()
    {
        await SeedOnceAsync();
        var response = await Client("s2-manager", ["qa-lead"]).PutAsJsonAsync(
            $"/api/v1/projects/{_projectA}", new { name = "Alpha", defaultEnvironmentId = _envB });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Archive_SoftDeletes_PreservingRow()
    {
        await SeedOnceAsync();
        var delete = await Client("s2-manager", ["qa-lead"]).DeleteAsync($"/api/v1/projects/{_projectA}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var project = await Client("s2-admin", ["admin"])
            .GetFromJsonAsync<ProjectPayload>($"/api/v1/projects/{_projectA}");
        Assert.Equal("Archived", project!.Status);
    }

    [Fact]
    public async Task Archive_AsOutsider_Returns403()
    {
        await SeedOnceAsync();
        var response = await Client("s2-outsider").DeleteAsync($"/api/v1/projects/{_projectB}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---------- members ----------

    [Fact]
    public async Task Members_List_AsMember_ReturnsSafeFields()
    {
        await SeedOnceAsync();
        var members = await Client("s2-member")
            .GetFromJsonAsync<List<MemberPayload>>($"/api/v1/projects/{_projectA}/members");
        Assert.NotNull(members);
        Assert.Equal(3, members!.Count);
        Assert.All(members, m =>
        {
            Assert.NotEqual(Guid.Empty, m.UserId);
            Assert.False(string.IsNullOrWhiteSpace(m.Email));
            Assert.False(string.IsNullOrWhiteSpace(m.RoleName));
        });
    }

    [Fact]
    public async Task Members_AddDuplicateOrUnknown_Returns409Or404()
    {
        await SeedOnceAsync();
        var client = Client("s2-manager", ["qa-lead"]);
        var newbie = await _factory.FindUserAsync("s2-newbie");
        Assert.NotNull(newbie);

        var added = await client.PostAsJsonAsync($"/api/v1/projects/{_projectA}/members",
            new { userId = newbie!.Id, roleId = TesterRoleId });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);

        var duplicate = await client.PostAsJsonAsync($"/api/v1/projects/{_projectA}/members",
            new { userId = newbie.Id, roleId = TesterRoleId });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var unknownUser = await client.PostAsJsonAsync($"/api/v1/projects/{_projectA}/members",
            new { userId = Guid.NewGuid(), roleId = TesterRoleId });
        Assert.Equal(HttpStatusCode.NotFound, unknownUser.StatusCode);

        var unknownRole = await client.PostAsJsonAsync($"/api/v1/projects/{_projectA}/members",
            new { userId = newbie.Id, roleId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, unknownRole.StatusCode);
    }

    [Fact]
    public async Task Members_AddByEmail_ResolvesUser()
    {
        await SeedOnceAsync();
        var response = await Client("s2-manager", ["qa-lead"]).PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/members",
            new { email = "n@x", roleId = TesterRoleId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var member = await response.Content.ReadFromJsonAsync<MemberPayload>();
        var newbie = await _factory.FindUserAsync("s2-newbie");
        Assert.Equal(newbie!.Id, member!.UserId);

        // Cleanup so later assertions on member counts stay deterministic.
        var removed = await Client("s2-manager", ["qa-lead"])
            .DeleteAsync($"/api/v1/projects/{_projectA}/members/{newbie.Id}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
    }

    [Fact]
    public async Task Roles_Anonymous_Returns401_Authenticated_ReturnsReferenceData()
    {
        await SeedOnceAsync();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Client(null).GetAsync("/api/v1/roles")).StatusCode);

        var roles = await Client("s2-viewer", ["viewer"]).GetFromJsonAsync<List<RolePayload>>("/api/v1/roles");
        Assert.NotNull(roles);
        Assert.Contains(roles!, r => r.Name == "qa-lead");
        Assert.Contains(roles!, r => r.Name == "tester");
    }

    private sealed record RolePayload(Guid Id, string Name, string? Description);

    [Fact]
    public async Task Members_AddAsViewer_Returns403()
    {
        await SeedOnceAsync();
        var newbie = await _factory.FindUserAsync("s2-newbie");
        var response = await Client("s2-viewer", ["viewer"]).PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/members",
            new { userId = newbie!.Id, roleId = TesterRoleId });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Members_UpdateRole_And_Remove()
    {
        await SeedOnceAsync();
        var client = Client("s2-manager", ["qa-lead"]);
        var newbie = await _factory.FindUserAsync("s2-newbie");
        Assert.NotNull(newbie);

        var add = await client.PostAsJsonAsync($"/api/v1/projects/{_projectA}/members",
            new { userId = newbie!.Id, roleId = TesterRoleId });
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);

        var updated = await client.PutAsJsonAsync(
            $"/api/v1/projects/{_projectA}/members/{newbie.Id}", new { roleId = ViewerRoleId });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var member = await updated.Content.ReadFromJsonAsync<MemberPayload>();
        Assert.Equal("viewer", member!.RoleName);

        var unknown = await client.PutAsJsonAsync(
            $"/api/v1/projects/{_projectA}/members/{Guid.NewGuid()}", new { roleId = ViewerRoleId });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var removed = await client.DeleteAsync($"/api/v1/projects/{_projectA}/members/{newbie.Id}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        var removedAgain = await client.DeleteAsync($"/api/v1/projects/{_projectA}/members/{newbie.Id}");
        Assert.Equal(HttpStatusCode.NotFound, removedAgain.StatusCode);
    }

    // ---------- environments ----------

    [Fact]
    public async Task Environments_Crud_AsManager()
    {
        await SeedOnceAsync();
        var client = Client("s2-manager", ["qa-lead"]);

        var created = await client.PostAsJsonAsync($"/api/v1/projects/{_projectA}/environments",
            new { name = "Staging", baseUrl = "https://staging.alpha.example" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var env = await created.Content.ReadFromJsonAsync<EnvPayload>();
        Assert.NotNull(env);
        Assert.False(env!.IsDefault);

        var listed = await client.GetFromJsonAsync<List<EnvPayload>>(
            $"/api/v1/projects/{_projectA}/environments");
        Assert.NotNull(listed);
        Assert.Contains(listed!, e => e.Id == env.Id);

        var updated = await client.PutAsJsonAsync($"/api/v1/environments/{env.Id}",
            new { name = "Staging EU", setAsDefault = true });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var updatedEnv = await updated.Content.ReadFromJsonAsync<EnvPayload>();
        Assert.True(updatedEnv!.IsDefault);

        var project = await client.GetFromJsonAsync<ProjectPayload>($"/api/v1/projects/{_projectA}");
        Assert.Equal(env.Id, project!.DefaultEnvironmentId);

        var deleted = await client.DeleteAsync($"/api/v1/environments/{env.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var afterDelete = await client.GetFromJsonAsync<ProjectPayload>($"/api/v1/projects/{_projectA}");
        Assert.Null(afterDelete!.DefaultEnvironmentId); // default cleared safely
    }

    [Fact]
    public async Task Environments_CreateAsViewer_Returns403()
    {
        await SeedOnceAsync();
        var response = await Client("s2-viewer", ["viewer"]).PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/environments", new { name = "Nope" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Environments_CreateInvalid_Returns400()
    {
        await SeedOnceAsync();
        var response = await Client("s2-manager", ["qa-lead"]).PostAsJsonAsync(
            $"/api/v1/projects/{_projectA}/environments", new { name = "", baseUrl = "nope" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
