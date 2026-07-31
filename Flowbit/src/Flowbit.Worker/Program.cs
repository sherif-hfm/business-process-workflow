using Flowbit.Infrastructure.DependencyInjection;
using Flowbit.Service.Abstractions;
using Flowbit.Service.DependencyInjection;
using Flowbit.Service.Services;
using Flowbit.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

CapWorkerPool(builder.Configuration);

var workerOptions = builder.Configuration
    .GetSection(WorkerOptions.SectionName)
    .Get<WorkerOptions>() ?? new WorkerOptions();
workerOptions.Validate();
builder.WebHost.UseUrls(workerOptions.HealthListenUrl);
builder.Services.AddSingleton(workerOptions);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<HostOptions>(options =>
    options.ShutdownTimeout = TimeSpan.FromSeconds(
        workerOptions.ShutdownDrainSeconds + 5));

var contextOptions = builder.Configuration
    .GetSection(WorkflowContextOptions.SectionName)
    .Get<WorkflowContextOptions>() ?? new WorkflowContextOptions();
builder.Services.AddSingleton(contextOptions);

var serviceTaskOptions = builder.Configuration
    .GetSection(ServiceTaskOptions.SectionName)
    .Get<ServiceTaskOptions>() ?? new ServiceTaskOptions();
if (serviceTaskOptions.MaxTimeoutSeconds <= 0 || serviceTaskOptions.MaxResponseBodyBytes <= 0)
{
    throw new InvalidOperationException("WorkflowServiceTasks limits must be positive.");
}
builder.Services.AddSingleton(serviceTaskOptions);

var scriptOptions = builder.Configuration
    .GetSection(ScriptOptions.SectionName)
    .Get<ScriptOptions>() ?? new ScriptOptions();
scriptOptions.Validate();
builder.Services.AddSingleton(scriptOptions);
builder.Services.AddSingleton(new MessageDeliveryOptions());

builder.Services
    .AddServiceLayer()
    .AddInfrastructure(builder.Configuration);

builder.Services.AddSingleton<PostgresJobWakeupSignal>();
builder.Services.AddSingleton<IJobWakeupSignal>(provider =>
    provider.GetRequiredService<PostgresJobWakeupSignal>());
builder.Services.AddHostedService(provider =>
    provider.GetRequiredService<PostgresJobWakeupSignal>());
builder.Services.AddHostedService<JobDispatcher>();
builder.Services.AddHostedService<QueueTelemetryService>();
builder.Services.AddHostedService<JobCleanupService>();
builder.Services.AddHostedService<TimerStartReconciliationService>();
builder.Services.AddSingleton<WorkerTelemetry>();
builder.Services.AddHealthChecks()
    .AddCheck(
        "self",
        static () => HealthCheckResult.Healthy(),
        tags: ["live"])
    .AddCheck<WorkerReadinessHealthCheck>("durable-queue", tags: ["ready"]);

var app = builder.Build();
app.MapWorkerOperationalEndpoints();

await app.RunAsync();

static void CapWorkerPool(ConfigurationManager configuration)
{
    var key = configuration.GetConnectionString("Flowbit") is not null
        ? "Flowbit"
        : "WorkflowEngine";
    var configured = configuration.GetConnectionString(key)
        ?? throw new InvalidOperationException(
            "Connection string 'Flowbit' is missing (legacy key 'WorkflowEngine' is also supported).");
    var builder = new NpgsqlConnectionStringBuilder(configured);
    var maximum = Math.Min(builder.MaxPoolSize, 16);
    builder.MinPoolSize = Math.Min(builder.MinPoolSize, maximum);
    builder.MaxPoolSize = maximum;
    builder.ApplicationName = "Flowbit.Worker";
    configuration[$"ConnectionStrings:{key}"] = builder.ConnectionString;
}
