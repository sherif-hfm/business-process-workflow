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
        services.TryAddSingleton(new DurableProcessingOptions());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IConditionalEventDefinitionAnalyzer, ConditionalEventDefinitionAnalyzer>();
        services.TryAddSingleton<IConditionalEventDependencyPlanCache, ConditionalEventDependencyPlanCache>();
        services.AddScoped<IInstanceVariableMutationTracker, InstanceVariableMutationTracker>();
        services.AddScoped<IWorkflowDefinitionService, WorkflowDefinitionService>();
        services.AddScoped<WorkflowEngineService>();
        services.AddScoped<IWorkflowEngineService>(provider =>
            provider.GetRequiredService<WorkflowEngineService>());
        services.AddScoped<IConditionalEventRuntimeCoordinator>(provider =>
            provider.GetRequiredService<WorkflowEngineService>());
        services.AddScoped<IAdministrativeActionBatchService, AdministrativeActionBatchService>();
        services.AddScoped<IAdministrativeActionBatchJobProcessor, AdministrativeActionBatchJobProcessor>();
        services.AddScoped<IInstanceVersionChangeBatchService, InstanceVersionChangeBatchService>();
        services.AddScoped<IInstanceVersionChangeBatchJobProcessor, InstanceVersionChangeBatchJobProcessor>();
        services.AddScoped<IInstanceVersionChangeBatchExecutor>(provider =>
            provider.GetRequiredService<WorkflowEngineService>());
        services.AddScoped<InstanceVariableUpdateService>();
        services.AddScoped<IInstanceVariableUpdateService>(provider =>
            provider.GetRequiredService<InstanceVariableUpdateService>());
        services.AddScoped<IInstanceVariableUpdateExecutor>(provider =>
            provider.GetRequiredService<InstanceVariableUpdateService>());
        services.AddScoped<IInstanceVariableUpdateBatchService, InstanceVariableUpdateBatchService>();
        services.AddScoped<IInstanceVariableUpdateBatchJobProcessor, InstanceVariableUpdateBatchJobProcessor>();
        services.AddScoped<IWorkflowJobProcessor, WorkflowJobProcessorRouter>();
        services.AddScoped<IWorkflowJobOperationsService, WorkflowJobOperationsService>();
        services.AddScoped<INodeExecutionQueryService, NodeExecutionQueryService>();
        services.AddScoped<IEngineSettingsService, EngineSettingsService>();
        services.AddScoped<IWorkflowSettingsService, WorkflowSettingsService>();
        services.AddScoped<IUserDelegationService, UserDelegationService>();
        return services;
    }
}
