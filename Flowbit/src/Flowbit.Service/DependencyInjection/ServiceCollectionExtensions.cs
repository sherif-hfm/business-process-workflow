using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Services;

namespace Flowbit.Service.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddServiceLayer(this IServiceCollection services)
    {
        services.TryAddSingleton(new ServiceTaskOptions());
        services.TryAddSingleton(new MessageDeliveryOptions());
        services.AddScoped<IWorkflowDefinitionService, WorkflowDefinitionService>();
        services.AddScoped<IWorkflowEngineService, WorkflowEngineService>();
        services.AddScoped<IEngineSettingsService, EngineSettingsService>();
        return services;
    }
}
