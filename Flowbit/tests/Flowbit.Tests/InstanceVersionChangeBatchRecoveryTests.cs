using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class InstanceVersionChangeBatchRecoveryTests(PostgresApiFixture fixture)
{
    [Fact]
    public async Task FinalLeaseExhaustionFailsRemainingItemsAndSettlesEachParentDeterministically()
    {
        var workflowKey = $"version-batch-lease-exhaustion-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        long preparingBatchId = 0;
        long runningBatchId = 0;
        long cancelledBatchId = 0;
        try
        {
            await using (var setup = fixture.CreateDbContext())
            {
                var source = NewDefinition(workflowKey, 1, "Lease source");
                var target = NewDefinition(workflowKey, 2, "Lease target");
                setup.WorkflowDefinitions.AddRange(source, target);
                await setup.SaveChangesAsync();

                var instances = Enumerable.Range(0, 10)
                    .Select(_ => new WorkflowInstanceEntity
                    {
                        WorkflowDefinitionId = source.Id,
                        WorkflowKey = workflowKey,
                        Status = "running",
                        StartedBy = "lease-recovery-test",
                        CreatedAt = now,
                        UpdatedAt = now
                    })
                    .ToArray();
                setup.WorkflowInstances.AddRange(instances);
                await setup.SaveChangesAsync();

                var preparationJob = NewFinalExpiredJob(
                    source,
                    workflowKey,
                    WorkflowJobKinds.InstanceVersionChangeBatchPrepare,
                    now.AddHours(-3));
                var executionJob = NewFinalExpiredJob(
                    source,
                    workflowKey,
                    WorkflowJobKinds.InstanceVersionChangeBatchExecute,
                    now.AddHours(-2));
                var cancelledExecutionJob = NewFinalExpiredJob(
                    source,
                    workflowKey,
                    WorkflowJobKinds.InstanceVersionChangeBatchExecute,
                    now.AddHours(-1));
                setup.WorkflowJobs.AddRange(
                    preparationJob,
                    executionJob,
                    cancelledExecutionJob);
                await setup.SaveChangesAsync();

                var preparingBatch = NewBatch(
                    source,
                    target,
                    workflowKey,
                    InstanceVersionChangeBatchStatuses.Preparing,
                    now);
                preparingBatch.PreparationJobId = preparationJob.Id;
                preparingBatch.TotalItemCount = 5;
                preparingBatch.EligibleItemCount = 1;
                preparingBatch.IneligibleItemCount = 2;
                preparingBatch.BlockedItemCount = 1;
                preparingBatch.WarningItemCount = 1;
                preparingBatch.StaleItemCount = 1;
                preparingBatch.SucceededItemCount = 1;

                var runningBatch = NewBatch(
                    source,
                    target,
                    workflowKey,
                    InstanceVersionChangeBatchStatuses.Running,
                    now);
                runningBatch.ExecutionJobId = executionJob.Id;
                runningBatch.TotalItemCount = 3;
                runningBatch.QueuedItemCount = 1;
                runningBatch.SucceededItemCount = 1;
                runningBatch.SkippedItemCount = 1;
                runningBatch.PreparedAt = now;
                runningBatch.ConfirmedAt = now;
                runningBatch.StartedAt = now;

                var cancelledBatch = NewBatch(
                    source,
                    target,
                    workflowKey,
                    InstanceVersionChangeBatchStatuses.Cancelled,
                    now);
                cancelledBatch.ExecutionJobId = cancelledExecutionJob.Id;
                cancelledBatch.TotalItemCount = 2;
                cancelledBatch.QueuedItemCount = 1;
                cancelledBatch.CancelledItemCount = 1;
                cancelledBatch.PreparedAt = now;
                cancelledBatch.ConfirmedAt = now;
                cancelledBatch.StartedAt = now;
                cancelledBatch.CancelledBy = "lease-recovery-test";
                cancelledBatch.CancelledAt = now;
                cancelledBatch.CompletedAt = now;

                setup.WorkflowInstanceVersionChangeBatches.AddRange(
                    preparingBatch,
                    runningBatch,
                    cancelledBatch);
                await setup.SaveChangesAsync();
                preparingBatchId = preparingBatch.Id;
                runningBatchId = runningBatch.Id;
                cancelledBatchId = cancelledBatch.Id;

                setup.WorkflowInstanceVersionChangeBatchItems.AddRange(
                    NewItem(preparingBatch, instances[0], source,
                        InstanceVersionChangeBatchItemStatuses.Preparing, now),
                    NewItem(preparingBatch, instances[1], source,
                        InstanceVersionChangeBatchItemStatuses.Eligible, now,
                        warnings: """[{"Code":"manual_review","Message":"Review this instance."}]"""),
                    NewItem(preparingBatch, instances[2], source,
                        InstanceVersionChangeBatchItemStatuses.Ineligible, now,
                        errorCode: "stale_since_selection",
                        errorDescription: "The instance changed after selection."),
                    NewItem(preparingBatch, instances[3], source,
                        InstanceVersionChangeBatchItemStatuses.Succeeded, now,
                        result: """{"outcome":"preserved"}"""),
                    NewItem(runningBatch, instances[4], source,
                        InstanceVersionChangeBatchItemStatuses.Queued, now,
                        startedAt: now),
                    NewItem(runningBatch, instances[5], source,
                        InstanceVersionChangeBatchItemStatuses.Succeeded, now,
                        result: """{"outcome":"preserved"}"""),
                    NewItem(runningBatch, instances[6], source,
                        InstanceVersionChangeBatchItemStatuses.Skipped, now,
                        errorCode: "stale_since_preparation",
                        errorDescription: "The instance changed after preparation."),
                    NewItem(cancelledBatch, instances[7], source,
                        InstanceVersionChangeBatchItemStatuses.Queued, now),
                    NewItem(cancelledBatch, instances[8], source,
                        InstanceVersionChangeBatchItemStatuses.Cancelled, now),
                    NewItem(preparingBatch, instances[9], source,
                        InstanceVersionChangeBatchItemStatuses.Ineligible, now,
                        errorCode: "incompatible",
                        errorDescription: "The instance has a compatibility blocker."));
                await setup.SaveChangesAsync();
            }

            await using (var leaseContext = fixture.CreateDbContext())
            {
                var repository = new WorkflowJobRepository(leaseContext, fixture.DataSource);
                await repository.LeaseRunnableAsync(
                    new WorkflowJobLeaseRequest(
                        $"version-batch-exhaustion-sweeper-{workflowKey}",
                        MaxCount: 3,
                        MaxActivityCount: 0,
                        MaxPerInstance: 1,
                        LeaseDuration: TimeSpan.FromMinutes(1)),
                    CancellationToken.None);
            }

            await using var verification = fixture.CreateDbContext();
            var preparing = await verification.WorkflowInstanceVersionChangeBatches
                .AsNoTracking()
                .SingleAsync(batch => batch.Id == preparingBatchId);
            Assert.Equal(InstanceVersionChangeBatchStatuses.Failed, preparing.Status);
            Assert.Equal(5, preparing.TotalItemCount);
            Assert.Equal(0, preparing.EligibleItemCount);
            Assert.Equal(2, preparing.IneligibleItemCount);
            Assert.Equal(1, preparing.BlockedItemCount);
            Assert.Equal(1, preparing.WarningItemCount);
            Assert.Equal(1, preparing.StaleItemCount);
            Assert.Equal(1, preparing.SucceededItemCount);
            Assert.Equal(2, preparing.FailedItemCount);
            Assert.NotNull(preparing.CompletedAt);
            Assert.Equal(
                "lease_exhausted",
                preparing.IssuesJson!.RootElement[0].GetProperty("Code").GetString());

            var preparingItems = await verification.WorkflowInstanceVersionChangeBatchItems
                .AsNoTracking()
                .Where(item => item.BatchId == preparingBatchId)
                .OrderBy(item => item.Id)
                .ToArrayAsync();
            Assert.All(preparingItems.Take(2), item =>
            {
                Assert.Equal(InstanceVersionChangeBatchItemStatuses.Failed, item.Status);
                Assert.Equal("lease_exhausted", item.ErrorCode);
                Assert.NotNull(item.CompletedAt);
            });
            Assert.NotNull(preparingItems[1].WarningsJson);
            Assert.Equal(InstanceVersionChangeBatchItemStatuses.Ineligible, preparingItems[2].Status);
            Assert.Equal("stale_since_selection", preparingItems[2].ErrorCode);
            Assert.Equal(InstanceVersionChangeBatchItemStatuses.Succeeded, preparingItems[3].Status);
            Assert.Equal(
                "preserved",
                preparingItems[3].ResultJson!.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(InstanceVersionChangeBatchItemStatuses.Ineligible, preparingItems[4].Status);
            Assert.Equal("incompatible", preparingItems[4].ErrorCode);

            var running = await verification.WorkflowInstanceVersionChangeBatches
                .AsNoTracking()
                .SingleAsync(batch => batch.Id == runningBatchId);
            Assert.Equal(InstanceVersionChangeBatchStatuses.Failed, running.Status);
            Assert.Equal(0, running.QueuedItemCount);
            Assert.Equal(1, running.SucceededItemCount);
            Assert.Equal(1, running.SkippedItemCount);
            Assert.Equal(1, running.StaleItemCount);
            Assert.Equal(0, running.BlockedItemCount);
            Assert.Equal(1, running.FailedItemCount);
            Assert.NotNull(running.CompletedAt);

            var cancelled = await verification.WorkflowInstanceVersionChangeBatches
                .AsNoTracking()
                .SingleAsync(batch => batch.Id == cancelledBatchId);
            Assert.Equal(InstanceVersionChangeBatchStatuses.Cancelled, cancelled.Status);
            Assert.Equal(0, cancelled.QueuedItemCount);
            Assert.Equal(1, cancelled.FailedItemCount);
            Assert.Equal(1, cancelled.CancelledItemCount);
            Assert.NotNull(cancelled.CompletedAt);
            Assert.InRange(
                (cancelled.CompletedAt.Value - now).Duration(),
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(1));

            var exhaustedJobs = await verification.WorkflowJobs
                .AsNoTracking()
                .Where(job => job.WorkflowKey == workflowKey)
                .OrderBy(job => job.Id)
                .ToArrayAsync();
            Assert.Equal(3, exhaustedJobs.Length);
            Assert.All(exhaustedJobs, job =>
            {
                Assert.Equal(WorkflowJobStatuses.Incident, job.Status);
                Assert.Equal("lease_exhausted", job.LastFailureCode);
            });
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    private static WorkflowDefinitionEntity NewDefinition(
        string workflowKey,
        int version,
        string name) =>
        new()
        {
            Name = name,
            WorkflowKey = workflowKey,
            Version = version,
            Definition = new WorkflowModel { Id = workflowKey, Name = name },
            IsPublished = true,
            IsDefault = false,
            CreatedAt = DateTimeOffset.UtcNow
        };

    private static WorkflowJobEntity NewFinalExpiredJob(
        WorkflowDefinitionEntity definition,
        string workflowKey,
        string kind,
        DateTimeOffset expiredAt)
    {
        var job = new WorkflowJobEntity
        {
            WorkflowDefinitionId = definition.Id,
            WorkflowKey = workflowKey,
            ActivationId = Guid.NewGuid(),
            NodeId = 0,
            NodeName = "Instance version-change batch",
            NodeType = "instanceVersionChangeBatch",
            Kind = kind,
            QueueClass = WorkflowJobClasses.Activity,
            Phase = kind == WorkflowJobKinds.InstanceVersionChangeBatchPrepare
                ? "prepare"
                : "execute",
            Status = WorkflowJobStatuses.Running,
            Priority = 100,
            AttemptCount = 1,
            MaxAttempts = 1,
            FailureHandling = WorkflowJobFailureHandling.RetryFirst,
            RetryDelays = [],
            DueAt = expiredAt.AddMinutes(-10),
            WorkerId = $"expired-version-batch-worker-{Guid.NewGuid():N}",
            LeaseToken = Guid.NewGuid(),
            LeaseGeneration = 1,
            LeaseExpiresAt = expiredAt,
            HeartbeatAt = expiredAt.AddMinutes(-1),
            StartedAt = expiredAt.AddMinutes(-2),
            CreatedAt = expiredAt.AddMinutes(-10),
            UpdatedAt = expiredAt
        };
        job.Attempts.Add(new WorkflowJobAttemptEntity
        {
            AttemptNumber = 1,
            Status = WorkflowJobAttemptStatuses.Running,
            WorkerId = job.WorkerId,
            LeaseGeneration = job.LeaseGeneration,
            StartedAt = job.StartedAt.Value
        });
        return job;
    }

    private static WorkflowInstanceVersionChangeBatchEntity NewBatch(
        WorkflowDefinitionEntity source,
        WorkflowDefinitionEntity target,
        string workflowKey,
        string status,
        DateTimeOffset now) =>
        new()
        {
            WorkflowKey = workflowKey,
            SourceWorkflowDefinitionId = source.Id,
            TargetWorkflowDefinitionId = target.Id,
            Reason = "Final lease-exhaustion recovery test.",
            SelectionJson = JsonDocument.Parse("""{"mode":"explicit"}"""),
            Status = status,
            PreparedBy = "lease-recovery-test",
            PreparedByRolesJson = JsonDocument.Parse("""["admin"]"""),
            CreatedAt = now,
            UpdatedAt = now
        };

    private static WorkflowInstanceVersionChangeBatchItemEntity NewItem(
        WorkflowInstanceVersionChangeBatchEntity batch,
        WorkflowInstanceEntity instance,
        WorkflowDefinitionEntity source,
        string status,
        DateTimeOffset now,
        string? warnings = null,
        string? result = null,
        string? errorCode = null,
        string? errorDescription = null,
        DateTimeOffset? startedAt = null) =>
        new()
        {
            BatchId = batch.Id,
            InstanceId = instance.Id,
            CapturedSourceWorkflowDefinitionId = source.Id,
            CapturedInstanceUpdatedAt = instance.UpdatedAt,
            Status = status,
            WarningsJson = warnings is null ? null : JsonDocument.Parse(warnings),
            ResultJson = result is null ? null : JsonDocument.Parse(result),
            ErrorCode = errorCode,
            ErrorDescription = errorDescription,
            CreatedAt = now,
            UpdatedAt = now,
            PreparedAt = status == InstanceVersionChangeBatchItemStatuses.Preparing
                ? null
                : now,
            StartedAt = startedAt,
            CompletedAt = status is InstanceVersionChangeBatchItemStatuses.Ineligible
                or InstanceVersionChangeBatchItemStatuses.Succeeded
                or InstanceVersionChangeBatchItemStatuses.Skipped
                or InstanceVersionChangeBatchItemStatuses.Failed
                or InstanceVersionChangeBatchItemStatuses.Cancelled
                ? now
                : null
        };

    private async Task DeleteWorkflowAsync(string workflowKey)
    {
        await using var cleanup = fixture.CreateDbContext();
        var batchIds = await cleanup.WorkflowInstanceVersionChangeBatches
            .Where(batch => batch.WorkflowKey == workflowKey)
            .Select(batch => batch.Id)
            .ToArrayAsync();
        if (batchIds.Length > 0)
        {
            await cleanup.WorkflowInstanceVersionChanges
                .Where(change => change.BatchId != null && batchIds.Contains(change.BatchId.Value))
                .ExecuteDeleteAsync();
            await cleanup.WorkflowInstanceVersionChangeBatchItems
                .Where(item => batchIds.Contains(item.BatchId))
                .ExecuteDeleteAsync();
            await cleanup.WorkflowInstanceVersionChangeBatches
                .Where(batch => batchIds.Contains(batch.Id))
                .ExecuteDeleteAsync();
        }

        await cleanup.WorkflowIncidents
            .Where(incident => incident.WorkflowKey == workflowKey)
            .ExecuteDeleteAsync();
        await cleanup.WorkflowJobs
            .Where(job => job.WorkflowKey == workflowKey)
            .ExecuteDeleteAsync();
        await cleanup.WorkflowInstances
            .Where(instance => instance.WorkflowKey == workflowKey)
            .ExecuteDeleteAsync();
        await cleanup.WorkflowDefinitions
            .Where(definition => definition.WorkflowKey == workflowKey)
            .ExecuteDeleteAsync();
    }
}
