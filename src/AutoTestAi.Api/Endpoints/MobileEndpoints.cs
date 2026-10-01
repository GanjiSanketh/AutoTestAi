using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Mobile;

namespace AutoTestAi.Api.Endpoints;

public sealed record CreateMobilePoolBody(string? Name, string? Platform);
public sealed record UpdateMobilePoolBody(string? Name, bool? Enabled, string? RowVersion);
public sealed record RegisterMobileDeviceBody(
    Guid? PoolId,
    string? Platform,
    string? PlatformVersion,
    string? Manufacturer,
    string? Model,
    string? Udid,
    string? AutomationName);
public sealed record UpdateMobileDeviceBody(
    string? PlatformVersion,
    string? Manufacturer,
    string? Model,
    string? Udid,
    string? AutomationName,
    bool? Enabled,
    string? RowVersion);
public sealed record CreateMobileAppBody(
    string? Platform,
    string? Name,
    string? PackageId,
    string? BundleId,
    string? Version,
    string? StorageKey,
    string? InstallPolicy,
    string? LaunchActivity,
    string? DeepLink);
public sealed record UpdateMobileAppBody(
    string? Name,
    string? PackageId,
    string? BundleId,
    string? Version,
    string? StorageKey,
    string? InstallPolicy,
    string? LaunchActivity,
    string? DeepLink,
    string? RowVersion);

