using AutoTestAi.Application.AI;
using AutoTestAi.Application.Defects;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.Projects;
using AutoTestAi.Application.Secrets;
using AutoTestAi.Application.Storage;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Application.Variables;
using AutoTestAi.Infrastructure.AI;
using AutoTestAi.Infrastructure.Cache;
using AutoTestAi.Infrastructure.Executions;
using AutoTestAi.Infrastructure.Identity;
using AutoTestAi.Infrastructure.Persistence;
using AutoTestAi.Infrastructure.Projects;
using AutoTestAi.Infrastructure.Secrets;
using AutoTestAi.Infrastructure.Storage;
using AutoTestAi.Infrastructure.TestCases;
using AutoTestAi.Infrastructure.Variables;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AutoTestAi.Infrastructure;

/// <summary>
/// Composition root for Infrastructure (ADR-002).
/// Optional external dependencies are registered conditionally so the API can
/// start without them; health endpoints report their real state explicitly.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<ValkeyOptions>(configuration.GetSection(ValkeyOptions.SectionName));
        services.Configure<MinioOptions>(configuration.GetSection(MinioOptions.SectionName));
        services.Configure<SecretVaultOptions>(configuration.GetSection(SecretVaultOptions.SectionName));

        var connectionString = configuration.GetConnectionString("Postgres");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            services.AddDbContext<AutoTestAiDbContext>(options =>
                options.UseNpgsql(connectionString));
            services.AddScoped<IProjectMembershipStore, EfProjectMembershipStore>();
            services.AddScoped<IExecutionProjectResolver, EfExecutionProjectResolver>();
            services.AddScoped<IUserDirectory, EfUserDirectory>();
            services.AddScoped<IProjectStore, EfProjectStore>();
            services.AddScoped<ITestCaseStore, EfTestCaseStore>();
            services.AddScoped<IExecutionStore, EfExecutionStore>();
            services.AddScoped<IDefectStore, Defects.EfDefectStore>();
            services.AddScoped<Application.Tickets.ITicketStore, Tickets.EfTicketStore>();
            services.AddScoped<Application.Tickets.IIntegrationStore, Tickets.EfIntegrationStore>();
            services.AddScoped<Application.Tickets.IAutoTicketPolicyStore, Tickets.EfAutoTicketPolicyStore>();
            services.AddScoped<Application.Tickets.IAutoTicketQueryStore, Tickets.EfAutoTicketQueryStore>();
            services.AddScoped<Application.Reports.IReportQueryStore, Reports.EfReportQueryStore>();
            services.AddScoped<Application.ExecutionGrid.IGridWorkerStore, ExecutionGrid.EfGridWorkerStore>();
            services.AddScoped<Application.ExecutionGrid.IGridAssignmentStore, ExecutionGrid.EfGridAssignmentStore>();
            services.AddScoped<Application.SelfHealing.ISelfHealingPolicyStore, SelfHealing.EfSelfHealingPolicyStore>();
            services.AddScoped<Application.SelfHealing.ISelfHealingAttemptStore, SelfHealing.EfSelfHealingAttemptStore>();
            services.AddScoped<IVariableSetStore, EfVariableSetStore>();
            services.AddScoped<IExecutionVariablesStore, EfExecutionVariablesStore>();
            services.AddScoped<ITestSuiteLookup, EfTestSuiteLookup>();
            services.AddScoped<Application.Webhooks.IWebhookDeliveryStore, Webhooks.EfWebhookDeliveryStore>();
            services.AddScoped<Application.Webhooks.ISuiteMemberLookup, Webhooks.EfSuiteMemberLookup>();
            services.AddScoped<Application.Mobile.IMobileRegistryStore, Mobile.EfMobileRegistryStore>();
            services.AddScoped<Application.Mobile.IVisualBaselineStore, Mobile.EfVisualBaselineStore>();
            services.AddScoped<Application.Maintenance.IMaintenanceStore, Maintenance.EfMaintenanceStore>();
            // Slice 3A: one vault class, two narrow capabilities. Execution
            // code resolves ISecretResolver; management resolves ISecretStore.
            services.AddScoped<EfSecretVault>();
            services.AddScoped<ISecretResolver>(provider => provider.GetRequiredService<EfSecretVault>());
            services.AddScoped<ISecretStore>(provider => provider.GetRequiredService<EfSecretVault>());
        }
        else
        {
            // Fail closed: without a database nobody is a project member and
            // no execution resolves, so protected resources deny access.
            services.AddSingleton<IProjectMembershipStore, DenyAllProjectMembershipStore>();
            services.AddSingleton<IExecutionProjectResolver, UnknownExecutionProjectResolver>();
            services.AddSingleton<IUserDirectory, NullUserDirectory>();
            services.AddSingleton<IProjectStore, UnavailableProjectStore>();
            services.AddSingleton<ITestCaseStore, UnavailableTestCaseStore>();
            services.AddSingleton<IExecutionStore, Executions.UnavailableExecutionStore>();
            services.AddSingleton<IDefectStore, Defects.UnavailableDefectStore>();
            services.AddSingleton<Application.Tickets.ITicketStore, Tickets.UnavailableTicketStore>();
            services.AddSingleton<Application.Tickets.IIntegrationStore, Tickets.UnavailableIntegrationStore>();
            services.AddSingleton<Application.Tickets.IAutoTicketPolicyStore, Tickets.UnavailableAutoTicketPolicyStore>();
            services.AddSingleton<Application.Tickets.IAutoTicketQueryStore, Tickets.UnavailableAutoTicketQueryStore>();
            services.AddSingleton<Application.Reports.IReportQueryStore, Reports.UnavailableReportQueryStore>();
            services.AddSingleton<Application.ExecutionGrid.IGridWorkerStore, ExecutionGrid.UnavailableGridWorkerStore>();
            services.AddSingleton<Application.ExecutionGrid.IGridAssignmentStore, ExecutionGrid.UnavailableGridAssignmentStore>();
            services.AddSingleton<Application.SelfHealing.ISelfHealingPolicyStore, SelfHealing.UnavailableSelfHealingPolicyStore>();
            services.AddSingleton<Application.SelfHealing.ISelfHealingAttemptStore, SelfHealing.UnavailableSelfHealingAttemptStore>();
            services.AddSingleton<IVariableSetStore, Variables.UnavailableVariableSetStore>();
            services.AddSingleton<IExecutionVariablesStore, Variables.UnavailableExecutionVariablesStore>();
            services.AddSingleton<ITestSuiteLookup, Variables.UnavailableTestSuiteLookup>();
            services.AddSingleton<Application.Webhooks.IWebhookDeliveryStore, Webhooks.UnavailableWebhookDeliveryStore>();
            services.AddSingleton<Application.Webhooks.ISuiteMemberLookup, Webhooks.UnavailableSuiteMemberLookup>();
            services.AddSingleton<Application.Mobile.IMobileRegistryStore, Mobile.UnavailableMobileRegistryStore>();
            services.AddSingleton<Application.Mobile.IVisualBaselineStore, Mobile.UnavailableVisualBaselineStore>();
            services.AddSingleton<Application.Maintenance.IMaintenanceStore, Maintenance.UnavailableMaintenanceStore>();
            services.AddSingleton<ISecretResolver, Secrets.UnavailableSecretResolver>();
            services.AddSingleton<ISecretStore, Secrets.UnavailableSecretStore>();
        }

        // Dapper remains referenced for future read-model queries (docs/04);
        // no Dapper support services are registered until they are needed.
        services.AddSingleton<IValkeyCache, ValkeyCache>();
        services.AddSingleton<IArtifactStorage, MinioArtifactStorage>();

        // AI provider adapters (ADR-003): HTTP-based, no vendor SDKs.
        // Gemini is intentionally not registered — the resolver reports it as
        // unsupported until a real adapter lands (Slice 4 §9).
        services.AddHttpClient("ai-ollama");
        services.AddHttpClient("ai-openai");
        services.AddTransient<IAiProvider, OllamaAiProvider>();
        services.AddTransient<IAiProvider, OpenAiAiProvider>();

        // Playwright execution plane (Slice 5 §36): HTTP boundary, token server-side.
        // Phase 2 Slice 9: per-worker transport plus the grid dispatch
        // decorator. The legacy single-worker client remains for fallback.
        services.AddHttpClient("playwright-worker");
        services.AddTransient<Executions.WorkerHttpTransport>();
        services.AddTransient<Executions.PlaywrightWorkerClient>();
        services.AddTransient<IPlaywrightWorkerClient, ExecutionGrid.GridPlaywrightWorkerClient>();

        // Slice 3C-4B-2: Appium execution plane. Same shape as above:
        // per-worker mobile transport plus the grid dispatch decorator.
        services.AddHttpClient("appium-worker");
        services.AddTransient<Executions.MobileWorkerTransport>();
        services.AddTransient<Application.TestExecution.IMobileWorkerClient, ExecutionGrid.GridAppiumWorkerClient>();

        // Slice 7: Jira ticketing boundary (manual creation only; token server-side).
        services.AddHttpClient(Jira.JiraTicketProvider.HttpClientName);
        services.AddTransient<Application.Tickets.IJiraTicketProvider, Jira.JiraTicketProvider>();

        // Slice 3B: CI/CD webhook provider adapters (no HTTP clients — inbound only).
        services.AddTransient<Application.Webhooks.ICiWebhookProvider, Webhooks.Providers.GithubWebhookProvider>();
        services.AddTransient<Application.Webhooks.ICiWebhookProvider, Webhooks.Providers.GitlabWebhookProvider>();
        services.AddTransient<Application.Webhooks.ICiWebhookProvider, Webhooks.Providers.JenkinsWebhookProvider>();
        services.AddTransient<Application.Webhooks.ICiWebhookProvider, Webhooks.Providers.AzureWebhookProvider>();
        services.AddTransient<Webhooks.Providers.CiWebhookProviderResolver>();

        return services;
    }
}
