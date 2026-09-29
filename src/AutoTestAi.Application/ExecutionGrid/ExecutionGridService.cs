using System.Text.Json;
using System.Text.RegularExpressions;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Application.TestGeneration;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoTestAi.Application.ExecutionGrid;

/// <summary>
/// Worker lifecycle + grid administration (Phase 2 Slice 9). Worker-facing
/// operations authenticate via per-worker credentials (never Keycloak);
/// admin operations require settings.manage. Credential values are returned
/// exactly once at registration and never logged, audited, or re-exposed.
/// </summary>
public sealed partial class ExecutionGridService : IExecutionGridService
{
    private static readonly IReadOnlySet<string> SupportedBrowsers =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "chromium", "firefox", "webkit" };

    private readonly IGridWorkerStore _workers;
    private readonly IGridAssignmentStore _assignments;
    private readonly IAuthorizationService _authorization;
    private readonly ICurrentUserService _currentUser;
    private readonly IDateTimeProvider _clock;
    private readonly IAuditService _audit;
    private readonly IExecutionEventPublisher _events;
    private readonly IOptions<GridOptions> _options;
    private readonly ILogger<ExecutionGridService> _logger;

    public ExecutionGridService(
        IGridWorkerStore workers,
        IGridAssignmentStore assignments,
        IAuthorizationService authorization,
        ICurrentUserService currentUser,
        IDateTimeProvider clock,
        IAuditService audit,
        IExecutionEventPublisher events,
        IOptions<GridOptions> options,
        ILogger<ExecutionGridService> logger)
    {
        _workers = workers;
        _assignments = assignments;
        _authorization = authorization;
        _currentUser = currentUser;
        _clock = clock;
        _audit = audit;
        _events = events;
        _options = options;
        _logger = logger;
    }

    // ---------- worker plane (machine auth) ----------

    public async Task<RegisterWorkerResult> RegisterWorkerAsync(
        RegisterWorkerCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var settings = _options.Value;

        if (string.IsNullOrWhiteSpace(settings.ProvisioningToken))
            throw new InvalidOperationException(
                "Worker registration is not configured on this server.");
        if (!FixedTimeEquals(
                settings.ProvisioningToken.Trim(),
                (command.ProvisioningToken ?? string.Empty).Trim()))
            throw new UnauthorizedAccessException("Invalid worker provisioning credential.");

        var errors = new List<FieldError>();
        var workerKey = (command.WorkerKey ?? string.Empty).Trim();
        if (!WorkerKeyPattern().IsMatch(workerKey))
            errors.Add(new FieldError("workerKey",
                "Worker key must be 3-100 characters: letters, digits, '.', '_' or '-'."));
        var workerType = (command.WorkerType ?? "playwright").Trim().ToLowerInvariant();
        if (workerType != "playwright")
            errors.Add(new FieldError("workerType", "Only the 'playwright' worker type is supported."));
        var framework = (command.Framework ?? "playwright").Trim().ToLowerInvariant();
        if (framework != "playwright")
            errors.Add(new FieldError("framework", "Only the 'playwright' framework is supported."));
        var browsers = (command.Browsers ?? Array.Empty<string>())
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Select(b => b.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (browsers.Count == 0 || browsers.Any(b => !SupportedBrowsers.Contains(b)))
            errors.Add(new FieldError("browsers", "At least one supported browser is required ('chromium', 'firefox' or 'webkit')."));
        var capacity = command.Capacity ?? settings.DefaultWorkerCapacity;
        if (capacity < 1 || capacity > settings.MaxWorkerCapacity)
            errors.Add(new FieldError("capacity",
                $"Capacity must be between 1 and {settings.MaxWorkerCapacity}."));
        var baseUrl = (command.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            errors.Add(new FieldError("baseUrl", "Worker base URL must be a valid http(s) URL."));
        ValidationException.ThrowIfInvalid(errors);

        var now = _clock.UtcNow;
        var credential = GridCredentialHasher.NewCredential();
        var salt = GridCredentialHasher.NewSalt();
        var existing = await _workers.FindByKeyAsync(workerKey, ct);
        GridWorker worker;
        if (existing is null)
        {
            worker = new GridWorker
            {
                WorkerKey = workerKey,
                DisplayName = string.IsNullOrWhiteSpace(command.DisplayName)
                    ? workerKey : command.DisplayName.Trim(),
                WorkerType = workerType,
                Framework = framework,
                Browsers = browsers,
                Version = (command.Version ?? string.Empty).Trim(),
                Status = GridWorkerStatus.Registered,
                Capacity = capacity,
                ActiveAssignmentCount = 0,
                BaseUrl = baseUrl,
                CredentialHash = GridCredentialHasher.Hash(salt, credential),
                CredentialSalt = salt,
                CreatedAt = now,
                UpdatedAt = now,
            };
            await _workers.AddAsync(worker, ct);
        }
        else
        {
            worker = existing;
            worker.RowVersion++;
            // Re-registration rotates the credential but never revives an
            // administratively disabled worker on its own.
            worker.DisplayName = string.IsNullOrWhiteSpace(command.DisplayName)
                ? worker.WorkerKey : command.DisplayName.Trim();
            worker.Framework = framework;
            worker.Browsers = browsers;
            worker.Version = (command.Version ?? string.Empty).Trim();
            worker.Capacity = capacity;
            worker.BaseUrl = baseUrl;
            worker.CredentialHash = GridCredentialHasher.Hash(salt, credential);
            worker.CredentialSalt = salt;
            if (worker.Status != GridWorkerStatus.Disabled)
                worker.Status = GridWorkerStatus.Registered;
            worker.UpdatedAt = now;
        }
        await _workers.SaveChangesAsync(ct);

        // Identifiers only — the credential value itself never appears here.
        _logger.LogInformation("Worker {WorkerKey} registered as {WorkerId}.", workerKey, worker.Id);
        await _audit.RecordAsync("worker.registered", "grid-worker",
            worker.Id.ToString(), null, SafeWorkerMeta(worker), ct);
        await PublishWorkerStatusAsync(worker, ct);

        return new RegisterWorkerResult(
            worker.Id, credential,
            Math.Clamp(settings.HeartbeatIntervalSeconds, 5, 600),
            (int)settings.LeaseDuration.TotalSeconds);
    }

    public async Task<WorkerHeartbeatResult> HeartbeatAsync(
        WorkerHeartbeatCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var worker = await _workers.GetByIdAsync(command.WorkerId, ct);
        if (worker is null || !GridCredentialHasher.Verify(
                worker.CredentialSalt, worker.CredentialHash, command.Credential ?? string.Empty))
            throw new UnauthorizedAccessException("Invalid worker credential.");
        if (worker.Status == GridWorkerStatus.Disabled)
            throw new ForbiddenException("This worker is disabled and cannot heartbeat.");

        var settings = _options.Value;
        var now = _clock.UtcNow;
        if (command.Capacity is not null)
        {
            if (command.Capacity < 1 || command.Capacity > settings.MaxWorkerCapacity)
                throw new ValidationException($"Capacity must be between 1 and {settings.MaxWorkerCapacity}.",
                    new[] { new FieldError("capacity", "Capacity is out of range.") });
            worker.Capacity = command.Capacity.Value;
        }
        if (!string.IsNullOrWhiteSpace(command.Version) &&
            !string.Equals(worker.Version, command.Version.Trim(), StringComparison.Ordinal))
            worker.Version = command.Version.Trim();
        worker.LastHeartbeatAt = now;
        if (worker.Status == GridWorkerStatus.Registered)
            worker.Status = GridWorkerStatus.Available;
        worker.RowVersion++;
        worker.UpdatedAt = now;
        await _workers.SaveChangesAsync(ct);

        return new WorkerHeartbeatResult(
            worker.Id,
            EffectiveStatus(worker, now, settings).ToString(),
            worker.Status == GridWorkerStatus.Draining,
            now.ToUnixTimeMilliseconds(),
            Math.Clamp(settings.HeartbeatIntervalSeconds, 5, 600));
    }

    public Task<GridWorker?> ValidateWorkerCredentialAsync(
        Guid workerId, string credential, CancellationToken ct)
        => ValidateWorkerCredentialCoreAsync(workerId, credential, ct);

    private async Task<GridWorker?> ValidateWorkerCredentialCoreAsync(
        Guid workerId, string credential, CancellationToken ct)
    {
        if (workerId == Guid.Empty || string.IsNullOrWhiteSpace(credential))
            return null;
        var worker = await _workers.GetByIdAsync(workerId, ct);
        if (worker is null || worker.Status == GridWorkerStatus.Disabled)
            return null;
        return GridCredentialHasher.Verify(worker.CredentialSalt, worker.CredentialHash, credential)
            ? worker : null;
    }

    // ---------- admin plane (settings.manage) ----------

    public async Task<GridWorkerDto> GetWorkerAsync(Guid workerId, CancellationToken ct)
    {
        RequireGridManage();
        var worker = await _workers.GetByIdAsync(workerId, ct)
            ?? throw new NotFoundException("Worker not found.");
        return Map(worker, _clock.UtcNow, _options.Value);
    }

    public async Task<IReadOnlyList<GridWorkerDto>> ListWorkersAsync(CancellationToken ct)
    {
        RequireGridManage();
        var now = _clock.UtcNow;
        var settings = _options.Value;
        return (await _workers.ListAsync(ct))
            .OrderBy(w => w.WorkerKey, StringComparer.Ordinal)
            .Select(w => Map(w, now, settings))
            .ToList();
    }

    public async Task<GridStatusDto> GetStatusAsync(CancellationToken ct)
    {
        RequireGridManage();
        var now = _clock.UtcNow;
        var settings = _options.Value;
        var workers = await _workers.ListAsync(ct);
        var active = await _assignments.ListActiveAsync(ct);
        var queued = await _assignments.CountQueuedExecutionsAsync(ct);
        var expiredLastHour = 0; // surfaced via logs/metrics, not stored per lease

        int available = 0, busy = 0, draining = 0, unhealthy = 0, offline = 0, disabled = 0, capacity = 0;
        foreach (var worker in workers)
        {
            capacity += Math.Max(0, worker.Capacity);
            switch (EffectiveStatus(worker, now, settings))
            {
                case GridWorkerStatus.Available: available++; break;
                case GridWorkerStatus.Busy: busy++; break;
                case GridWorkerStatus.Draining: draining++; break;
                case GridWorkerStatus.Unhealthy: unhealthy++; break;
                case GridWorkerStatus.Offline: offline++; break;
                case GridWorkerStatus.Disabled: disabled++; break;
                default: available++; break;
            }
        }
        var activeCount = active.Count;
        return new GridStatusDto(
            workers.Count, available, busy, draining, unhealthy, offline, disabled,
            capacity, activeCount, Math.Max(0, capacity - activeCount),
            queued, activeCount, expiredLastHour,
            workers.OrderBy(w => w.WorkerKey, StringComparer.Ordinal)
                .Select(w => Map(w, now, settings)).ToList());
    }

    public async Task<GridWorkerDto> DrainWorkerAsync(Guid workerId, CancellationToken ct)
        => await TransitionAsync(workerId, "drain",
            static s => s == GridWorkerStatus.Disabled
                ? throw new ConflictException("A disabled worker must be enabled before draining.")
                : GridWorkerStatus.Draining, ct);

    public async Task<GridWorkerDto> DisableWorkerAsync(Guid workerId, CancellationToken ct)
        => await TransitionAsync(workerId, "disable",
            static _ => GridWorkerStatus.Disabled, ct);

    public async Task<GridWorkerDto> EnableWorkerAsync(Guid workerId, CancellationToken ct)
        => await TransitionAsync(workerId, "enable",
            static s => s != GridWorkerStatus.Disabled && s != GridWorkerStatus.Draining
                ? throw new ConflictException("Only a disabled or draining worker can be enabled.")
                : GridWorkerStatus.Available, ct);

    // ---------- helpers ----------

    private void RequireGridManage()
    {
        // settings.manage is admin-held; anonymous callers fail closed here
        // even if an endpoint mapping is ever misconfigured.
        if (!_currentUser.IsAuthenticated)
            throw new UnauthorizedAccessException("Authentication is required.");
        if (!_authorization.HasPermission(Permissions.SettingsManage))
            throw new ForbiddenException("Managing the execution grid requires the 'settings.manage' permission.");
    }

    private async Task<GridWorkerDto> TransitionAsync(
        Guid workerId, string action, Func<GridWorkerStatus, GridWorkerStatus> next, CancellationToken ct)
    {
        RequireGridManage();
        var worker = await _workers.GetByIdAsync(workerId, ct)
            ?? throw new NotFoundException("Worker not found.");
        worker.Status = next(worker.Status);
        worker.RowVersion++;
        worker.UpdatedAt = _clock.UtcNow;
        await _workers.SaveChangesAsync(ct);
        _logger.LogInformation("Worker {WorkerId} transitioned via {Action} to {Status}.",
            worker.Id, action, worker.Status);
        await _audit.RecordAsync($"worker.{action}d", "grid-worker",
            worker.Id.ToString(), null, SafeWorkerMeta(worker), ct);
        await PublishWorkerStatusAsync(worker, ct);
        return Map(worker, _clock.UtcNow, _options.Value);
    }

    private async Task PublishWorkerStatusAsync(GridWorker worker, CancellationToken ct)
    {
        try
        {
            await _events.PublishAsync(Guid.Empty, ExecutionEvents.WorkerStatusChanged,
                new
                {
                    workerId = worker.Id,
                    workerKey = worker.WorkerKey,
                    status = EffectiveStatus(worker, _clock.UtcNow, _options.Value).ToString(),
                }, ct);
        }
        catch (Exception ex)
        {
            // Status fan-out is supplementary; registration/lifecycle always wins.
            _logger.LogWarning(ex, "Worker status event for {WorkerId} was not published.", worker.Id);
        }
    }

    internal static GridWorkerStatus EffectiveStatus(GridWorker worker, DateTimeOffset now, GridOptions settings)
    {
        if (worker.Status == GridWorkerStatus.Disabled) return GridWorkerStatus.Disabled;
        if (worker.Status == GridWorkerStatus.Draining) return GridWorkerStatus.Draining;
        if (worker.LastHeartbeatAt is null) return GridWorkerStatus.Registered;
        var age = now - worker.LastHeartbeatAt.Value;
        if (age >= settings.OfflineThreshold) return GridWorkerStatus.Offline;
        if (age >= settings.HeartbeatTimeout) return GridWorkerStatus.Unhealthy;
        if (worker.ActiveAssignmentCount >= Math.Max(1, worker.Capacity)) return GridWorkerStatus.Busy;
        return worker.Status == GridWorkerStatus.Registered ? GridWorkerStatus.Registered : GridWorkerStatus.Available;
    }

    internal static GridWorkerDto Map(GridWorker worker, DateTimeOffset now, GridOptions settings)
    {
        var effective = EffectiveStatus(worker, now, settings);
        var slots = Math.Max(0, worker.Capacity - worker.ActiveAssignmentCount);
        return new GridWorkerDto(
            worker.Id, worker.WorkerKey, worker.DisplayName, worker.WorkerType,
            worker.Framework, worker.Browsers.ToList(), worker.Version,
            worker.Status.ToString(), effective.ToString(),
            worker.Capacity, worker.ActiveAssignmentCount, slots,
            worker.LastHeartbeatAt, worker.CreatedAt, worker.UpdatedAt);
    }

    private string SafeWorkerMeta(GridWorker worker)
        => SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
        {
            workerId = worker.Id,
            workerKey = worker.WorkerKey,
            workerType = worker.WorkerType,
            framework = worker.Framework,
            status = worker.Status.ToString(),
        }));

    private static bool FixedTimeEquals(string expected, string presented)
    {
        var a = System.Text.Encoding.UTF8.GetBytes(expected);
        var b = System.Text.Encoding.UTF8.GetBytes(presented);
        return a.Length == b.Length &&
            System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{2,99}$")]
    private static partial Regex WorkerKeyPattern();
}
