using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class AdministrativeActionBatchAuthorizationPersistenceTests(
    PostgresApiFixture fixture)
{
    [Fact]
    public async Task ListAppliesFlowRoleVisibilityBeforeCountOrderAndPage()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var workflowKey = $"batch-list-auth-{suffix}";
        var visibleRole = $"batch-reviewer-{suffix}";
        long firstVisibleId;
        long secondVisibleId;

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
                    Name = workflowKey,
                    SequenceFlows =
                    [
                        new SequenceFlowModel
                        {
                            Id = 101,
                            Name = "Visible send-back",
                            ExternalId = "VISIBLE_SEND_BACK",
                            SourceRef = 2,
                            TargetRef = 1,
                            Roles = [visibleRole.ToUpperInvariant()]
                        },
                        new SequenceFlowModel
                        {
                            Id = 102,
                            Name = "Hidden send-back",
                            ExternalId = "HIDDEN_SEND_BACK",
                            SourceRef = 2,
                            TargetRef = 1,
                            Roles = [$"other-role-{suffix}"]
                        }
                    ]
                }
            };
            setup.WorkflowDefinitions.Add(definition);
            await setup.SaveChangesAsync();

            var now = DateTimeOffset.UtcNow;
            var mixedNewest = Batch(
                definition,
                now.AddMinutes(5),
                (101, "VISIBLE_SEND_BACK", visibleRole),
                (102, "HIDDEN_SEND_BACK", $"other-role-{suffix}"));
            var hiddenNewest = Batch(
                definition,
                102,
                $"other-role-{suffix}",
                now.AddMinutes(4));
            var visibleFirst = Batch(
                definition,
                101,
                visibleRole.ToUpperInvariant(),
                now.AddMinutes(3));
            var hiddenMiddle = Batch(
                definition,
                102,
                $"other-role-{suffix}",
                now.AddMinutes(2));
            var visibleSecond = Batch(
                definition,
                101,
                visibleRole.ToUpperInvariant(),
                now.AddMinutes(1));
            setup.AdministrativeActionBatches.AddRange(
                mixedNewest,
                hiddenNewest,
                visibleFirst,
                hiddenMiddle,
                visibleSecond);
            await setup.SaveChangesAsync();
            firstVisibleId = visibleFirst.Id;
            secondVisibleId = visibleSecond.Id;

            // Batch audit visibility is tied to the frozen mapping role
            // snapshot, not to whether that immutable version remains published.
            definition.IsPublished = false;
            await setup.SaveChangesAsync();
        }

        var authorization = new AdministrativeActionBatchListAuthorization(
            [visibleRole.ToLowerInvariant()]);
        var search = new AdministrativeActionBatchSearch(
            workflowKey,
            null,
            null,
            1,
            1);

        await using var firstContext = fixture.CreateDbContext();
        var firstRepository = new AdministrativeActionBatchRepository(firstContext);
        var first = await firstRepository.ListAsync(
            search,
            authorization,
            CancellationToken.None);
        Assert.Equal(2, first.TotalCount);
        Assert.Equal(firstVisibleId, Assert.Single(first.Items).Id);

        await using var secondContext = fixture.CreateDbContext();
        var secondRepository = new AdministrativeActionBatchRepository(secondContext);
        var second = await secondRepository.ListAsync(
            search with { Page = 2 },
            authorization,
            CancellationToken.None);
        Assert.Equal(2, second.TotalCount);
        Assert.Equal(secondVisibleId, Assert.Single(second.Items).Id);

        await using var hiddenContext = fixture.CreateDbContext();
        var hiddenRepository = new AdministrativeActionBatchRepository(hiddenContext);
        var hidden = await hiddenRepository.ListAsync(
            search,
            new AdministrativeActionBatchListAuthorization([$"unknown-{suffix}"]),
            CancellationToken.None);
        Assert.Equal(0, hidden.TotalCount);
        Assert.Empty(hidden.Items);
    }

    private static AdministrativeActionBatchEntity Batch(
        WorkflowDefinitionEntity definition,
        int flowId,
        string role,
        DateTimeOffset updatedAt) =>
        Batch(
            definition,
            updatedAt,
            (flowId,
                flowId == 101 ? "VISIBLE_SEND_BACK" : "HIDDEN_SEND_BACK",
                role));

    private static AdministrativeActionBatchEntity Batch(
        WorkflowDefinitionEntity definition,
        DateTimeOffset updatedAt,
        params (int FlowId, string? ExternalId, string Role)[] mappings) =>
        new()
        {
            WorkflowKey = definition.WorkflowKey,
            FlowMappingsJson = JsonDocument.Parse(JsonSerializer.Serialize(
                mappings.Select(mapping =>
                    new AdministrativeActionFlowMappingRecord(
                        definition.Id,
                        definition.Version,
                        mapping.FlowId,
                        mapping.ExternalId,
                        "Send back",
                        2,
                        "Second approval",
                        1,
                        "First approval",
                        [mapping.Role],
                        [])).ToArray(),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))),
            Reason = "Authorization paging test",
            CommonVariablesJson = JsonDocument.Parse("{}"),
            SelectionJson = JsonDocument.Parse("{}"),
            Status = AdministrativeActionBatchStatuses.Ready,
            PreparedBy = "batch-list-test",
            PreparedByRolesJson = JsonDocument.Parse("[\"admin\"]"),
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt
        };
}
