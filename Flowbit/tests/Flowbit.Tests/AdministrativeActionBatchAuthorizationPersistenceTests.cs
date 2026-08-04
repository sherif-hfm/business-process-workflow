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
                            Roles = [visibleRole.ToUpperInvariant()],
                            IsAdministrative = true,
                            IsBatchable = true
                        },
                        new SequenceFlowModel
                        {
                            Id = 102,
                            Name = "Hidden send-back",
                            ExternalId = "HIDDEN_SEND_BACK",
                            SourceRef = 2,
                            TargetRef = 1,
                            Roles = [$"other-role-{suffix}"],
                            IsAdministrative = true,
                            IsBatchable = true
                        }
                    ]
                }
            };
            setup.WorkflowDefinitions.Add(definition);
            await setup.SaveChangesAsync();

            var now = DateTimeOffset.UtcNow;
            var hiddenNewest = Batch(
                definition,
                "HIDDEN_SEND_BACK",
                now.AddMinutes(4));
            var visibleFirst = Batch(
                definition,
                "VISIBLE_SEND_BACK",
                now.AddMinutes(3));
            var hiddenMiddle = Batch(
                definition,
                "HIDDEN_SEND_BACK",
                now.AddMinutes(2));
            var visibleSecond = Batch(
                definition,
                "VISIBLE_SEND_BACK",
                now.AddMinutes(1));
            setup.AdministrativeActionBatches.AddRange(
                hiddenNewest,
                visibleFirst,
                hiddenMiddle,
                visibleSecond);
            await setup.SaveChangesAsync();
            firstVisibleId = visibleFirst.Id;
            secondVisibleId = visibleSecond.Id;

            // Batch audit visibility is tied to the immutable version and its
            // flow roles, not to whether that version remains published later.
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
        string flowExternalId,
        DateTimeOffset updatedAt) =>
        new()
        {
            TargetWorkflowDefinitionId = definition.Id,
            WorkflowKey = definition.WorkflowKey,
            FlowExternalId = flowExternalId,
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
