using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class NodeExecutionSearchMatrixApiTests(PostgresApiFixture fixture)
{
    private const string ReaderRole = "NodeMatrixReader";

    [Fact]
    public async Task SearchFiltersMultiInstanceAndParallelCorrelations()
    {
        var seed = await SeedCorrelationRowsAsync();
        var prefix = $"/api/node-executions?workflowKey={seed.WorkflowKey}";

        var multiInstance = await SearchAsync(
            prefix
            + $"&multiInstanceExecutionId={seed.MultiInstanceExecutionId}"
            + "&executionKind=userTaskItem"
            + "&isMultiInstance=true"
            + "&sort=id:asc");

        Assert.Equal(2, multiInstance.TotalCount);
        Assert.Equal(
            new[] { seed.FirstMultiInstanceExecutionId, seed.SecondMultiInstanceExecutionId }
                .Order(),
            multiInstance.Items.Select(item => item.Id).Order());
        Assert.All(
            multiInstance.Items,
            item => Assert.Equal(seed.MultiInstanceExecutionId, item.MultiInstanceExecutionId));

        var item = await SearchAsync(
            prefix
            + $"&multiInstanceExecutionId={seed.MultiInstanceExecutionId}"
            + $"&userTaskId={seed.SecondUserTaskId}"
            + "&itemIndex=1");

        var selectedItem = Assert.Single(item.Items);
        Assert.Equal(seed.SecondMultiInstanceExecutionId, selectedItem.Id);
        Assert.Equal(1, selectedItem.ItemIndex);

        var aggregate = await SearchAsync(
            prefix + $"&aggregateFlowId={seed.AggregateFlowId}&sort=id:asc");

        Assert.Equal(2, aggregate.TotalCount);
        Assert.All(
            aggregate.Items,
            execution => Assert.Equal(seed.AggregateFlowId, execution.AggregateFlowId));
        Assert.Equal(
            new[] { seed.FirstMultiInstanceExecutionId, seed.SecondMultiInstanceExecutionId }
                .Order(),
            aggregate.Items.Select(item => item.Id).Order());

        var eitherSideOfBranch = await SearchAsync(
            prefix + $"&parallelBranchId={seed.MatchingBranchId}&sort=id:asc");

        Assert.Equal(2, eitherSideOfBranch.TotalCount);
        Assert.Equal(
            new[] { seed.EntryBranchExecutionId, seed.ExitBranchExecutionId }.Order(),
            eitherSideOfBranch.Items.Select(item => item.Id).Order());
        Assert.Contains(
            eitherSideOfBranch.Items,
            item => item.EntryParallelBranchId == seed.MatchingBranchId
                    && item.ExitParallelBranchId is null);
        Assert.Contains(
            eitherSideOfBranch.Items,
            item => item.EntryParallelBranchId is null
                    && item.ExitParallelBranchId == seed.MatchingBranchId);

        var otherBranch = await SearchAsync(
            prefix + $"&parallelBranchId={seed.OtherBranchId}");

        Assert.Equal(seed.OtherBranchExecutionId, Assert.Single(otherBranch.Items).Id);
    }

    [Fact]
    public async Task TimestampRangesUseInclusiveFromAndExclusiveToForEveryLifecycleTime()
    {
        var seed = await SeedCorrelationRowsAsync();
        var prefix = $"/api/node-executions?workflowKey={seed.WorkflowKey}";

        var ranges = new[]
        {
            (
                From: "createdFrom",
                To: "createdTo",
                Lower: seed.FirstCreatedAt,
                Upper: seed.SecondCreatedAt),
            (
                From: "startedFrom",
                To: "startedTo",
                Lower: seed.FirstStartedAt,
                Upper: seed.SecondStartedAt),
            (
                From: "updatedFrom",
                To: "updatedTo",
                Lower: seed.FirstUpdatedAt,
                Upper: seed.SecondUpdatedAt),
            (
                From: "completedFrom",
                To: "completedTo",
                Lower: seed.FirstCompletedAt,
                Upper: seed.SecondCompletedAt)
        };

        foreach (var range in ranges)
        {
            var result = await SearchAsync(
                prefix
                + $"&{range.From}={Encode(range.Lower)}"
                + $"&{range.To}={Encode(range.Upper)}"
                + "&sort=id:asc");

            Assert.Equal(seed.FirstMultiInstanceExecutionId, Assert.Single(result.Items).Id);
            Assert.Equal(1, result.TotalCount);
        }
    }

    private async Task<CorrelationSeed> SeedCorrelationRowsAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var workflowKey = $"node-matrix-{suffix}";
        var rawBasis = DateTimeOffset.UtcNow.AddHours(-6);
        var basis = new DateTimeOffset(
            rawBasis.Ticks - rawBasis.Ticks % TimeSpan.TicksPerMillisecond,
            TimeSpan.Zero);

        await using var db = fixture.CreateDbContext();
        var definition = new WorkflowDefinitionEntity
        {
            Name = "Node search matrix",
            WorkflowKey = workflowKey,
            Version = 1,
            IsPublished = true,
            Definition = new WorkflowModel
            {
                Id = workflowKey,
                Name = "Node search matrix",
                TaskAssignmentRoles = [ReaderRole],
                FlowNodes =
                [
                    new FlowNodeModel
                    {
                        Id = 20,
                        Name = "Parallel review",
                        Type = BpmnFlowNodeTypes.UserTask
                    }
                ]
            }
        };
        db.WorkflowDefinitions.Add(definition);
        await db.SaveChangesAsync();

        var instance = new WorkflowInstanceEntity
        {
            WorkflowDefinitionId = definition.Id,
            WorkflowKey = workflowKey,
            Status = WorkflowInstanceStatuses.Running,
            StartedBy = "matrix-seed",
            CreatedAt = basis,
            UpdatedAt = basis
        };
        db.WorkflowInstances.Add(instance);
        await db.SaveChangesAsync();

        var tokens = Enumerable.Range(0, 4)
            .Select(index => new ExecutionTokenEntity
            {
                InstanceId = instance.Id,
                NodeId = 20 + index,
                NodeName = $"Matrix position {index}",
                NodeType = index == 0
                    ? BpmnFlowNodeTypes.UserTask
                    : BpmnFlowNodeTypes.Task,
                Status = ExecutionTokenStatuses.Completed,
                CreatedAt = basis.AddMinutes(index),
                UpdatedAt = basis.AddMinutes(index)
            })
            .ToArray();
        db.ExecutionTokens.AddRange(tokens);
        await db.SaveChangesAsync();

        const int aggregateFlowId = 990;
        var multiInstance = new MultiInstanceExecutionEntity
        {
            InstanceId = instance.Id,
            TokenId = tokens[0].Id,
            NodeId = 20,
            Mode = "parallel",
            Source = "collection",
            ResultVariable = "reviews",
            Status = MultiInstanceExecutionStatuses.Completed,
            TotalCount = 2,
            CompletedCount = 2,
            WinningFlowId = aggregateFlowId,
            CompletionReason = "condition",
            CreatedAt = basis.AddMinutes(5),
            UpdatedAt = basis.AddMinutes(22),
            CompletedAt = basis.AddMinutes(22)
        };
        var parallelExecution = new ParallelGatewayExecutionEntity
        {
            InstanceId = instance.Id,
            ForkNodeId = 30,
            Status = ParallelGatewayExecutionStatuses.Completed,
            CompletionReason = "joined",
            CreatedAt = basis.AddMinutes(25),
            UpdatedAt = basis.AddMinutes(55),
            CompletedAt = basis.AddMinutes(55)
        };
        db.MultiInstanceExecutions.Add(multiInstance);
        db.ParallelGatewayExecutions.Add(parallelExecution);
        await db.SaveChangesAsync();

        var matchingBranch = new ParallelGatewayBranchEntity
        {
            ExecutionId = parallelExecution.Id,
            OriginatingFlowId = 300,
            Ordinal = 0,
            Status = ParallelGatewayBranchStatuses.Completed,
            CreatedAt = basis.AddMinutes(25),
            UpdatedAt = basis.AddMinutes(55),
            CompletedAt = basis.AddMinutes(55)
        };
        var otherBranch = new ParallelGatewayBranchEntity
        {
            ExecutionId = parallelExecution.Id,
            OriginatingFlowId = 301,
            Ordinal = 1,
            Status = ParallelGatewayBranchStatuses.Completed,
            CreatedAt = basis.AddMinutes(25),
            UpdatedAt = basis.AddMinutes(55),
            CompletedAt = basis.AddMinutes(55)
        };
        db.ParallelGatewayBranches.AddRange(matchingBranch, otherBranch);
        await db.SaveChangesAsync();

        var firstUserTask = CompletedMultiInstanceTask(
            instance.Id,
            tokens[0].Id,
            multiInstance.Id,
            itemIndex: 0,
            basis.AddMinutes(10),
            basis.AddMinutes(12));
        var secondUserTask = CompletedMultiInstanceTask(
            instance.Id,
            tokens[0].Id,
            multiInstance.Id,
            itemIndex: 1,
            basis.AddMinutes(20),
            basis.AddMinutes(22));
        db.UserTasks.AddRange(firstUserTask, secondUserTask);
        await db.SaveChangesAsync();

        var first = CompletedExecution(
            instance.Id,
            tokens[0].Id,
            nodeId: 20,
            "Review item zero",
            BpmnFlowNodeTypes.UserTask,
            basis.AddMinutes(10),
            basis.AddMinutes(11),
            basis.AddMinutes(12),
            NodeExecutionCompletionReasons.MultiInstanceItem);
        first.ExecutionKind = NodeExecutionKinds.UserTaskItem;
        first.UserTaskId = firstUserTask.Id;
        first.MultiInstanceExecutionId = multiInstance.Id;
        first.ItemIndex = 0;

        var second = CompletedExecution(
            instance.Id,
            tokens[0].Id,
            nodeId: 20,
            "Review item one",
            BpmnFlowNodeTypes.UserTask,
            basis.AddMinutes(20),
            basis.AddMinutes(21),
            basis.AddMinutes(22),
            NodeExecutionCompletionReasons.MultiInstanceItem);
        second.ExecutionKind = NodeExecutionKinds.UserTaskItem;
        second.UserTaskId = secondUserTask.Id;
        second.MultiInstanceExecutionId = multiInstance.Id;
        second.ItemIndex = 1;

        var entryMatch = CompletedExecution(
            instance.Id,
            tokens[1].Id,
            nodeId: 31,
            "Entered matching branch",
            BpmnFlowNodeTypes.Task,
            basis.AddMinutes(30),
            basis.AddMinutes(31),
            basis.AddMinutes(32),
            NodeExecutionCompletionReasons.Normal);
        entryMatch.EntryParallelBranchId = matchingBranch.Id;

        var exitMatch = CompletedExecution(
            instance.Id,
            tokens[2].Id,
            nodeId: 32,
            "Exited matching branch",
            BpmnFlowNodeTypes.Task,
            basis.AddMinutes(40),
            basis.AddMinutes(41),
            basis.AddMinutes(42),
            NodeExecutionCompletionReasons.Normal);
        exitMatch.ExitParallelBranchId = matchingBranch.Id;

        var branchDecoy = CompletedExecution(
            instance.Id,
            tokens[3].Id,
            nodeId: 33,
            "Other branch",
            BpmnFlowNodeTypes.Task,
            basis.AddMinutes(50),
            basis.AddMinutes(51),
            basis.AddMinutes(52),
            NodeExecutionCompletionReasons.Normal);
        branchDecoy.EntryParallelBranchId = otherBranch.Id;
        branchDecoy.ExitParallelBranchId = otherBranch.Id;

        db.NodeExecutions.AddRange(
            first,
            second,
            entryMatch,
            exitMatch,
            branchDecoy);
        await db.SaveChangesAsync();

        return new CorrelationSeed(
            workflowKey,
            multiInstance.Id,
            firstUserTask.Id,
            secondUserTask.Id,
            first.Id,
            second.Id,
            aggregateFlowId,
            matchingBranch.Id,
            otherBranch.Id,
            entryMatch.Id,
            exitMatch.Id,
            branchDecoy.Id,
            first.CreatedAt,
            second.CreatedAt,
            first.StartedAt!.Value,
            second.StartedAt!.Value,
            first.UpdatedAt,
            second.UpdatedAt,
            first.CompletedAt!.Value,
            second.CompletedAt!.Value);
    }

    private static UserTaskEntity CompletedMultiInstanceTask(
        long instanceId,
        long tokenId,
        long multiInstanceExecutionId,
        int itemIndex,
        DateTimeOffset createdAt,
        DateTimeOffset completedAt) =>
        new()
        {
            InstanceId = instanceId,
            TokenId = tokenId,
            MultiInstanceExecutionId = multiInstanceExecutionId,
            ItemIndex = itemIndex,
            NodeId = 20,
            NodeName = $"Review item {itemIndex}",
            Roles = ["Reviewer"],
            Status = UserTaskStatuses.Completed,
            SelectedFlowId = 800 + itemIndex,
            ResultJson = JsonDocument.Parse($$"""{"item":{{itemIndex}}}"""),
            CompletedBy = $"reviewer-{itemIndex}",
            CompletedByRoles = ["Reviewer"],
            CreatedAt = createdAt,
            UpdatedAt = completedAt,
            CompletedAt = completedAt
        };

    private static NodeExecutionEntity CompletedExecution(
        long instanceId,
        long tokenId,
        int nodeId,
        string nodeName,
        string nodeType,
        DateTimeOffset createdAt,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        string completionReason) =>
        new()
        {
            InstanceId = instanceId,
            ExecutionTokenId = tokenId,
            NodeId = nodeId,
            NodeName = nodeName,
            NodeType = nodeType,
            ExecutionKind = NodeExecutionKinds.Node,
            Status = NodeExecutionStatuses.Completed,
            CompletionReason = completionReason,
            CreatedAt = createdAt,
            StartedAt = startedAt,
            UpdatedAt = completedAt,
            CompletedAt = completedAt
        };

    private async Task<PagedResult<NodeExecutionSummaryDto>> SearchAsync(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        ApiTestAuth.Authorize(request, "matrix-reader", [ReaderRole]);
        request.Headers.TryAddWithoutValidation("X-Test-Suppress-Admin", "true");

        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content
            .ReadFromJsonAsync<PagedResult<NodeExecutionSummaryDto>>())!;
    }

    private static string Encode(DateTimeOffset value) =>
        Uri.EscapeDataString(value.ToString("O"));

    private sealed record CorrelationSeed(
        string WorkflowKey,
        long MultiInstanceExecutionId,
        long FirstUserTaskId,
        long SecondUserTaskId,
        long FirstMultiInstanceExecutionId,
        long SecondMultiInstanceExecutionId,
        int AggregateFlowId,
        long MatchingBranchId,
        long OtherBranchId,
        long EntryBranchExecutionId,
        long ExitBranchExecutionId,
        long OtherBranchExecutionId,
        DateTimeOffset FirstCreatedAt,
        DateTimeOffset SecondCreatedAt,
        DateTimeOffset FirstStartedAt,
        DateTimeOffset SecondStartedAt,
        DateTimeOffset FirstUpdatedAt,
        DateTimeOffset SecondUpdatedAt,
        DateTimeOffset FirstCompletedAt,
        DateTimeOffset SecondCompletedAt);
}
