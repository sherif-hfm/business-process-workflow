using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class InstanceVersionChangeBatchPersistenceTests(PostgresApiFixture fixture)
{
    [Fact]
    public async Task WorkflowDefinitionDeletionRetainsEveryFrozenBatchDefinitionReference()
    {
        var workflowKey = $"version-batch-retention-{Guid.NewGuid():N}";
        long sourceId = 0;
        long targetId = 0;
        long capturedId = 0;
        try
        {
            await using (var setup = fixture.CreateDbContext())
            {
                var anchor = NewDefinition(workflowKey, 1, "Runtime anchor");
                var source = NewDefinition(workflowKey, 2, "Frozen source");
                var target = NewDefinition(workflowKey, 3, "Frozen target");
                var captured = NewDefinition(workflowKey, 4, "Captured item source");
                setup.WorkflowDefinitions.AddRange(anchor, source, target, captured);
                await setup.SaveChangesAsync();

                var now = DateTimeOffset.UtcNow;
                var instance = new WorkflowInstanceEntity
                {
                    WorkflowDefinitionId = anchor.Id,
                    WorkflowKey = workflowKey,
                    Status = "running",
                    StartedBy = "batch-retention-test",
                    CreatedAt = now,
                    UpdatedAt = now
                };
                setup.WorkflowInstances.Add(instance);
                await setup.SaveChangesAsync();

                var batch = new WorkflowInstanceVersionChangeBatchEntity
                {
                    WorkflowKey = workflowKey,
                    SourceWorkflowDefinitionId = source.Id,
                    TargetWorkflowDefinitionId = target.Id,
                    Reason = "Retain all immutable definition references.",
                    SelectionJson = JsonDocument.Parse("""{"mode":"explicit","instanceIds":[]}"""),
                    Status = InstanceVersionChangeBatchStatuses.Ready,
                    PreparedBy = "batch-retention-test",
                    PreparedByRolesJson = JsonDocument.Parse("""["admin"]"""),
                    TotalItemCount = 1,
                    EligibleItemCount = 1,
                    CreatedAt = now,
                    UpdatedAt = now,
                    PreparedAt = now
                };
                setup.WorkflowInstanceVersionChangeBatches.Add(batch);
                await setup.SaveChangesAsync();

                setup.WorkflowInstanceVersionChangeBatchItems.Add(
                    new WorkflowInstanceVersionChangeBatchItemEntity
                    {
                        BatchId = batch.Id,
                        InstanceId = instance.Id,
                        CapturedSourceWorkflowDefinitionId = captured.Id,
                        CapturedInstanceUpdatedAt = instance.UpdatedAt,
                        Status = InstanceVersionChangeBatchItemStatuses.Eligible,
                        CreatedAt = now,
                        UpdatedAt = now,
                        PreparedAt = now
                    });
                await setup.SaveChangesAsync();

                sourceId = source.Id;
                targetId = target.Id;
                capturedId = captured.Id;
            }

            foreach (var definitionId in new[] { sourceId, targetId, capturedId })
            {
                await using var context = fixture.CreateDbContext();
                using var cache = new MemoryCache(new MemoryCacheOptions());
                var repository = new WorkflowDefinitionRepository(context, cache);

                var exception = await Assert.ThrowsAsync<WorkflowConflictException>(
                    () => repository.DeleteAsync(definitionId, CancellationToken.None));

                Assert.Contains(
                    "runtime or version-change history",
                    exception.Message,
                    StringComparison.Ordinal);
                Assert.True(await context.WorkflowDefinitions.AnyAsync(
                    definition => definition.Id == definitionId));
            }
        }
        finally
        {
            await using var cleanup = fixture.CreateDbContext();
            var batchIds = await cleanup.WorkflowInstanceVersionChangeBatches
                .Where(batch => batch.WorkflowKey == workflowKey)
                .Select(batch => batch.Id)
                .ToArrayAsync();
            if (batchIds.Length > 0)
            {
                await cleanup.WorkflowInstanceVersionChangeBatchItems
                    .Where(item => batchIds.Contains(item.BatchId))
                    .ExecuteDeleteAsync();
                await cleanup.WorkflowInstanceVersionChangeBatches
                    .Where(batch => batchIds.Contains(batch.Id))
                    .ExecuteDeleteAsync();
            }

            await cleanup.WorkflowInstances
                .Where(instance => instance.WorkflowKey == workflowKey)
                .ExecuteDeleteAsync();
            await cleanup.WorkflowDefinitions
                .Where(definition => definition.WorkflowKey == workflowKey)
                .ExecuteDeleteAsync();
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
}
