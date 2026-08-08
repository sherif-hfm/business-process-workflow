using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class AdministrativeSequenceFlowEvidencePersistenceTests(
    PostgresApiFixture fixture)
{
    [Fact]
    public async Task AdministrativeOccurrenceAndBothLastEvidenceViewsRetainExactCorrelation()
    {
        var (instanceId, workflowDefinitionId) = await CreateInstanceAsync();
        var occurredAt = new DateTimeOffset(2026, 8, 8, 9, 30, 0, TimeSpan.Zero);
        var administrativeAction = new SequenceFlowAdministrativeActionRecord(
            BatchId: 73,
            WorkflowDefinitionId: workflowDefinitionId,
            ActionKind: AdministrativeActionKinds.DirectFlow,
            FlowId: 201,
            BoundaryNodeId: null,
            TimerSubscriptionId: null,
            MultiInstanceMode: AdministrativeActionMultiInstanceModes.ForceParent,
            Reason: "Rework the selected approvals");

        await using (var context = fixture.CreateDbContext())
        {
            var repository = new WorkflowRuntimeRepository(context);
            await repository.AppendSequenceFlowOccurrenceAsync(
                new SequenceFlowOccurrenceWriteRecord(
                    instanceId,
                    101,
                    1,
                    2,
                    null,
                    null,
                    null,
                    null,
                    "automatic",
                    false,
                    true,
                    null,
                    [],
                    null,
                    occurredAt.AddMinutes(-1)),
                CancellationToken.None);
            var summary = await repository.AppendSequenceFlowOccurrenceAsync(
                new SequenceFlowOccurrenceWriteRecord(
                    instanceId,
                    201,
                    2,
                    3,
                    900,
                    null,
                    44,
                    null,
                    NodeExecutionCompletionReasons.AdministrativeAction,
                    true,
                    true,
                    "batch-operator",
                    ["Operations", "Auditor"],
                    new Dictionary<string, JsonElement>
                    {
                        ["comment"] = JsonSerializer.SerializeToElement("Needs another review")
                    },
                    occurredAt)
                {
                    AdministrativeAction = administrativeAction
                },
                CancellationToken.None);

            Assert.Equal(administrativeAction, summary.LastAction?.AdministrativeAction);
            Assert.Equal(administrativeAction, summary.LastTraversal?.AdministrativeAction);
            await context.SaveChangesAsync();
        }

        await using var verify = fixture.CreateDbContext();
        var normal = await verify.SequenceFlowOccurrences.AsNoTracking()
            .SingleAsync(item => item.InstanceId == instanceId && item.SequenceFlowId == 101);
        Assert.Null(normal.AdministrativeActionJson);

        var occurrence = await verify.SequenceFlowOccurrences.AsNoTracking()
            .SingleAsync(item => item.InstanceId == instanceId && item.SequenceFlowId == 201);
        var stored = Assert.IsType<JsonDocument>(occurrence.AdministrativeActionJson);
        Assert.Equal(73, stored.RootElement.GetProperty("batchId").GetInt64());
        Assert.Equal(
            workflowDefinitionId,
            stored.RootElement.GetProperty("workflowDefinitionId").GetInt64());
        Assert.Equal("directFlow", stored.RootElement.GetProperty("actionKind").GetString());
        Assert.Equal(201, stored.RootElement.GetProperty("flowId").GetInt32());
        Assert.Equal(
            "forceParent",
            stored.RootElement.GetProperty("multiInstanceMode").GetString());

        var persisted = await new WorkflowRuntimeRepository(verify)
            .ListSequenceFlowSummariesAsync(instanceId, CancellationToken.None);
        var evidence = persisted[201];
        Assert.Equal(administrativeAction, evidence.LastAction?.AdministrativeAction);
        Assert.Equal(administrativeAction, evidence.LastTraversal?.AdministrativeAction);

        var flowInfo = new SequenceFlowRuntimeSummary(
            201,
            new SequenceFlowRuntimeView(
                evidence.ActionCount,
                ToRuntimeEvidence(evidence.LastAction)),
            new SequenceFlowRuntimeView(
                evidence.TraversalCount,
                ToRuntimeEvidence(evidence.LastTraversal)));
        var json = flowInfo.ToJsonElement();
        Assert.Equal(
            73,
            json.GetProperty("actions").GetProperty("last")
                .GetProperty("administrativeAction").GetProperty("batchId").GetInt64());
        Assert.Equal(
            "directFlow",
            json.GetProperty("traversals").GetProperty("last")
                .GetProperty("administrativeAction").GetProperty("actionKind").GetString());
    }

    [Fact]
    public async Task DatabaseRequiresAdministrativeMetadataOnlyForAdministrativeOccurrences()
    {
        var (instanceId, workflowDefinitionId) = await CreateInstanceAsync();

        await using (var missing = fixture.CreateDbContext())
        {
            missing.SequenceFlowOccurrences.Add(new SequenceFlowOccurrenceEntity
            {
                InstanceId = instanceId,
                WorkflowDefinitionId = workflowDefinitionId,
                SequenceFlowId = 201,
                SourceNodeId = 2,
                TargetNodeId = 3,
                Kind = NodeExecutionCompletionReasons.AdministrativeAction,
                IsAction = true,
                IsTraversal = true,
                UserRoles = []
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => missing.SaveChangesAsync());
        }

        await using (var attachedToNormal = fixture.CreateDbContext())
        {
            attachedToNormal.SequenceFlowOccurrences.Add(new SequenceFlowOccurrenceEntity
            {
                InstanceId = instanceId,
                WorkflowDefinitionId = workflowDefinitionId,
                SequenceFlowId = 202,
                SourceNodeId = 2,
                TargetNodeId = 4,
                Kind = "gateway",
                IsAction = false,
                IsTraversal = true,
                UserRoles = [],
                AdministrativeActionJson = JsonDocument.Parse($$"""
                    {
                      "batchId": 1,
                      "workflowDefinitionId": {{workflowDefinitionId}},
                      "actionKind": "directFlow",
                      "flowId": 202,
                      "boundaryNodeId": null,
                      "timerSubscriptionId": null,
                      "multiInstanceMode": null,
                      "reason": null
                    }
                    """)
            });
            await Assert.ThrowsAsync<DbUpdateException>(
                () => attachedToNormal.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task AdministrativeBatchMigrationContainsOccurrenceAndSummaryEvidenceColumns()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using (var columns = new NpgsqlCommand("""
            SELECT table_name || '.' || column_name
            FROM information_schema.columns
            WHERE table_schema = 'flowbit'
              AND (
                  (table_name = 'sequence_flow_occurrences'
                   AND column_name = 'AdministrativeActionJson')
                  OR
                  (table_name = 'sequence_flow_summaries'
                   AND column_name IN (
                       'LastActionAdministrativeActionJson',
                       'LastTraversalAdministrativeActionJson')))
            ORDER BY 1
            """, connection))
        await using (var reader = await columns.ExecuteReaderAsync())
        {
            var actual = new List<string>();
            while (await reader.ReadAsync())
            {
                actual.Add(reader.GetString(0));
            }
            Assert.Equal(
                [
                    "sequence_flow_occurrences.AdministrativeActionJson",
                    "sequence_flow_summaries.LastActionAdministrativeActionJson",
                    "sequence_flow_summaries.LastTraversalAdministrativeActionJson"
                ],
                actual);
        }

        await using var constraint = new NpgsqlCommand("""
            SELECT pg_get_constraintdef(oid)
            FROM pg_catalog.pg_constraint
            WHERE conname = 'CK_sequence_flow_occurrences_administrative_action'
              AND conrelid = 'flowbit.sequence_flow_occurrences'::regclass
            """, connection);
        var definition = Assert.IsType<string>(await constraint.ExecuteScalarAsync());
        Assert.Contains("administrativeAction", definition, StringComparison.Ordinal);
        Assert.Contains("workflowDefinitionId", definition, StringComparison.Ordinal);
        Assert.Contains("timerSubscriptionId", definition, StringComparison.Ordinal);
    }

    [Fact]
    public void FlowInfoValidationAcceptsAdministrativeActionEvidencePaths()
    {
        foreach (var path in new[]
                 {
                     "actions.last.administrativeAction",
                     "traversals.last.administrativeAction"
                 })
        {
            var valid = SequenceFlowConditionEvaluator.TryValidateFlowInfoReferences(
                $"FlowInfo(201, '{path}') != null",
                new HashSet<int> { 201 },
                allowed: true,
                out var error);
            Assert.True(valid, error);
        }
    }

    private async Task<(long InstanceId, long WorkflowDefinitionId)> CreateInstanceAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var workflowKey = $"administrative-flow-evidence-{suffix}";
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
        return (instance.Id, definition.Id);
    }

    private static SequenceFlowLastOccurrence? ToRuntimeEvidence(
        SequenceFlowEvidenceRecord? evidence) =>
        evidence is null
            ? null
            : new SequenceFlowLastOccurrence(
                evidence.User,
                evidence.UserRoles,
                evidence.OccurredAt,
                evidence.Kind,
                evidence.Values is null
                    ? null
                    : JsonSerializer.SerializeToElement(evidence.Values))
            {
                ActingFor = evidence.ActingFor,
                DelegationId = evidence.DelegationId,
                AdministrativeAction = evidence.AdministrativeAction
            };
}
