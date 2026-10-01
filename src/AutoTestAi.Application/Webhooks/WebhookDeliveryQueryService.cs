using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Tickets;

namespace AutoTestAi.Application.Webhooks;

/// <summary>Authorized delivery history reads (executions.read; no secrets ever leave).</summary>
public sealed class WebhookDeliveryQueryService : IWebhookDeliveryQueryService
{
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;

    private readonly IWebhookDeliveryStore _deliveries;
    private readonly IIntegrationStore _integrations;
    private readonly IAuthorizationService _authorization;

    public WebhookDeliveryQueryService(
        IWebhookDeliveryStore deliveries,
        IIntegrationStore integrations,
        IAuthorizationService authorization)
    {
        _deliveries = deliveries;
        _integrations = integrations;
        _authorization = authorization;
    }

    public async Task<PagedResult<WebhookDeliveryDto>> ListAsync(
        Guid projectId, Guid integrationId, int page, int pageSize, CancellationToken ct)
    {
        await RequireIntegrationAsync(projectId, integrationId, ct);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize <= 0 ? DefaultPageSize : pageSize, 1, MaxPageSize);
        var total = await _deliveries.CountByIntegrationAsync(integrationId, ct);
        var rows = await _deliveries.ListByIntegrationAsync(integrationId, (page - 1) * pageSize, pageSize, ct);
        return new PagedResult<WebhookDeliveryDto>(
            rows.Select(WebhookModelMapper.Map).ToList(), total, page, pageSize);
    }

    public async Task<WebhookDeliveryDto> GetAsync(Guid projectId, Guid deliveryId, CancellationToken ct)
    {
        var delivery = await _deliveries.GetByIdAsync(deliveryId, ct);
        await RequireIntegrationAsync(projectId, delivery?.IntegrationId ?? Guid.Empty, ct);
        if (delivery is null || delivery.ProjectId != projectId)
            throw new NotFoundException("Webhook delivery not found.");
        return WebhookModelMapper.Map(delivery);
    }

    private async Task RequireIntegrationAsync(Guid projectId, Guid integrationId, CancellationToken ct)
    {
        var integration = integrationId == Guid.Empty
            ? null
            : await _integrations.GetByIdAsync(integrationId, ct);
        // Authorize against the project scope (unknown ids => 403, never existence-revealing 404).
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.ExecutionsRead, ct);
        if (integration is null || integration.ProjectId != projectId ||
            !string.Equals(integration.IntegrationType, CiProviderNames.IntegrationType, StringComparison.Ordinal))
            throw new NotFoundException("Webhook integration not found.");
    }
}
