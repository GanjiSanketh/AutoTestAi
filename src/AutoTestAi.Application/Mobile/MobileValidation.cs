using AutoTestAi.Application.Common;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Application.Mobile;

/// <summary>
/// Shared mobile registry validation (Phase 3 Slice 3C-1/3C-2).
/// Closed allowlists only: no arbitrary platforms, automation names,
/// install policies, URLs, or capability payloads.
/// </summary>
public static class MobileValidation
{
    public const int MaxNameLength = 200;
    public const int MaxPlatformVersionLength = 100;
    public const int MaxManufacturerLength = 200;
    public const int MaxModelLength = 200;
    public const int MaxUdidLength = 200;
    public const int MaxPackageIdLength = 300;
    public const int MaxStorageKeyLength = 500;
    public const int MaxLaunchActivityLength = 500;
    public const int MaxVersionLength = 100;

    public static MobilePlatform ParsePlatform(string? platform, string field, List<FieldError> errors)
    {
        var normalized = (platform ?? string.Empty).Trim().ToLowerInvariant();
        if (string.Equals(normalized, "android", StringComparison.Ordinal))
            return MobilePlatform.Android;
        if (string.Equals(normalized, "ios", StringComparison.Ordinal))
            return MobilePlatform.Ios;
        errors.Add(new FieldError(field, "Platform must be 'android' or 'ios'."));
        return MobilePlatform.Android;
    }

    public static string RequireName(string? name, string field, List<FieldError> errors)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add(new FieldError(field, "Name is required."));
            return string.Empty;
        }
        if (name.Trim().Length > MaxNameLength)
            errors.Add(new FieldError(field, $"Name must be at most {MaxNameLength} characters."));
        return name.Trim();
    }

    public static string? OptionalBounded(string? value, int maxLength, string field, List<FieldError> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (value.Trim().Length > maxLength)
            errors.Add(new FieldError(field, $"Must be at most {maxLength} characters."));
        return value.Trim();
    }

    public static string ParseAutomationName(string? automationName, MobilePlatform platform, List<FieldError> errors)
    {
        var normalized = (automationName ?? string.Empty).Trim();
        if (!MobileAutomationNames.IsSupported(normalized))
        {
            errors.Add(new FieldError("automationName",
                $"Automation name must be one of: {MobileAutomationNames.UiAutomator2}, {MobileAutomationNames.XcUiTest}."));
            return MobileAutomationNames.UiAutomator2;
        }
        if (!MobileAutomationNames.MatchesPlatform(normalized, platform))
            errors.Add(new FieldError("automationName", "Automation name must match the device platform."));
        return normalized;
    }

    public static MobileInstallPolicy ParseInstallPolicy(string? policy, List<FieldError> errors)
    {
        var normalized = (policy ?? string.Empty).Trim().ToLowerInvariant();
        if (string.Equals(normalized, "preinstalled", StringComparison.Ordinal))
            return MobileInstallPolicy.Preinstalled;
        if (string.Equals(normalized, "install", StringComparison.Ordinal))
            return MobileInstallPolicy.Install;
        if (string.Equals(normalized, "reinstall", StringComparison.Ordinal))
            return MobileInstallPolicy.Reinstall;
        errors.Add(new FieldError("installPolicy", "Install policy must be 'Preinstalled', 'Install', or 'Reinstall'."));
        return MobileInstallPolicy.Preinstalled;
    }

    /// <summary>
    /// Storage reference only: object key, never a URL or credential.
    /// Rejects schemes, whitespace, parent traversal, and absolute paths.
    /// </summary>
    public static string? ParseStorageKey(string? storageKey, List<FieldError> errors)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
            return null;
        var key = storageKey.Trim();
        if (key.Length > MaxStorageKeyLength)
            errors.Add(new FieldError("storageKey", $"Storage key must be at most {MaxStorageKeyLength} characters."));
        else if (key.Contains("://", StringComparison.Ordinal) ||
                 key.Any(char.IsWhiteSpace) ||
                 key.StartsWith("/", StringComparison.Ordinal) ||
                 key.Split('/').Contains(".."))
            errors.Add(new FieldError("storageKey", "Storage key must be a plain object-storage key (no URLs, credentials, or traversal)."));
        return key;
    }

    /// <summary>Deep links must be bounded and never use executable schemes.</summary>
    public static string? ParseDeepLink(string? deepLink, List<FieldError> errors)
    {
        if (string.IsNullOrWhiteSpace(deepLink))
            return null;
        var link = deepLink.Trim();
        if (link.Length > 2000)
            errors.Add(new FieldError("deepLink", "Deep link must be at most 2000 characters."));
        else if (link.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                 link.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                 link.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase))
            errors.Add(new FieldError("deepLink", "Deep link uses a forbidden scheme."));
        return link;
    }

    public static void ThrowIfStale(byte[]? expected, byte[]? current, string resource)
    {
        if (expected is not null && current is not null && !expected.SequenceEqual(current))
            throw new ConflictException($"The {resource} was modified by another user. Reload and retry.");
    }
}
