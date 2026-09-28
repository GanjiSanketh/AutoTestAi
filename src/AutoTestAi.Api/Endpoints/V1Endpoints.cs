using System.Reflection;
using AutoTestAi.Api.Health;
using AutoTestAi.Api.Hubs;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Infrastructure.Cache;
using AutoTestAi.Infrastructure.Storage;
using AutoTestAi.Workflows.Abstractions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AutoTestAi.Api.Endpoints;

public sealed record DependencyState(string Name, string Status, string? Detail);
public sealed record ApiHealthReport(
    string Status,
    string Version,
    DateTimeOffset Timestamp,
    IReadOnlyList<DependencyState> Dependencies);
public sealed record StartExecutionRequest(Guid? SuiteId, Guid? EnvironmentId, IReadOnlyList<Guid>? TestCaseIds);
public sealed record StartExecutionResponse(Guid ExecutionId, string WorkflowId, string Status);

/// <summary>Versioned API surface (/api/v1). Phase 0 proves the foundation only.</summary>
public static class V1Endpoints
{
    public static IEndpointRouteBuilder MapV1Endpoints(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1");

        v1.MapGet("/health", async (
                IConfiguration configuration,
                IValkeyCache cache,
                IArtifactStorage storage,
                ITestExecutionWorkflowStarter starter,
                CancellationToken cancellationToken) =>
            {
                var dependencies = new List<DependencyState>
                {
                    await CheckPostgresAsync(configuration, cancellationToken),
                    await CheckValkeyAsync(cache, cancellationToken),
                    await CheckMinioAsync(storage, cancellationToken),
                    CheckTemporal(starter),
                };
                var version = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-phase0";
                return Results.Ok(new ApiHealthReport("healthy", version, DateTimeOffset.UtcNow, dependencies));
            })
            .WithName("GetApiHealth")
            .WithSummary("Phase-0 API health including dependency states.")
            .AllowAnonymous();

        // Proves API → Temporal initiation. Slice 1 adds the authorization
        // boundary (authenticated + executions.execute + project member);
        // full persistence lands in Phase 2+.
        v1.MapPost("/projects/{projectId:guid}/executions", async (
                Guid projectId,
                StartExecutionRequest? request,
                IAuthorizationService authorization,
                ITestExecutionWorkflowStarter starter,
                CancellationToken cancellationToken) =>
            {
                if (projectId == Guid.Empty)
                    throw new ArgumentException("Project id must not be empty.", nameof(projectId));
                await authorization.RequireProjectAccessAsync(
                    projectId, Permissions.ExecutionsExecute, cancellationToken);
                var executionId = Guid.NewGuid();
                var workflowId = await starter.StartTestExecutionAsync(executionId, projectId, cancellationToken);
                return Results.Accepted(
                    $"/api/v1/executions/{executionId}",
                    new StartExecutionResponse(executionId, workflowId, "Queued"));
            })
            .WithName("StartExecution")
            .WithSummary("Start the TestExecutionWorkflow for a project (authorized).")
            .RequireAuthorization();

        return app;
    }

    private static async Task<DependencyState> CheckPostgresAsync(IConfiguration configuration, CancellationToken ct)
    {
        var cs = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(cs))
            return new DependencyState("postgres", "disabled", "Connection string not configured.");
        try
        {
            await using var connection = new Npgsql.NpgsqlConnection(cs);
            await connection.OpenAsync(ct);
            return new DependencyState("postgres", "up", null);
        }
        catch (Exception ex)
        {
            return new DependencyState("postgres", "down", ex.Message);
        }
    }

    private static async Task<DependencyState> CheckValkeyAsync(IValkeyCache cache, CancellationToken ct)
        => !cache.IsConfigured
            ? new DependencyState("valkey", "disabled", "Connection string not configured.")
            : await cache.PingAsync(ct)
                ? new DependencyState("valkey", "up", null)
                : new DependencyState("valkey", "down", "Ping failed.");

    private static async Task<DependencyState> CheckMinioAsync(IArtifactStorage storage, CancellationToken ct)
        => !storage.IsConfigured
            ? new DependencyState("minio", "disabled", "Endpoint/credentials not configured.")
            : await storage.CheckConnectivityAsync(ct)
                ? new DependencyState("minio", "up", null)
                : new DependencyState("minio", "down", "Bucket check failed.");

    private static DependencyState CheckTemporal(ITestExecutionWorkflowStarter starter)
        => starter.IsConfigured
            ? new DependencyState("temporal", "enabled", "Client connects on demand; see /health/ready.")
            : new DependencyState("temporal", "disabled", "Address not configured.");
}
