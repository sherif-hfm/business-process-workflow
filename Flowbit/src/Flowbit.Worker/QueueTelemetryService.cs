using Flowbit.Service.Abstractions;

namespace Flowbit.Worker;

/// <summary>
/// Samples authoritative queue gauges independently of dispatcher activity, so
/// depth and oldest-age metrics continue to move while every slot is busy.
/// </summary>
public sealed class QueueTelemetryService(
    IServiceScopeFactory scopeFactory,
    WorkerTelemetry telemetry,
    ILogger<QueueTelemetryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var repository = scope.ServiceProvider
                    .GetRequiredService<IWorkflowJobRepository>();
                telemetry.RecordQueueSnapshot(
                    await repository.GetQueueStatisticsAsync(stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogDebug(
                    exception,
                    "Could not sample durable queue telemetry.");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken))
            {
                break;
            }
        }
    }
}
