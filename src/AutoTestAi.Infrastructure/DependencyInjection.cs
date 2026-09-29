using AutoTestAi.Application.AI;
using AutoTestAi.Application.Defects;
using AutoTestAi.Application.Identity;
using AutoTestAi.Application.Projects;
using AutoTestAi.Application.Storage;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Infrastructure.AI;
using AutoTestAi.Infrastructure.Cache;
using AutoTestAi.Infrastructure.Executions;
using AutoTestAi.Infrastructure.Identity;
using AutoTestAi.Infrastructure.Persistence;
using AutoTestAi.Infrastructure.Projects;
using AutoTestAi.Infrastructure.Storage;
using AutoTestAi.Infrastructure.TestCases;
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
        services.AddHttpClient("playwright-worker");
        services.AddTransient<IPlaywrightWorkerClient, Executions.PlaywrightWorkerClient>();

        // Slice 7: Jira ticketing boundary (manual creation only; token server-side).
        services.AddHttpClient(Jira.JiraTicketProvider.HttpClientName);
        services.AddTransient<Application.Tickets.IJiraTicketProvider, Jira.JiraTicketProvider>();

        return services;
    }
}
