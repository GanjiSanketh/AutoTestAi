using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.TestGeneration;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Application.Mobile;

/// <summary>
/// Project-scoped mobile device registry (Phase 3 Slice 3C-1/3C-2).
/// Registration atomically creates the device plus exactly one default
/// slot (SlotNumber 1) in a single SaveChanges unit. No claim, release,
/// expiry, session, or scheduling behavior exists in this checkpoint.
/// Mutations require settings.manage; reads require executions.read.
/// </summary>
public sealed class MobileDeviceService : IMobileDeviceService
{
    private readonly IMobileRegistryStore _store;
    private readonly IAuthorizationService _authorization;
    private readonly IDateTimeProvider _clock;
    private readonly IAuditService _audit;

    public MobileDeviceService(
        IMobileRegistryStore store,
        IAuthorizationService authorization,
        IDateTimeProvider clock,
        IAuditService audit)
    {
        _store = store;
        _authorization = authorization;
        _clock = clock;
        _audit = audit;
    }

    public async Task<MobileDeviceDto> RegisterAsync(RegisterMobileDeviceCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _authorization.RequireProjectAccessAsync(command.ProjectId, Permissions.SettingsManage, ct);

        var errors = new List<FieldError>();
        var platform = MobileValidation.ParsePlatform(command.Platform, "platform", errors);
        var automationName = MobileValidation.ParseAutomationName(command.AutomationName, platform, errors);
        var platformVersion = MobileValidation.OptionalBounded(command.PlatformVersion, MobileValidation.MaxPlatformVersionLength, "platformVersion", errors);
        var manufacturer = MobileValidation.OptionalBounded(command.Manufacturer, MobileValidation.MaxManufacturerLength, "manufacturer", errors);
        var model = MobileValidation.OptionalBounded(command.Model, MobileValidation.MaxModelLength, "model", errors);
        var udid = MobileValidation.OptionalBounded(command.Udid, MobileValidation.MaxUdidLength, "udid", errors);
        ValidationException.ThrowIfInvalid(errors);

        var pool = await _store.GetPoolByIdAsync(command.PoolId, ct);
        if (pool is null || pool.ProjectId != command.ProjectId)
            throw new ValidationException("The specified pool does not belong to this project.",
                new[] { new FieldError("poolId", "Pool must belong to the project.") });
        if (pool.Platform != platform)
            throw new ValidationException("Device platform must match the pool platform.",
                new[] { new FieldError("platform", "Device platform must match the pool platform.") });
        if (pool.Status != MobilePoolStatus.Active)
            throw new ValidationException("Devices cannot be registered in a disabled pool.",
                new[] { new FieldError("poolId", "Pool must be active.") });
        if (udid is not null)
        {
            var clash = await _store.FindDeviceByUdidAsync(command.ProjectId, udid, ct);
            if (clash is not null)
                throw new ConflictException("A device with this UDID is already registered in this project.");
        }

        var now = _clock.UtcNow;
        var device = new MobileDevice
        {
            ProjectId = command.ProjectId,
            PoolId = pool.Id,
            Platform = platform,
            PlatformVersion = platformVersion,
            Manufacturer = manufacturer,
            Model = model,
            Udid = udid,
            AutomationName = automationName,
            Status = MobileDeviceStatus.Available,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var slot = new MobileDeviceSlot
        {
            ProjectId = command.ProjectId,
            PoolId = pool.Id,
            DeviceId = device.Id,
            SlotNumber = 1,
            Status = MobileSlotStatus.Free,
            CreatedAt = now,
            UpdatedAt = now,
        };
        try
        {
            // Single SaveChanges unit: device without its default slot (or
            // vice versa) can never persist — no partial registration.
            await _store.AddDeviceAsync(device, ct);
            await _store.AddSlotAsync(slot, ct);
            await _store.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsConcurrencyConflict(ex))
        {
            throw new ConflictException("The device registry was modified concurrently. Reload and retry.");
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            throw new ConflictException("A device with this identity is already registered in this project.");
        }

        await _audit.RecordAsync("mobile.device_registered", "mobile_device",
            device.Id.ToString(), command.ProjectId, SafeMeta(device, pool), ct);
        return await MapAsync(device, pool.Name, ct);
    }

    public async Task<MobileDeviceDto> GetAsync(Guid projectId, Guid deviceId, CancellationToken ct)
    {
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.ExecutionsRead, ct);
        var device = await _store.GetDeviceByIdAsync(deviceId, ct);
        if (device is null || device.ProjectId != projectId)
            throw new NotFoundException("Device not found.");
        var pool = await _store.GetPoolByIdAsync(device.PoolId, ct);
        return await MapAsync(device, pool?.Name ?? string.Empty, ct);
    }

