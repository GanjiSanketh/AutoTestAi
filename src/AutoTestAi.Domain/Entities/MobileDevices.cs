using AutoTestAi.Domain.Common;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Domain.Entities;

/// <summary>
/// Project-scoped named group of mobile devices (Phase 3 Slice 3C-1/3C-2).
/// Selection unit for future mobile execution; carries no runtime lease
/// state. No cross-project pools in this checkpoint.
/// </summary>
public sealed class MobileDevicePool : EntityBase
{
    public Guid ProjectId { get; set; }

    public string Name { get; set; } = string.Empty;

    public MobilePlatform Platform { get; set; } = MobilePlatform.Android;

    public MobilePoolStatus Status { get; set; } = MobilePoolStatus.Active;

    /// <summary>Optimistic concurrency token for administrative updates.</summary>
    public byte[]? RowVersion { get; set; }
}

/// <summary>
/// Static mobile device identity/capability record (Phase 3 Slice 3C-1/3C-2).
/// Identity only: lease state lives on <see cref="MobileDeviceSlot"/>,
/// runtime state on <see cref="MobileDeviceSession"/>. Structured capability
/// fields only — never arbitrary Appium capabilities JSON.
/// </summary>
public sealed class MobileDevice : EntityBase
{
    public Guid ProjectId { get; set; }

    public Guid PoolId { get; set; }

    public MobilePlatform Platform { get; set; } = MobilePlatform.Android;

    public string? PlatformVersion { get; set; }

    public string? Manufacturer { get; set; }

    public string? Model { get; set; }

    /// <summary>
    /// Device identity: required for real devices; emulator/simulator naming
    /// conventions allowed. Null when not supplied.
    /// </summary>
    public string? Udid { get; set; }

    public string AutomationName { get; set; } = MobileAutomationNames.UiAutomator2;

    /// <summary>
    /// Administrative lifecycle state. Available/Unhealthy/Offline are
    /// worker-derived in future slices; registry CRUD only toggles
    /// Disabled/Available via enable/disable.
    /// </summary>
    public MobileDeviceStatus Status { get; set; } = MobileDeviceStatus.Available;

    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>Optimistic concurrency token for administrative updates.</summary>
    public byte[]? RowVersion { get; set; }
}

/// <summary>
/// Explicit concurrency unit for a device (Phase 3 Slice 3C-1/3C-2).
/// Exactly one execution may hold a slot at a time (future lease).
/// This checkpoint creates the persistence model only: ClaimToken,
/// ClaimExpiresAt, and AssignmentId are structural preparation for the
/// future slot-leasing slice and remain unused by registry CRUD.
/// </summary>
public sealed class MobileDeviceSlot : EntityBase
{
    public Guid ProjectId { get; set; }

    public Guid PoolId { get; set; }

    public Guid DeviceId { get; set; }

    /// <summary>Deterministic per-device slot number (default registration uses 1).</summary>
    public int SlotNumber { get; set; } = 1;

    public MobileSlotStatus Status { get; set; } = MobileSlotStatus.Free;

    /// <summary>Future lease owner. Null means unclaimed. Unused by registry CRUD.</summary>
    public Guid? ClaimToken { get; set; }

    /// <summary>Future lease expiry. Unused by registry CRUD.</summary>
    public DateTimeOffset? ClaimExpiresAt { get; set; }

    /// <summary>Future owning assignment. Unused by registry CRUD.</summary>
    public Guid? AssignmentId { get; set; }

    /// <summary>Optimistic concurrency token for future claim/release writes.</summary>
    public byte[]? RowVersion { get; set; }
}

/// <summary>
/// Future runtime record for an Appium session (Phase 3 Slice 3C-1/3C-2).
/// Persistence model only: no session lifecycle service exists in this
/// checkpoint, and registry CRUD never creates sessions.
/// </summary>
public sealed class MobileDeviceSession : EntityBase
{
    public Guid ProjectId { get; set; }

    public Guid DeviceId { get; set; }

    public Guid DeviceSlotId { get; set; }

    public Guid? ExecutionId { get; set; }

    public Guid? AssignmentId { get; set; }

    public string? AppiumSessionId { get; set; }

    public MobileSessionStatus Status { get; set; } = MobileSessionStatus.Creating;

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    public DateTimeOffset? LastHeartbeatAt { get; set; }

    /// <summary>Optimistic concurrency token for future lifecycle writes.</summary>
    public byte[]? RowVersion { get; set; }
}

/// <summary>
/// Project-scoped mobile application under test (Phase 3 Slice 3C-1/3C-2).
/// Metadata and object-storage references only — never binary contents.
/// </summary>
public sealed class MobileApp : EntityBase
{
    public Guid ProjectId { get; set; }

    public MobilePlatform Platform { get; set; } = MobilePlatform.Android;

    public string Name { get; set; } = string.Empty;

    /// <summary>Android application package. Required for Android, null for iOS.</summary>
    public string? PackageId { get; set; }

    /// <summary>iOS bundle identifier. Required for iOS, null for Android.</summary>
    public string? BundleId { get; set; }

    public string? Version { get; set; }

    /// <summary>
    /// Object-storage object key for the installable binary. Null for
    /// preinstalled applications. Reference only: no URLs, no credentials.
    /// </summary>
    public string? StorageKey { get; set; }

    public MobileInstallPolicy InstallPolicy { get; set; } = MobileInstallPolicy.Preinstalled;

    /// <summary>Android-only launch activity. Null otherwise.</summary>
    public string? LaunchActivity { get; set; }

    public string? DeepLink { get; set; }

    /// <summary>Optimistic concurrency token for administrative updates.</summary>
    public byte[]? RowVersion { get; set; }
}

/// <summary>
/// Canonical Appium automation names accepted by the mobile registry
/// (Phase 3 Slice 3C-1/3C-2). Closed set; platform consistency is enforced
/// at the application boundary (UiAutomator2 ↔ android, XCUITest ↔ ios).
/// </summary>
public static class MobileAutomationNames
{
    public const string UiAutomator2 = "UiAutomator2";
    public const string XcUiTest = "XCUITest";

    public static bool IsSupported(string? value)
        => string.Equals(value, UiAutomator2, StringComparison.Ordinal) ||
           string.Equals(value, XcUiTest, StringComparison.Ordinal);

    public static bool MatchesPlatform(string automationName, MobilePlatform platform)
        => (platform == MobilePlatform.Android && string.Equals(automationName, UiAutomator2, StringComparison.Ordinal)) ||
           (platform == MobilePlatform.Ios && string.Equals(automationName, XcUiTest, StringComparison.Ordinal));
}
