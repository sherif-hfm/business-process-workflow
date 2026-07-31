using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Flowbit.Worker;

public sealed class WorkerReadinessHealthCheck(WorkerTelemetry telemetry) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(telemetry.IsReady
            ? HealthCheckResult.Healthy("The durable queue has been queried successfully.")
            : HealthCheckResult.Unhealthy("The durable queue has not completed its first query."));
}
