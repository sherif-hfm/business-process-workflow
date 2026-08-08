using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class AdministrativeActionBatchRuntimeIdentityTests(
    PostgresApiFixture fixture)
{
    [Fact]
    public async Task AtomicItemCompletionRejectsAnyFlowOrDefinitionOtherThanTheFrozenMapping()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var workflowKey = $"batch-runtime-identity-{suffix}";
        var now = DateTimeOffset.UtcNow;
        long definitionId;
        long batchId;
        long instanceId;
        long taskId;
        long tokenId;
        long batchItemId;
        var activationId = Guid.NewGuid();

        await using (var setup = fixture.CreateDbContext())
        {
            var definition = new WorkflowDefinitionEntity
            {
                Name = workflowKey,
                WorkflowKey = workflowKey,
                Version = 1,
                IsPublished = true,
                Definition = new WorkflowModel { Id = workflowKey, Name = workflowKey },
                CreatedAt = now
            };
            setup.WorkflowDefinitions.Add(definition);
            await setup.SaveChangesAsync();
            definitionId = definition.Id;

            var instance = new WorkflowInstanceEntity
            {
                WorkflowDefinitionId = definitionId,
                WorkflowKey = workflowKey,
                Status = WorkflowInstanceStatuses.Running,
                CreatedAt = now,
                UpdatedAt = now
            };
            setup.WorkflowInstances.Add(instance);
            await setup.SaveChangesAsync();
            instanceId = instance.Id;

            var token = new ExecutionTokenEntity
            {
                InstanceId = instance.Id,
                NodeId = 20,
                NodeName = "Approval",
                NodeType = BpmnFlowNodeTypes.UserTask,
                Status = ExecutionTokenStatuses.Active,
                ActivationId = activationId,
                CreatedAt = now,
                UpdatedAt = now
            };
            setup.ExecutionTokens.Add(token);
            await setup.SaveChangesAsync();
            tokenId = token.Id;

            var task = new UserTaskEntity
            {
                InstanceId = instance.Id,
                TokenId = token.Id,
                NodeId = token.NodeId,
                NodeName = token.NodeName,
                Roles = ["reviewer"],
                Status = UserTaskStatuses.Active,
                CreatedAt = now,
                UpdatedAt = now
            };
            setup.UserTasks.Add(task);
            await setup.SaveChangesAsync();
            taskId = task.Id;

            var batches = new AdministrativeActionBatchRepository(setup);
            using var selection = JsonDocument.Parse("{\"mode\":\"explicit\"}");
            var batch = await batches.AddAsync(
                new NewAdministrativeActionBatchRecord(
                    workflowKey,
                    definitionId,
                    20,
                    AdministrativeActionKinds.DirectFlow,
                    44,
                    null,
                    null,
                    new AdministrativeActionSnapshotRecord(
                        definitionId,
                        1,
                        AdministrativeActionKinds.DirectFlow,
                        44,
                        null,
                        "Return",
                        20,
                        "Approval",
                        10,
                        "Correction",
                        BpmnFlowNodeTypes.UserTask,
                        null,
                        ["admin"],
                        [],
                        null,
                        null,
                        null,
                        null),
                    "Correct the item",
                    new Dictionary<string, JsonElement>(),
                    selection.RootElement.Clone(),
                    "operator",
                    ["admin"],
                    null,
                    now),
                CancellationToken.None);
            batchId = batch.Id;
            var items = await batches.AddItemsAsync(
                batchId,
                [new NewAdministrativeActionBatchItemRecord(
                    AdministrativeActionPositionKinds.UserTask,
                    task.Id,
                    instance.Id,
                    task.Id,
                    null,
                    token.Id,
                    activationId,
                    definitionId,
                    20,
                    44,
                    now,
                    null,
                    null,
                    null,
                    null,
                    null,
                    1,
                    now)],
                CancellationToken.None);
            batchItemId = Assert.Single(items).Id;
            Assert.Equal(
                1,
                await batches.TransitionItemsAsync(
                    batchId,
                    [AdministrativeActionBatchItemStatuses.Preparing],
                    AdministrativeActionBatchItemStatuses.Queued,
                    now,
                    CancellationToken.None));
        }

        await AssertRejectedAsync(instanceId + 1, taskId, tokenId, activationId, definitionId, 20, 44);
        await AssertRejectedAsync(instanceId, taskId, tokenId + 1, activationId, definitionId, 20, 44);
        await AssertRejectedAsync(instanceId, taskId, tokenId, Guid.NewGuid(), definitionId, 20, 44);
        await AssertRejectedAsync(instanceId, taskId, tokenId, activationId, definitionId + 1, 20, 44);
        await AssertRejectedAsync(instanceId, taskId, tokenId, activationId, definitionId, 20, 45);

        await using (var complete = fixture.CreateDbContext())
        {
            var runtime = new WorkflowRuntimeRepository(complete);
            await runtime.CompleteAdministrativeActionBatchItemAsync(
                batchItemId,
                batchId,
                instanceId,
                AdministrativeActionPositionKinds.UserTask,
                taskId,
                tokenId,
                activationId,
                definitionId,
                20,
                44,
                1,
                JsonSerializer.SerializeToElement(new { flowId = 44 }),
                now.AddSeconds(1),
                CancellationToken.None);
            await complete.SaveChangesAsync();
        }

        await using var verify = fixture.CreateDbContext();
        var persisted = await verify.AdministrativeActionBatchItems
            .AsNoTracking()
            .SingleAsync(item => item.BatchId == batchId);
        Assert.Equal(AdministrativeActionBatchItemStatuses.Succeeded, persisted.Status);
        Assert.Equal(44, persisted.FlowId);
        Assert.Equal(definitionId, persisted.WorkflowDefinitionId);

        async Task AssertRejectedAsync(
            long suppliedInstanceId,
            long suppliedTaskId,
            long suppliedTokenId,
            Guid suppliedActivationId,
            long suppliedDefinitionId,
            int suppliedSourceNodeId,
            int suppliedFlowId)
        {
            await using var attempt = fixture.CreateDbContext();
            var runtime = new WorkflowRuntimeRepository(attempt);
            var exception = await Assert.ThrowsAsync<WorkflowConflictException>(() =>
                runtime.CompleteAdministrativeActionBatchItemAsync(
                    batchItemId,
                    batchId,
                    suppliedInstanceId,
                    AdministrativeActionPositionKinds.UserTask,
                    suppliedTaskId,
                    suppliedTokenId,
                    suppliedActivationId,
                    suppliedDefinitionId,
                    suppliedSourceNodeId,
                    suppliedFlowId,
                    1,
                    null,
                    now,
                    CancellationToken.None));
            Assert.Contains(
                "frozen position, token activation, workflow, flow, and affected-task count",
                exception.Message);

            var unchanged = await attempt.AdministrativeActionBatchItems
                .AsNoTracking()
                .SingleAsync(item => item.BatchId == batchId);
            Assert.Equal(AdministrativeActionBatchItemStatuses.Queued, unchanged.Status);
        }
    }
}
