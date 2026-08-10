using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class InstanceVariableUpdateCandidateRepositoryTests(
    PostgresApiFixture fixture)
{
    [Fact]
    public async Task ExplicitMaterializationSpansFamilyVersionsAndHonorsExactVersionAndExclusions()
    {
        await using var db = fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var workflowKey = $"variable-update-candidates-{Guid.NewGuid():N}";
        var versionOne = NewDefinition(workflowKey, 1);
        var versionTwo = NewDefinition(workflowKey, 2);
        var otherFamily = NewDefinition($"other-{Guid.NewGuid():N}", 1);
        db.WorkflowDefinitions.AddRange(versionOne, versionTwo, otherFamily);
        await db.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        var first = NewInstance(versionOne, WorkflowInstanceStatuses.Running, null, now);
        var second = NewInstance(versionTwo, WorkflowInstanceStatuses.Running, null, now);
        var excluded = NewInstance(versionTwo, WorkflowInstanceStatuses.Running, null, now);
        var terminal = NewInstance(versionOne, WorkflowInstanceStatuses.Completed, null, now);
        var wrongFamily = NewInstance(otherFamily, WorkflowInstanceStatuses.Running, null, now);
        db.WorkflowInstances.AddRange(first, second, excluded, terminal, wrongFamily);
        await db.SaveChangesAsync();

        var repository = new InstanceVariableUpdateCandidateRepository(db, runtime: null!);
        var family = await repository.MaterializeAsync(
            NewQuery(
                workflowKey,
                workflowDefinitionId: null,
                instanceIds:
                [
                    first.Id,
                    first.Id,
                    second.Id,
                    excluded.Id,
                    terminal.Id,
                    wrongFamily.Id
                ]),
            excludedInstanceIds: [excluded.Id, excluded.Id],
            limit: 10,
            CancellationToken.None);

        Assert.Equal([first.Id, second.Id], family.Select(candidate => candidate.InstanceId));
        Assert.Equal(
            [versionOne.Id, versionTwo.Id],
            family.Select(candidate => candidate.WorkflowDefinitionId));

        var exactVersion = await repository.MaterializeAsync(
            NewQuery(
                workflowKey,
                versionOne.Id,
                [first.Id, second.Id]),
            excludedInstanceIds: [],
            limit: 10,
            CancellationToken.None);

        var candidate = Assert.Single(exactVersion);
        Assert.Equal(first.Id, candidate.InstanceId);
        Assert.Equal(versionOne.Id, candidate.WorkflowDefinitionId);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task AllMatchingUsesActivePositionsAcrossVersionsAndNeverCrossesFamily()
    {
        await using var db = fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var workflowKey = $"variable-update-node-{Guid.NewGuid():N}";
        var versionOne = NewDefinition(workflowKey, 1);
        var versionTwo = NewDefinition(workflowKey, 2);
        var other = NewDefinition($"other-node-{Guid.NewGuid():N}", 1);
        db.WorkflowDefinitions.AddRange(versionOne, versionTwo, other);
        await db.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        var first = NewInstance(versionOne, WorkflowInstanceStatuses.Running, null, now);
        var second = NewInstance(versionTwo, WorkflowInstanceStatuses.Running, null, now);
        var historicalOnly = NewInstance(versionTwo, WorkflowInstanceStatuses.Running, null, now);
        var wrongFamily = NewInstance(other, WorkflowInstanceStatuses.Running, null, now);
        db.WorkflowInstances.AddRange(first, second, historicalOnly, wrongFamily);
        await db.SaveChangesAsync();
        db.ExecutionTokens.AddRange(
            NewToken(first.Id, 7, ExecutionTokenStatuses.Active, now),
            NewToken(second.Id, 7, ExecutionTokenStatuses.Active, now),
            NewToken(historicalOnly.Id, 7, ExecutionTokenStatuses.Completed, now),
            NewToken(historicalOnly.Id, 9, ExecutionTokenStatuses.Active, now),
            NewToken(wrongFamily.Id, 7, ExecutionTokenStatuses.Active, now));
        await db.SaveChangesAsync();

        var repository = new InstanceVariableUpdateCandidateRepository(db, runtime: null!);
        var candidates = await repository.MaterializeAsync(
            NewQuery(workflowKey, workflowDefinitionId: null, nodeId: 7),
            excludedInstanceIds: [],
            limit: 10,
            CancellationToken.None);

        Assert.Equal([first.Id, second.Id], candidates.Select(candidate => candidate.InstanceId));
        Assert.Equal(
            [versionOne.Id, versionTwo.Id],
            candidates.Select(candidate => candidate.WorkflowDefinitionId));

        await transaction.RollbackAsync();
    }

    private static InstanceVariableUpdateCandidateQuery NewQuery(
        string workflowKey,
        long? workflowDefinitionId,
        IReadOnlyList<long>? instanceIds = null,
        int? nodeId = null) =>
        new(
            workflowKey,
            workflowDefinitionId,
            InstanceId: null,
            BusinessKey: null,
            NodeId: nodeId,
            NodeExternalId: null,
            VariableFilter: null,
            Sort:
            [
                new InstanceSortCriterion(
                    InstanceSortField.UpdatedAt,
                    SortDirection.Descending),
                new InstanceSortCriterion(
                    InstanceSortField.Id,
                    SortDirection.Descending)
            ],
            Cursor: null,
            IncludeVariables: false,
            Page: 1,
            PageSize: 50,
            InstanceIds: instanceIds);

    private static WorkflowDefinitionEntity NewDefinition(
        string workflowKey,
        int version) =>
        new()
        {
            Name = $"Variable update candidate v{version}",
            WorkflowKey = workflowKey,
            Version = version,
            Definition = new WorkflowModel
            {
                Id = workflowKey,
                Name = $"Variable update candidate v{version}"
            },
            IsPublished = true,
            IsDefault = false,
            CreatedAt = DateTimeOffset.UtcNow
        };

    private static WorkflowInstanceEntity NewInstance(
        WorkflowDefinitionEntity definition,
        string status,
        string? businessKey,
        DateTimeOffset now) =>
        new()
        {
            WorkflowDefinitionId = definition.Id,
            WorkflowKey = definition.WorkflowKey,
            Status = status,
            BusinessKey = businessKey,
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
