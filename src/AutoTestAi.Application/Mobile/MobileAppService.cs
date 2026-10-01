using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.TestGeneration;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Application.Mobile;

/// <summary>
/// Project-scoped mobile application registry (Phase 3 Slice 3C-1/3C-2).
/// Metadata and object-storage references only: binary contents are never
/// accepted, stored, or returned here. Mutations require settings.manage;
/// reads require executions.read.
/// </summary>
public sealed class MobileAppService : IMobileAppService
{
    private readonly IMobileRegistryStore _store;
    private readonly IAuthorizationService _authorization;
    private readonly IDateTimeProvider _clock;
    private readonly IAuditService _audit;

    public MobileAppService(
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

    public async Task<MobileAppDto> CreateAsync(CreateMobileAppCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _authorization.RequireProjectAccessAsync(command.ProjectId, Permissions.SettingsManage, ct);

        var errors = new List<FieldError>();
        var platform = MobileValidation.ParsePlatform(command.Platform, "platform", errors);
        var name = MobileValidation.RequireName(command.Name, "name", errors);
        var (packageId, bundleId) = ValidatePackageIdentity(platform, command.PackageId, command.BundleId, errors);
        var version = MobileValidation.OptionalBounded(command.Version, MobileValidation.MaxVersionLength, "version", errors);
        var storageKey = MobileValidation.ParseStorageKey(command.StorageKey, errors);
        var installPolicy = MobileValidation.ParseInstallPolicy(command.InstallPolicy, errors);
        var launchActivity = ValidateLaunchActivity(platform, command.LaunchActivity, errors);
        var deepLink = MobileValidation.ParseDeepLink(command.DeepLink, errors);
        ValidationException.ThrowIfInvalid(errors);

        var now = _clock.UtcNow;
        var app = new MobileApp
        {
            ProjectId = command.ProjectId,
            Platform = platform,
            Name = name,
            PackageId = packageId,
            BundleId = bundleId,
            Version = version,
            StorageKey = storageKey,
            InstallPolicy = installPolicy,
            LaunchActivity = launchActivity,
            DeepLink = deepLink,
            CreatedAt = now,
            UpdatedAt = now,
        };
        try
        {
            await _store.AddAppAsync(app, ct);
            await _store.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsConcurrencyConflict(ex))
        {
            throw new ConflictException("The mobile app registry was modified concurrently. Reload and retry.");
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            throw new ConflictException("This application identity is already registered in this project.");
        }

        await _audit.RecordAsync("mobile.app_created", "mobile_app",
            app.Id.ToString(), command.ProjectId, SafeMeta(app), ct);
        return Map(app);
    }

    public async Task<MobileAppDto> GetAsync(Guid projectId, Guid appId, CancellationToken ct)
    {
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.ExecutionsRead, ct);
        var app = await _store.GetAppByIdAsync(appId, ct);
        if (app is null || app.ProjectId != projectId)
            throw new NotFoundException("Mobile app not found.");
        return Map(app);
    }

