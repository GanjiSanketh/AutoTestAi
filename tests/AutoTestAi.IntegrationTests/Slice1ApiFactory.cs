using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AutoTestAi.Application.Defects;
using AutoTestAi.Application.Defects;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.Projects;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Infrastructure.Defects;
using AutoTestAi.Infrastructure.Executions;
using AutoTestAi.Infrastructure.Identity;
using AutoTestAi.Infrastructure.Persistence;
using AutoTestAi.Infrastructure.Projects;
using AutoTestAi.Infrastructure.TestCases;
using AutoTestAi.Workflows.Abstractions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace AutoTestAi.IntegrationTests;

/// <summary>Deterministic HMAC test issuer exercising the real JwtBearer handler.</summary>
public static class TestTokens
{
    public static readonly SymmetricSecurityKey SigningKey = new(
        Encoding.UTF8.GetBytes("slice1-test-signing-key-0123456789abcdef"));

    public static string Create(
        string sub,
        string[] roles,
        string? email = null,
        DateTime? expires = null,
        SigningCredentials? credentials = null)
    {
        var claims = new List<Claim> { new("sub", sub), new("name", sub) };
        if (email is not null) claims.Add(new Claim("email", email));
        claims.AddRange(roles.Select(role => new Claim("role", role)));
        var token = new JwtSecurityToken(
            issuer: "https://test.local",
            audience: "autotestai-api",
            claims: claims,
            expires: expires ?? DateTime.UtcNow.AddMinutes(10),
            signingCredentials: credentials ?? new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static string WithWrongSignature(string sub, string[] roles)
        => Create(sub, roles, credentials: new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes("wrong-key-wrong-key-wrong-key-0000")),
            SecurityAlgorithms.HmacSha256));
}

/// <summary>Fake workflow starter isolating authorization tests from Temporal.</summary>
public sealed class FakeWorkflowStarter : ITestExecutionWorkflowStarter
{
    public bool IsConfigured => true;
    public Task<string> StartTestExecutionAsync(Guid executionId, Guid projectId, CancellationToken ct)
        => Task.FromResult($"wf-test-{executionId:N}");
}

/// <summary>Fake coordinator backing Slice-5 execution tests (records starts/cancels).</summary>
public sealed class FakeWorkflowCoordinator : IExecutionWorkflowCoordinator
{
    public bool IsConfigured { get; set; } = true;
    public readonly List<(Guid ExecutionId, Guid ProjectId)> Started = new();
    public readonly List<string> Cancelled = new();
    public Func<string, bool>? CancelHandler { get; set; }

    public Task<string> StartAsync(Guid executionId, Guid projectId, CancellationToken ct)
    {
        Started.Add((executionId, projectId));
        return Task.FromResult($"wf-test-{executionId:N}");
    }

    public Task<bool> CancelAsync(string workflowId, CancellationToken ct)
    {
        Cancelled.Add(workflowId);
        return Task.FromResult(CancelHandler?.Invoke(workflowId) ?? true);
    }
}

