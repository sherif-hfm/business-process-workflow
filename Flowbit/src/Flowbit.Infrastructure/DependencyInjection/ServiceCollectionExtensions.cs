using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Http;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Infrastructure.Scripting;
using Flowbit.Service.Abstractions;

namespace Flowbit.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Flowbit")
            ?? configuration.GetConnectionString("WorkflowEngine")
            ?? throw new InvalidOperationException(
                "Connection string 'Flowbit' is missing (legacy key 'WorkflowEngine' is also supported).");

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.EnableDynamicJson();
        var dataSource = dataSourceBuilder.Build();

        services.AddSingleton(dataSource);
        services.TryAddSingleton(new ServiceTaskOptions());
        services.TryAddSingleton(new ScriptOptions());
        services.AddDbContext<AppDbContext>(options => options.UseNpgsql(dataSource));
        services.AddMemoryCache();
        services.AddScoped<IWorkflowDefinitionRepository, WorkflowDefinitionRepository>();
        services.AddScoped<IWorkflowRuntimeRepository, WorkflowRuntimeRepository>();
        services.AddScoped<IWorkflowSettingsRepository, WorkflowSettingsRepository>();
        services.AddScoped<IEngineSettingsRepository, EngineSettingsRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<DatabaseInitializer>();
        services.AddHttpClient<IServiceTaskInvoker, HttpServiceTaskInvoker>(client =>
            client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<IScriptEvaluator, JintScriptEvaluator>();

        return services;
    }
}