/// <summary>
/// Mobile registry surface (Phase 3 Slice 3C-1/3C-2, docs/06 mobile registry).
/// Administrative configuration only: pools, devices, apps. No slot leasing,
/// session, worker, or execution endpoints exist in this checkpoint.
/// All routes require authentication; the service layer enforces permissions
/// (settings.manage writes, executions.read reads) + project membership.
/// </summary>
public static class MobileEndpoints
{
    public static IEndpointRouteBuilder MapMobileEndpoints(this IEndpointRouteBuilder app)
    {
        var projects = app.MapGroup("/api/v1/projects").RequireAuthorization();

        // ---------- pools ----------
        projects.MapGet("/{projectId:guid}/mobile/pools", (
                Guid projectId,
                IMobilePoolService service,
                CancellationToken ct) =>
            service.ListAsync(projectId, ct))
            .WithName("ListMobilePools")
            .WithSummary("List a project's mobile device pools.");

        projects.MapPost("/{projectId:guid}/mobile/pools", async (
                Guid projectId,
                CreateMobilePoolBody? body,
                IMobilePoolService service,
                CancellationToken ct) =>
            {
                var result = await service.CreateAsync(new CreateMobilePoolCommand(
                    projectId, body?.Name ?? string.Empty, body?.Platform ?? string.Empty), ct);
                return Results.Created($"/api/v1/projects/{projectId}/mobile/pools/{result.Id}", result);
            })
            .WithName("CreateMobilePool")
            .WithSummary("Create a mobile device pool (settings.manage).");

        projects.MapGet("/{projectId:guid}/mobile/pools/{poolId:guid}", (
                Guid projectId,
                Guid poolId,
                IMobilePoolService service,
                CancellationToken ct) =>
            service.GetAsync(projectId, poolId, ct))
            .WithName("GetMobilePool")
            .WithSummary("One mobile device pool (project ownership enforced).");

        projects.MapPut("/{projectId:guid}/mobile/pools/{poolId:guid}", (
                Guid projectId,
                Guid poolId,
                UpdateMobilePoolBody? body,
                IMobilePoolService service,
                CancellationToken ct) =>
            service.UpdateAsync(projectId, new UpdateMobilePoolCommand(
                poolId, body?.Name ?? string.Empty, body?.Enabled ?? true,
                ParseRowVersion(body?.RowVersion)), ct))
            .WithName("UpdateMobilePool")
            .WithSummary("Rename/enable/disable a pool (settings.manage, optimistic concurrency).");

        projects.MapPost("/{projectId:guid}/mobile/pools/{poolId:guid}/enable", (
                Guid projectId,
                Guid poolId,
                IMobilePoolService service,
                IAuthorizationService authorization,
                CancellationToken ct) =>
            SetPoolEnabledAsync(poolId, true, null, service, authorization, projectId, ct))
            .WithName("EnableMobilePool")
            .WithSummary("Enable a mobile device pool (settings.manage).");

        projects.MapPost("/{projectId:guid}/mobile/pools/{poolId:guid}/disable", (
                Guid projectId,
                Guid poolId,
                IMobilePoolService service,
                IAuthorizationService authorization,
                CancellationToken ct) =>
            SetPoolEnabledAsync(poolId, false, null, service, authorization, projectId, ct))
            .WithName("DisableMobilePool")
            .WithSummary("Disable a mobile device pool without destroying devices/history.");

        // ---------- devices ----------
        projects.MapGet("/{projectId:guid}/mobile/devices", (
                Guid projectId,
                Guid? poolId,
                IMobileDeviceService service,
                CancellationToken ct) =>
            service.ListAsync(projectId, poolId, ct))
            .WithName("ListMobileDevices")
            .WithSummary("List a project's mobile devices, optionally filtered by pool.");

        projects.MapPost("/{projectId:guid}/mobile/devices", async (
                Guid projectId,
                RegisterMobileDeviceBody? body,
                IMobileDeviceService service,
                CancellationToken ct) =>
            {
                var result = await service.RegisterAsync(new RegisterMobileDeviceCommand(
                    projectId, body?.PoolId ?? Guid.Empty,
                    body?.Platform ?? string.Empty, body?.PlatformVersion,
                    body?.Manufacturer, body?.Model, body?.Udid,
                    body?.AutomationName ?? string.Empty), ct);
                return Results.Created($"/api/v1/projects/{projectId}/mobile/devices/{result.Id}", result);
            })
            .WithName("RegisterMobileDevice")
            .WithSummary("Register a device; atomically creates exactly one default slot.");

        projects.MapGet("/{projectId:guid}/mobile/devices/{deviceId:guid}", (
                Guid projectId,
                Guid deviceId,
                IMobileDeviceService service,
                CancellationToken ct) =>
            service.GetAsync(projectId, deviceId, ct))
            .WithName("GetMobileDevice")
            .WithSummary("One mobile device (project ownership enforced).");

        projects.MapPut("/{projectId:guid}/mobile/devices/{deviceId:guid}", (
                Guid projectId,
                Guid deviceId,
                UpdateMobileDeviceBody? body,
                IMobileDeviceService service,
                CancellationToken ct) =>
            service.UpdateAsync(projectId, new UpdateMobileDeviceCommand(
                deviceId, body?.PlatformVersion, body?.Manufacturer, body?.Model,
                body?.Udid, body?.AutomationName, body?.Enabled ?? true,
                ParseRowVersion(body?.RowVersion)), ct))
            .WithName("UpdateMobileDevice")
            .WithSummary("Edit device metadata/enable state (settings.manage, optimistic concurrency).");

        projects.MapPost("/{projectId:guid}/mobile/devices/{deviceId:guid}/enable", (
                Guid projectId,
                Guid deviceId,
                IMobileDeviceService service,
                IAuthorizationService authorization,
                CancellationToken ct) =>
            SetDeviceEnabledAsync(deviceId, true, service, authorization, projectId, ct))
            .WithName("EnableMobileDevice")
            .WithSummary("Enable a mobile device (settings.manage).");

        projects.MapPost("/{projectId:guid}/mobile/devices/{deviceId:guid}/disable", (
                Guid projectId,
                Guid deviceId,
                IMobileDeviceService service,
                IAuthorizationService authorization,
                CancellationToken ct) =>
            SetDeviceEnabledAsync(deviceId, false, service, authorization, projectId, ct))
            .WithName("DisableMobileDevice")
            .WithSummary("Disable a mobile device without destroying history.");

        // ---------- apps ----------
        projects.MapGet("/{projectId:guid}/mobile/apps", (
                Guid projectId,
                IMobileAppService service,
                CancellationToken ct) =>
            service.ListAsync(projectId, ct))
            .WithName("ListMobileApps")
            .WithSummary("List a project's mobile applications under test.");

        projects.MapPost("/{projectId:guid}/mobile/apps", async (
                Guid projectId,
                CreateMobileAppBody? body,
                IMobileAppService service,
                CancellationToken ct) =>
            {
                var result = await service.CreateAsync(new CreateMobileAppCommand(
                    projectId, body?.Platform ?? string.Empty, body?.Name ?? string.Empty,
                    body?.PackageId, body?.BundleId, body?.Version, body?.StorageKey,
                    body?.InstallPolicy ?? string.Empty, body?.LaunchActivity, body?.DeepLink), ct);
                return Results.Created($"/api/v1/projects/{projectId}/mobile/apps/{result.Id}", result);
            })
            .WithName("CreateMobileApp")
            .WithSummary("Register mobile application metadata (settings.manage; reference only).");

        projects.MapGet("/{projectId:guid}/mobile/apps/{appId:guid}", (
                Guid projectId,
                Guid appId,
                IMobileAppService service,
                CancellationToken ct) =>
            service.GetAsync(projectId, appId, ct))
            .WithName("GetMobileApp")
            .WithSummary("One mobile application (project ownership enforced).");

        projects.MapPut("/{projectId:guid}/mobile/apps/{appId:guid}", (
                Guid projectId,
                Guid appId,
                UpdateMobileAppBody? body,
                IMobileAppService service,
                CancellationToken ct) =>
            service.UpdateAsync(projectId, new UpdateMobileAppCommand(
                appId, body?.Name ?? string.Empty, body?.PackageId, body?.BundleId,
                body?.Version, body?.StorageKey, body?.InstallPolicy ?? string.Empty,
                body?.LaunchActivity, body?.DeepLink,
                ParseRowVersion(body?.RowVersion)), ct))
            .WithName("UpdateMobileApp")
            .WithSummary("Edit mobile application metadata (settings.manage, optimistic concurrency).");

        return app;
    }

