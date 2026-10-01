using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Webhooks;
using Microsoft.Extensions.Options;

namespace AutoTestAi.Api.Endpoints;

public sealed record UpsertCiIntegrationBody(
    string? Provider,
    bool? Enabled,
    Guid? DefaultSuiteId,
    Guid? DefaultEnvironmentId,
    List<string>? EventAllowlist,
    List<string>? BranchAllowlist,
    List<string>? RepositoryAllowlist,
    Dictionary<string, string>? VariableMapping,
    string? Username,
    Dictionary<string, string>? SecretMapping,
    string? WebhookSecret);

/// <summary>
/// Slice 3B CI/CD webhook surface (docs/06 §webhooks). Ingress is
/// AllowAnonymous at the HTTP layer — providers hold no user session — and
/// provider verification is the trust boundary. Management routes require
/// authentication + service-layer permissions (settings.manage for writes and
/// retry, executions.read for reads).
/// </summary>
public static class WebhookEndpoints
{
    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        // ---------- provider ingress (AllowAnonymous + provider verification) ----------
        app.MapPost("/api/v1/webhooks/{provider}/{projectId:guid}/{integrationId:guid}", async (
                string provider,
                Guid projectId,
                Guid integrationId,
                HttpContext context,
                IWebhookIngestionService ingestion,
                IOptions<WebhookOptions> options,
                CancellationToken ct) =>
            {
                var rawBody = await ReadBoundedBodyAsync(context.Request, options.Value.MaxBodyBytes, ct);
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, values) in context.Request.Headers)
                    headers[key] = values.ToString();
                var result = await ingestion.IngestAsync(
                    new WebhookIngressInput(provider, projectId, integrationId, rawBody, headers), ct);
                return result.Outcome switch
                {
                    WebhookIngressResult.Duplicate => Results.Ok(new
                    {
                        outcome = result.Outcome,
                        deliveryId = result.DeliveryId,
                        eventType = result.EventType,
                        executionId = result.ExecutionId,
                        triggeredCount = result.TriggeredCount,
                    }),
                    _ => Results.Accepted(
                        $"/api/v1/projects/{projectId}/integrations/cicd/{provider}/deliveries/{result.DeliveryId}",
                        new
                        {
                            outcome = result.Outcome,
                            deliveryId = result.DeliveryId,
                            eventType = result.EventType,
                        }),
                };
            })
            .AllowAnonymous()
            .WithName("IngestCiWebhook")
            .WithSummary("Provider-signed CI/CD webhook ingress (202 accepted; 200 duplicate).");

        var projects = app.MapGroup("/api/v1/projects").RequireAuthorization();

        // ---------- CI/CD integration management ----------
        projects.MapPut("/{projectId:guid}/integrations/cicd", async (
                Guid projectId,
                UpsertCiIntegrationBody? body,
                ICiIntegrationService service,
                IAuthorizationService authorization,
                CancellationToken ct) =>
            {
                await authorization.RequireProjectAccessAsync(projectId, Permissions.SettingsManage, ct);
                var result = await service.UpsertAsync(new UpsertCiIntegrationCommand(
                    projectId,
                    body?.Provider ?? string.Empty,
                    body?.Enabled ?? true,
                    body?.DefaultSuiteId,
                    body?.DefaultEnvironmentId,
                    body?.EventAllowlist,
                    body?.BranchAllowlist,
                    body?.RepositoryAllowlist,
                    body?.VariableMapping,
                    body?.Username,
                    body?.SecretMapping,
                    body?.WebhookSecret), ct);
                return Results.Ok(result);
            })
            .WithName("UpsertCiIntegration")
            .WithSummary("Create or replace a project's CI/CD integration (settings.manage).");

        projects.MapGet("/{projectId:guid}/integrations/cicd", (
                Guid projectId,
                ICiIntegrationService service,
                CancellationToken ct) =>
            service.ListAsync(projectId, ct))
            .WithName("ListCiIntegrations")
            .WithSummary("List a project's CI/CD integrations (secret metadata only).");

        projects.MapGet("/{projectId:guid}/integrations/cicd/{provider}", async (
                Guid projectId,
                string provider,
                ICiIntegrationService service,
                CancellationToken ct) =>
            {
                var result = await service.GetAsync(projectId, provider, ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            })
            .WithName("GetCiIntegration")
            .WithSummary("Get one CI/CD integration (secret metadata only).");

        // ---------- delivery history + retry ----------
        projects.MapGet("/{projectId:guid}/integrations/cicd/{provider}/deliveries", (
                Guid projectId,
                string provider,
                int? page,
                int? pageSize,
                ICiIntegrationService integrations,
                IWebhookDeliveryQueryService deliveries,
                CancellationToken ct) =>
            ListDeliveriesAsync(projectId, provider, page, pageSize, integrations, deliveries, ct))
            .WithName("ListWebhookDeliveries")
            .WithSummary("Paginated webhook delivery history (executions.read).");

        projects.MapGet("/{projectId:guid}/integrations/cicd/{provider}/deliveries/{deliveryId:guid}", async (
                Guid projectId,
                string provider,
                Guid deliveryId,
                IWebhookDeliveryQueryService deliveries,
                CancellationToken ct) =>
            await deliveries.GetAsync(projectId, deliveryId, ct))
            .WithName("GetWebhookDelivery")
            .WithSummary("One webhook delivery (executions.read).");

        projects.MapPost("/{projectId:guid}/integrations/cicd/{provider}/deliveries/{deliveryId:guid}/retry", async (
                Guid projectId,
                string provider,
                Guid deliveryId,
                IWebhookProcessingService processing,
                IAuthorizationService authorization,
                CancellationToken ct) =>
            {
                await authorization.RequireProjectAccessAsync(projectId, Permissions.SettingsManage, ct);
                await processing.RetryAsync(deliveryId, ct);
                return Results.Accepted();
            })
            .WithName("RetryWebhookDelivery")
            .WithSummary("Re-drive a Failed delivery (settings.manage; idempotent).");

        return app;
    }

    private static async Task<IResult> ListDeliveriesAsync(
        Guid projectId, string provider, int? page, int? pageSize,
        ICiIntegrationService integrations, IWebhookDeliveryQueryService deliveries, CancellationToken ct)
    {
        var integration = await integrations.GetAsync(projectId, provider, ct);
        if (integration is null)
            throw new NotFoundException("Webhook integration not found.");
        var result = await deliveries.ListAsync(projectId, integration.Id, page ?? 1, pageSize ?? 25, ct);
        return Results.Ok(result);
    }

    /// <summary>
    /// Bounded body read: Content-Length pre-check plus a guarded copy so a
    /// lying or chunked sender cannot force unbounded buffering. Throws
    /// WebhookTooLargeException (413) when the limit is exceeded.
    /// </summary>
    internal static async Task<byte[]> ReadBoundedBodyAsync(HttpRequest request, long maxBytes, CancellationToken ct)
    {
        if (request.ContentLength.HasValue && request.ContentLength.Value > maxBytes)
            throw new WebhookTooLargeException($"Webhook body exceeds the {maxBytes} byte limit.");
        const int chunk = 81920;
        using var stream = new MemoryStream();
        var buffer = new byte[chunk];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, ct)) > 0)
        {
            stream.Write(buffer, 0, read);
            if (stream.Length > maxBytes)
                throw new WebhookTooLargeException($"Webhook body exceeds the {maxBytes} byte limit.");
        }
        return stream.ToArray();
    }
}
