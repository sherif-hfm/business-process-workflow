using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class ParallelGatewayPersistenceTests(PostgresApiFixture fixture)
{
    [Fact]
    public async Task InstanceListNodeFiltersMatchEveryVisibleTokenAndExcludeMergedTokens()
    {
        var workflowKey = $"parallel-position-filter-{Guid.NewGuid():N}";
        long instanceId;

        await using (var setup = fixture.CreateDbContext())
        {
            var definition = new WorkflowDefinitionEntity
            {
                Name = workflowKey,
                WorkflowKey = workflowKey,
                Version = 1,
                IsPublished = true,
                Definition = new WorkflowModel { Id = workflowKey, Name = workflowKey }
            };
            setup.WorkflowDefinitions.Add(definition);
            await setup.SaveChangesAsync();

            var instance = new WorkflowInstanceEntity
            {
                WorkflowDefinitionId = definition.Id,
                WorkflowKey = workflowKey,
                Status = WorkflowInstanceStatuses.Running
            };
            setup.WorkflowInstances.Add(instance);
            await setup.SaveChangesAsync();
            instanceId = instance.Id;

            setup.ExecutionTokens.AddRange(
                new ExecutionTokenEntity
                {
                    InstanceId = instance.Id,
                    NodeId = 10,
                    NodeName = "Manager review",
                    NodeExternalId = "manager-review",
                    NodeType = BpmnFlowNodeTypes.UserTask,
                    Status = ExecutionTokenStatuses.Active
                },
                new ExecutionTokenEntity
                {
                    InstanceId = instance.Id,
                    NodeId = 20,
                    NodeName = "Finance review",
                    NodeExternalId = "finance-review",
                    NodeType = BpmnFlowNodeTypes.UserTask,
                    Status = ExecutionTokenStatuses.Active
                },
                new ExecutionTokenEntity
                {
                    InstanceId = instance.Id,
                    NodeId = 30,
                    NodeName = "Merged join position",
                    NodeExternalId = "merged-position",
                    NodeType = BpmnFlowNodeTypes.ParallelGateway,
                    Status = ExecutionTokenStatuses.Merged
                });
            await setup.SaveChangesAsync();
        }

        await using var db = fixture.CreateDbContext();
        var repository = new WorkflowRuntimeRepository(db);

        var byManager = await repository.ListInstancesAsync(
            null, null, null, workflowKey, null, 10, null, [], [], false, 1, 20, CancellationToken.None);
        var byFinanceExternalId = await repository.ListInstancesAsync(
            null, null, null, workflowKey, null, null, "FINANCE-REVIEW", [], [], false, 1, 20,
            CancellationToken.None);
        var byMerged = await repository.ListInstancesAsync(
            null, null, null, workflowKey, null, 30, null, [], [], false, 1, 20, CancellationToken.None);
        var unfiltered = await repository.ListInstancesAsync(
            null, null, null, workflowKey, null, null, null, [], [], false, 1, 20, CancellationToken.None);

        Assert.Equal(instanceId, Assert.Single(byManager.Items).Id);
        Assert.Equal(instanceId, Assert.Single(byFinanceExternalId.Items).Id);
        Assert.Empty(byMerged.Items);
        Assert.Equal(0, byMerged.TotalCount);
        var onlyInstance = Assert.Single(unfiltered.Items);
        Assert.Equal(1, unfiltered.TotalCount);
        Assert.Equal(10, onlyInstance.CurrentNodeId);
    }

    [Fact]
    public async Task InstanceCancellationClosesAllTokensTasksAndParallelScopes()
    {
        var workflowKey = $"parallel-cancel-{Guid.NewGuid():N}";
        long instanceId;

        await using (var setup = fixture.CreateDbContext())
        {
            var definition = new WorkflowDefinitionEntity
            {
                Name = workflowKey,
                WorkflowKey = workflowKey,
                Version = 1,
                IsPublished = true,
                Definition = new WorkflowModel { Id = workflowKey, Name = workflowKey }
            };
            setup.WorkflowDefinitions.Add(definition);
            await setup.SaveChangesAsync();

            var instance = new WorkflowInstanceEntity
            {
                WorkflowDefinitionId = definition.Id,
                WorkflowKey = workflowKey,
                Status = WorkflowInstanceStatuses.Running
            };
            setup.WorkflowInstances.Add(instance);
            await setup.SaveChangesAsync();
            instanceId = instance.Id;

            var execution = new ParallelGatewayExecutionEntity
            {
                InstanceId = instance.Id,
                ForkNodeId = 2,
                Status = ParallelGatewayExecutionStatuses.Active,
                Branches =
                [
                    new ParallelGatewayBranchEntity
                    {
                        OriginatingFlowId = 100,
                        Ordinal = 0,
                        Status = ParallelGatewayBranchStatuses.Active
                    },
                    new ParallelGatewayBranchEntity
                    {
                        OriginatingFlowId = 101,
                        Ordinal = 1,
                        Status = ParallelGatewayBranchStatuses.Active
                    }
                ]
            };
            setup.ParallelGatewayExecutions.Add(execution);
            await setup.SaveChangesAsync();

            var managerToken = new ExecutionTokenEntity
            {
                InstanceId = instance.Id,
                ParallelBranchId = execution.Branches[0].Id,
                NodeId = 3,
                NodeName = "Manager review",
                NodeType = BpmnFlowNodeTypes.UserTask,
                Status = ExecutionTokenStatuses.Active
            };
            var financeToken = new ExecutionTokenEntity
            {
                InstanceId = instance.Id,
                ParallelBranchId = execution.Branches[1].Id,
                NodeId = 4,
                NodeName = "Finance review",
                NodeType = BpmnFlowNodeTypes.UserTask,
                Status = ExecutionTokenStatuses.Active
            };
            setup.ExecutionTokens.AddRange(managerToken, financeToken);
            await setup.SaveChangesAsync();

            var multiInstance = new MultiInstanceExecutionEntity
            {
                InstanceId = instance.Id,
                TokenId = financeToken.Id,
                NodeId = financeToken.NodeId,
                Mode = "parallel",
                Source = "cardinality",
                ResultVariable = "financeResults",
                Status = MultiInstanceExecutionStatuses.Active,
                TotalCount = 2
            };
            setup.MultiInstanceExecutions.Add(multiInstance);
            await setup.SaveChangesAsync();

            setup.UserTasks.AddRange(
                new UserTaskEntity
                {
                    InstanceId = instance.Id,
                    TokenId = managerToken.Id,
                    NodeId = managerToken.NodeId,
                    NodeName = managerToken.NodeName,
                    Status = UserTaskStatuses.Active
                },
                new UserTaskEntity
                {
                    InstanceId = instance.Id,
                    TokenId = financeToken.Id,
                    NodeId = financeToken.NodeId,
                    NodeName = financeToken.NodeName,
                    Status = UserTaskStatuses.Active,
                    MultiInstanceExecutionId = multiInstance.Id,
                    ItemIndex = 0
                },
                new UserTaskEntity
                {
                    InstanceId = instance.Id,
                    TokenId = financeToken.Id,
                    NodeId = financeToken.NodeId,
                    NodeName = financeToken.NodeName,
                    Status = UserTaskStatuses.Pending,
                    MultiInstanceExecutionId = multiInstance.Id,
                    ItemIndex = 1
                });
            await setup.SaveChangesAsync();
        }

        await using (var mutate = fixture.CreateDbContext())
        {
            var repository = new WorkflowRuntimeRepository(mutate);
            var instance = await repository.GetInstanceForUpdateAsync(instanceId, false, CancellationToken.None);
            Assert.NotNull(instance);

            var activeTokens = await repository.ListExecutionTokensAsync(
                instanceId, ExecutionTokenRecordStatuses.Active, CancellationToken.None);
            var tokenIds = activeTokens.Select(token => token.Id).ToList();
            await repository.CancelActiveMultiInstancesForTokensAsync(tokenIds, CancellationToken.None);
            await repository.CancelOpenUserTasksForTokensAsync(tokenIds, CancellationToken.None);
            foreach (var tokenId in tokenIds)
            {
                await repository.SetExecutionTokenStatusAsync(
                    tokenId,
                    ExecutionTokenRecordStatuses.Cancelled,
                    ExecutionTokenTerminationReasons.InstanceCancelled,
                    CancellationToken.None);
            }
            await repository.SetInstanceStatusAsync(
                instanceId, WorkflowInstanceStatuses.Cancelled, CancellationToken.None);
            await mutate.SaveChangesAsync();
        }

        await using var verify = fixture.CreateDbContext();
        var tokens = await verify.ExecutionTokens.AsNoTracking()
            .Where(token => token.InstanceId == instanceId)
            .ToListAsync();
        var tasks = await verify.UserTasks.AsNoTracking()
            .Where(task => task.InstanceId == instanceId)
            .ToListAsync();
        var executionState = await verify.ParallelGatewayExecutions.AsNoTracking()
            .SingleAsync(execution => execution.InstanceId == instanceId);
        var branches = await verify.ParallelGatewayBranches.AsNoTracking()
            .Where(branch => branch.ExecutionId == executionState.Id)
            .ToListAsync();
        var multiInstanceState = await verify.MultiInstanceExecutions.AsNoTracking()
            .SingleAsync(execution => execution.InstanceId == instanceId);

        Assert.All(tokens, token =>
        {
            Assert.Equal(ExecutionTokenStatuses.Cancelled, token.Status);
            Assert.Equal(ExecutionTokenTerminationReasons.InstanceCancelled, token.TerminationReason);
        });
        Assert.All(tasks, task => Assert.Equal(UserTaskStatuses.Cancelled, task.Status));
        Assert.Equal(MultiInstanceExecutionStatuses.Cancelled, multiInstanceState.Status);
        Assert.Equal("instanceCancel", multiInstanceState.CompletionReason);
        Assert.Equal(ParallelGatewayExecutionStatuses.Cancelled, executionState.Status);
        Assert.Equal("instanceCancel", executionState.CompletionReason);
        Assert.NotNull(executionState.CompletedAt);
        Assert.All(branches, branch =>
        {
            Assert.Equal(ParallelGatewayBranchStatuses.Cancelled, branch.Status);
            Assert.NotNull(branch.CompletedAt);
        });
    }
}