    public async Task<IReadOnlyList<MobileAppDto>> ListAsync(Guid projectId, CancellationToken ct)
    {
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.ExecutionsRead, ct);
        var apps = await _store.ListAppsAsync(projectId, ct);
        return apps.Select(Map).ToList();
    }

    public async Task<MobileAppDto> UpdateAsync(Guid projectId, UpdateMobileAppCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.SettingsManage, ct);
        var app = await _store.GetAppByIdAsync(command.AppId, ct);
        if (app is null || app.ProjectId != projectId)
            throw new NotFoundException("Mobile app not found.");

        var errors = new List<FieldError>();
        var name = MobileValidation.RequireName(command.Name, "name", errors);
        // Platform is immutable after creation: package/bundle identity is
        // validated against the stored platform.
        var (packageId, bundleId) = ValidatePackageIdentity(app.Platform, command.PackageId, command.BundleId, errors);
        var version = command.Version is null
            ? app.Version
            : MobileValidation.OptionalBounded(command.Version, MobileValidation.MaxVersionLength, "version", errors);
        // Null means "leave unchanged"; empty clears the reference.
        var storageKey = command.StorageKey is null
            ? app.StorageKey
            : MobileValidation.ParseStorageKey(command.StorageKey, errors);
        var installPolicy = MobileValidation.ParseInstallPolicy(command.InstallPolicy, errors);
        var launchActivity = command.LaunchActivity is null
            ? app.LaunchActivity
            : ValidateLaunchActivity(app.Platform, command.LaunchActivity, errors);
        var deepLink = command.DeepLink is null
            ? app.DeepLink
            : MobileValidation.ParseDeepLink(command.DeepLink, errors);
        ValidationException.ThrowIfInvalid(errors);

        app.Name = name;
        app.PackageId = packageId;
        app.BundleId = bundleId;
        app.Version = version;
        app.StorageKey = storageKey;
        app.InstallPolicy = installPolicy;
        app.LaunchActivity = launchActivity;
        app.DeepLink = deepLink;
        app.UpdatedAt = _clock.UtcNow;
        MobileValidation.ThrowIfStale(command.RowVersion, app.RowVersion, "mobile app");
        try
        {
            await _store.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsConcurrencyConflict(ex))
        {
            throw new ConflictException("The mobile app was modified by another user. Reload and retry.");
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            throw new ConflictException("This application identity is already registered in this project.");
        }

        await _audit.RecordAsync("mobile.app_updated", "mobile_app",
            app.Id.ToString(), app.ProjectId, SafeMeta(app), ct);
        return Map(app);
    }

    /// <summary>
    /// Android requires PackageId (BundleId must be null);
    /// iOS requires BundleId (PackageId must be null).
    /// </summary>
    private static (string? PackageId, string? BundleId) ValidatePackageIdentity(
        MobilePlatform platform, string? packageId, string? bundleId, List<FieldError> errors)
    {
        var package = string.IsNullOrWhiteSpace(packageId) ? null : packageId.Trim();
        var bundle = string.IsNullOrWhiteSpace(bundleId) ? null : bundleId.Trim();
        if (package is not null && package.Length > MobileValidation.MaxPackageIdLength)
            errors.Add(new FieldError("packageId", $"Package ID must be at most {MobileValidation.MaxPackageIdLength} characters."));
        if (bundle is not null && bundle.Length > MobileValidation.MaxPackageIdLength)
            errors.Add(new FieldError("bundleId", $"Bundle ID must be at most {MobileValidation.MaxPackageIdLength} characters."));
        if (platform == MobilePlatform.Android)
        {
            if (package is null)
                errors.Add(new FieldError("packageId", "Android applications require a package ID."));
            if (bundle is not null)
                errors.Add(new FieldError("bundleId", "Bundle ID is iOS-only and must be null for Android."));
        }
        else
        {
            if (bundle is null)
                errors.Add(new FieldError("bundleId", "iOS applications require a bundle ID."));
            if (package is not null)
                errors.Add(new FieldError("packageId", "Package ID is Android-only and must be null for iOS."));
        }
        return (package, bundle);
    }

    private static string? ValidateLaunchActivity(MobilePlatform platform, string? launchActivity, List<FieldError> errors)
    {
        if (string.IsNullOrWhiteSpace(launchActivity))
            return null;
        if (platform != MobilePlatform.Android)
        {
            errors.Add(new FieldError("launchActivity", "Launch activity is Android-only."));
            return null;
        }
        return MobileValidation.OptionalBounded(launchActivity, MobileValidation.MaxLaunchActivityLength, "launchActivity", errors);
    }

    private static MobileAppDto Map(MobileApp app) => new(
        app.Id, app.ProjectId, app.Platform.ToString(), app.Name,
        app.PackageId, app.BundleId, app.Version, app.StorageKey,
        !string.IsNullOrEmpty(app.StorageKey),
        app.InstallPolicy.ToString(), app.LaunchActivity, app.DeepLink,
        app.RowVersion, app.CreatedAt, app.UpdatedAt);

    private static string SafeMeta(MobileApp app)
        => SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
        {
            appId = app.Id,
            name = app.Name,
            platform = app.Platform.ToString(),
            packageId = app.PackageId,
            bundleId = app.BundleId,
            installPolicy = app.InstallPolicy.ToString(),
            hasBinary = !string.IsNullOrEmpty(app.StorageKey),
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
