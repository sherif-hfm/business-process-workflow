using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class MultiInstanceBulkPersistenceTests(PostgresApiFixture fixture)
{
    [Fact]
    public async Task ThousandItemFanOutAndCancellationUseBulkPersistenceAndPreserveSnapshots()
    {
        const int itemCount = 1_000;
        var workflowKey = $"bulk-mi-{Guid.NewGuid():N}";
        long instanceId;
        long tokenId;
        long executionId;

        await using (var setup = fixture.CreateDbContext())
        {
            var definition = new WorkflowDefinitionEntity
            {
                Name = workflowKey,
                WorkflowKey = workflowKey,
                Version = 1,
                IsPublished = true,
                Definition = new WorkflowModel
                {
                    Id = workflowKey,
                    Name = workflowKey
                }
            };
            setup.WorkflowDefinitions.Add(definition);
            await setup.SaveChangesAsync();

            var instance = new WorkflowInstanceEntity
            {
                WorkflowDefinitionId = definition.Id,
                WorkflowKey = workflowKey,
                Status = WorkflowInstanceStatuses.Running,
                StartedBy = "starter"
            };
            setup.WorkflowInstances.Add(instance);
            await setup.SaveChangesAsync();
            instanceId = instance.Id;

            var token = new ExecutionTokenEntity
            {
                InstanceId = instance.Id,
                NodeId = 20,
                NodeName = "Bulk reviewers",
                NodeExternalId = "bulk-reviewers",
                NodeType = BpmnFlowNodeTypes.UserTask,
                Status = ExecutionTokenStatuses.Active,
                ArrivedViaFlowId = 101
            };
            setup.ExecutionTokens.Add(token);
            await setup.SaveChangesAsync();
            tokenId = token.Id;

            var repository = new WorkflowRuntimeRepository(setup);
            await using var transaction =
                await setup.Database.BeginTransactionAsync();
            var execution = await repository.AddMultiInstanceAsync(
                instance.Id,
                token.Id,
                new CurrentNodeSnapshot(
                    20,
                    "Bulk reviewers",
                    "bulk-reviewers",
                    BpmnFlowNodeTypes.UserTask,
                    ["reviewer", "approver"],
                    RequiresClaim: true,
                    RequiresAssignment: false,
                    Assignee: null,
                    IsMultiInstance: true),
                new MultiInstanceModel
                {
                    Mode = MultiInstanceModes.Parallel,
                    Source = MultiInstanceSources.Cardinality,
                    CardinalityExpression = itemCount.ToString(),
                    OnePerActor = true,
                    ResultVariable = "reviewResults"
                },
                Enumerable.Repeat<JsonElement?>(null, itemCount).ToArray(),
                [201, 202],
                new NodeExecutionActorRecord("starter", ["requester"])
                {
                    ActingFor = "request-owner",
                    DelegationId = 41
                },
                CancellationToken.None);
            executionId = execution.Id;

            // Bulk fan-out is written directly in three bounded statements.
            // It must not materialize or track 2,000 child entities.
            Assert.Empty(setup.UserTasks.Local);
            Assert.Empty(setup.NodeExecutions.Local);
            Assert.Single(setup.MultiInstanceExecutions.Local);

            await setup.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        await using (var verifyFanOut = fixture.CreateDbContext())
        {
            var persistedExecution = await verifyFanOut.MultiInstanceExecutions
                .AsNoTracking()
                .SingleAsync(execution => execution.Id == executionId);
            Assert.Equal(instanceId, persistedExecution.InstanceId);
            Assert.Equal(tokenId, persistedExecution.TokenId);
            Assert.Equal(itemCount, persistedExecution.TotalCount);
            Assert.True(persistedExecution.OnePerActor);
            Assert.Equal(MultiInstanceExecutionStatuses.Active, persistedExecution.Status);

            var tasks = await verifyFanOut.UserTasks.AsNoTracking()
                .Where(task => task.MultiInstanceExecutionId == executionId)
                .OrderBy(task => task.ItemIndex)
                .ToListAsync();
            Assert.Equal(itemCount, tasks.Count);
            Assert.Equal(Enumerable.Range(0, itemCount), tasks.Select(task => task.ItemIndex!.Value));
            Assert.All(tasks, task =>
            {
                Assert.Equal(UserTaskStatuses.Active, task.Status);
                Assert.Equal(instanceId, task.InstanceId);
                Assert.Equal(tokenId, task.TokenId);
                Assert.Equal(["reviewer", "approver"], task.Roles);
                Assert.True(task.RequiresClaim);
                Assert.Null(task.ItemValueJson);
                Assert.Null(task.Assignee);
            });

            var executions = await verifyFanOut.NodeExecutions.AsNoTracking()
                .Where(nodeExecution =>
                    nodeExecution.MultiInstanceExecutionId == executionId)
                .OrderBy(nodeExecution => nodeExecution.ItemIndex)
                .ToListAsync();
            Assert.Equal(itemCount, executions.Count);
            Assert.Equal(
                tasks.Select(task => task.Id),
                executions.Select(nodeExecution => nodeExecution.UserTaskId!.Value));
            Assert.All(executions, nodeExecution =>
            {
                Assert.Equal(NodeExecutionKinds.UserTaskItem, nodeExecution.ExecutionKind);
                Assert.Equal(NodeExecutionStatuses.Active, nodeExecution.Status);
                Assert.Equal(101, nodeExecution.EnteredViaFlowId);
                Assert.Equal("starter", nodeExecution.TriggeredBy);
                Assert.Equal("request-owner", nodeExecution.TriggeredActingFor);
                Assert.Equal(41, nodeExecution.TriggeredDelegationId);
                Assert.Equal(
                    ["reviewer", "approver"],
                    JsonSerializer.Deserialize<string[]>(
                        nodeExecution.NodeRolesJson!.RootElement.GetRawText())
                    ?? []);
                Assert.Equal(
                    ["requester"],
                    JsonSerializer.Deserialize<string[]>(
                        nodeExecution.TriggeredByRolesJson!.RootElement.GetRawText())
                    ?? []);
            });
        }

        await using (var cancel = fixture.CreateDbContext())
        {
            _ = await cancel.MultiInstanceExecutions.SingleAsync(
                execution => execution.Id == executionId);
            var repository = new WorkflowRuntimeRepository(cancel);
            await repository.CloseMultiInstanceAsync(
                executionId,
                202,
                "interrupt",
                new NodeExecutionActorRecord("supervisor", ["admin"])
                {
                    ActingFor = "operations",
                    DelegationId = 84
                },
                CancellationToken.None);
        }

        await using (var verifyCancellation = fixture.CreateDbContext())
        {
            var persistedExecution = await verifyCancellation.MultiInstanceExecutions
                .AsNoTracking()
                .SingleAsync(execution => execution.Id == executionId);
            Assert.Equal(MultiInstanceExecutionStatuses.Interrupted, persistedExecution.Status);
            Assert.Equal(itemCount, persistedExecution.CancelledCount);
            Assert.Equal(202, persistedExecution.WinningFlowId);
            Assert.Equal("interrupt", persistedExecution.CompletionReason);
            Assert.NotNull(persistedExecution.CompletedAt);

            Assert.Equal(
                itemCount,
                await verifyCancellation.UserTasks.AsNoTracking().CountAsync(task =>
                    task.MultiInstanceExecutionId == executionId
                    && task.Status == UserTaskStatuses.Cancelled
                    && task.SelectedFlowId == null
                    && task.ResultJson == null
                    && task.CompletedBy == null
                    && task.CompletedAt != null));

            Assert.Equal(
                itemCount,
                await verifyCancellation.NodeExecutions.AsNoTracking().CountAsync(
                    nodeExecution =>
                        nodeExecution.MultiInstanceExecutionId == executionId
                        && nodeExecution.Status == NodeExecutionStatuses.Cancelled
                        && nodeExecution.CompletionReason
                        == NodeExecutionCompletionReasons.MultiInstanceInterrupt
                        && nodeExecution.CompletedBy == "supervisor"
                        && nodeExecution.CompletedActingFor == "operations"
                        && nodeExecution.CompletedDelegationId == 84
                        && nodeExecution.CompletedAt != null));
        }
    }
}
