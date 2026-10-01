using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.TestGeneration;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Application.Mobile;

/// <summary>
/// Project-scoped mobile device pool registry (Phase 3 Slice 3C-1/3C-2).
/// Administrative configuration only: no scheduling, leasing, or runtime
/// behavior. Mutations require settings.manage; reads require executions.read.
/// </summary>
public sealed class MobilePoolService : IMobilePoolService
{
    private readonly IMobileRegistryStore _store;
    private readonly IAuthorizationService _authorization;
    private readonly IDateTimeProvider _clock;
    private readonly IAuditService _audit;

    public MobilePoolService(
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

    public async Task<MobilePoolDto> CreateAsync(CreateMobilePoolCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _authorization.RequireProjectAccessAsync(command.ProjectId, Permissions.SettingsManage, ct);

        var errors = new List<FieldError>();
        var name = MobileValidation.RequireName(command.Name, "name", errors);
        var platform = MobileValidation.ParsePlatform(command.Platform, "platform", errors);
        ValidationException.ThrowIfInvalid(errors);

        var existing = await _store.FindPoolByNameAsync(command.ProjectId, name, ct);
        if (existing is not null)
            throw new ConflictException($"A device pool named '{name}' already exists in this project.");

        var now = _clock.UtcNow;
        var pool = new MobileDevicePool
        {
            ProjectId = command.ProjectId,
            Name = name,
            Platform = platform,
            Status = MobilePoolStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        try
        {
            await _store.AddPoolAsync(pool, ct);
            await _store.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            throw new ConflictException($"A device pool named '{name}' already exists in this project.");
        }

        await _audit.RecordAsync("mobile.pool_created", "mobile_device_pool",
            pool.Id.ToString(), command.ProjectId, SafeMeta(pool), ct);
        return await MapAsync(pool, ct);
    }

    public async Task<MobilePoolDto> GetAsync(Guid projectId, Guid poolId, CancellationToken ct)
    {
        // Authorize against the route project first (unknown/outsider => 403,
        // never existence-revealing), then prove the row belongs to it.
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.ExecutionsRead, ct);
        var pool = await _store.GetPoolByIdAsync(poolId, ct);
        if (pool is null || pool.ProjectId != projectId)
            throw new NotFoundException("Device pool not found.");
        return await MapAsync(pool, ct);
    }

    public async Task<IReadOnlyList<MobilePoolDto>> ListAsync(Guid projectId, CancellationToken ct)
    {
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.ExecutionsRead, ct);
        var pools = await _store.ListPoolsAsync(projectId, ct);
        var result = new List<MobilePoolDto>(pools.Count);
        foreach (var pool in pools)
            result.Add(await MapAsync(pool, ct));
        return result;
    }

    public async Task<MobilePoolDto> UpdateAsync(Guid projectId, UpdateMobilePoolCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.SettingsManage, ct);
        var pool = await _store.GetPoolByIdAsync(command.PoolId, ct);
        if (pool is null || pool.ProjectId != projectId)
            throw new NotFoundException("Device pool not found.");

        var errors = new List<FieldError>();
        var name = MobileValidation.RequireName(command.Name, "name", errors);
        ValidationException.ThrowIfInvalid(errors);

        // Platform is immutable: a pool never changes its mobile OS family.
        if (!string.Equals(pool.Name, name, StringComparison.Ordinal))
        {
            var clash = await _store.FindPoolByNameAsync(pool.ProjectId, name, ct);
            if (clash is not null && clash.Id != pool.Id)
                throw new ConflictException($"A device pool named '{name}' already exists in this project.");
            pool.Name = name;
        }
        var wasEnabled = pool.Status == MobilePoolStatus.Active;
        pool.Status = command.Enabled ? MobilePoolStatus.Active : MobilePoolStatus.Disabled;
        pool.UpdatedAt = _clock.UtcNow;
        MobileValidation.ThrowIfStale(command.RowVersion, pool.RowVersion, "device pool");
        try
        {
            await _store.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (IsConcurrencyConflict(ex))
        {
            throw new ConflictException("The device pool was modified by another user. Reload and retry.");
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            throw new ConflictException($"A device pool named '{name}' already exists in this project.");
        }

        await _audit.RecordAsync(
            wasEnabled == command.Enabled ? "mobile.pool_updated"
                : command.Enabled ? "mobile.pool_enabled" : "mobile.pool_disabled",
            "mobile_device_pool", pool.Id.ToString(), pool.ProjectId, SafeMeta(pool), ct);
        return await MapAsync(pool, ct);
    }

    private async Task<MobilePoolDto> MapAsync(MobileDevicePool pool, CancellationToken ct)
    {
        var deviceCount = await _store.CountDevicesInPoolAsync(pool.Id, ct);
        return new MobilePoolDto(
            pool.Id, pool.ProjectId, pool.Name, pool.Platform.ToString(),
            pool.Status == MobilePoolStatus.Active, deviceCount,
            pool.RowVersion, pool.CreatedAt, pool.UpdatedAt);
    }

    private static string SafeMeta(MobileDevicePool pool)
        => SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
        {
            poolId = pool.Id,
            name = pool.Name,
            platform = pool.Platform.ToString(),
            enabled = pool.Status == MobilePoolStatus.Active,
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
