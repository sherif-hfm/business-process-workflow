using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class InstanceVersionChangeCandidateRepositoryTests(PostgresApiFixture fixture)
{
    [Fact]
    public async Task ExplicitMaterializationKeepsOnlyRunningExactSourceAndAppliesExclusions()
    {
        await using var db = fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var source = NewDefinition("explicit-source");
        var other = NewDefinition("explicit-other");
        db.WorkflowDefinitions.AddRange(source, other);
        await db.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        var included = NewInstance(source, WorkflowInstanceStatuses.Running, now);
        var excluded = NewInstance(source, WorkflowInstanceStatuses.Running, now);
        var terminal = NewInstance(source, WorkflowInstanceStatuses.Completed, now);
        var wrongSource = NewInstance(other, WorkflowInstanceStatuses.Running, now);
        db.WorkflowInstances.AddRange(included, excluded, terminal, wrongSource);
        await db.SaveChangesAsync();

        var repository = new InstanceVersionChangeCandidateRepository(db, runtime: null!);
        var candidates = await repository.MaterializeAsync(
            new InstanceVersionChangeCandidateQuery
            {
                SourceWorkflowDefinitionId = source.Id,
                InstanceIds =
                [
                    included.Id,
                    included.Id,
                    excluded.Id,
                    terminal.Id,
                    wrongSource.Id
                ]
            },
            excludedInstanceIds: [excluded.Id, excluded.Id],
            limit: 10,
            CancellationToken.None);

        var candidate = Assert.Single(candidates);
        Assert.Equal(included.Id, candidate.InstanceId);
        Assert.Equal(source.Id, candidate.WorkflowDefinitionId);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task AllMatchingNodeFilterUsesOnlyActiveExecutionPositions()
    {
        await using var db = fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var source = NewDefinition("active-node");
        db.WorkflowDefinitions.Add(source);
        await db.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        var activeMatch = NewInstance(source, WorkflowInstanceStatuses.Running, now);
        var historicalMatchOnly = NewInstance(source, WorkflowInstanceStatuses.Running, now);
        db.WorkflowInstances.AddRange(activeMatch, historicalMatchOnly);
        await db.SaveChangesAsync();
        db.ExecutionTokens.AddRange(
            NewToken(activeMatch.Id, 2, ExecutionTokenStatuses.Active, now),
            NewToken(activeMatch.Id, 99, ExecutionTokenStatuses.Completed, now),
            NewToken(historicalMatchOnly.Id, 99, ExecutionTokenStatuses.Active, now),
            NewToken(historicalMatchOnly.Id, 2, ExecutionTokenStatuses.Completed, now));
        await db.SaveChangesAsync();

        var repository = new InstanceVersionChangeCandidateRepository(db, runtime: null!);
        var candidates = await repository.MaterializeAsync(
            new InstanceVersionChangeCandidateQuery
            {
                SourceWorkflowDefinitionId = source.Id,
                NodeId = 2
            },
            excludedInstanceIds: [],
            limit: 10,
            CancellationToken.None);

        var candidate = Assert.Single(candidates);
        Assert.Equal(activeMatch.Id, candidate.InstanceId);

        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData(250, 500, 250)]
    [InlineData(10_001, 10_000, 10_001)]
    public async Task MaterializeAsyncDoesNotApplyInteractivePageClamp(
        int instanceCount,
        int limit,
        int expectedCount)
    {
        await using var db = fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var workflowKey = $"batch-candidates-{Guid.NewGuid():N}";
        var definition = new WorkflowDefinitionEntity
        {
            Name = "Batch candidate scale",
            WorkflowKey = workflowKey,
            Version = 1,
            Definition = new WorkflowModel
            {
                Id = workflowKey,
                Name = "Batch candidate scale"
            },
            IsPublished = true,
            IsDefault = false,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.WorkflowDefinitions.Add(definition);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO flowbit.workflow_instances
                ("WorkflowDefinitionId", "WorkflowKey", "Status", "CreatedAt", "UpdatedAt")
            SELECT {definition.Id}, {workflowKey}, 'running', clock_timestamp(), clock_timestamp()
            FROM generate_series(1, {instanceCount})
            """);

        var repository = new InstanceVersionChangeCandidateRepository(db, runtime: null!);
        var candidates = await repository.MaterializeAsync(
            new InstanceVersionChangeCandidateQuery
            {
                SourceWorkflowDefinitionId = definition.Id
            },
            excludedInstanceIds: [],
            limit,
            CancellationToken.None);

        Assert.Equal(expectedCount, candidates.Count);
        Assert.All(
            candidates,
            candidate => Assert.Equal(definition.Id, candidate.WorkflowDefinitionId));
        Assert.Equal(
            candidates.OrderBy(candidate => candidate.InstanceId).Select(candidate => candidate.InstanceId),
            candidates.Select(candidate => candidate.InstanceId));

        await transaction.RollbackAsync();
    }

    private static WorkflowDefinitionEntity NewDefinition(string label)
    {
        var workflowKey = $"batch-candidates-{label}-{Guid.NewGuid():N}";
        return new WorkflowDefinitionEntity
        {
            Name = "Batch candidate test",
            WorkflowKey = workflowKey,
            Version = 1,
            Definition = new WorkflowModel
            {
                Id = workflowKey,
                Name = "Batch candidate test"
            },
            IsPublished = true,
            IsDefault = false,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static WorkflowInstanceEntity NewInstance(
        WorkflowDefinitionEntity definition,
        string status,
        DateTimeOffset now) =>
        new()
        {
            WorkflowDefinition = definition,
            WorkflowDefinitionId = definition.Id,
            WorkflowKey = definition.WorkflowKey,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now
        };

    private static ExecutionTokenEntity NewToken(
        long instanceId,
        int nodeId,
        string status,
        DateTimeOffset now) =>
        new()
        {
            InstanceId = instanceId,
            NodeId = nodeId,
            NodeName = $"Node {nodeId}",
            NodeExternalId = $"node-{nodeId}",
            NodeType = BpmnFlowNodeTypes.UserTask,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now
        };
}