    // Enable/disable preserve stored values and skip the version check
    // (administrative intent). Project isolation, authorization, and audit
    // are enforced inside UpdateAsync.
    private static async Task<IResult> SetPoolEnabledAsync(
        Guid poolId, bool enabled, byte[]? rowVersion,
        IMobilePoolService service, IAuthorizationService authorization,
        Guid projectId, CancellationToken ct)
    {
        await authorization.RequireProjectAccessAsync(projectId, Permissions.SettingsManage, ct);
        var current = await service.GetAsync(projectId, poolId, ct);
        var result = await service.UpdateAsync(projectId,
            new UpdateMobilePoolCommand(poolId, current.Name, enabled, rowVersion), ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> SetDeviceEnabledAsync(
        Guid deviceId, bool enabled,
        IMobileDeviceService service, IAuthorizationService authorization,
        Guid projectId, CancellationToken ct)
    {
        await authorization.RequireProjectAccessAsync(projectId, Permissions.SettingsManage, ct);
        var current = await service.GetAsync(projectId, deviceId, ct);
        var result = await service.UpdateAsync(projectId, new UpdateMobileDeviceCommand(
            deviceId, current.PlatformVersion, current.Manufacturer, current.Model,
            current.Udid, current.AutomationName, enabled, null), ct);
        return Results.Ok(result);
    }

    private static byte[]? ParseRowVersion(string? rowVersion)
    {
        if (string.IsNullOrWhiteSpace(rowVersion)) return null;
        try { return Convert.FromBase64String(rowVersion.Trim()); }
        catch (FormatException)
        {
            throw new ValidationException("Row version is invalid.",
                new[] { new FieldError("rowVersion", "Must be base64.") });
        }
    }
}
