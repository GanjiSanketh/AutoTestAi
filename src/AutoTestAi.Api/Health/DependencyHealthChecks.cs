using AutoTestAi.Infrastructure.Cache;
using AutoTestAi.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace AutoTestAi.Api.Health;

/// <summary>
/// Dependency health checks (STEP 21).
/// Postgres is the only readiness-blocking dependency, and only when configured:
/// an UNCONFIGURED optional dependency reports Degraded, never Unhealthy, so a
/// minimal local setup stays honest without failing readiness.
/// </summary>
public sealed class PostgresHealthCheck(IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var connectionString = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(connectionString))
            return HealthCheckResult.Degraded("PostgreSQL connection string is not configured.");

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT 1;", connection)
            {
                CommandTimeout = 3
            };
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy("PostgreSQL reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL unreachable.", ex);
        }
    }
}

public sealed class ValkeyHealthCheck(IValkeyCache cache) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!cache.IsConfigured)
            return HealthCheckResult.Degraded("Valkey is not configured (optional cache).");
        return await cache.PingAsync(cancellationToken)
            ? HealthCheckResult.Healthy("Valkey reachable.")
            : HealthCheckResult.Degraded("Valkey configured but unreachable.");
    }
}

public sealed class MinioHealthCheck(IArtifactStorage storage) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!storage.IsConfigured)
            return HealthCheckResult.Degraded("MinIO is not configured (optional artifact storage).");
        return await storage.CheckConnectivityAsync(cancellationToken)
            ? HealthCheckResult.Healthy("MinIO reachable.")
            : HealthCheckResult.Degraded("MinIO configured but unreachable.");
    }
}

public sealed class TemporalHealthCheck : IHealthCheck
{
    private readonly AutoTestAi.Workflows.Configuration.TemporalOptions _options;

    public TemporalHealthCheck(Microsoft.Extensions.Options.IOptions<AutoTestAi.Workflows.Configuration.TemporalOptions> options)
        => _options = options.Value;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!_options.Configured)
            return HealthCheckResult.Degraded("Temporal is not configured (optional workflow engine).");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var client = await Temporalio.Client.TemporalClient.ConnectAsync(
                new Temporalio.Client.TemporalClientConnectOptions(_options.Address)
                {
                    Namespace = _options.Namespace
                }).WaitAsync(linked.Token);
            _ = client;
            return HealthCheckResult.Healthy("Temporal reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded("Temporal configured but unreachable.", ex);
        }
    }
}
