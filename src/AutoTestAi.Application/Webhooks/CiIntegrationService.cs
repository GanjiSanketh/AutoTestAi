using System.Text.Json;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Projects;
using AutoTestAi.Application.Secrets;
using AutoTestAi.Application.TestGeneration;
using AutoTestAi.Application.Tickets;
using AutoTestAi.Application.Variables;
using AutoTestAi.Domain.Entities;
using AutoTestAi.Domain.Enums;

namespace AutoTestAi.Application.Webhooks;

/// <summary>
/// Project-scoped CI/CD integration configuration (Phase 3 Slice 3B).
/// Reuses the Integration aggregate with IntegrationType="cicd". Provider
/// secrets are provisioned as Slice 3A EnvironmentSecrets and referenced via
/// Integration.SecretReference (env_secret:&lt;guid&gt;) — never persisted
/// as plaintext. Write requires settings.manage; read requires executions.read.
/// </summary>
public sealed class CiIntegrationService : ICiIntegrationService
{
    private readonly IIntegrationStore _store;
    private readonly IAuthorizationService _authorization;
    private readonly IProjectStore _projects;
    private readonly ITestSuiteLookup _suites;
    private readonly ISecretStore _secrets;
    private readonly ISecretResolver _resolver;
    private readonly IDateTimeProvider _clock;
    private readonly IAuditService _audit;

    public CiIntegrationService(
        IIntegrationStore store,
        IAuthorizationService authorization,
        IProjectStore projects,
        ITestSuiteLookup suites,
        ISecretStore secrets,
        ISecretResolver resolver,
        IDateTimeProvider clock,
        IAuditService audit)
    {
        _store = store;
        _authorization = authorization;
        _projects = projects;
        _suites = suites;
        _secrets = secrets;
        _resolver = resolver;
        _clock = clock;
        _audit = audit;
    }

    public static string SecretNameFor(string provider)
        => $"CI_WEBHOOK_{provider.Trim().ToUpperInvariant()}";

    public async Task<CiIntegrationDto> UpsertAsync(UpsertCiIntegrationCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _authorization.RequireProjectAccessAsync(command.ProjectId, Permissions.SettingsManage, ct);

        var errors = new List<FieldError>();
        var provider = (command.Provider ?? string.Empty).Trim().ToLowerInvariant();
        if (!CiProviderNames.IsSupported(provider))
            errors.Add(new FieldError("provider", $"Provider must be one of: {string.Join(", ", CiProviderNames.All)}."));
        CiIntegrationConfig.ValidateFields(
            command.DefaultSuiteId, command.DefaultEnvironmentId,
            command.EventAllowlist, command.BranchAllowlist, command.RepositoryAllowlist,
            command.VariableMapping, command.Username, command.SecretMapping, errors);
        var secretValue = (command.WebhookSecret ?? string.Empty).Trim();
        if (secretValue.Length > 8000)
            errors.Add(new FieldError("webhookSecret", "Secret must be at most 8000 characters."));
        ValidationException.ThrowIfInvalid(errors);

        // Validate referenced suite/environment ownership before persisting.
        if (command.DefaultSuiteId.HasValue)
            await RequireSuiteAsync(command.ProjectId, command.DefaultSuiteId.Value, ct);
        TestEnvironment? environment = null;
        if (command.DefaultEnvironmentId.HasValue)
            environment = await RequireEnvironmentAsync(command.ProjectId, command.DefaultEnvironmentId.Value, ct);

        var existing = await _store.FindByProjectAndProviderAsync(command.ProjectId, provider, ct);
        string? secretRef = existing?.SecretReference;
        if (!string.IsNullOrEmpty(secretValue))
        {
            if (environment is null)
                throw new ValidationException("A default environment is required to store the webhook secret.",
                    new[] { new FieldError("defaultEnvironmentId", "Configure the default environment before setting the webhook secret.") });
            secretRef = await ProvisionSecretAsync(command.ProjectId, environment.Id, provider, secretValue, ct);
        }

        var config = new CiIntegrationConfig(
            command.DefaultSuiteId,
            command.DefaultEnvironmentId,
            (command.EventAllowlist ?? Array.Empty<string>()).Select(s => s.Trim()).Where(s => s.Length > 0).ToList(),
            (command.BranchAllowlist ?? Array.Empty<string>()).Select(s => s.Trim()).Where(s => s.Length > 0).ToList(),
            (command.RepositoryAllowlist ?? Array.Empty<string>()).Select(s => s.Trim()).Where(s => s.Length > 0).ToList(),
            new Dictionary<string, string>(command.VariableMapping ?? new Dictionary<string, string>(), StringComparer.Ordinal),
            string.IsNullOrWhiteSpace(command.Username) ? null : command.Username.Trim(),
            new Dictionary<string, string>(command.SecretMapping ?? new Dictionary<string, string>(), StringComparer.Ordinal));

        var now = _clock.UtcNow;
        Integration row;
        var isNew = false;
        if (existing is null)
        {
            row = new Integration
            {
                ProjectId = command.ProjectId,
                Provider = provider,
                IntegrationType = CiProviderNames.IntegrationType,
                Configuration = JsonDocument.Parse(SensitiveDataRedactor.Redact(config.ToJson())),
                SecretReference = secretRef,
                Status = command.Enabled ? IntegrationStatus.Active : IntegrationStatus.Disabled,
                CreatedAt = now,
                UpdatedAt = now,
            };
            await _store.AddAsync(row, ct);
            isNew = true;
        }
        else
        {
            if (!string.Equals(existing.IntegrationType, CiProviderNames.IntegrationType, StringComparison.Ordinal) ||
                !string.Equals(existing.Provider, provider, StringComparison.OrdinalIgnoreCase))
                throw new ConflictException("The existing integration for this provider is not a CI/CD integration.");
            row = existing;
            row.Configuration = JsonDocument.Parse(SensitiveDataRedactor.Redact(config.ToJson()));
            row.SecretReference = secretRef;
            row.Status = command.Enabled ? IntegrationStatus.Active : IntegrationStatus.Disabled;
            row.UpdatedAt = now;
        }
        await _store.SaveChangesAsync(ct);

        await _audit.RecordAsync(
            isNew ? "webhook.integration_configured" : "webhook.integration_updated",
            "integration", row.Id.ToString(), command.ProjectId,
            SensitiveDataRedactor.Redact(JsonSerializer.Serialize(new
            {
                provider,
                enabled = command.Enabled,
                hasSecret = !string.IsNullOrEmpty(secretRef),
            })), ct);

        return await MapAsync(row, config, ct);
    }