    public async Task<IReadOnlyList<MobileDeviceDto>> ListAsync(Guid projectId, Guid? poolId, CancellationToken ct)
    {
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.ExecutionsRead, ct);
        if (poolId.HasValue)
        {
            var pool = await _store.GetPoolByIdAsync(poolId.Value, ct);
            if (pool is null || pool.ProjectId != projectId)
                throw new NotFoundException("Device pool not found.");
        }
        var devices = await _store.ListDevicesAsync(projectId, poolId, ct);
        var result = new List<MobileDeviceDto>(devices.Count);
        foreach (var device in devices)
        {
            var pool = await _store.GetPoolByIdAsync(device.PoolId, ct);
            result.Add(await MapAsync(device, pool?.Name ?? string.Empty, ct));
        }
        return result;
    }

    public async Task<MobileDeviceDto> UpdateAsync(Guid projectId, UpdateMobileDeviceCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.SettingsManage, ct);
        var device = await _store.GetDeviceByIdAsync(command.DeviceId, ct);
        if (device is null || device.ProjectId != projectId)
            throw new NotFoundException("Device not found.");

        var errors = new List<FieldError>();
        string? automationName = null;
        if (command.AutomationName is not null)
            automationName = MobileValidation.ParseAutomationName(command.AutomationName, device.Platform, errors);
        var platformVersion = command.PlatformVersion is null
            ? device.PlatformVersion
            : MobileValidation.OptionalBounded(command.PlatformVersion, MobileValidation.MaxPlatformVersionLength, "platformVersion", errors);
        var manufacturer = command.Manufacturer is null
            ? device.Manufacturer
            : MobileValidation.OptionalBounded(command.Manufacturer, MobileValidation.MaxManufacturerLength, "manufacturer", errors);
        var model = command.Model is null
            ? device.Model
            : MobileValidation.OptionalBounded(command.Model, MobileValidation.MaxModelLength, "model", errors);
        string? udid = device.Udid;
        if (command.Udid is not null)
        {
            udid = MobileValidation.OptionalBounded(command.Udid, MobileValidation.MaxUdidLength, "udid", errors);
            if (udid is not null && !string.Equals(udid, device.Udid, StringComparison.Ordinal))
            {
                var clash = await _store.FindDeviceByUdidAsync(device.ProjectId, udid, ct);
                if (clash is not null && clash.Id != device.Id)
                    throw new ConflictException("A device with this UDID is already registered in this project.");
            }
        }
        ValidationException.ThrowIfInvalid(errors);

        // Platform is immutable after registration; capability edits only.
        device.PlatformVersion = platformVersion;
        device.Manufacturer = manufacturer;
        device.Model = model;
        device.Udid = udid;
        if (automationName is not null)
            device.AutomationName = automationName;
        var wasEnabled = device.Status != MobileDeviceStatus.Disabled;
        // Registry toggles Disabled/Available only; worker-derived states
        // (Unhealthy/Offline) are never set here.
        if (!command.Enabled && wasEnabled)
            device.Status = MobileDeviceStatus.Disabled;
        else if (command.Enabled && !wasEnabled)
            device.Status = MobileDeviceStatus.Available;
        device.UpdatedAt = _clock.UtcNow;
        MobileValidation.ThrowIfStale(command.RowVersion, device.RowVersion, "device");
        try
        {
            await _store.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsConcurrencyConflict(ex))
        {
            throw new ConflictException("The device was modified by another user. Reload and retry.");
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            throw new ConflictException("A device with this identity is already registered in this project.");
        }

        var pool = await _store.GetPoolByIdAsync(device.PoolId, ct);
        await _audit.RecordAsync(
            wasEnabled == command.Enabled ? "mobile.device_updated"
                : command.Enabled ? "mobile.device_enabled" : "mobile.device_disabled",
            "mobile_device", device.Id.ToString(), device.ProjectId,
            SafeMeta(device, pool), ct);
        return await MapAsync(device, pool?.Name ?? string.Empty, ct);
    }

    private async Task<MobileDeviceDto> MapAsync(MobileDevice device, string poolName, CancellationToken ct)
    {
        var slots = await _store.ListSlotsByDeviceAsync(device.Id, ct);
        return new MobileDeviceDto(
            device.Id, device.ProjectId, device.PoolId, poolName,
            device.Platform.ToString(), device.PlatformVersion, device.Manufacturer,
            device.Model, device.Udid, device.AutomationName,
            device.Status.ToString(), device.Status != MobileDeviceStatus.Disabled,
            slots.Count, device.LastSeenAt, device.RowVersion,
            device.CreatedAt, device.UpdatedAt);
    }

    private static string SafeMeta(MobileDevice device, MobileDevicePool? pool)
        => SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
        {
            deviceId = device.Id,
            poolId = device.PoolId,
            poolName = pool?.Name,
            platform = device.Platform.ToString(),
            automationName = device.AutomationName,
            status = device.Status.ToString(),
        }));

    private static bool IsConcurrencyConflict(Exception ex)
    {
        var typeName = ex.GetType().FullName ?? string.Empty;
        if (typeName.Contains("DbUpdateConcurrencyException", StringComparison.Ordinal))
            return true;
        return ex.InnerException is not null && IsConcurrencyConflict(ex.InnerException);
    }

    private static bool IsUniqueViolation(Exception ex)
    {
        var typeName = ex.GetType().FullName ?? string.Empty;
        if (typeName.Contains("DbUpdateException", StringComparison.Ordinal))
            return true;
        if (ex.InnerException is not null && IsUniqueViolation(ex.InnerException))
            return true;
        return ex.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("unique", StringComparison.OrdinalIgnoreCase);
    }
}