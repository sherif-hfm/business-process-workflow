using System.Diagnostics;
using System.Net;
using System.Text.Json;
using WorkflowEngine.Shared.Dtos;
using WorkflowEngine.Shared.Models;

sealed partial class MultiInstanceApiSuite
{
    private async Task CardinalityBoundariesAsync(CaseScope scope)
    {
        var cardinalityId = RequireWorkflow(_state.CardinalityWorkflowId, "cardinality");
        await ExpectCardinalityEntryFailureAsync(scope, cardinalityId, "zero", JsonSerializer.SerializeToElement(0));
        await ExpectCardinalityEntryFailureAsync(scope, cardinalityId, "negative", JsonSerializer.SerializeToElement(-1));
        await ExpectCardinalityEntryFailureAsync(scope, cardinalityId, "decimal", JsonSerializer.SerializeToElement(1.5));
        await ExpectCardinalityEntryFailureAsync(scope,
            RequireWorkflow(_missingCardinalityWorkflowId, "missing-cardinality"), "missing", null);
        await ExpectCardinalityEntryFailureAsync(scope,
            RequireWorkflow(_loadWorkflowId, "load"), "over-maximum", JsonSerializer.SerializeToElement(1001));

        _thousandScenario = await StartAndEnterAsync(scope,
            RequireWorkflow(_loadWorkflowId, "load"), "CAR-004-maximum",
            new Dictionary<string, JsonElement> { ["voters"] = JsonSerializer.SerializeToElement(1000) });
        var maximum = RequireProgress(_thousandScenario.Detail.MultiInstance, scope);
        AssertProgress(scope, maximum, "parallel", "active", 1000, 0, 1000, 0, 0);
        scope.Equal(1000, maximum.Total, "Configured maximum cardinality is accepted");
    }

    private async Task ExpectCardinalityEntryFailureAsync(
        CaseScope scope,
        long workflowId,
        string label,
        JsonElement? cardinality)
    {
        var variables = cardinality is null
            ? null
            : new Dictionary<string, JsonElement> { ["voters"] = cardinality.Value };
        var review = await StartAtReviewAsync(scope, workflowId, $"CAR-004-{label}", variables);
        var response = await scope.SendAsync(Manager, HttpMethod.Post,
            $"/api/instances/{review.Id}/flows/204", EmptyVariables());
        scope.ExpectStatus(response, HttpStatusCode.BadRequest);
        var after = await GetInstanceAsync(scope, review.Id, Admin);
        scope.Equal(5, after.CurrentNodeId, $"{label} cardinality rollback leaves review active");
        scope.True(after.MultiInstance is null,
            $"{label} cardinality creates no active execution", "multiInstance was present");
    }

