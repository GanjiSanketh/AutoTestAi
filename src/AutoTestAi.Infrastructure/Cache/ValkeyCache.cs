using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace AutoTestAi.Infrastructure.Cache;

public sealed class ValkeyOptions
{
    public const string SectionName = "Valkey";
    public string? ConnectionString { get; set; }
    public bool Configured => !string.IsNullOrWhiteSpace(ConnectionString);
}

/// <summary>
/// Cache/ephemeral-state abstraction over Valkey (Redis protocol).
/// PostgreSQL data must never depend on Valkey availability (docs/03 §14).
/// </summary>
public interface IValkeyCache
{
    bool IsConfigured { get; }
    Task<string?> GetStringAsync(string key, CancellationToken cancellationToken);
    Task SetStringAsync(string key, string value, TimeSpan? expiry, CancellationToken cancellationToken);
    Task<bool> PingAsync(CancellationToken cancellationToken);
}

public sealed class ValkeyCache : IValkeyCache, IDisposable
{
    private readonly Lazy<ConnectionMultiplexer> _connection;
    private readonly ILogger<ValkeyCache> _logger;

    public bool IsConfigured { get; }

    public ValkeyCache(IOptions<ValkeyOptions> options, ILogger<ValkeyCache> logger)
    {
        _logger = logger;
        IsConfigured = options.Value.Configured;
        var connectionString = options.Value.ConnectionString ?? string.Empty;
        _connection = new Lazy<ConnectionMultiplexer>(() =>
            ConnectionMultiplexer.Connect(connectionString));
    }

    public async Task<string?> GetStringAsync(string key, CancellationToken cancellationToken)
    {
        if (!IsConfigured) return null;
        try
        {
            return await _connection.Value.GetDatabase().StringGetAsync(key);
        }
        catch (Exception ex)
        {
            // Cache failures must never break authoritative flows.
            _logger.LogWarning(ex, "Valkey GET failed for key {CacheKey}.", key);
            return null;
        }
    }

    public async Task SetStringAsync(string key, string value, TimeSpan? expiry, CancellationToken cancellationToken)
    {
        if (!IsConfigured) return;
        try
        {
            await _connection.Value.GetDatabase().StringSetAsync(key, value, expiry);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Valkey SET failed for key {CacheKey}.", key);
        }
    }

    public async Task<bool> PingAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured) return false;
        try
        {
            var latency = await _connection.Value.GetDatabase().PingAsync();
            _logger.LogDebug("Valkey ping took {Latency}.", latency);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Valkey ping failed.");
            return false;
        }
    }

    public void Dispose()
    {
        if (_connection.IsValueCreated)
            _connection.Value.Dispose();
    }
}
