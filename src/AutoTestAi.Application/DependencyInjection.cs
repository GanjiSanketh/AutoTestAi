using AutoTestAi.Application.AI;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Projects;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Application.TestGeneration;
using Microsoft.Extensions.DependencyInjection;

namespace AutoTestAi.Application;

/// <summary>Composition root for the Application layer (ADR-002 modular monolith).</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
        // Deterministic stub proves the IAiProvider seam. Real adapters
        // (Ollama/OpenAI) register in Infrastructure; the resolver picks the
        // configured one. Nothing here references vendor SDKs (ADR-003).
        services.AddSingleton<IAiProvider, StubAiProvider>();
        services.AddSingleton<IAiProviderResolver, AiProviderResolver>();
        services.AddSingleton<IAiTestGenerationPromptBuilder, AiTestGenerationPromptBuilder>();
        services.AddSingleton<AiGenerationValidator>();
        services.AddSingleton<AiGenerationRateLimiter>();
        services.AddScoped<IAuthorizationService, AuthorizationService>();
        services.AddScoped<IExecutionSubscriptionAuthorizer, ExecutionSubscriptionAuthorizer>();
        services.AddScoped<IProjectService, ProjectService>();
        services.AddScoped<ITestCaseService, TestCaseService>();
        services.AddScoped<IAiTestGenerator, TestGenerationService>();
        services.AddScoped<IAuditService, AuditService>();
        return services;
    }
}