    private async Task CardinalityClaimsAsync(CaseScope scope)
    {
        var scenario = await StartAndEnterAsync(scope,
            RequireWorkflow(_claimsWorkflowId, "one-per-actor-claims"), "CAR-005",
            new Dictionary<string, JsonElement> { ["voters"] = JsonSerializer.SerializeToElement(3) });
        var tasks = (await GetTaskPageAsync(scope, Actor("ClaimUser"), scenario.InstanceId, "active")).Items
            .OrderBy(t => t.ItemIndex).ToList();
        scope.Equal(3, tasks.Count, "Claim scenario creates three active tasks");
        scope.True(tasks.All(t => t.RequiresClaim), "Every cardinality task requires claim",
            "At least one task did not require claim");

        var claimRace = await Task.WhenAll(
            scope.SendAsync(Actor("ClaimUser"), HttpMethod.Post, $"/api/user-tasks/{tasks[0].Id}/claim"),
            scope.SendAsync(Actor("claimuser"), HttpMethod.Post, $"/api/user-tasks/{tasks[1].Id}/claim"));
        scope.Equal(1, claimRace.Count(r => r.StatusCode == HttpStatusCode.OK),
            "Case-insensitive actor race claims one item");
        scope.Equal(1, claimRace.Count(r => r.StatusCode == HttpStatusCode.Conflict),
            "Case-insensitive actor cannot claim a second item in the execution");
        var claimed = scope.Deserialize<UserTaskDto>(claimRace.Single(r => r.StatusCode == HttpStatusCode.OK));
        scope.True(string.Equals(claimed.ClaimedBy, "ClaimUser", StringComparison.OrdinalIgnoreCase),
            "Claim owner is the racing actor", claimed.ClaimedBy ?? "null");

        var remaining = tasks.Where(t => t.Id != claimed.Id).ToList();
        var unclaimedAction = await scope.SendAsync(Actor("voter2"), HttpMethod.Post,
            $"/api/user-tasks/{remaining[0].Id}/flows/201", EmptyVariables());
        scope.ExpectStatus(unclaimedAction, HttpStatusCode.BadRequest);

        await CompleteTaskAsync(scope, Actor("ClaimUser"), claimed.Id, 201, EmptyVariables());
        scope.Equal(0L, (await GetInboxAsync(scope, Actor("claimuser"), scenario.InstanceId)).TotalCount,
            "onePerActor removes the completed actor from the inbox");

        var claimSecond = await scope.SendAsync(Actor("voter2"), HttpMethod.Post,
            $"/api/user-tasks/{remaining[0].Id}/claim");
        scope.ExpectStatus(claimSecond, HttpStatusCode.OK);
        var unclaimSecond = await scope.SendAsync(Actor("voter2"), HttpMethod.Post,
            $"/api/user-tasks/{remaining[0].Id}/unclaim");
        scope.ExpectStatus(unclaimSecond, HttpStatusCode.OK);
        scope.True(scope.Deserialize<UserTaskDto>(unclaimSecond).ClaimedBy is null,
            "Unclaim releases the cardinality work item", "Claim remained assigned");
        var reclaimSecond = await scope.SendAsync(Actor("voter2"), HttpMethod.Post,
            $"/api/user-tasks/{remaining[0].Id}/claim");
        scope.ExpectStatus(reclaimSecond, HttpStatusCode.OK);
        await CompleteTaskAsync(scope, Actor("voter2"), remaining[0].Id, 201, EmptyVariables());

        var claimThird = await scope.SendAsync(Actor("voter3"), HttpMethod.Post,
            $"/api/user-tasks/{remaining[1].Id}/claim");
        scope.ExpectStatus(claimThird, HttpStatusCode.OK);
        var final = await CompleteTaskAsync(scope, Actor("voter3"), remaining[1].Id, 205, EmptyVariables());
        var progress = RequireProgress(final.MultiInstance, scope);
        scope.Equal(201, progress.WinningFlowId, "Two claimed approvals win after all three actors complete");
        scope.Equal(2, progress.FlowCounts.Single(f => f.FlowId == 201).Count, "Claimed approval count is two");
        scope.Equal(1, progress.FlowCounts.Single(f => f.FlowId == 205).Count, "Claimed reject count is one");
    }

    private async Task MultiInstanceContextAndVariablesAsync(CaseScope scope)
    {
        var scenario = await StartAndEnterAsync(scope,
            RequireWorkflow(_contextWorkflowId, "mi-context-variables"), "CTX-001");
        var tasks = await GetAssignedCollectionTasksAsync(scope, scenario.InstanceId,
            ["alice", "bob", "carol", "dave", "erin"]);
        var alice = tasks.Single(t => t.ActorName == "alice");
        var flowResponse = await scope.SendAsync(Actor("alice"), HttpMethod.Get,
            $"/api/user-tasks/{alice.TaskId}/flows");
        scope.ExpectStatus(flowResponse, HttpStatusCode.OK);
        scope.True(scope.Deserialize<List<SequenceFlowModel>>(flowResponse).Any(f => f.Id == 201),
            "Flow condition can read mi.index, mi.item, mi.total, mi.completed, and mi.remaining",
            flowResponse.Body);

        var missingScore = await scope.SendAsync(Actor("alice"), HttpMethod.Post,
            $"/api/user-tasks/{alice.TaskId}/flows/201",
            new TakeFlowRequest(new Dictionary<string, JsonElement>
            {
                ["vote"] = JsonSerializer.SerializeToElement("approve")
            }));
        scope.ExpectStatus(missingScore, HttpStatusCode.BadRequest);
        var badType = await scope.SendAsync(Actor("alice"), HttpMethod.Post,
            $"/api/user-tasks/{alice.TaskId}/flows/201",
            ContextVariables("approve", JsonSerializer.SerializeToElement("not-a-number")));
        scope.ExpectStatus(badType, HttpStatusCode.BadRequest);
        var badValidation = await scope.SendAsync(Actor("alice"), HttpMethod.Post,
            $"/api/user-tasks/{alice.TaskId}/flows/201",
            ContextVariables("x", JsonSerializer.SerializeToElement(1)));
        scope.ExpectStatus(badValidation, HttpStatusCode.BadRequest);

        var unchanged = await GetUserTaskAsync(scope, Actor("alice"), alice.TaskId);
        scope.Equal("active", unchanged.Status, "Invalid submissions leave the child task active");
        scope.True(unchanged.SelectedFlowId is null, "Invalid submissions do not select a flow",
            unchanged.SelectedFlowId?.ToString() ?? "null");
        AssertProgress(scope, RequireProgress(unchanged.MultiInstance, scope),
            "parallel", "active", 5, 0, 5, 0, 0);

        await CompleteTaskAsync(scope, Actor("alice"), alice.TaskId, 201,
            ContextVariables("approve", JsonSerializer.SerializeToElement(2)));
        await CompleteTaskAsync(scope, Actor("bob"), tasks.Single(t => t.ActorName == "bob").TaskId, 201,
            ContextVariables("approve", JsonSerializer.SerializeToElement(3)));
        var final = await CompleteTaskAsync(scope, Actor("carol"), tasks.Single(t => t.ActorName == "carol").TaskId, 201,
            ContextVariables("approve", JsonSerializer.SerializeToElement(4)));
        scope.Equal("completed", final.InstanceStatus, "Valid context-aware submissions reach quorum");
        var detail = await GetInstanceAsync(scope, scenario.InstanceId, Admin);
        var aliceResult = LatestResult(scope, detail, "voteResults").Single(r => r.Index == 0);
        scope.True(aliceResult.Variables is not null
                   && aliceResult.Variables["vote"].GetString() == "approve"
                   && aliceResult.Variables["score"].GetInt32() == 2,
            "Only the valid typed payload is persisted in the item result",
            JsonSerializer.Serialize(aliceResult.Variables));
    }

