using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class AdministrativeActionCandidatePersistenceTests(
    PostgresApiFixture fixture)
{
    [Fact]
    public async Task OrdinarySearchUsesExactDefinitionAndNodeAndReturnsTimerFenceWithParallelSibling()
    {
        var now = DateTimeOffset.UtcNow;
        var workflowKey = $"position-candidate-{Guid.NewGuid():N}";
        long definitionId;
        long taskId;
        long timerSubscriptionId;
        long timerJobId;

        await using (var setup = fixture.CreateDbContext())
        {
            var definition = Definition(workflowKey, 1, 2, now);
            setup.WorkflowDefinitions.Add(definition);
            await setup.SaveChangesAsync();
            definitionId = definition.Id;

            var position = await AddOrdinaryPositionAsync(setup, definition, 2, now);
            taskId = position.Task.Id;
            // A sibling branch must not disqualify the selected position.
            _ = await AddOrdinaryPositionAsync(
                setup,
                definition,
                7,
                now.AddMinutes(1),
                position.Instance);

            var subscription = new TimerSubscriptionEntity
            {
                InstanceId = position.Instance.Id,
                WorkflowDefinitionId = definition.Id,
                WorkflowKey = workflowKey,
                TokenId = position.Token.Id,
                ActivationId = position.Token.ActivationId,
                TimerNodeId = 30,
                TimerNodeName = "Review timeout",
                AttachedToNodeId = 2,
                ScheduleKind = TimerScheduleKinds.Duration,
                ScheduleExpression = "PT1H",
                CancelActivity = false,
                Status = TimerSubscriptionStatuses.Active,
                NextDueAt = now.AddHours(1),
                Occurrence = 0,
                CreatedAt = now,
                UpdatedAt = now
            };
            setup.TimerSubscriptions.Add(subscription);
            await setup.SaveChangesAsync();
            timerSubscriptionId = subscription.Id;

            var job = new WorkflowJobEntity
            {
                InstanceId = position.Instance.Id,
                WorkflowDefinitionId = definition.Id,
                WorkflowKey = workflowKey,
                TokenId = position.Token.Id,
                TimerSubscriptionId = subscription.Id,
                ActivationId = position.Token.ActivationId,
                NodeId = 30,
                NodeName = "Review timeout",
                NodeType = BpmnFlowNodeTypes.TimerBoundaryEvent,
                Kind = WorkflowJobKinds.TimerBoundary,
                QueueClass = WorkflowJobClasses.Control,
                Phase = "execute",
                Status = WorkflowJobStatuses.Queued,
                MaxAttempts = 1,
                DueAt = subscription.NextDueAt,
                ScheduledOccurrenceAt = subscription.NextDueAt,
                CreatedAt = now,
                UpdatedAt = now
            };
            setup.WorkflowJobs.Add(job);
            await setup.SaveChangesAsync();
            timerJobId = job.Id;
        }

        var query = new AdministrativeActionCandidateQuery
        {
            WorkflowDefinitionId = definitionId,
            SourceNodeId = 2,
            Page = 1,
            PageSize = 20
        };
        await using (var searchContext = fixture.CreateDbContext())
        {
            var repository = new AdministrativeActionCandidateRepository(searchContext);
            var page = await repository.SearchAsync(query, CancellationToken.None);
            var candidate = Assert.Single(page.Items);
            Assert.Equal(1, page.TotalCount);
            Assert.Equal(AdministrativeActionPositionKinds.UserTask, candidate.PositionKind);
            Assert.Equal(taskId, candidate.PositionId);
            Assert.Equal(1, candidate.AffectedTaskCount);
            var timer = Assert.Single(candidate.TimerBoundaries);
            Assert.Equal(30, timer.BoundaryNodeId);
            Assert.Equal(timerSubscriptionId, timer.TimerSubscriptionId);
            Assert.Equal(timerJobId, timer.TimerJobId);
            Assert.Equal(TimerSubscriptionStatuses.Active, timer.Status);
        }

        await using (var staleContext = fixture.CreateDbContext())
        {
            var task = (await staleContext.UserTasks.FindAsync(taskId))!;
            task.Status = UserTaskStatuses.Completed;
            task.UpdatedAt = now.AddMinutes(2);
            var token = (await staleContext.ExecutionTokens.FindAsync(task.TokenId))!;
            token.Status = ExecutionTokenStatuses.Completed;
            await staleContext.SaveChangesAsync();
        }
        await using (var materializeContext = fixture.CreateDbContext())
        {
            var repository = new AdministrativeActionCandidateRepository(materializeContext);
            var frozen = await repository.MaterializeAsync(
                query with
                {
                    Positions =
                    [
                        new AdministrativeActionPositionKey(
                            AdministrativeActionPositionKinds.UserTask,
                            taskId)
                    ]
                },
                [],
                10,
                CancellationToken.None);
            Assert.Equal(taskId, Assert.Single(frozen).PositionId);
        }
    }

    [Fact]
    public async Task MultiInstanceSearchReturnsOneParentPositionAndCountsUnfinishedChildren()
    {
        var now = DateTimeOffset.UtcNow;
        var workflowKey = $"mi-position-candidate-{Guid.NewGuid():N}";
        long definitionId;
        long executionId;

        await using (var setup = fixture.CreateDbContext())
        {
            var definition = Definition(workflowKey, 1, 5, now);
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
            var token = new ExecutionTokenEntity
            {
                InstanceId = instance.Id,
                NodeId = 5,
                NodeName = "Parallel review",
                NodeType = BpmnFlowNodeTypes.UserTask,
                Status = ExecutionTokenStatuses.Active,
                CreatedAt = now,
                UpdatedAt = now
            };
            setup.ExecutionTokens.Add(token);
            await setup.SaveChangesAsync();
            var execution = new MultiInstanceExecutionEntity
            {
                InstanceId = instance.Id,
                TokenId = token.Id,
                NodeId = 5,
                ResultVariable = "results",
                Status = MultiInstanceExecutionStatuses.Active,
                TotalCount = 3,
                CompletedCount = 1,
                CreatedAt = now,
                UpdatedAt = now
            };
            setup.MultiInstanceExecutions.Add(execution);
            await setup.SaveChangesAsync();
            executionId = execution.Id;
            setup.UserTasks.AddRange(
                Child(instance.Id, token.Id, execution.Id, 0, UserTaskStatuses.Completed, now),
                Child(instance.Id, token.Id, execution.Id, 1, UserTaskStatuses.Active, now),
                Child(instance.Id, token.Id, execution.Id, 2, UserTaskStatuses.Pending, now));
            await setup.SaveChangesAsync();
        }

        var query = new AdministrativeActionCandidateQuery
        {
            WorkflowDefinitionId = definitionId,
            SourceNodeId = 5
        };
        await using var context = fixture.CreateDbContext();
        var repository = new AdministrativeActionCandidateRepository(context);
        var page = await repository.SearchAsync(query, CancellationToken.None);
        var candidate = Assert.Single(page.Items);
        Assert.Equal(AdministrativeActionPositionKinds.MultiInstanceExecution, candidate.PositionKind);
        Assert.Equal(executionId, candidate.PositionId);
        Assert.Null(candidate.UserTaskId);
        Assert.Equal(2, candidate.AffectedTaskCount);

        var excluded = await repository.MaterializeAsync(
            query,
            [new AdministrativeActionPositionKey(candidate.PositionKind, candidate.PositionId)],
            10,
            CancellationToken.None);
        Assert.Empty(excluded);
    }

    private static WorkflowDefinitionEntity Definition(
        string workflowKey,
        int version,
        int nodeId,
        DateTimeOffset createdAt) =>
        new()
        {
            Name = $"Candidate v{version}",
            WorkflowKey = workflowKey,
            Version = version,
            IsPublished = true,
            Definition = new WorkflowModel
            {
                Id = workflowKey,
                Name = $"Candidate v{version}",
                FlowNodes =
                [
                    new FlowNodeModel
                    {
                        Id = nodeId,
                        Name = "Review",
                        Type = BpmnFlowNodeTypes.UserTask
                    }
                ]
            },
            CreatedAt = createdAt
        };

    private static async Task<(WorkflowInstanceEntity Instance, ExecutionTokenEntity Token, UserTaskEntity Task)>
        AddOrdinaryPositionAsync(
            Flowbit.Infrastructure.Data.AppDbContext context,
            WorkflowDefinitionEntity definition,
            int nodeId,
            DateTimeOffset updatedAt,
            WorkflowInstanceEntity? existingInstance = null)
    {
        var instance = existingInstance ?? new WorkflowInstanceEntity
        {
            WorkflowDefinitionId = definition.Id,
            WorkflowKey = definition.WorkflowKey,
            Status = "running",
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt
        };
        if (existingInstance is null)
        {
            context.WorkflowInstances.Add(instance);
            await context.SaveChangesAsync();
        }
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
        return (instance, token, task);
    }

    private static UserTaskEntity Child(
        long instanceId,
        long tokenId,
        long executionId,
        int itemIndex,
        string status,
        DateTimeOffset now) =>
        new()
        {
            InstanceId = instanceId,
            TokenId = tokenId,
            MultiInstanceExecutionId = executionId,
            NodeId = 5,
            NodeName = "Parallel review",
            ItemIndex = itemIndex,
            Roles = ["reviewer"],
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = status == UserTaskStatuses.Completed ? now : null
        };
}
