using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Application.Mobile;

// ---------- Commands ----------

public sealed record CreateMobilePoolCommand(
    Guid ProjectId,
    string Name,
    string Platform);

public sealed record UpdateMobilePoolCommand(
    Guid PoolId,
    string Name,
    bool Enabled,
    byte[]? RowVersion);

public sealed record RegisterMobileDeviceCommand(
    Guid ProjectId,
    Guid PoolId,
    string Platform,
    string? PlatformVersion,
    string? Manufacturer,
    string? Model,
    string? Udid,
    string AutomationName);

/// <summary>
/// Device metadata update. Null optional fields mean "leave unchanged";
/// empty/whitespace means "clear". Platform is immutable after registration.
/// Only Disabled/Available toggling is supported; worker-derived states
/// (Unhealthy/Offline) are never set here.
/// </summary>
public sealed record UpdateMobileDeviceCommand(
    Guid DeviceId,
    string? PlatformVersion,
    string? Manufacturer,
    string? Model,
    string? Udid,
    string? AutomationName,
    bool Enabled,
    byte[]? RowVersion);

public sealed record CreateMobileAppCommand(
    Guid ProjectId,
    string Platform,
    string Name,
    string? PackageId,
    string? BundleId,
    string? Version,
    string? StorageKey,
    string InstallPolicy,
    string? LaunchActivity,
    string? DeepLink);

/// <summary>
/// Mobile app update. Null optional reference fields (version, storageKey,
/// launchActivity, deepLink) mean "leave unchanged"; empty clears them.
/// Platform and package/bundle identity rules match creation.
/// </summary>
public sealed record UpdateMobileAppCommand(
    Guid AppId,
    string Name,
    string? PackageId,
    string? BundleId,
    string? Version,
    string? StorageKey,
    string InstallPolicy,
    string? LaunchActivity,
    string? DeepLink,
    byte[]? RowVersion);

// ---------- DTOs (never carry secrets, lease tokens, or credentials) ----------

public sealed record MobilePoolDto(
    Guid Id,
    Guid ProjectId,
    string Name,
    string Platform,
    bool Enabled,
    int DeviceCount,
    byte[]? RowVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MobileDeviceDto(
    Guid Id,
    Guid ProjectId,
    Guid PoolId,
    string PoolName,
    string Platform,
    string? PlatformVersion,
    string? Manufacturer,
    string? Model,
    string? Udid,
    string AutomationName,
    string Status,
    bool Enabled,
    int SlotCount,
    DateTimeOffset? LastSeenAt,
    byte[]? RowVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MobileAppDto(
    Guid Id,
    Guid ProjectId,
    string Platform,
    string Name,
    string? PackageId,
    string? BundleId,
    string? Version,
    string? StorageKey,
    bool HasBinary,
    string InstallPolicy,
    string? LaunchActivity,
    string? DeepLink,
    byte[]? RowVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

// ---------- Services ----------

public interface IMobilePoolService
{
    Task<MobilePoolDto> CreateAsync(CreateMobilePoolCommand command, CancellationToken ct);
    Task<MobilePoolDto> GetAsync(Guid projectId, Guid poolId, CancellationToken ct);
    Task<IReadOnlyList<MobilePoolDto>> ListAsync(Guid projectId, CancellationToken ct);
    Task<MobilePoolDto> UpdateAsync(Guid projectId, UpdateMobilePoolCommand command, CancellationToken ct);
}

public interface IMobileDeviceService
{
    Task<MobileDeviceDto> RegisterAsync(RegisterMobileDeviceCommand command, CancellationToken ct);
    Task<MobileDeviceDto> GetAsync(Guid projectId, Guid deviceId, CancellationToken ct);
    Task<IReadOnlyList<MobileDeviceDto>> ListAsync(Guid projectId, Guid? poolId, CancellationToken ct);
    Task<MobileDeviceDto> UpdateAsync(Guid projectId, UpdateMobileDeviceCommand command, CancellationToken ct);
}

public interface IMobileAppService
{
    Task<MobileAppDto> CreateAsync(CreateMobileAppCommand command, CancellationToken ct);
    Task<MobileAppDto> GetAsync(Guid projectId, Guid appId, CancellationToken ct);
    Task<IReadOnlyList<MobileAppDto>> ListAsync(Guid projectId, CancellationToken ct);
    Task<MobileAppDto> UpdateAsync(Guid projectId, UpdateMobileAppCommand command, CancellationToken ct);
}