    private static TakeFlowRequest ContextVariables(string vote, JsonElement score) =>
        new(new Dictionary<string, JsonElement>
        {
            ["vote"] = JsonSerializer.SerializeToElement(vote),
            ["score"] = score
        });

    private async Task RestartRecoveryAsync(CaseScope scope)
    {
        if (_restartApi is null)
            throw new SkipTestException("Run with --manage-api to execute a real API restart.");

        var parallel = await StartAndEnterAsync(scope,
            RequireWorkflow(_state.ParallelWorkflowId, "parallel"), "REC-001-parallel");
        var parallelTasks = await GetAssignedCollectionTasksAsync(scope, parallel.InstanceId,
            ["alice", "bob", "carol", "dave", "erin"]);
        await CompleteTaskAsync(scope, Actor("alice"), parallelTasks[0].TaskId, 201, VoteVariables("before-restart"));

        var sequential = await StartAndEnterAsync(scope,
            RequireWorkflow(_state.SequentialWorkflowId, "sequential"), "REC-001-sequential");
        var sequentialTasks = (await GetAssignedTaskRecordsAsync(scope, sequential.InstanceId,
            ["alice", "alice1", "alice2"])).OrderBy(t => t.Task.ItemIndex).ToList();
        await CompleteTaskAsync(scope, Actor("alice"), sequentialTasks[0].Task.Id, 201, EmptyVariables());

        await _restartApi();
        _state.Identifiers["api.restart.recovered"] = "true";

        var parallelAfter = await GetInstanceAsync(scope, parallel.InstanceId, Admin);
        var parallelProgress = RequireProgress(parallelAfter.MultiInstance, scope);
        scope.Equal(parallel.ExecutionId, parallelProgress.ExecutionId,
            "Parallel execution ID survives API restart");
        AssertProgress(scope, parallelProgress, "parallel", "active", 5, 1, 4, 0, 0);

        var sequentialAfter = await GetInstanceAsync(scope, sequential.InstanceId, Admin);
        var sequentialProgress = RequireProgress(sequentialAfter.MultiInstance, scope);
        scope.Equal(sequential.ExecutionId, sequentialProgress.ExecutionId,
            "Sequential execution ID survives API restart");
        AssertProgress(scope, sequentialProgress, "sequential", "active", 3, 1, 1, 1, 0);
        scope.Equal("active", (await GetUserTaskAsync(scope, Actor("alice1"), sequentialTasks[1].Task.Id)).Status,
            "The correct next sequential task remains active after restart");

        await CompleteTaskAsync(scope, Actor("bob"), parallelTasks[1].TaskId, 201, VoteVariables("after-restart"));
        var parallelFinal = await CompleteTaskAsync(scope, Actor("carol"), parallelTasks[2].TaskId, 201,
            VoteVariables("after-restart"));
        scope.Equal("completed", parallelFinal.InstanceStatus, "Recovered parallel execution can reach quorum");
        await CompleteTaskAsync(scope, Actor("alice1"), sequentialTasks[1].Task.Id, 201, EmptyVariables());
        var sequentialFinal = await CompleteTaskAsync(scope, Actor("alice2"), sequentialTasks[2].Task.Id, 201, EmptyVariables());
        scope.Equal("completed", sequentialFinal.InstanceStatus, "Recovered sequential execution can finish in order");
    }