/// <summary>
/// Slice-1 factory: real JwtBearer validation (HMAC, offline metadata),
/// InMemory EF instead of Postgres, fake workflow starter.
/// </summary>
public class Slice1ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = $"slice1-{Guid.NewGuid():N}";

    /// <summary>Workflow coordinator fake shared by execution tests.</summary>
    public FakeWorkflowCoordinator WorkflowCoordinator { get; } = new();

    static Slice1ApiFactory()
    {
        // Program reads these eagerly at startup (WebApplication.CreateBuilder time),
        // before ConfigureWebHost sources are merged — so they must be environment
        // variables, not ConfigureAppConfiguration values. Test-process scoped only.
        Environment.SetEnvironmentVariable("Authentication__Authority", "https://test.local/realms/autotestai");
        Environment.SetEnvironmentVariable("Authentication__Audience", "autotestai-api");
        Environment.SetEnvironmentVariable("Authentication__RequireHttpsMetadata", "false");
        Environment.SetEnvironmentVariable("Temporal__Enabled", "false");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = "",
                ["Authentication:Authority"] = "https://test.local/realms/autotestai",
                ["Authentication:Audience"] = "autotestai-api",
                ["Authentication:RequireHttpsMetadata"] = "false",
                ["Temporal:Enabled"] = "false",
                ["Webhooks:BackgroundProcessingEnabled"] = "false",
            }));
        builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme,
                options =>
                {
                    options.Authority = null;
                    options.MetadataAddress = null;
                    options.RequireHttpsMetadata = false;
                    // Preset configuration skips OIDC discovery; signature + lifetime
                    // are still validated by the real handler.
                    options.Configuration = new OpenIdConnectConfiguration();
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = false,
                        ValidateAudience = false,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = TestTokens.SigningKey,
                        ClockSkew = TimeSpan.Zero,
                    };
                });

            services.RemoveAll<DbContextOptions<AutoTestAiDbContext>>();
            services.AddDbContext<AutoTestAiDbContext>(options =>
                options.UseInMemoryDatabase(_databaseName));
            services.RemoveAll<IProjectMembershipStore>();
            services.RemoveAll<IExecutionProjectResolver>();
            services.RemoveAll<IUserDirectory>();
            services.AddScoped<IProjectMembershipStore, EfProjectMembershipStore>();
            services.AddScoped<IExecutionProjectResolver, EfExecutionProjectResolver>();
            services.AddScoped<IUserDirectory, EfUserDirectory>();
            services.RemoveAll<IProjectStore>();
            services.AddScoped<IProjectStore, EfProjectStore>();
            services.RemoveAll<ITestCaseStore>();
            services.AddScoped<ITestCaseStore, EfTestCaseStore>();
            services.RemoveAll<IExecutionStore>();
            services.AddScoped<IExecutionStore, EfExecutionStore>();
            services.RemoveAll<IDefectStore>();
            services.AddScoped<IDefectStore, EfDefectStore>();
            services.RemoveAll<AutoTestAi.Application.Tickets.ITicketStore>();
            services.AddScoped<AutoTestAi.Application.Tickets.ITicketStore, AutoTestAi.Infrastructure.Tickets.EfTicketStore>();
            services.RemoveAll<AutoTestAi.Application.Tickets.IIntegrationStore>();
            services.AddScoped<AutoTestAi.Application.Tickets.IIntegrationStore, AutoTestAi.Infrastructure.Tickets.EfIntegrationStore>();
            services.RemoveAll<AutoTestAi.Application.Tickets.IAutoTicketPolicyStore>();
            services.AddScoped<AutoTestAi.Application.Tickets.IAutoTicketPolicyStore, AutoTestAi.Infrastructure.Tickets.EfAutoTicketPolicyStore>();
            services.RemoveAll<AutoTestAi.Application.Tickets.IAutoTicketQueryStore>();
            services.AddScoped<AutoTestAi.Application.Tickets.IAutoTicketQueryStore, AutoTestAi.Infrastructure.Tickets.EfAutoTicketQueryStore>();
            services.RemoveAll<AutoTestAi.Application.Reports.IReportQueryStore>();
            services.AddScoped<AutoTestAi.Application.Reports.IReportQueryStore, AutoTestAi.Infrastructure.Reports.EfReportQueryStore>();
            services.RemoveAll<AutoTestAi.Application.ExecutionGrid.IGridWorkerStore>();
            services.AddScoped<AutoTestAi.Application.ExecutionGrid.IGridWorkerStore, AutoTestAi.Infrastructure.ExecutionGrid.EfGridWorkerStore>();
            services.RemoveAll<AutoTestAi.Application.ExecutionGrid.IGridAssignmentStore>();
            services.AddScoped<AutoTestAi.Application.ExecutionGrid.IGridAssignmentStore, AutoTestAi.Infrastructure.ExecutionGrid.EfGridAssignmentStore>();
            services.RemoveAll<AutoTestAi.Application.SelfHealing.ISelfHealingPolicyStore>();
            services.AddScoped<AutoTestAi.Application.SelfHealing.ISelfHealingPolicyStore, AutoTestAi.Infrastructure.SelfHealing.EfSelfHealingPolicyStore>();
            services.RemoveAll<AutoTestAi.Application.SelfHealing.ISelfHealingAttemptStore>();
            services.AddScoped<AutoTestAi.Application.SelfHealing.ISelfHealingAttemptStore, AutoTestAi.Infrastructure.SelfHealing.EfSelfHealingAttemptStore>();
            services.RemoveAll<AutoTestAi.Application.Variables.IVariableSetStore>();
            services.AddScoped<AutoTestAi.Application.Variables.IVariableSetStore, AutoTestAi.Infrastructure.Variables.EfVariableSetStore>();
            services.RemoveAll<AutoTestAi.Application.Variables.IExecutionVariablesStore>();
            services.AddScoped<AutoTestAi.Application.Variables.IExecutionVariablesStore, AutoTestAi.Infrastructure.Variables.EfExecutionVariablesStore>();
            services.RemoveAll<AutoTestAi.Application.Variables.ITestSuiteLookup>();
            services.AddScoped<AutoTestAi.Application.Variables.ITestSuiteLookup, AutoTestAi.Infrastructure.Variables.EfTestSuiteLookup>();
            services.RemoveAll<AutoTestAi.Application.Webhooks.IWebhookDeliveryStore>();
            services.AddScoped<AutoTestAi.Application.Webhooks.IWebhookDeliveryStore, AutoTestAi.Infrastructure.Webhooks.EfWebhookDeliveryStore>();
            services.RemoveAll<AutoTestAi.Application.Mobile.IMobileRegistryStore>();
            services.AddScoped<AutoTestAi.Application.Mobile.IMobileRegistryStore, AutoTestAi.Infrastructure.Mobile.EfMobileRegistryStore>();
            services.RemoveAll<AutoTestAi.Application.Mobile.IVisualBaselineStore>();
            services.AddScoped<AutoTestAi.Application.Mobile.IVisualBaselineStore, AutoTestAi.Infrastructure.Mobile.EfVisualBaselineStore>();
            services.RemoveAll<AutoTestAi.Application.Maintenance.IMaintenanceStore>();
            services.AddScoped<AutoTestAi.Application.Maintenance.IMaintenanceStore, AutoTestAi.Infrastructure.Maintenance.EfMaintenanceStore>();
            services.RemoveAll<AutoTestAi.Application.Webhooks.ISuiteMemberLookup>();
            services.AddScoped<AutoTestAi.Application.Webhooks.ISuiteMemberLookup, AutoTestAi.Infrastructure.Webhooks.EfSuiteMemberLookup>();
            services.RemoveAll<AutoTestAi.Application.Secrets.ISecretResolver>();
            services.RemoveAll<AutoTestAi.Application.Secrets.ISecretStore>();
            services.AddScoped<AutoTestAi.Infrastructure.Secrets.EfSecretVault>();
            services.AddScoped<AutoTestAi.Application.Secrets.ISecretResolver>(provider => provider.GetRequiredService<AutoTestAi.Infrastructure.Secrets.EfSecretVault>());
            services.AddScoped<AutoTestAi.Application.Secrets.ISecretStore>(provider => provider.GetRequiredService<AutoTestAi.Infrastructure.Secrets.EfSecretVault>());
            services.AddSingleton<IExecutionWorkflowCoordinator>(WorkflowCoordinator);
            services.RemoveAll<ITestExecutionWorkflowStarter>();
            services.AddSingleton<ITestExecutionWorkflowStarter>(new FakeWorkflowStarter());
        });
    }

    public async Task SeedAsync(Func<AutoTestAiDbContext, Task> seed)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AutoTestAiDbContext>();
        await seed(db);
        await db.SaveChangesAsync();
    }

    public async Task<Domain.Entities.User?> FindUserAsync(string externalIdentityId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AutoTestAiDbContext>();
        return await db.Users.FirstOrDefaultAsync(u => u.ExternalIdentityId == externalIdentityId);
    }
}
