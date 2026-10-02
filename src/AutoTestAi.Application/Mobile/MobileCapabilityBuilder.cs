using AutoTestAi.Application.Common;
using AutoTestAi.Application.ExecutionGrid;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Options;

namespace AutoTestAi.Application.Mobile;

/// <summary>
/// Server-side Appium capability builder (Slice 3C-4A). Builds the fixed
/// <see cref="MobileCapabilitiesDto"/> schema ONLY from validated structured
/// data (MobileDevice + MobileApp + options). There is deliberately no
/// dictionary/JSON input surface: arbitrary capability keys cannot be
/// injected by any caller. Tokens, credentials, URLs, and filesystem paths
/// are never capability values.
/// </summary>
public sealed class MobileCapabilityBuilder
{
    private readonly IOptions<MobileOptions> _options;

    public MobileCapabilityBuilder(IOptions<MobileOptions> options)
    {
        _options = options;
    }

    /// <summary>
    /// Builds capabilities for one validated device/app pair.
    /// <paramref name="appDownloadUrl"/>, when supplied, must be an https URL
    /// minted server-side (presigned storage URL); it is validated as a shape
    /// only and only consumed when the install policy requires a binary.
    /// </summary>
    public MobileCapabilitiesDto Build(
        MobileDevice device,
        MobileApp app,
        string? appDownloadUrl = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(app);

        var errors = new List<FieldError>();
        if (!MobileAutomationNames.IsSupported(device.AutomationName))
            errors.Add(new FieldError("automationName",
                $"Automation name must be one of: {MobileAutomationNames.UiAutomator2}, {MobileAutomationNames.XcUiTest}."));
        else if (!MobileAutomationNames.MatchesPlatform(device.AutomationName, device.Platform))
            errors.Add(new FieldError("automationName", "Automation name must match the device platform."));
        if (app.Platform != device.Platform)
            errors.Add(new FieldError("platform", "App platform must match the device platform."));
        var downloadUrl = ValidateDownloadUrl(appDownloadUrl, app, errors);
        ValidationException.ThrowIfInvalid(errors);

        var deviceName = string.IsNullOrWhiteSpace(device.Model)
            ? device.Udid?.Trim()
            : device.Model.Trim();
        var install = app.InstallPolicy;
        var needsBinary = install is MobileInstallPolicy.Install or MobileInstallPolicy.Reinstall;
        if (needsBinary && downloadUrl is null)
            throw new ValidationException("An application binary reference is required by the install policy.",
                new[] { new FieldError("app", "Install/Reinstall policies require a storage-backed binary.") });

        return new MobileCapabilitiesDto(
            PlatformName: device.Platform == MobilePlatform.Android ? "Android" : "iOS",
            AutomationName: device.AutomationName.Trim(),
            DeviceName: string.IsNullOrWhiteSpace(deviceName) ? null : deviceName,
            Udid: string.IsNullOrWhiteSpace(device.Udid) ? null : device.Udid.Trim(),
            AppPackage: device.Platform == MobilePlatform.Android ? app.PackageId?.Trim() : null,
            AppActivity: device.Platform == MobilePlatform.Android ? app.LaunchActivity?.Trim() : null,
            BundleId: device.Platform == MobilePlatform.Ios ? app.BundleId?.Trim() : null,
            App: needsBinary ? downloadUrl : null,
            NoReset: install == MobileInstallPolicy.Preinstalled,
            FullReset: install == MobileInstallPolicy.Reinstall,
            NewCommandTimeout: (int)_options.Value.NewCommandTimeout.TotalSeconds);
    }

    private static string? ValidateDownloadUrl(string? appDownloadUrl, MobileApp app, List<FieldError> errors)
    {
        if (string.IsNullOrWhiteSpace(appDownloadUrl))
            return null;
        var trimmed = appDownloadUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            errors.Add(new FieldError("appDownloadUrl", "Application download URL must be an absolute https URL."));
        return trimmed;
    }
}