    private async Task ThousandItemLoadAsync(CaseScope scope)
    {
        var scenario = _thousandScenario
            ?? throw new SkipTestException("The maximum-cardinality setup did not create the 1,000-item execution.");
        var workflowId = RequireWorkflow(_loadWorkflowId, "load");
        var allTaskIds = new List<long>(1000);
        for (var page = 1; page <= 5; page++)
        {
            var response = await scope.SendAsync(Actor("loaduser"), HttpMethod.Get,
                $"/api/instances/{scenario.InstanceId}/user-tasks?status=active&page={page}&pageSize=200");
            scope.ExpectStatus(response, HttpStatusCode.OK);
            var result = scope.Deserialize<PagedResult<UserTaskDto>>(response);
            scope.Equal(1000L, result.TotalCount, $"Task page {page} reports the full 1,000-item total");
            scope.Equal(200, result.Items.Count, $"Task page {page} contains 200 items");
            allTaskIds.AddRange(result.Items.Select(t => t.Id));
        }
        scope.Equal(1000, allTaskIds.Distinct().Count(), "Five task pages contain 1,000 distinct IDs");

        var inboxPage1Response = await scope.SendAsync(Actor("loaduser"), HttpMethod.Get,
            $"/api/instances/inbox?instanceId={scenario.InstanceId}&workflowId={workflowId}&nodeId=2&page=1&pageSize=200");
        scope.ExpectStatus(inboxPage1Response, HttpStatusCode.OK);
        var inboxPage1 = scope.Deserialize<PagedResult<InboxItemDto>>(inboxPage1Response);
        scope.Equal(1000L, inboxPage1.TotalCount, "Filtered inbox reports 1,000 eligible tasks");
        scope.Equal(200, inboxPage1.Items.Count, "Filtered inbox honors page size 200");
        var inboxPage2Response = await scope.SendAsync(Actor("loaduser"), HttpMethod.Get,
            $"/api/instances/inbox?instanceId={scenario.InstanceId}&workflowId={workflowId}&nodeId=2&page=2&pageSize=200");
        scope.ExpectStatus(inboxPage2Response, HttpStatusCode.OK);
        var inboxPage2 = scope.Deserialize<PagedResult<InboxItemDto>>(inboxPage2Response);
        scope.Equal(0, inboxPage1.Items.Select(i => i.UserTaskId)
            .Intersect(inboxPage2.Items.Select(i => i.UserTaskId)).Count(),
            "Adjacent inbox pages do not overlap");

        using var gate = new SemaphoreSlim(64);
        var stopwatch = Stopwatch.StartNew();
        var responses = await Task.WhenAll(allTaskIds.Select(async taskId =>
        {
            await gate.WaitAsync();
            try
            {
                return await scope.SendAsync(Actor("loaduser"), HttpMethod.Post,
                    $"/api/user-tasks/{taskId}/flows/201", EmptyVariables());
            }
            finally
            {
                gate.Release();
            }
        }));
        stopwatch.Stop();
        scope.Equal(1000, responses.Count(r => r.StatusCode == HttpStatusCode.OK),
            "All 1,000 concurrent item actions succeed");
        var closing = responses.Where(r => r.StatusCode == HttpStatusCode.OK)
            .Select(scope.Deserialize<UserTaskActionAckDto>)
            .Where(a => a.InstanceStatus == "completed").ToList();
        scope.Equal(1, closing.Count, "The 1,000-item execution advances its parent exactly once");
        var finalProgress = RequireProgress(closing.Single().MultiInstance, scope);
        AssertProgress(scope, finalProgress, "parallel", "completed", 1000, 1000, 0, 0, 0);
        scope.Equal(201, finalProgress.WinningFlowId, "The load execution selects flow 201 after all items");
        var rate = 1000d / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
        _state.Identifiers["load.1000.durationSeconds"] = stopwatch.Elapsed.TotalSeconds.ToString("F3");
        _state.Identifiers["load.1000.itemsPerSecond"] = rate.ToString("F2");

        var detail = await GetInstanceAsync(scope, scenario.InstanceId, Admin);
        scope.Equal("completed", detail.Status, "The persisted 1,000-item instance is completed");
        AssertOrderedIndexes(scope, LatestResult(scope, detail, "voteResult"), 1000);
    }
}
