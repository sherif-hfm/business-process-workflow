using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class AdministrativeActionBatchRepositoryPersistenceTests(
    PostgresApiFixture fixture)
{
    [Fact]
    public async Task RepositoryRoundTripsFrozenActionAndExactPositionIdentity()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var workflowKey = $"batch-record-mappings-{suffix}";
        var now = DateTimeOffset.UtcNow;
        long definitionId;
        long instanceId;
        long taskId;
        long tokenId;

        await using (var setup = fixture.CreateDbContext())
        {
            var definition = new WorkflowDefinitionEntity
            {
                Name = workflowKey,
                WorkflowKey = workflowKey,
                Version = 4,
                IsPublished = true,
                Definition = new WorkflowModel { Id = workflowKey, Name = workflowKey },
                CreatedAt = now
            };
            setup.WorkflowDefinitions.Add(definition);
            await setup.SaveChangesAsync();
            definitionId = definition.Id;

            var instance = new WorkflowInstanceEntity
            {
                WorkflowDefinitionId = definition.Id,
                WorkflowKey = workflowKey,
                Status = "running",
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
        }

        var action = new AdministrativeActionSnapshotRecord(
            definitionId,
            4,
            "directFlow",
            44,
            null,
            "Return for correction",
            20,
            "Approval",
            10,
            "Correction",
            BpmnFlowNodeTypes.UserTask,
            "false",
            ["ADMIN", "workflow-operator"],
            [
                new VariableModel
                {
                    Id = 1,
                    Name = "comment",
                    DataType = WorkflowVariableTypes.String,
                    Required = true
                }
            ],
            null,
            null,
            null,
            null);
        long batchId;

        await using (var createContext = fixture.CreateDbContext())
        {
            var repository = new AdministrativeActionBatchRepository(createContext);
            using var selection = JsonDocument.Parse("{\"mode\":\"explicit\"}");
            var batch = await repository.AddAsync(
                new NewAdministrativeActionBatchRecord(
                    workflowKey,
                    definitionId,
                    20,
                    "directFlow",
                    44,
                    null,
                    null,
                    action,
                    null,
                    new Dictionary<string, JsonElement>(),
                    selection.RootElement.Clone(),
                    "batch-admin",
                    ["admin"],
                    $"idempotency-{suffix}",
                    now),
                CancellationToken.None);
            batchId = batch.Id;

            Assert.Equal(definitionId, batch.WorkflowDefinitionId);
            Assert.Equal(44, batch.Action.FlowId);
            Assert.Null(batch.Action.FlowExternalId);
            Assert.Equal("comment", Assert.Single(batch.Action.Variables).Name);
            Assert.Null(batch.Reason);

            var items = await repository.AddItemsAsync(
                batch.Id,
                [
                    new NewAdministrativeActionBatchItemRecord(
                        "userTask",
                        taskId,
                        instanceId,
                        taskId,
                        null,
                        tokenId,
                        (await createContext.ExecutionTokens.FindAsync(tokenId))!.ActivationId,
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
                        now)
                ],
                CancellationToken.None);
            var item = Assert.Single(items);
            Assert.Equal(definitionId, item.WorkflowDefinitionId);
            Assert.Equal(44, item.FlowId);
            Assert.Equal("userTask", item.PositionKind);
            Assert.Equal(taskId, item.PositionId);
            Assert.Equal(1, item.AffectedTaskCount);
            Assert.Equal(
                1,
                await repository.SumAffectedTaskCountAsync(
                    batch.Id,
                    null,
                    CancellationToken.None));
        }

        await using (var readContext = fixture.CreateDbContext())
        {
            var repository = new AdministrativeActionBatchRepository(readContext);
            var batch = await repository.GetAsync(
                batchId,
                forUpdate: false,
                CancellationToken.None);
            Assert.NotNull(batch);
            Assert.Equal(["ADMIN", "workflow-operator"], batch.Action.Roles);

            var items = await repository.ListItemsAsync(
                batchId,
                null,
                1,
                20,
                CancellationToken.None);
            var item = Assert.Single(items.Items);
            Assert.Equal(definitionId, item.WorkflowDefinitionId);
            Assert.Equal(44, item.FlowId);
        }
    }
}
