using Microsoft.Extensions.DependencyInjection;
using WorkflowEngine.Service.Abstractions;
using WorkflowEngine.Service.Services;

namespace WorkflowEngine.Service.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddServiceLayer(this IServiceCollection services)
    {
        services.AddScoped<IWorkflowDefinitionService, WorkflowDefinitionService>();
        services.AddScoped<IWorkflowEngineService, WorkflowEngineService>();
        services.AddScoped<IEngineSettingsService, EngineSettingsService>();
        return services;
    }
}
