using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class AdministrativeActionBatchAuthorizationPersistenceTests(
    PostgresApiFixture fixture)
{
    [Fact]
    public async Task ListDoesNotApplyFrozenFlowRolesAndPagesByExactDefinition()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var workflowKey = $"batch-list-auth-{suffix}";
        long definitionId;
        long newestId;
        long oldestId;

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
            definitionId = definition.Id;

            var now = DateTimeOffset.UtcNow;
            var oldest = Batch(definition, 101, "reviewer", now.AddMinutes(1));
            var newest = Batch(definition, 102, "finance", now.AddMinutes(2));
            setup.AdministrativeActionBatches.AddRange(oldest, newest);
            await setup.SaveChangesAsync();
            newestId = newest.Id;
            oldestId = oldest.Id;

            // Audit visibility is authentication-only and does not depend on
            // publication state or the role snapshot of the selected action.
            definition.IsPublished = false;
            await setup.SaveChangesAsync();
        }

        var search = new AdministrativeActionBatchSearch(
            workflowKey,
            definitionId,
            null,
            null,
            1,
            1);
        await using var context = fixture.CreateDbContext();
        var repository = new AdministrativeActionBatchRepository(context);

        var first = await repository.ListAsync(search, CancellationToken.None);
        Assert.Equal(2, first.TotalCount);
        Assert.Equal(newestId, Assert.Single(first.Items).Id);
        Assert.Equal("finance", Assert.Single(first.Items[0].Action.Roles));

        var second = await repository.ListAsync(
            search with { Page = 2 },
            CancellationToken.None);
        Assert.Equal(2, second.TotalCount);
        Assert.Equal(oldestId, Assert.Single(second.Items).Id);
        Assert.Equal("reviewer", Assert.Single(second.Items[0].Action.Roles));
    }

    private static AdministrativeActionBatchEntity Batch(
        WorkflowDefinitionEntity definition,
        int flowId,
        string role,
        DateTimeOffset updatedAt)
    {
        var action = new AdministrativeActionSnapshotRecord(
            definition.Id,
            definition.Version,
            AdministrativeActionKinds.DirectFlow,
            flowId,
            null,
            "Administrative action",
            2,
            "Second approval",
            1,
            "First approval",
            BpmnFlowNodeTypes.UserTask,
            null,
            [role],
            [],
            null,
            null,
            null,
            null);
        return new AdministrativeActionBatchEntity
        {
            WorkflowKey = definition.WorkflowKey,
            WorkflowDefinitionId = definition.Id,
            SourceNodeId = 2,
            ActionKind = AdministrativeActionKinds.DirectFlow,
            FlowId = flowId,
            ActionSnapshotJson = JsonDocument.Parse(JsonSerializer.Serialize(
                action,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))),
            CommonVariablesJson = JsonDocument.Parse("{}"),
            SelectionJson = JsonDocument.Parse("{}"),
            Status = AdministrativeActionBatchStatuses.Ready,
            PreparedBy = "batch-list-test",
            PreparedByRolesJson = JsonDocument.Parse("[]"),
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt
        };
    }
}
