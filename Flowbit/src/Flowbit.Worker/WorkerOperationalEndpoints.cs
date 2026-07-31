using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;

namespace Flowbit.Worker;

public static class WorkerOperationalEndpoints
{
    public static WebApplication MapWorkerOperationalEndpoints(this WebApplication app)
    {
        app.MapHealthChecks(
            "/health/live",
            new HealthCheckOptions
            {
                Predicate = registration => registration.Tags.Contains("live")
            });
        app.MapHealthChecks(
            "/health/ready",
            new HealthCheckOptions
            {
                Predicate = registration => registration.Tags.Contains("ready")
            });
        app.MapGet(
            "/metrics",
            (WorkerTelemetry telemetry) => Results.Text(
                telemetry.ExportPrometheus(),
                "text/plain; version=0.0.4; charset=utf-8"));
        return app;
    }
}