    public async Task<CiIntegrationDto?> GetAsync(Guid projectId, string provider, CancellationToken ct)
    {
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.ExecutionsRead, ct);
        var normalized = (provider ?? string.Empty).Trim().ToLowerInvariant();
        if (!CiProviderNames.IsSupported(normalized))
            return null;
        var row = await _store.FindByProjectAndProviderAsync(projectId, normalized, ct);
        if (row is null || !IsCiRow(row))
            return null;
        return await MapAsync(row, CiIntegrationConfig.FromJson(row.Configuration), ct);
    }

    public async Task<IReadOnlyList<CiIntegrationDto>> ListAsync(Guid projectId, CancellationToken ct)
    {
        await _authorization.RequireProjectAccessAsync(projectId, Permissions.ExecutionsRead, ct);
        var result = new List<CiIntegrationDto>();
        foreach (var provider in CiProviderNames.All)
        {
            var row = await _store.FindByProjectAndProviderAsync(projectId, provider, ct);
            if (row is not null && IsCiRow(row))
                result.Add(await MapAsync(row, CiIntegrationConfig.FromJson(row.Configuration), ct));
        }
        return result;
    }

    internal static bool IsCiRow(Integration row)
        => string.Equals(row.IntegrationType, CiProviderNames.IntegrationType, StringComparison.Ordinal);

    private async Task<TestSuite> RequireSuiteAsync(Guid projectId, Guid suiteId, CancellationToken ct)
    {
        var suite = await _suites.GetSuiteByIdAsync(suiteId, ct);
        if (suite is null || suite.ProjectId != projectId)
            throw new ValidationException("The specified suite does not belong to this project.",
                new[] { new FieldError("defaultSuiteId", "Suite must belong to the project.") });
        if (suite.Status != ProjectStatus.Active)
            throw new ValidationException("The specified suite is not active.",
                new[] { new FieldError("defaultSuiteId", "Suite must be active.") });
        return suite;
    }

    private async Task<TestEnvironment> RequireEnvironmentAsync(Guid projectId, Guid environmentId, CancellationToken ct)
    {
        var environment = await _projects.GetEnvironmentByIdAsync(environmentId, ct);
        if (environment is null || environment.ProjectId != projectId)
            throw new ValidationException("The specified environment does not belong to this project.",
                new[] { new FieldError("defaultEnvironmentId", "Environment must belong to the project.") });
        if (environment.Status != ProjectStatus.Active)
            throw new ValidationException("The specified environment is not active.",
                new[] { new FieldError("defaultEnvironmentId", "Environment must be active.") });
        return environment;
    }

    private async Task<string> ProvisionSecretAsync(
        Guid projectId, Guid environmentId, string provider, string value, CancellationToken ct)
    {
        var name = SecretNameFor(provider);
        var existing = await _secrets.ListMetadataAsync(projectId, environmentId, ct);
        var match = existing.FirstOrDefault(m =>
            string.Equals(m.Name, name, StringComparison.Ordinal) && m.ProjectId == projectId);
        SecretMetadata metadata;
        if (match is null)
        {
            metadata = await _secrets.CreateAsync(projectId, environmentId, name, value,
                $"CI/CD webhook secret for {provider} (managed by CI/CD integration settings).", ct);
        }
        else
        {
            metadata = await _secrets.UpdateAsync(match.Id, null, value, null, ct);
        }
        return metadata.SecretReference;
    }

    private async Task<CiIntegrationDto> MapAsync(Integration row, CiIntegrationConfig config, CancellationToken ct)
    {
        var hasSecret = !string.IsNullOrEmpty(row.SecretReference) &&
                        await _resolver.ExistsAsync(row.SecretReference!, ct);
        return new CiIntegrationDto(
            row.Id, row.ProjectId ?? Guid.Empty, row.Provider,
            row.Status == IntegrationStatus.Active, hasSecret,
            config.DefaultSuiteId, config.DefaultEnvironmentId,
            config.EventAllowlist, config.BranchAllowlist, config.RepositoryAllowlist,
            config.VariableMapping, config.Username, config.SecretMapping,
            hasSecret,
            $"/api/v1/webhooks/{row.Provider}/{row.ProjectId}/{row.Id}",
            row.UpdatedAt);
    }
}
