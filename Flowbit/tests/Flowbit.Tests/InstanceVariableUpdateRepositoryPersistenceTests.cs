using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class InstanceVariableUpdateRepositoryPersistenceTests(
    PostgresApiFixture fixture)
{
    [Fact]
    public async Task RepositoriesRoundTripCrossVersionBatchAuditItemsAndCleanupSafeJobLinks()
    {
        await using var db = fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var workflowKey = $"variable-update-persistence-{Guid.NewGuid():N}";
        var versionOne = NewDefinition(workflowKey, 1);
        var versionTwo = NewDefinition(workflowKey, 2);
        db.WorkflowDefinitions.AddRange(versionOne, versionTwo);
        await db.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        var first = NewInstance(versionOne, now);
        var second = NewInstance(versionTwo, now.AddSeconds(1));
        db.WorkflowInstances.AddRange(first, second);
        await db.SaveChangesAsync();

        var firstJob = NewJob(versionOne, workflowKey, now, 1);
        var secondJob = NewJob(versionTwo, workflowKey, now, 2);
        db.WorkflowJobs.AddRange(firstJob, secondJob);
        await db.SaveChangesAsync();

        var variables = JsonSerializer.SerializeToElement(new object[]
        {
            new { name = "priority", value = 7 },
            new { name = "metadata", value = new { source = "admin" } }
        });
        var selection = JsonSerializer.SerializeToElement(new
        {
            mode = "explicit",
            instanceIds = new[] { first.Id, second.Id }
        });
        var batchRepository = new InstanceVariableUpdateBatchRepository(db);
        var batch = await batchRepository.AddAsync(
            new NewInstanceVariableUpdateBatchRecord(
                workflowKey,
                variables,
                selection,
                "Correct imported values.",
                "batch-admin",
                ["admin", "workflow-operator"],
                "batch-idempotency",
                now),
            CancellationToken.None);
        await batchRepository.AddItemsAsync(
            batch.Id,
            [
                new NewInstanceVariableUpdateBatchItemRecord(
                    first.Id,
                    versionOne.Id,
                    first.UpdatedAt,
                    now),
                new NewInstanceVariableUpdateBatchItemRecord(
                    second.Id,
                    versionTwo.Id,
                    second.UpdatedAt,
                    now)
            ],
            CancellationToken.None);

        var firstItem = Assert.Single((await batchRepository.ListItemsForProcessingAsync(
            batch.Id,
            versionOne.Id,
            [InstanceVariableUpdateBatchItemStatuses.Preparing],
            afterItemId: null,
            take: 10,
            CancellationToken.None)));
        var secondItem = Assert.Single((await batchRepository.ListItemsForProcessingAsync(
            batch.Id,
            versionTwo.Id,
            [InstanceVariableUpdateBatchItemStatuses.Preparing],
            afterItemId: null,
            take: 10,
            CancellationToken.None)));
        Assert.Equal(first.Id, firstItem.InstanceId);
        Assert.Equal(second.Id, secondItem.InstanceId);

        await batchRepository.AddJobLinkAsync(
            new NewInstanceVariableUpdateBatchJobLinkRecord(
                batch.Id,
                versionOne.Id,
                InstanceVariableUpdateBatchPhases.Prepare,
                firstJob.Id,
                firstJob.Id),
            CancellationToken.None);
        await batchRepository.AddJobLinkAsync(
            new NewInstanceVariableUpdateBatchJobLinkRecord(
                batch.Id,
                versionTwo.Id,
                InstanceVariableUpdateBatchPhases.Prepare,
                secondJob.Id,
                secondJob.Id),
            CancellationToken.None);

        var plan = JsonSerializer.SerializeToElement(new[]
        {
            new { name = "priority", outcome = InstanceVariableUpdateOutcomes.Added },
            new { name = "metadata", outcome = InstanceVariableUpdateOutcomes.Updated }
        });
        var warnings = JsonSerializer.SerializeToElement(new[]
        {
            new { code = "active_jobs", message = "One active job may use older values." }
        });
        firstItem = await batchRepository.UpdateItemAsync(
            new InstanceVariableUpdateBatchItemUpdateRecord(
                firstItem.Id,
                InstanceVariableUpdateBatchItemStatuses.Eligible,
                plan,
                warnings,
                Result: null,
                UpdateOperationId: null,
                ErrorCode: null,
                ErrorDescription: null,
                UpdatedAt: now.AddMinutes(1),
                PreparedAt: now.AddMinutes(1),
                StartedAt: null,
                CompletedAt: null),
            CancellationToken.None);
        Assert.Equal(
            1,
            await batchRepository.CountItemsWithWarningsAsync(
                batch.Id,
                CancellationToken.None));

        var updateRepository = new InstanceVariableUpdateRepository(db);
        var requested = JsonSerializer.SerializeToElement(new[]
        {
            new { name = "priority", value = 7 }
        });
        var audit = await updateRepository.AddAsync(
            new NewInstanceVariableUpdateAuditRecord(
                first.Id,
                versionOne.Id,
                "batch-admin",
                ["admin", "workflow-operator"],
                "Correct imported values.",
                requested,
                "operation-idempotency",
                batch.Id,
                firstItem.Id,
                now.AddMinutes(2)),
            CancellationToken.None);
        db.InstanceVariables.Add(new InstanceVariableEntity
        {
            InstanceId = first.Id,
            InstanceVariableUpdateAuditId = audit.Id,
            VariableName = "priority",
            ValueJson = JsonDocument.Parse("7"),
            SetBy = "batch-admin",
            SetAt = now.AddMinutes(2)
        });
        await db.SaveChangesAsync();

        var persistedVariable = Assert.Single(await updateRepository.ListVariablesAsync(
            audit.Id,
            CancellationToken.None));
        Assert.Equal("priority", persistedVariable.Name);
        Assert.Equal(7, persistedVariable.Value.GetInt32());

        var result = JsonSerializer.SerializeToElement(new[]
        {
            new
            {
                name = "priority",
                outcome = InstanceVariableUpdateOutcomes.Added,
                variableId = persistedVariable.Id,
                value = 7
            }
        });
        audit = await updateRepository.SetResultAsync(
            audit.Id,
            result,
            CancellationToken.None);
        Assert.True(JsonElement.DeepEquals(result, audit.Result));
        Assert.Equal(
            audit.Id,
            (await updateRepository.FindByIdempotencyKeyAsync(
                first.Id,
                "batch-admin",
                "operation-idempotency",
                CancellationToken.None))?.Id);
        Assert.Equal(
            audit.Id,
            Assert.Single(await updateRepository.ListByInstanceAsync(
                first.Id,
                CancellationToken.None)).Id);

        firstItem = await batchRepository.UpdateItemAsync(
            new InstanceVariableUpdateBatchItemUpdateRecord(
                firstItem.Id,
                InstanceVariableUpdateBatchItemStatuses.Succeeded,
                firstItem.Plan,
                firstItem.Warnings,
                JsonSerializer.SerializeToElement(new { operationId = audit.Id }),
                audit.Id,
                ErrorCode: null,
                ErrorDescription: null,
                UpdatedAt: now.AddMinutes(2),
                PreparedAt: firstItem.PreparedAt,
                StartedAt: now.AddMinutes(1),
                CompletedAt: now.AddMinutes(2)),
            CancellationToken.None);
        Assert.Equal(audit.Id, firstItem.UpdateOperationId);

        var replayedBatch = await batchRepository.FindByIdempotencyKeyAsync(
            "batch-admin",
            "batch-idempotency",
            CancellationToken.None);
        Assert.NotNull(replayedBatch);
        Assert.True(JsonElement.DeepEquals(variables, replayedBatch.Variables));
        Assert.Equal(
            ["admin", "workflow-operator"],
            replayedBatch.PreparedByRoles);

        // Cleanup may delete a terminal durable job. The immutable identity must
        // remain while the live FK is cleared by ON DELETE SET NULL.
        db.ChangeTracker.Clear();
        Assert.Equal(
            1,
            await db.WorkflowJobs
                .Where(job => job.Id == firstJob.Id)
                .ExecuteDeleteAsync());
        var links = await new InstanceVariableUpdateBatchRepository(db)
            .ListJobLinksAsync(batch.Id, CancellationToken.None);
        Assert.Equal(2, links.Count);
        var detached = Assert.Single(links, link => link.OriginalJobId == firstJob.Id);
        Assert.Null(detached.JobId);
        var live = Assert.Single(links, link => link.OriginalJobId == secondJob.Id);
        Assert.Equal(secondJob.Id, live.JobId);

        await transaction.RollbackAsync();
    }

    private static WorkflowDefinitionEntity NewDefinition(
        string workflowKey,
        int version) =>
        new()
        {
            Name = $"Variable update persistence v{version}",
            WorkflowKey = workflowKey,
            Version = version,
            Definition = new WorkflowModel
            {
                Id = workflowKey,
                Name = $"Variable update persistence v{version}"
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

    private static WorkflowJobEntity NewJob(
        WorkflowDefinitionEntity definition,
        string workflowKey,
        DateTimeOffset now,
        int index) =>
        new()
        {
            WorkflowDefinitionId = definition.Id,
            WorkflowKey = workflowKey,
            ActivationId = Guid.NewGuid(),
            NodeId = 0,
            NodeName = "Instance variable-update batch",
            NodeType = "instanceVariableUpdateBatch",
            Kind = WorkflowJobKinds.InstanceVariableUpdateBatchPrepare,
            QueueClass = WorkflowJobClasses.Control,
            Phase = InstanceVariableUpdateBatchPhases.Prepare,
            Status = WorkflowJobStatuses.Completed,
            Priority = index,
            MaxAttempts = 4,
            FailureHandling = WorkflowJobFailureHandling.RetryFirst,
            RetryDelays = [],
            DueAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = now
        };
}
