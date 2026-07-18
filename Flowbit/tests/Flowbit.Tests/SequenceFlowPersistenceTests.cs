using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class SequenceFlowPersistenceTests(PostgresApiFixture fixture)
{
    [Fact]
    public async Task AppendMaintainsIndependentSummariesAndSameTransactionLastEvidence()
    {
        var instanceId = await CreateInstanceAsync();
        var actionAt = new DateTimeOffset(2026, 7, 18, 8, 0, 0, TimeSpan.Zero);
        var traversalAt = actionAt.AddMinutes(1);
        var firstCombinedAt = actionAt.AddMinutes(2);
        var lastCombinedAt = actionAt.AddMinutes(3);

        await using (var context = fixture.CreateDbContext())
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            var repository = new WorkflowRuntimeRepository(context);
            Assert.Empty(await repository.ListSequenceFlowSummariesAsync(instanceId, CancellationToken.None));

            var actionOnly = await repository.AppendSequenceFlowOccurrenceAsync(
                Occurrence(
                    instanceId,
                    sequenceFlowId: 201,
                    kind: "userTaskAction",
                    isAction: true,
                    isTraversal: false,
                    user: "alice",
                    userRoles: ["User", "Manager"],
                    values: Values(("decision", "\"confirm\""), ("amount", "42")),
                    occurredAt: actionAt,
                    userTaskId: -(instanceId * 10 + 1)),
                CancellationToken.None);

            Assert.Equal(1, actionOnly.ActionCount);
            Assert.Equal(0, actionOnly.TraversalCount);
            Assert.Null(actionOnly.LastTraversal);
            AssertEvidence(
                actionOnly.LastAction,
                "alice",
                ["User", "Manager"],
                "userTaskAction",
                actionAt,
                "decision",
                "confirm");

            var traversalOnly = await repository.AppendSequenceFlowOccurrenceAsync(
                Occurrence(
                    instanceId,
                    sequenceFlowId: 202,
                    kind: "gateway",
                    isAction: false,
                    isTraversal: true,
                    user: "alice",
                    userRoles: ["User", "Manager"],
                    values: null,
                    occurredAt: traversalAt),
                CancellationToken.None);

            Assert.Equal(0, traversalOnly.ActionCount);
            Assert.Equal(1, traversalOnly.TraversalCount);
            Assert.Null(traversalOnly.LastAction);
            AssertEvidence(
                traversalOnly.LastTraversal,
                "alice",
                ["User", "Manager"],
                "gateway",
                traversalAt);

            var firstCombined = await repository.AppendSequenceFlowOccurrenceAsync(
                Occurrence(
                    instanceId,
                    sequenceFlowId: 203,
                    kind: "userTaskAction",
                    isAction: true,
                    isTraversal: true,
                    user: "bob",
                    userRoles: ["User"],
                    values: Values(("round", "1")),
                    occurredAt: firstCombinedAt,
                    userTaskId: -(instanceId * 10 + 2)),
                CancellationToken.None);
            var lastCombined = await repository.AppendSequenceFlowOccurrenceAsync(
                Occurrence(
                    instanceId,
                    sequenceFlowId: 203,
                    kind: "multiInstanceInterrupt",
                    isAction: true,
                    isTraversal: true,
                    user: "carol",
                    userRoles: ["Manager", "Auditor"],
                    values: Values(("round", "2"), ("decision", "\"override\"")),
                    occurredAt: lastCombinedAt,
                    userTaskId: -(instanceId * 10 + 3)),
                CancellationToken.None);

            Assert.Equal(1, firstCombined.ActionCount);
            Assert.Equal(1, firstCombined.TraversalCount);
            Assert.Equal(2, lastCombined.ActionCount);
            Assert.Equal(2, lastCombined.TraversalCount);
            AssertEvidence(
                lastCombined.LastAction,
                "carol",
                ["Manager", "Auditor"],
                "multiInstanceInterrupt",
                lastCombinedAt,
                "decision",
                "override");
            AssertEvidence(
                lastCombined.LastTraversal,
                "carol",
                ["Manager", "Auditor"],
                "multiInstanceInterrupt",
                lastCombinedAt,
                "round",
                2);

            // A second read before SaveChanges must include both staged appends.
            var staged = await repository.ListSequenceFlowSummariesAsync(instanceId, CancellationToken.None);
            Assert.Equal(3, staged.Count);
            Assert.Equal(2, staged[203].ActionCount);
            Assert.Equal(2, staged[203].TraversalCount);

            await context.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        await using var verify = fixture.CreateDbContext();
        var occurrences = await verify.SequenceFlowOccurrences.AsNoTracking()
            .Where(occurrence => occurrence.InstanceId == instanceId)
            .OrderBy(occurrence => occurrence.Id)
            .ToListAsync();
        Assert.Equal(4, occurrences.Count);
        Assert.True(occurrences.Single(occurrence => occurrence.SequenceFlowId == 201).IsAction);
        Assert.False(occurrences.Single(occurrence => occurrence.SequenceFlowId == 201).IsTraversal);
        Assert.False(occurrences.Single(occurrence => occurrence.SequenceFlowId == 202).IsAction);
        Assert.True(occurrences.Single(occurrence => occurrence.SequenceFlowId == 202).IsTraversal);
        Assert.All(
            occurrences.Where(occurrence => occurrence.SequenceFlowId == 203),
            occurrence =>
            {
                Assert.True(occurrence.IsAction);
                Assert.True(occurrence.IsTraversal);
            });

        var persistedRepository = new WorkflowRuntimeRepository(verify);
        var persisted = await persistedRepository.ListSequenceFlowSummariesAsync(instanceId, CancellationToken.None);
        Assert.Equal(3, persisted.Count);
        Assert.Equal(1, persisted[201].ActionCount);
        Assert.Equal(0, persisted[201].TraversalCount);
        Assert.Equal(0, persisted[202].ActionCount);
        Assert.Equal(1, persisted[202].TraversalCount);
        Assert.Equal(2, persisted[203].ActionCount);
        Assert.Equal(2, persisted[203].TraversalCount);
        AssertEvidence(
            persisted[203].LastAction,
            "carol",
            ["Manager", "Auditor"],
            "multiInstanceInterrupt",
            lastCombinedAt,
            "decision",
            "override");
    }

    [Fact]
    public async Task DeletingInstanceCascadesOccurrencesAndSummaries()
    {
        var instanceId = await CreateInstanceAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            var repository = new WorkflowRuntimeRepository(setup);
            await repository.AppendSequenceFlowOccurrenceAsync(
                Occurrence(
                    instanceId,
                    sequenceFlowId: 301,
                    kind: "userTaskAction",
                    isAction: true,
                    isTraversal: true,
                    user: "dana",
                    userRoles: ["Manager"],
                    values: Values(("decision", "\"confirm\"")),
                    occurredAt: new DateTimeOffset(2026, 7, 18, 9, 0, 0, TimeSpan.Zero),
                    userTaskId: -(instanceId * 10 + 1)),
                CancellationToken.None);
            await setup.SaveChangesAsync();
        }

        await using (var delete = fixture.CreateDbContext())
        {
            var instance = await delete.WorkflowInstances.SingleAsync(candidate => candidate.Id == instanceId);
            delete.WorkflowInstances.Remove(instance);
            await delete.SaveChangesAsync();
        }

        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.SequenceFlowOccurrences.AsNoTracking()
            .AnyAsync(occurrence => occurrence.InstanceId == instanceId));
        Assert.False(await verify.SequenceFlowSummaries.AsNoTracking()
            .AnyAsync(summary => summary.InstanceId == instanceId));
    }

    [Fact]
    public async Task DatabaseRejectsOccurrenceThatIsNeitherActionNorTraversal()
    {
        var instanceId = await CreateInstanceAsync();
        await using var context = fixture.CreateDbContext();
        context.SequenceFlowOccurrences.Add(new SequenceFlowOccurrenceEntity
        {
            InstanceId = instanceId,
            SequenceFlowId = 401,
            SourceNodeId = 4,
            TargetNodeId = 5,
            Kind = "invalid",
            IsAction = false,
            IsTraversal = false,
            UserRoles = []
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    private async Task<long> CreateInstanceAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var workflowKey = $"flow-info-persistence-{suffix}";
        await using var context = fixture.CreateDbContext();
        var definition = new WorkflowDefinitionEntity
        {
            Name = workflowKey,
            WorkflowKey = workflowKey,
            Version = 1,
            IsPublished = true,
            Definition = new WorkflowModel { Id = workflowKey, Name = workflowKey }
        };
        var instance = new WorkflowInstanceEntity
        {
            WorkflowDefinition = definition,
            WorkflowKey = workflowKey,
            Status = WorkflowInstanceStatuses.Running
        };
        context.WorkflowInstances.Add(instance);
        await context.SaveChangesAsync();
        return instance.Id;
    }

    private static SequenceFlowOccurrenceWriteRecord Occurrence(
        long instanceId,
        int sequenceFlowId,
        string kind,
        bool isAction,
        bool isTraversal,
        string? user,
        IReadOnlyList<string> userRoles,
        Dictionary<string, JsonElement>? values,
        DateTimeOffset occurredAt,
        long? userTaskId = null) =>
        new(
            instanceId,
            sequenceFlowId,
            SourceNodeId: sequenceFlowId * 10,
            TargetNodeId: sequenceFlowId * 10 + 1,
            TokenId: instanceId * 100,
            UserTaskId: userTaskId,
            MultiInstanceExecutionId: null,
            ItemIndex: null,
            Kind: kind,
            IsAction: isAction,
            IsTraversal: isTraversal,
            User: user,
            UserRoles: userRoles,
            Values: values,
            OccurredAt: occurredAt);

    private static Dictionary<string, JsonElement> Values(
        params (string Name, string Json)[] values) =>
        values.ToDictionary(
            pair => pair.Name,
            pair => JsonDocument.Parse(pair.Json).RootElement.Clone());

    private static void AssertEvidence(
        SequenceFlowEvidenceRecord? evidence,
        string expectedUser,
        IReadOnlyList<string> expectedRoles,
        string expectedKind,
        DateTimeOffset expectedAt,
        string? valueName = null,
        object? expectedValue = null)
    {
        var actual = Assert.IsType<SequenceFlowEvidenceRecord>(evidence);
        Assert.Equal(expectedUser, actual.User);
        Assert.Equal(expectedRoles, actual.UserRoles);
        Assert.Equal(expectedKind, actual.Kind);
        Assert.Equal(expectedAt, actual.OccurredAt);
        if (valueName is null)
        {
            Assert.Null(actual.Values);
            return;
        }

        Assert.NotNull(actual.Values);
        var value = actual.Values[valueName];
        if (expectedValue is string text)
        {
            Assert.Equal(text, value.GetString());
        }
        else if (expectedValue is int number)
        {
            Assert.Equal(number, value.GetInt32());
        }
        else
        {
            throw new InvalidOperationException("Unsupported expected test value.");
        }
    }
}
