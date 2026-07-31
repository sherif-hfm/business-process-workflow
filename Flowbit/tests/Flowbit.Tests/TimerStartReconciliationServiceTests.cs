extern alias FlowbitWorker;

using System.Reflection;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using TimerStartReconciliationService = FlowbitWorker::Flowbit.Worker.TimerStartReconciliationService;
using WorkerOptions = FlowbitWorker::Flowbit.Worker.WorkerOptions;
using WorkerTelemetry = FlowbitWorker::Flowbit.Worker.WorkerTelemetry;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class TimerStartReconciliationServiceTests(PostgresApiFixture fixture)
{
    [Fact]
    public async Task ReconciliationLeaderCreatesOneDurableOccurrenceForPublishedDefault()
    {
        var workflowKey = $"timer-reconcile-{Guid.NewGuid():N}";
        await using (var context = fixture.CreateDbContext())
        using (var cache = new MemoryCache(new MemoryCacheOptions()))
        {
            var definitions = new WorkflowDefinitionRepository(context, cache);
            await definitions.AddAsync(
                "Reconciliation timer",
                new WorkflowModel
                {
                    Id = workflowKey,
                    Name = "Reconciliation timer",
                    FlowNodes =
                    [
                        new FlowNodeModel
                        {
                            Id = 1,
                            Name = "Scheduled start",
                            Type = BpmnFlowNodeTypes.TimerStartEvent,
                            Timer = new TimerDefinitionModel { TimeDuration = "PT1H" }
                        }
                    ]
                },
                true,
                CancellationToken.None);
        }

        var options = new WorkerOptions { TimerStartReconcileBatchSize = 1000 };
        using var telemetry = new WorkerTelemetry();
        var service = new TimerStartReconciliationService(
            fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            options,
            TimeProvider.System,
            telemetry,
            NullLogger<TimerStartReconciliationService>.Instance);
        var reconcile = typeof(TimerStartReconciliationService).GetMethod(
            "ReconcileAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ReconcileAsync was not found.");

        var invocation = reconcile.Invoke(service, [CancellationToken.None]);
        await Assert.IsAssignableFrom<Task>(invocation);

        await using var verification = fixture.CreateDbContext();
        var subscription = await verification.TimerSubscriptions
            .AsNoTracking()
            .SingleAsync(row => row.WorkflowKey == workflowKey);
        Assert.Equal(TimerSubscriptionStatuses.Active, subscription.Status);
        var occurrence = await verification.WorkflowJobs
            .AsNoTracking()
            .SingleAsync(job => job.TimerSubscriptionId == subscription.Id);
        Assert.Equal(WorkflowJobKinds.TimerStart, occurrence.Kind);
        Assert.Equal(WorkflowJobStatuses.Queued, occurrence.Status);
        Assert.Equal(subscription.NextDueAt, occurrence.ScheduledOccurrenceAt);
    }
}
