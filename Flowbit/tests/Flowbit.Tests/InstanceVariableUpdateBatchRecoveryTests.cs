using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class InstanceVariableUpdateBatchRecoveryTests(
    PostgresApiFixture fixture)
{
    [Fact]
    public async Task FinalPrepareAndExecuteLeaseExhaustionFailsOnlyOwningDefinitionGroupsAndSettlesParents()
    {
        var workflowKey = $"variable-update-recovery-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        long preparingBatchId = 0;
        long runningBatchId = 0;
        long expiredPrepareJobId = 0;
        long expiredExecuteJobId = 0;
        try
        {
            await using (var setup = fixture.CreateDbContext())
            {
                var versionOne = NewDefinition(workflowKey, 1);
                var versionTwo = NewDefinition(workflowKey, 2);
                setup.WorkflowDefinitions.AddRange(versionOne, versionTwo);
                await setup.SaveChangesAsync();

                var instances = new[]
                {
                    NewInstance(versionOne, now),
                    NewInstance(versionTwo, now),
                    NewInstance(versionOne, now),
                    NewInstance(versionTwo, now)
                };
                setup.WorkflowInstances.AddRange(instances);
                await setup.SaveChangesAsync();

                var expiredPrepare = NewFinalExpiredJob(
                    versionOne,
                    workflowKey,
                    WorkflowJobKinds.InstanceVariableUpdateBatchPrepare,
                    InstanceVariableUpdateBatchPhases.Prepare,
                    now.AddHours(-2));
                var settledPrepare = NewSettledJob(
                    versionTwo,
                    workflowKey,
                    WorkflowJobKinds.InstanceVariableUpdateBatchPrepare,
                    InstanceVariableUpdateBatchPhases.Prepare,
                    now.AddHours(-2));
                var expiredExecute = NewFinalExpiredJob(
                    versionOne,
                    workflowKey,
                    WorkflowJobKinds.InstanceVariableUpdateBatchExecute,
                    InstanceVariableUpdateBatchPhases.Execute,
                    now.AddHours(-1));
                var settledExecute = NewSettledJob(
                    versionTwo,
                    workflowKey,
                    WorkflowJobKinds.InstanceVariableUpdateBatchExecute,
                    InstanceVariableUpdateBatchPhases.Execute,
                    now.AddHours(-1));
                setup.WorkflowJobs.AddRange(
                    expiredPrepare,
                    settledPrepare,
                    expiredExecute,
                    settledExecute);
                await setup.SaveChangesAsync();
                expiredPrepareJobId = expiredPrepare.Id;
                expiredExecuteJobId = expiredExecute.Id;

                var preparingBatch = NewBatch(
                    workflowKey,
                    InstanceVariableUpdateBatchStatuses.Preparing,
                    now);
                preparingBatch.TotalItemCount = 2;
                preparingBatch.EligibleItemCount = 1;

                var runningBatch = NewBatch(
                    workflowKey,
                    InstanceVariableUpdateBatchStatuses.Running,
                    now);
                runningBatch.TotalItemCount = 2;
                runningBatch.QueuedItemCount = 1;
                runningBatch.SucceededItemCount = 1;
                runningBatch.PreparedAt = now;
                runningBatch.ConfirmedAt = now;
                runningBatch.StartedAt = now;
                setup.InstanceVariableUpdateBatches.AddRange(preparingBatch, runningBatch);
                await setup.SaveChangesAsync();
                preparingBatchId = preparingBatch.Id;
                runningBatchId = runningBatch.Id;

                setup.InstanceVariableUpdateBatchJobLinks.AddRange(
                    NewLink(preparingBatch, versionOne, expiredPrepare, InstanceVariableUpdateBatchPhases.Prepare),
                    NewLink(preparingBatch, versionTwo, settledPrepare, InstanceVariableUpdateBatchPhases.Prepare),
                    NewLink(runningBatch, versionOne, expiredExecute, InstanceVariableUpdateBatchPhases.Execute),
                    NewLink(runningBatch, versionTwo, settledExecute, InstanceVariableUpdateBatchPhases.Execute));
                setup.InstanceVariableUpdateBatchItems.AddRange(
                    NewItem(
                        preparingBatch,
                        instances[0],
                        versionOne,
                        InstanceVariableUpdateBatchItemStatuses.Preparing,
                        now),
                    NewItem(
                        preparingBatch,
                        instances[1],
                        versionTwo,
                        InstanceVariableUpdateBatchItemStatuses.Eligible,
                        now,
                        plan: """[{"Name":"approved","Outcome":"added"}]""",
                        warnings: """[{"Code":"settled","Message":"Preserve this warning."}]"""),
                    NewItem(
                        runningBatch,
                        instances[2],
                        versionOne,
                        InstanceVariableUpdateBatchItemStatuses.Queued,
                        now,
                        startedAt: now),
                    NewItem(
                        runningBatch,
                        instances[3],
                        versionTwo,
                        InstanceVariableUpdateBatchItemStatuses.Succeeded,
                        now,
                        result: """{"operationId":4321,"outcome":"preserved"}"""));
                await setup.SaveChangesAsync();
            }

            await using (var sweep = fixture.CreateDbContext())
            {
                var repository = new WorkflowJobRepository(sweep, fixture.DataSource);
                await repository.LeaseRunnableAsync(
                    new WorkflowJobLeaseRequest(
                        $"variable-update-recovery-sweeper-{workflowKey}",
                        MaxCount: 4,
                        MaxActivityCount: 0,
                        MaxPerInstance: 1,
                        LeaseDuration: TimeSpan.FromMinutes(1)),
                    CancellationToken.None);
            }

            await using var verification = fixture.CreateDbContext();
            var preparing = await verification.InstanceVariableUpdateBatches
                .AsNoTracking()
                .SingleAsync(batch => batch.Id == preparingBatchId);
            Assert.Equal(InstanceVariableUpdateBatchStatuses.Ready, preparing.Status);
            Assert.Equal(2, preparing.TotalItemCount);
            Assert.Equal(1, preparing.EligibleItemCount);
            Assert.Equal(1, preparing.FailedItemCount);
            Assert.Equal(1, preparing.WarningItemCount);
            Assert.NotNull(preparing.PreparedAt);
            Assert.Null(preparing.CompletedAt);
            Assert.Equal(
                "lease_exhausted",
                preparing.IssuesJson!.RootElement[0].GetProperty("Code").GetString());

            var preparingItems = await verification.InstanceVariableUpdateBatchItems
                .AsNoTracking()
                .Where(item => item.BatchId == preparingBatchId)
                .OrderBy(item => item.CapturedWorkflowDefinitionId)
                .ToArrayAsync();
            Assert.Equal(InstanceVariableUpdateBatchItemStatuses.Failed, preparingItems[0].Status);
            Assert.Equal("lease_exhausted", preparingItems[0].ErrorCode);
            Assert.NotNull(preparingItems[0].CompletedAt);
            Assert.Equal(InstanceVariableUpdateBatchItemStatuses.Eligible, preparingItems[1].Status);
            Assert.Null(preparingItems[1].ErrorCode);
            Assert.Equal(
                "approved",
                preparingItems[1].PlanJson!.RootElement[0].GetProperty("Name").GetString());
            Assert.Equal(
                "settled",
                preparingItems[1].WarningsJson!.RootElement[0].GetProperty("Code").GetString());

            var running = await verification.InstanceVariableUpdateBatches
                .AsNoTracking()
                .SingleAsync(batch => batch.Id == runningBatchId);
            Assert.Equal(
                InstanceVariableUpdateBatchStatuses.CompletedWithIssues,
                running.Status);
            Assert.Equal(2, running.TotalItemCount);
            Assert.Equal(0, running.QueuedItemCount);
            Assert.Equal(1, running.SucceededItemCount);
            Assert.Equal(1, running.FailedItemCount);
            Assert.NotNull(running.CompletedAt);
            Assert.Equal(
                "lease_exhausted",
                running.IssuesJson!.RootElement[0].GetProperty("Code").GetString());

            var runningItems = await verification.InstanceVariableUpdateBatchItems
                .AsNoTracking()
                .Where(item => item.BatchId == runningBatchId)
                .OrderBy(item => item.CapturedWorkflowDefinitionId)
                .ToArrayAsync();
            Assert.Equal(InstanceVariableUpdateBatchItemStatuses.Failed, runningItems[0].Status);
            Assert.Equal("lease_exhausted", runningItems[0].ErrorCode);
            Assert.Equal(InstanceVariableUpdateBatchItemStatuses.Succeeded, runningItems[1].Status);
            Assert.Null(runningItems[1].ErrorCode);
            Assert.Equal(
                "preserved",
                runningItems[1].ResultJson!.RootElement.GetProperty("outcome").GetString());

            var exhaustedJobs = await verification.WorkflowJobs
                .AsNoTracking()
                .Where(job => job.Id == expiredPrepareJobId || job.Id == expiredExecuteJobId)
                .OrderBy(job => job.Id)
                .ToArrayAsync();
            Assert.Equal(2, exhaustedJobs.Length);
            Assert.All(exhaustedJobs, job =>
            {
                Assert.Equal(WorkflowJobStatuses.Incident, job.Status);
                Assert.Equal("lease_exhausted", job.LastFailureCode);
                Assert.Null(job.LeaseToken);
                Assert.Null(job.LeaseExpiresAt);
            });

            var incidents = await verification.WorkflowIncidents
                .AsNoTracking()
                .Where(incident =>
                    incident.OriginalJobId == expiredPrepareJobId
                    || incident.OriginalJobId == expiredExecuteJobId)
                .OrderBy(incident => incident.OriginalJobId)
                .ToArrayAsync();
            Assert.Equal(2, incidents.Length);
            Assert.All(incidents, incident =>
            {
                Assert.Equal("lease_exhausted", incident.Type);
                Assert.Equal(WorkflowIncidentStatuses.Open, incident.Status);
                Assert.Equal(incident.OriginalJobId, incident.JobId);
            });
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    private static WorkflowDefinitionEntity NewDefinition(
        string workflowKey,
        int version) =>
        new()
        {
            Name = $"Variable update recovery v{version}",
            WorkflowKey = workflowKey,
            Version = version,
            Definition = new WorkflowModel
            {
                Id = workflowKey,
                Name = $"Variable update recovery v{version}"
            },
            IsPublished = true,
            IsDefault = false,
            CreatedAt = DateTimeOffset.UtcNow
        };

    private static WorkflowInstanceEntity NewInstance(
        WorkflowDefinitionEntity definition,
        DateTimeOffset now) =>
        new()
        {
            WorkflowDefinitionId = definition.Id,
            WorkflowKey = definition.WorkflowKey,
            Status = WorkflowInstanceStatuses.Running,
            CreatedAt = now,
            UpdatedAt = now
        };

    private static WorkflowJobEntity NewFinalExpiredJob(
        WorkflowDefinitionEntity definition,
        string workflowKey,
        string kind,
        string phase,
        DateTimeOffset expiredAt)
    {
        var job = NewJob(definition, workflowKey, kind, phase, expiredAt);
        job.Status = WorkflowJobStatuses.Running;
        job.AttemptCount = 1;
        job.MaxAttempts = 1;
        job.WorkerId = $"expired-variable-update-{Guid.NewGuid():N}";
        job.LeaseToken = Guid.NewGuid();
        job.LeaseGeneration = 1;
        job.LeaseExpiresAt = expiredAt;
        job.HeartbeatAt = expiredAt.AddMinutes(-1);
        job.StartedAt = expiredAt.AddMinutes(-2);
        job.Attempts.Add(new WorkflowJobAttemptEntity
        {
            AttemptNumber = 1,
            Status = WorkflowJobAttemptStatuses.Running,
            WorkerId = job.WorkerId,
            LeaseGeneration = 1,
            StartedAt = job.StartedAt.Value
        });
        return job;
    }

    private static WorkflowJobEntity NewSettledJob(
        WorkflowDefinitionEntity definition,
        string workflowKey,
        string kind,
        string phase,
        DateTimeOffset completedAt)
    {
        var job = NewJob(definition, workflowKey, kind, phase, completedAt);
        job.Status = WorkflowJobStatuses.Completed;
        job.CompletedAt = completedAt;
        return job;
    }

    private static WorkflowJobEntity NewJob(
        WorkflowDefinitionEntity definition,
        string workflowKey,
        string kind,
        string phase,
        DateTimeOffset at) =>
        new()
        {
            WorkflowDefinitionId = definition.Id,
            WorkflowKey = workflowKey,
            ActivationId = Guid.NewGuid(),
            NodeId = 0,
            NodeName = "Instance variable-update batch",
            NodeType = "instanceVariableUpdateBatch",
            Kind = kind,
            QueueClass = WorkflowJobClasses.Activity,
            Phase = phase,
            Status = WorkflowJobStatuses.Queued,
            Priority = 100,
            MaxAttempts = 1,
            FailureHandling = WorkflowJobFailureHandling.RetryFirst,
            RetryDelays = [],
            DueAt = at.AddMinutes(-10),
            CreatedAt = at.AddMinutes(-10),
            UpdatedAt = at
        };

    private static InstanceVariableUpdateBatchEntity NewBatch(
        string workflowKey,
        string status,
        DateTimeOffset now) =>
        new()
        {
            WorkflowKey = workflowKey,
            VariablesJson = JsonDocument.Parse("""[{"Name":"approved","Value":true}]"""),
            SelectionJson = JsonDocument.Parse("""{"Mode":"explicit"}"""),
            Reason = "Final lease-exhaustion recovery test.",
            Status = status,
            PreparedBy = "lease-recovery-test",
            PreparedByRolesJson = JsonDocument.Parse("""["admin"]"""),
            ConfirmedBy = status == InstanceVariableUpdateBatchStatuses.Preparing
                ? null
                : "lease-recovery-test",
            ConfirmedByRolesJson = status == InstanceVariableUpdateBatchStatuses.Preparing
                ? null
                : JsonDocument.Parse("""["admin"]"""),
            CreatedAt = now,
            UpdatedAt = now
        };

    private static InstanceVariableUpdateBatchJobLinkEntity NewLink(
        InstanceVariableUpdateBatchEntity batch,
        WorkflowDefinitionEntity definition,
        WorkflowJobEntity job,
        string phase) =>
        new()
        {
            BatchId = batch.Id,
            WorkflowDefinitionId = definition.Id,
            Phase = phase,
            OriginalJobId = job.Id,
            JobId = job.Id
        };

    private static InstanceVariableUpdateBatchItemEntity NewItem(
        InstanceVariableUpdateBatchEntity batch,
        WorkflowInstanceEntity instance,
        WorkflowDefinitionEntity definition,
        string status,
        DateTimeOffset now,
        string? plan = null,
        string? warnings = null,
        string? result = null,
        DateTimeOffset? startedAt = null) =>
        new()
        {
            BatchId = batch.Id,
            InstanceId = instance.Id,
            CapturedWorkflowDefinitionId = definition.Id,
            CapturedInstanceUpdatedAt = instance.UpdatedAt,
            Status = status,
            PlanJson = plan is null ? null : JsonDocument.Parse(plan),
            WarningsJson = warnings is null ? null : JsonDocument.Parse(warnings),
            ResultJson = result is null ? null : JsonDocument.Parse(result),
            CreatedAt = now,
            UpdatedAt = now,
            PreparedAt = status == InstanceVariableUpdateBatchItemStatuses.Preparing
                ? null
                : now,
            StartedAt = startedAt,
            CompletedAt = status is InstanceVariableUpdateBatchItemStatuses.Succeeded
                or InstanceVariableUpdateBatchItemStatuses.Skipped
                or InstanceVariableUpdateBatchItemStatuses.Failed
                or InstanceVariableUpdateBatchItemStatuses.Cancelled
                ? now
                : null
        };

    private async Task DeleteWorkflowAsync(string workflowKey)
    {
        await using var cleanup = fixture.CreateDbContext();
        var batchIds = await cleanup.InstanceVariableUpdateBatches
            .Where(batch => batch.WorkflowKey == workflowKey)
            .Select(batch => batch.Id)
            .ToArrayAsync();
        if (batchIds.Length > 0)
        {
            await cleanup.InstanceVariableUpdateBatchJobLinks
                .Where(link => batchIds.Contains(link.BatchId))
                .ExecuteDeleteAsync();
            await cleanup.InstanceVariableUpdateBatchItems
                .Where(item => batchIds.Contains(item.BatchId))
                .ExecuteDeleteAsync();
            await cleanup.InstanceVariableUpdateBatches
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
