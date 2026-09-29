using AutoTestAi.Application.ExecutionGrid;
using AutoTestAi.Domain.Entities;

namespace AutoTestAi.Api.Endpoints;

public sealed record RegisterWorkerBody(
    string? WorkerKey,
    string? DisplayName,
    string? WorkerType,
    string? Framework,
    IReadOnlyList<string>? Browsers,
    string? Version,
    int? Capacity,
    string? BaseUrl,
    string? ProvisioningToken);

public sealed record WorkerHeartbeatBody(
    int? Capacity,
    int? ActiveAssignmentCount,
    string? Version);

/// <summary>
/// Machine-auth endpoint filter for worker-plane routes (Phase 2 Slice 9).
/// Workers authenticate with per-worker Bearer credentials, never Keycloak:
/// the filter binds the route workerId to a validated credential and stores
/// the worker for the handler. Unknown/disabled/mismatched → 401.
/// </summary>
public sealed class GridWorkerAuthFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var service = context.HttpContext.RequestServices
            .GetRequiredService<IExecutionGridService>();
        if (!context.HttpContext.Request.RouteValues.TryGetValue("workerId", out var raw) ||
            !Guid.TryParse(raw?.ToString(), out var workerId))
            return Results.Unauthorized();
        var header = context.HttpContext.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        var credential = header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim() : string.Empty;
        var worker = await service.ValidateWorkerCredentialAsync(
            workerId, credential, context.HttpContext.RequestAborted);
        if (worker is null)
            return Results.Unauthorized();
        context.HttpContext.Items["GridWorker"] = worker;
        return await next(context);
    }
}

/// <summary>
/// Execution-grid surface (Phase 2 Slice 9, docs/06). Two planes:
/// worker machine-auth (register/heartbeat) and Keycloak admin
/// (settings.manage status + lifecycle). No credentials are ever returned.
/// </summary>
public static class ExecutionGridEndpoints
{
    public static IEndpointRouteBuilder MapExecutionGridEndpoints(this IEndpointRouteBuilder app)
    {
        var workers = app.MapGroup("/api/v1/execution-grid/workers");

        workers.MapPost("/register", (
                RegisterWorkerBody? body,
                IExecutionGridService service,
                CancellationToken ct) =>
            service.RegisterWorkerAsync(new RegisterWorkerCommand(
                body?.WorkerKey ?? string.Empty,
                body?.DisplayName,
                body?.WorkerType,
                body?.Framework,
                body?.Browsers,
                body?.Version,
                body?.Capacity,
                body?.BaseUrl,
                body?.ProvisioningToken), ct))
            .WithName("RegisterGridWorker")
            .WithSummary("Register a worker; issues a one-time credential (provisioning token required).")
            .AllowAnonymous();

        workers.MapPost("/{workerId:guid}/heartbeat", async (
                Guid workerId,
                WorkerHeartbeatBody? body,
                IExecutionGridService service,
                HttpContext httpContext,
                CancellationToken ct) =>
            {
                _ = (GridWorker)httpContext.Items["GridWorker"]!;
                var credential = CredentialFrom(httpContext);
                return await service.HeartbeatAsync(new WorkerHeartbeatCommand(
                    workerId, credential,
                    body?.Capacity, body?.ActiveAssignmentCount, body?.Version), ct);
            })
            .WithName("GridWorkerHeartbeat")
            .WithSummary("Worker heartbeat (idempotent; rejects unknown/disabled workers).")
            .AllowAnonymous()
            .AddEndpointFilter<GridWorkerAuthFilter>();

        var grid = app.MapGroup("/api/v1/execution-grid").RequireAuthorization();

        grid.MapGet("/status", (
                IExecutionGridService service,
                CancellationToken ct) =>
            service.GetStatusAsync(ct))
            .WithName("GetGridStatus")
            .WithSummary("Grid summary: workers, capacity, leases, queue (settings.manage).");

        grid.MapGet("/workers", (
                IExecutionGridService service,
                CancellationToken ct) =>
            service.ListWorkersAsync(ct))
            .WithName("ListGridWorkers")
            .WithSummary("Registered workers with effective status (settings.manage).");

        grid.MapGet("/workers/{workerId:guid}", (
                Guid workerId,
                IExecutionGridService service,
                CancellationToken ct) =>
            service.GetWorkerAsync(workerId, ct))
            .WithName("GetGridWorker")
            .WithSummary("One worker with effective status (settings.manage).");

        grid.MapPost("/workers/{workerId:guid}/drain", (
                Guid workerId,
                IExecutionGridService service,
                CancellationToken ct) =>
            service.DrainWorkerAsync(workerId, ct))
            .WithName("DrainGridWorker")
            .WithSummary("Stop new assignments; running work may complete (settings.manage).");

        grid.MapPost("/workers/{workerId:guid}/disable", (
                Guid workerId,
                IExecutionGridService service,
                CancellationToken ct) =>
            service.DisableWorkerAsync(workerId, ct))
            .WithName("DisableGridWorker")
            .WithSummary("Immediately stop all new work on a worker (settings.manage).");

        grid.MapPost("/workers/{workerId:guid}/enable", (
                Guid workerId,
                IExecutionGridService service,
                CancellationToken ct) =>
            service.EnableWorkerAsync(workerId, ct))
            .WithName("EnableGridWorker")
            .WithSummary("Return a disabled/draining worker to service (settings.manage).");

        return app;
    }

    private static string CredentialFrom(HttpContext httpContext)
    {
        var header = httpContext.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? header[prefix.Length..].Trim() : string.Empty;
    }
}
