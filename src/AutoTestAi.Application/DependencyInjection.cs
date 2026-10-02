using AutoTestAi.Application.AI;
using AutoTestAi.Application.Audit;
using AutoTestAi.Application.Authorization;
using AutoTestAi.Application.Common;
using AutoTestAi.Application.Defects;
using AutoTestAi.Application.FailureAnalysis;
using AutoTestAi.Application.Projects;
using AutoTestAi.Application.Secrets;
using AutoTestAi.Application.TestCases;
using AutoTestAi.Application.TestExecution;
using AutoTestAi.Application.TestGeneration;
using AutoTestAi.Application.Variables;
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
        services.AddSingleton<SelfHealing.SelfHealingAiPromptBuilder>();
        services.AddSingleton<AiGenerationRateLimiter>();
        services.AddScoped<IAuthorizationService, AuthorizationService>();
        services.AddScoped<IExecutionSubscriptionAuthorizer, ExecutionSubscriptionAuthorizer>();
        services.AddScoped<IProjectService, ProjectService>();
        services.AddScoped<ITestCaseService, TestCaseService>();
        services.AddScoped<ITestExecutionService, TestExecutionService>();
        services.AddScoped<IExecutionEngine, ExecutionEngine>();
        services.AddScoped<IFailureEvidenceService, FailureEvidenceService>();
        services.AddScoped<IFailureAnalysisService, FailureAnalysisService>();
        services.AddScoped<SelfHealing.ISelfHealingPolicyService, SelfHealing.SelfHealingPolicyService>();
        services.AddScoped<SelfHealing.ISelfHealingService, SelfHealing.SelfHealingService>();
        services.AddScoped<IDefectService, DefectService>();
        services.AddScoped<Tickets.ITicketService, Tickets.TicketService>();
        services.AddScoped<Tickets.IJiraIntegrationService, Tickets.JiraIntegrationService>();
        services.AddSingleton<Tickets.AutoTicketQueue>();
        services.AddScoped<Tickets.IAutoTicketPolicyService, Tickets.AutoTicketPolicyService>();
        services.AddScoped<Tickets.IAutomatedTicketService, Tickets.AutomatedTicketService>();
        services.AddScoped<Reports.IDashboardService, Reports.DashboardService>();
        services.AddScoped<Reports.IReportService, Reports.ReportService>();
        services.AddScoped<Variables.IVariableSetService, Variables.VariableSetService>();
        services.AddScoped<Variables.IVariableResolutionService, Variables.VariableResolutionService>();
        services.AddScoped<Secrets.ISecretMetadataService, Secrets.SecretMetadataService>();
        services.AddScoped<Mobile.IMobilePoolService, Mobile.MobilePoolService>();
        services.AddScoped<Mobile.IMobileDeviceService, Mobile.MobileDeviceService>();
        services.AddScoped<Mobile.IMobileAppService, Mobile.MobileAppService>();
        services.AddScoped<Mobile.MobileCapabilityBuilder>();
        services.AddSingleton<Webhooks.WebhookQueue>();
        services.AddSingleton<Webhooks.WebhookRateLimiter>();
        services.AddScoped<Webhooks.ICiIntegrationService, Webhooks.CiIntegrationService>();
        services.AddScoped<Webhooks.IWebhookIngestionService, Webhooks.WebhookIngestionService>();
        services.AddScoped<Webhooks.IWebhookProcessingService, Webhooks.WebhookProcessingService>();
        services.AddScoped<Webhooks.IWebhookDeliveryQueryService, Webhooks.WebhookDeliveryQueryService>();
        services.AddScoped<ExecutionGrid.IExecutionGridService, ExecutionGrid.ExecutionGridService>();
        services.AddScoped<ExecutionGrid.IGridScheduler, ExecutionGrid.GridScheduler>();
        services.AddScoped<ExecutionGrid.IGridLeaseManager, ExecutionGrid.GridLeaseManager>();
        services.AddScoped<ExecutionGrid.IMobileSlotLeaseService, ExecutionGrid.MobileSlotLeaseService>();
        services.AddScoped<IAiTestGenerator, TestGenerationService>();
        services.AddScoped<IAuditService, AuditService>();
        return services;
    }
}
