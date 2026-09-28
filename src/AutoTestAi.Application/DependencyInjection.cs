using AutoTestAi.Application.AI;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Projects;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Application.TestExecution;
using Microsoft.Extensions.DependencyInjection;

namespace AutoTestAi.Application;

/// <summary>Composition root for the Application layer (ADR-002 modular monolith).</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
        // Phase 0: a deterministic stub proves the IAiProvider seam works.
        // Real adapters (Ollama/OpenAI/Gemini) plug in here in Phase 1.
        services.AddSingleton<IAiProvider, StubAiProvider>();
        services.AddScoped<IAuthorizationService, AuthorizationService>();
        services.AddScoped<IExecutionSubscriptionAuthorizer, ExecutionSubscriptionAuthorizer>();
        services.AddScoped<IProjectService, ProjectService>();
        services.AddScoped<ITestCaseService, TestCaseService>();
        services.AddScoped<IAuditService, AuditService>();
        return services;
    }
}
