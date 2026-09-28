using AutoTestAi.Application.TestExecution;
using AutoTestAi.Workflows.Abstractions;
using AutoTestAi.Workflows.Configuration;
using AutoTestAi.Workflows.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AutoTestAi.Workflows;

public static class DependencyInjection
{
    public static IServiceCollection AddWorkflows(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<TemporalOptions>(configuration.GetSection(TemporalOptions.SectionName));
        services.AddSingleton<ITestExecutionWorkflowStarter, TemporalWorkflowStarter>();
        services.AddSingleton<IExecutionWorkflowCoordinator, TemporalExecutionWorkflowCoordinator>();
        services.AddHostedService<TemporalWorkerService>();
        return services;
    }
}
