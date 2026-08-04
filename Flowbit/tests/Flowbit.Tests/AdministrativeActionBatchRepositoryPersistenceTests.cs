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
    public async Task RepositoryRoundTripsFrozenMappingsAndExactItemFlowIdentity()
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

        var mapping = new AdministrativeActionFlowMappingRecord(
            definitionId,
            4,
            44,
            null,
            "Return for correction",
            20,
            "Approval",
            10,
            "Correction",
            ["ADMIN", "workflow-operator"],
            [
                new VariableModel
                {
                    Id = 1,
                    Name = "comment",
                    DataType = WorkflowVariableTypes.String,
                    Required = true
                }
            ]);
        long batchId;

        await using (var createContext = fixture.CreateDbContext())
        {
            var repository = new AdministrativeActionBatchRepository(createContext);
            using var selection = JsonDocument.Parse("{\"mode\":\"explicit\"}");
            var batch = await repository.AddAsync(
                new NewAdministrativeActionBatchRecord(
                    workflowKey,
                    [mapping],
                    "Correct an operational mistake",
                    new Dictionary<string, JsonElement>(),
                    selection.RootElement.Clone(),
                    "batch-admin",
                    ["admin"],
                    $"idempotency-{suffix}",
                    now),
                CancellationToken.None);
            batchId = batch.Id;

            var persistedMapping = Assert.Single(batch.FlowMappings);
            Assert.Equal(definitionId, persistedMapping.WorkflowDefinitionId);
            Assert.Equal(44, persistedMapping.FlowId);
            Assert.Null(persistedMapping.FlowExternalId);
            Assert.Equal("comment", Assert.Single(persistedMapping.Variables).Name);

            var items = await repository.AddItemsAsync(
                batch.Id,
                [
                    new NewAdministrativeActionBatchItemRecord(
                        instanceId,
                        taskId,
                        tokenId,
                        definitionId,
                        44,
                        now,
                        now,
                        now)
                ],
                CancellationToken.None);
            var item = Assert.Single(items);
            Assert.Equal(definitionId, item.WorkflowDefinitionId);
            Assert.Equal(44, item.FlowId);
        }

        await using (var readContext = fixture.CreateDbContext())
        {
            var repository = new AdministrativeActionBatchRepository(readContext);
            var batch = await repository.GetAsync(
                batchId,
                forUpdate: false,
                CancellationToken.None);
            Assert.NotNull(batch);
            var persistedMapping = Assert.Single(batch.FlowMappings);
            Assert.Equal(["ADMIN", "workflow-operator"], persistedMapping.Roles);

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
