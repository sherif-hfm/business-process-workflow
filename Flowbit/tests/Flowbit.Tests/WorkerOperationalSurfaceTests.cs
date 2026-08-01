extern alias FlowbitWorker;

using System.Diagnostics.Metrics;
using System.Net;
using Flowbit.Service.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;
using WorkerOptions = FlowbitWorker::Flowbit.Worker.WorkerOptions;
using WorkerOperationalEndpoints = FlowbitWorker::Flowbit.Worker.WorkerOperationalEndpoints;
using WorkerReadinessHealthCheck = FlowbitWorker::Flowbit.Worker.WorkerReadinessHealthCheck;
using WorkerTelemetry = FlowbitWorker::Flowbit.Worker.WorkerTelemetry;

namespace Flowbit.Tests;

public sealed class WorkerOperationalSurfaceTests
{
    [Fact]
    public async Task OperationalEndpointsExposeLivenessReadinessAndMetrics()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var telemetry = new WorkerTelemetry();
        builder.Services.AddSingleton(telemetry);
        builder.Services.AddHealthChecks()
            .AddCheck(
                "self",
                static () => HealthCheckResult.Healthy(),
                tags: ["live"])
            .AddCheck<WorkerReadinessHealthCheck>("durable-queue", tags: ["ready"]);
        await using var app = builder.Build();
        WorkerOperationalEndpoints.MapWorkerOperationalEndpoints(app);
        await app.StartAsync();
        using var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(
            HttpStatusCode.ServiceUnavailable,
            (await client.GetAsync("/health/ready")).StatusCode);

        telemetry.RecordAcquisition(
            Array.Empty<WorkflowJobLeaseRecord>(),
            TimeSpan.FromMilliseconds(12.5),
            DateTimeOffset.UtcNow);
        using var runtimeMeter = new Meter("Flowbit.Runtime.Jobs");
        runtimeMeter.CreateCounter<long>("flowbit.jobs.output_conflicts").Add(2);
        runtimeMeter.CreateCounter<long>("flowbit.jobs.automatic_loop_limit").Add(1);
        runtimeMeter.CreateHistogram<double>("flowbit.jobs.instance_lock.wait").Record(25);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        var metricsResponse = await client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, metricsResponse.StatusCode);
        Assert.StartsWith("text/plain", metricsResponse.Content.Headers.ContentType?.MediaType);
        var metrics = await metricsResponse.Content.ReadAsStringAsync();
        Assert.Contains("flowbit_worker_ready 1", metrics, StringComparison.Ordinal);
        Assert.Contains(
            "flowbit_worker_acquisition_duration_seconds_count 1",
            metrics,
            StringComparison.Ordinal);
        Assert.Contains(
            "flowbit_worker_acquisition_duration_seconds_sum 0.0125",
            metrics,
            StringComparison.Ordinal);
        Assert.Contains(
            "flowbit_jobs_output_conflicts_total 2",
            metrics,
            StringComparison.Ordinal);
        Assert.Contains(
            "flowbit_jobs_automatic_loop_limit_total 1",
            metrics,
            StringComparison.Ordinal);
        Assert.Contains(
            "flowbit_jobs_instance_lock_wait_seconds_sum 0.025",
            metrics,
            StringComparison.Ordinal);
    }

    [Fact]
    public void HealthListenUrlMustBeAnAbsoluteHttpEndpoint()
    {
        var options = new WorkerOptions { HealthListenUrl = "not-a-url" };

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains("HealthListenUrl", exception.Message, StringComparison.Ordinal);
    }
}
