using AutoTestAi.Application.AI;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Defects;
using AutoTestAi.Application.FailureAnalysis;
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
        services.AddSingleton<IAiFailureAnalysisPromptBuilder, AiFailureAnalysisPromptBuilder>();
        services.AddSingleton<AiGenerationValidator>();
        services.AddSingleton<AiAnalysisValidator>();
        services.AddSingleton<AiGenerationRateLimiter>();
        services.AddScoped<IAuthorizationService, AuthorizationService>();
        services.AddScoped<IExecutionSubscriptionAuthorizer, ExecutionSubscriptionAuthorizer>();
        services.AddScoped<IProjectService, ProjectService>();
        services.AddScoped<ITestCaseService, TestCaseService>();
        services.AddScoped<ITestExecutionService, TestExecutionService>();
        services.AddScoped<IExecutionEngine, ExecutionEngine>();
        services.AddScoped<IFailureEvidenceService, FailureEvidenceService>();
        services.AddScoped<IFailureAnalysisService, FailureAnalysisService>();
        services.AddScoped<IDefectService, DefectService>();
        services.AddScoped<IAiTestGenerator, TestGenerationService>();
        services.AddScoped<IAuditService, AuditService>();
        return services;
    }
}
