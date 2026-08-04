using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class AdministrativeActionCandidatePersistenceTests(
    PostgresApiFixture fixture)
{
    [Fact]
    public async Task SearchAndMaterializeUseExactFlowMappingForEachRunningVersion()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var workflowKey = $"batch-candidate-mapping-{suffix}";
        var now = DateTimeOffset.UtcNow;
        long firstDefinitionId;
        long secondDefinitionId;
        long firstTaskId;
        long secondTaskId;

        await using (var setup = fixture.CreateDbContext())
        {
            var firstDefinition = Definition(workflowKey, 1, 11, 2, 1, now);
            var secondDefinition = Definition(workflowKey, 2, 99, 5, 4, now);
            setup.WorkflowDefinitions.AddRange(firstDefinition, secondDefinition);
            await setup.SaveChangesAsync();
            firstDefinitionId = firstDefinition.Id;
            secondDefinitionId = secondDefinition.Id;

            firstTaskId = await AddPositionAsync(setup, firstDefinition, 2, now);
            secondTaskId = await AddPositionAsync(setup, secondDefinition, 5, now.AddMinutes(1));
            _ = await AddPositionAsync(setup, firstDefinition, 7, now.AddMinutes(2));
        }

        var targets = new AdministrativeActionFlowTarget[]
        {
            new(firstDefinitionId, 11, 2),
            new(secondDefinitionId, 99, 5)
        };
        var query = new AdministrativeActionCandidateQuery
        {
            Targets = targets,
            Page = 1,
            PageSize = 20
        };

        await using (var searchContext = fixture.CreateDbContext())
        {
            var repository = new AdministrativeActionCandidateRepository(searchContext);
            var page = await repository.SearchAsync(query, CancellationToken.None);

            Assert.Equal(2, page.TotalCount);
            var byTask = page.Items.ToDictionary(candidate => candidate.UserTaskId);
            Assert.Equal(firstDefinitionId, byTask[firstTaskId].WorkflowDefinitionId);
            Assert.Equal(11, byTask[firstTaskId].FlowId);
            Assert.Equal(secondDefinitionId, byTask[secondTaskId].WorkflowDefinitionId);
            Assert.Equal(99, byTask[secondTaskId].FlowId);
        }

        await using (var staleContext = fixture.CreateDbContext())
        {
            var task = await staleContext.UserTasks.FindAsync(firstTaskId);
            Assert.NotNull(task);
            task.Status = UserTaskStatuses.Completed;
            task.UpdatedAt = now.AddMinutes(3);
            var token = await staleContext.ExecutionTokens.FindAsync(task.TokenId);
            Assert.NotNull(token);
            token.Status = ExecutionTokenStatuses.Completed;
            token.UpdatedAt = task.UpdatedAt;
            await staleContext.SaveChangesAsync();
        }

        await using (var materializeContext = fixture.CreateDbContext())
        {
            var repository = new AdministrativeActionCandidateRepository(materializeContext);
            var frozen = await repository.MaterializeAsync(
                query with { UserTaskIds = [firstTaskId, secondTaskId] },
                [],
                10,
                CancellationToken.None);

            Assert.Equal(
                new[] { firstTaskId, secondTaskId },
                frozen.Select(item => item.UserTaskId).ToArray());
            Assert.Equal(new[] { 11, 99 }, frozen.Select(item => item.FlowId).ToArray());
        }
    }

    [Fact]
    public async Task SearchRejectsMoreThanOneFlowMappingForTheSameVersion()
    {
        await using var context = fixture.CreateDbContext();
        var repository = new AdministrativeActionCandidateRepository(context);
        var query = new AdministrativeActionCandidateQuery
        {
            Targets =
            [
                new AdministrativeActionFlowTarget(101, 1, 10),
                new AdministrativeActionFlowTarget(101, 2, 10)
            ]
        };

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => repository.SearchAsync(query, CancellationToken.None));
        Assert.Contains("one exact flow target", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static WorkflowDefinitionEntity Definition(
        string workflowKey,
        int version,
        int flowId,
        int sourceNodeId,
        int targetNodeId,
        DateTimeOffset createdAt) =>
        new()
        {
            Name = $"Candidate mapping v{version}",
            WorkflowKey = workflowKey,
            Version = version,
            IsPublished = true,
            Definition = new WorkflowModel
            {
                Id = workflowKey,
                Name = $"Candidate mapping v{version}",
                FlowNodes =
                [
                    new FlowNodeModel
                    {
                        Id = sourceNodeId,
                        Name = "Review",
                        Type = BpmnFlowNodeTypes.UserTask
                    },
                    new FlowNodeModel
                    {
                        Id = targetNodeId,
                        Name = "Rework",
                        Type = BpmnFlowNodeTypes.UserTask
                    }
                ],
                SequenceFlows =
                [
                    new SequenceFlowModel
                    {
                        Id = flowId,
                        Name = "Send back",
                        SourceRef = sourceNodeId,
                        TargetRef = targetNodeId,
                        Roles = ["admin"]
                    }
                ]
            },
            CreatedAt = createdAt
        };

    private static async Task<long> AddPositionAsync(
        Flowbit.Infrastructure.Data.AppDbContext context,
        WorkflowDefinitionEntity definition,
        int nodeId,
        DateTimeOffset updatedAt)
    {
        var instance = new WorkflowInstanceEntity
        {
            WorkflowDefinitionId = definition.Id,
            WorkflowKey = definition.WorkflowKey,
            Status = "running",
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt
        };
        context.WorkflowInstances.Add(instance);
        await context.SaveChangesAsync();

        var token = new ExecutionTokenEntity
        {
            InstanceId = instance.Id,
            NodeId = nodeId,
            NodeName = $"Node {nodeId}",
            NodeType = BpmnFlowNodeTypes.UserTask,
            Status = ExecutionTokenStatuses.Active,
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt
        };
        context.ExecutionTokens.Add(token);
        await context.SaveChangesAsync();

        var task = new UserTaskEntity
        {
            InstanceId = instance.Id,
            TokenId = token.Id,
            NodeId = nodeId,
            NodeName = token.NodeName,
            Roles = ["reviewer"],
            Status = UserTaskStatuses.Active,
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt
        };
        context.UserTasks.Add(task);
        await context.SaveChangesAsync();
        return task.Id;
    }
}
