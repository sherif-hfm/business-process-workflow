using System.Net;
using System.Text.Json;
using WorkflowEngine.Shared.Dtos;
using WorkflowEngine.Shared.Models;

sealed partial class MultiInstanceApiSuite
{
    private long? _sequentialEarlyWorkflowId;
    private long? _sequentialAfterAllWorkflowId;
    private long? _priorityWorkflowId;
    private long? _parallelDefaultWorkflowId;
    private long? _duplicateWorkflowId;
    private long? _cancelWorkflowId;
    private long? _claimsWorkflowId;
    private long? _contextWorkflowId;
    private long? _loadWorkflowId;
    private long? _missingCardinalityWorkflowId;
    private MiScenario? _thousandScenario;

    private async Task CreateAdditionalDefinitionsAsync(CaseScope scope)
    {
        RequireApi();
        if (_parallelTemplate is null || _sequentialTemplate is null || _cardinalityTemplate is null)
            throw new SkipTestException("Base workflow fixture setup did not complete.");

        var sequentialEarly = Clone(_sequentialTemplate);
        SetProcessDefault(sequentialEarly, "voters", new[] { "seq0", "seq1", "seq2", "seq3", "seq4" });
        SetProcessDefault(sequentialEarly, "requiredApprovals", 2);
        sequentialEarly.FlowNodes.Single(n => n.MultiInstance is not null).MultiInstance!.CompletionEvaluation =
            MultiInstanceCompletionEvaluations.AfterEach;
        _sequentialEarlyWorkflowId = await CreateWorkflowAsync(scope, sequentialEarly, "sequential-early");

        var sequentialAfterAll = Clone(sequentialEarly);
        sequentialAfterAll.FlowNodes.Single(n => n.MultiInstance is not null).MultiInstance!.CompletionEvaluation =
            MultiInstanceCompletionEvaluations.AfterAll;
        _sequentialAfterAllWorkflowId = await CreateWorkflowAsync(scope, sequentialAfterAll, "sequential-after-all");

        var priority = Clone(_cardinalityTemplate);
        priority.FlowNodes.Single(n => n.MultiInstance is not null).MultiInstance!.OnePerActor = false;
        var priorityApprove = priority.SequenceFlows.Single(f => f.Id == 201);
        var priorityReject = priority.SequenceFlows.Single(f => f.Id == 205);
        priorityApprove.CompletionCondition = "[mi.completed] == [mi.total]";
        priorityApprove.CompletionPriority = 20;
        priorityReject.CompletionCondition = "[mi.completed] == [mi.total]";
        priorityReject.CompletionPriority = 10;
        _priorityWorkflowId = await CreateWorkflowAsync(scope, priority, "completion-priority");

        var parallelDefault = Clone(_parallelTemplate);
        SetProcessDefault(parallelDefault, "requiredApprovals", 6);
        _parallelDefaultWorkflowId = await CreateWorkflowAsync(scope, parallelDefault, "parallel-default");

        var duplicate = Clone(_parallelTemplate);
        SetProcessDefault(duplicate, "voters", new[] { "dupe", "DUPE", "dupe" });
        SetProcessDefault(duplicate, "requiredApprovals", 3);
        _duplicateWorkflowId = await CreateWorkflowAsync(scope, duplicate, "duplicate-users");

        var cancellable = Clone(_parallelTemplate);
        cancellable.CancelRoles = ["Manager"];
        _cancelWorkflowId = await CreateWorkflowAsync(scope, cancellable, "cancel-race");

        var claims = Clone(_cardinalityTemplate);
        claims.FlowNodes.Single(n => n.MultiInstance is not null).RequiresClaim = true;
        claims.FlowNodes.Single(n => n.MultiInstance is not null).MultiInstance!.OnePerActor = true;
        _claimsWorkflowId = await CreateWorkflowAsync(scope, claims, "one-per-actor-claims");

        var context = Clone(_parallelTemplate);
        var contextFlow = context.SequenceFlows.Single(f => f.Id == 201);
        contextFlow.Condition = "[mi.index] >= 0 and not IsNullOrEmpty([mi.item]) and [mi.total] == 5 and [mi.completed] >= 0 and [mi.remaining] > 0";
        contextFlow.Variables.Single(v => v.Name == "vote").Validation = "Length(vote) >= 3";
        contextFlow.Variables.Add(new VariableModel
        {
            Id = 2,
            Name = "score",
            DataType = WorkflowVariableTypes.Number,
            Required = true,
            Validation = "score >= 1"
        });
        _contextWorkflowId = await CreateWorkflowAsync(scope, context, "mi-context-variables");

        var load = Clone(_cardinalityTemplate);
        var loadMulti = load.FlowNodes.Single(n => n.MultiInstance is not null).MultiInstance!;
        loadMulti.OnePerActor = false;
        loadMulti.CompletionEvaluation = MultiInstanceCompletionEvaluations.AfterAll;
        load.SequenceFlows.Single(f => f.Id == 201).CompletionCondition = "CountFlow(201) == [mi.total]";
        _loadWorkflowId = await CreateWorkflowAsync(scope, load, "load-1000");

        var missingCardinality = Clone(_cardinalityTemplate);
        var voters = missingCardinality.FlowNodes.Single(n => n.Type == BpmnFlowNodeTypes.StartEvent)
            .Variables.Single(v => v.Name == "voters");
        voters.DefaultValue = null;
        voters.Required = false;
        _missingCardinalityWorkflowId = await CreateWorkflowAsync(scope, missingCardinality, "cardinality-missing");

        scope.True(new long?[]
            {
                _sequentialEarlyWorkflowId, _sequentialAfterAllWorkflowId, _priorityWorkflowId,
                _parallelDefaultWorkflowId, _duplicateWorkflowId, _cancelWorkflowId,
                _claimsWorkflowId, _contextWorkflowId, _loadWorkflowId, _missingCardinalityWorkflowId
            }.All(id => id is > 0),
            "All ten extended workflows are created and published", "One or more workflow IDs were missing");
    }

    private async Task AuthenticationBoundariesAsync(CaseScope scope)
    {
        RequireApi();
        foreach (var actorName in new[] { "missing-token", "malformed-token", "expired-token" })
        {
            var response = await scope.SendAsync(Actor(actorName), HttpMethod.Get, "/api/instances?page=1&pageSize=1");
            scope.ExpectStatus(response, HttpStatusCode.Unauthorized);
        }

        var noRoleInstances = await scope.SendAsync(Actor("no-role"), HttpMethod.Get,
            "/api/instances?page=1&pageSize=1");
        scope.ExpectStatus(noRoleInstances, HttpStatusCode.OK);
        var noRoleDefinitions = await scope.SendAsync(Actor("no-role"), HttpMethod.Get, "/api/workflows");
        scope.ExpectStatus(noRoleDefinitions, HttpStatusCode.Forbidden);
        var ordinaryDefinitions = await scope.SendAsync(Actor("alice"), HttpMethod.Get, "/api/workflows");
        scope.ExpectStatus(ordinaryDefinitions, HttpStatusCode.Forbidden);
    }

    private async Task InvalidCollectionMembersAsync(CaseScope scope)
    {
        RequireApi();
        if (_parallelTemplate is null) throw new SkipTestException("Parallel template was not loaded.");
        var invalidValues = new (string Label, JsonElement Value)[]
        {
            ("null", JsonSerializer.SerializeToElement(new object?[] { null })),
            ("empty", JsonSerializer.SerializeToElement(new[] { "" })),
            ("number", JsonSerializer.SerializeToElement(new object[] { 123 })),
            ("object", JsonSerializer.SerializeToElement(new object[] { new { name = "alice" } }))
        };

        foreach (var invalid in invalidValues)
        {
            var model = Clone(_parallelTemplate);
            model.Variables.Single(v => v.Name == "voters").DefaultValue = invalid.Value;
            var workflowId = await CreateWorkflowAsync(scope, model, $"invalid-collection-{invalid.Label}");
            var review = await StartAtReviewAsync(scope, workflowId, $"COL-001-{invalid.Label}");
            var enter = await scope.SendAsync(Manager, HttpMethod.Post,
                $"/api/instances/{review.Id}/flows/204", EmptyVariables());
            scope.ExpectStatus(enter, HttpStatusCode.BadRequest);
            scope.True(enter.Body.Contains("empty or non-string username", StringComparison.OrdinalIgnoreCase),
                $"{invalid.Label} collection member reports the username validation error", enter.Body);
            var after = await GetInstanceAsync(scope, review.Id, Admin);
            scope.Equal(5, after.CurrentNodeId, $"{invalid.Label} member rollback leaves the instance on review");
            scope.True(after.MultiInstance is null,
                $"{invalid.Label} member creates no active multi-instance execution", "multiInstance was present");
        }
    }

    private async Task DuplicateCollectionUsersAsync(CaseScope scope)
    {
        var scenario = await StartAndEnterAsync(scope,
            RequireWorkflow(_duplicateWorkflowId, "duplicate-user"), "COL-002");
        var inbox = await GetInboxAsync(scope, Actor("dupe"), scenario.InstanceId);
        scope.Equal(3L, inbox.TotalCount,
            "A case-insensitive matching actor sees all three duplicate-assigned items");
        scope.SequenceEqual(["dupe", "DUPE", "dupe"], inbox.Items.OrderBy(i => i.ItemIndex)
            .Select(i => i.ItemValue!.Value.GetString()!).ToArray(),
            "Collection snapshots preserve authored username casing and duplicates");
        scope.Equal(3, inbox.Items.Select(i => i.UserTaskId).Distinct().Count(),
            "Duplicate usernames still receive distinct user-task IDs");

        UserTaskActionAckDto? final = null;
        foreach (var item in inbox.Items.OrderBy(i => i.ItemIndex))
            final = await CompleteTaskAsync(scope, Actor("dupe"), item.UserTaskId, 201,
                VoteVariables($"duplicate-{item.ItemIndex}"));
        scope.Equal("completed", final!.InstanceStatus,
            "The same actor can complete all collection-assigned duplicates");
        var detail = await GetInstanceAsync(scope, scenario.InstanceId, Admin);
        var result = LatestResult(scope, detail, "voteResults");
        scope.Equal(3, result.Count, "Duplicate-user result contains all three items");
        scope.True(result.All(r => r.CompletedBy == "dupe"),
            "Duplicate items record the completing actor consistently",
            string.Join(",", result.Select(r => r.CompletedBy)));
    }

    private async Task ParallelDefaultAfterAllItemsAsync(CaseScope scope)
    {
        var scenario = await StartAndEnterAsync(scope,
            RequireWorkflow(_parallelDefaultWorkflowId, "parallel-default"), "PAR-005");
        var tasks = await GetAssignedCollectionTasksAsync(scope, scenario.InstanceId,
            ["alice", "bob", "carol", "dave", "erin"]);
        UserTaskActionAckDto? final = null;
        foreach (var task in tasks)
            final = await CompleteTaskAsync(scope, Actor(task.ActorName), task.TaskId, 201,
                VoteVariables("no-quorum"));
        var progress = RequireProgress(final!.MultiInstance, scope);
        scope.Equal("completed", final.InstanceStatus, "All parallel items close through the default");
        scope.Equal(205, progress.WinningFlowId, "Engine-only default 205 wins when quorum is impossible");
        scope.Equal("all", progress.CompletionReason, "Default route records all-items completion");
        AssertProgress(scope, progress, "parallel", "completed", 5, 5, 0, 0, 0);
        scope.Equal(5, progress.FlowCounts.Single(f => f.FlowId == 201).Count,
            "All five user selections remain counted on flow 201");
    }

    private async Task CompletionVersusInterruptAsync(CaseScope scope)
    {
        var scenario = await StartAndEnterAsync(scope,
            RequireWorkflow(_state.ParallelWorkflowId, "parallel"), "RACE-001");
        var tasks = await GetAssignedCollectionTasksAsync(scope, scenario.InstanceId,
            ["alice", "bob", "carol", "dave", "erin"]);
        await CompleteTaskAsync(scope, Actor("alice"), tasks[0].TaskId, 201, VoteVariables("approve"));
        await CompleteTaskAsync(scope, Actor("bob"), tasks[1].TaskId, 201, VoteVariables("approve"));

        var completionTask = scope.SendAsync(Actor("carol"), HttpMethod.Post,
            $"/api/user-tasks/{tasks[2].TaskId}/flows/201", VoteVariables("approve"));
        var interruptTask = scope.SendAsync(Manager, HttpMethod.Post,
            $"/api/multi-instance-executions/{scenario.ExecutionId}/flows/203", EmptyVariables());
        await Task.WhenAll(completionTask, interruptTask);
        var completion = await completionTask;
        var interrupt = await interruptTask;
        scope.Equal(1, new[] { completion, interrupt }.Count(r => r.StatusCode == HttpStatusCode.OK),
            "Exactly one competing parent route succeeds");
        scope.Equal(1, new[] { completion, interrupt }.Count(r => r.StatusCode == HttpStatusCode.Conflict),
            "The losing parent route reports a stale conflict");

        var detail = await GetInstanceAsync(scope, scenario.InstanceId, Admin);
        var taskDetails = await GetAssignedTaskDetailsAsync(scope, tasks);
        var progress = RequireProgress(taskDetails[0].MultiInstance, scope);
        if (completion.StatusCode == HttpStatusCode.OK)
        {
            scope.Equal("completed", detail.Status, "Completion winner completes the instance");
            scope.Equal(201, progress.WinningFlowId, "Completion winner records outcome 201");
            AssertProgress(scope, progress, "parallel", "completed", 5, 3, 0, 0, 2);
        }
        else
        {
            scope.Equal("running", detail.Status, "Interrupt winner leaves the instance on review");
            scope.Equal(5, detail.CurrentNodeId, "Interrupt winner routes to review");
            scope.Equal(203, progress.WinningFlowId, "Interrupt winner records flow 203");
            AssertProgress(scope, progress, "parallel", "interrupted", 5, 2, 0, 0, 3);
        }
        var terminalHistory = detail.History.Count(h => h.MultiInstanceExecutionId == scenario.ExecutionId
                                                        && h.Note is "multiInstanceComplete" or "multiInstanceInterrupt");
        scope.Equal(1, terminalHistory, "Completion/interrupt race advances the parent exactly once");
    }

    private async Task CompletionVersusCancellationAsync(CaseScope scope)
    {
        var scenario = await StartAndEnterAsync(scope,
            RequireWorkflow(_cancelWorkflowId, "cancel-race"), "RACE-002");
        var tasks = await GetAssignedCollectionTasksAsync(scope, scenario.InstanceId,
            ["alice", "bob", "carol", "dave", "erin"]);
        await CompleteTaskAsync(scope, Actor("alice"), tasks[0].TaskId, 201, VoteVariables("approve"));
        await CompleteTaskAsync(scope, Actor("bob"), tasks[1].TaskId, 201, VoteVariables("approve"));

        var completionTask = scope.SendAsync(Actor("carol"), HttpMethod.Post,
            $"/api/user-tasks/{tasks[2].TaskId}/flows/201", VoteVariables("approve"));
        var cancelTask = scope.SendAsync(Manager, HttpMethod.Post,
            $"/api/instances/{scenario.InstanceId}/cancel");
        await Task.WhenAll(completionTask, cancelTask);
        var completion = await completionTask;
        var cancel = await cancelTask;

        var cancellationWon = cancel.StatusCode == HttpStatusCode.NoContent;
        if (cancellationWon)
        {
            scope.ExpectStatus(completion, HttpStatusCode.Conflict);
        }
        else
        {
            scope.ExpectStatus(completion, HttpStatusCode.OK);
            scope.True(cancel.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict,
                "A cancellation that loses to completion reports a stale/invalid operation",
                $"HTTP {(int)cancel.StatusCode} {cancel.StatusCode}: {cancel.Body}");
        }

        var detail = await GetInstanceAsync(scope, scenario.InstanceId, Admin);
        var taskDetails = await GetAssignedTaskDetailsAsync(scope, tasks);
        var progress = RequireProgress(taskDetails[0].MultiInstance, scope);
        if (cancellationWon)
        {
            scope.Equal("cancelled", detail.Status, "Cancellation winner persists cancelled status");
            AssertProgress(scope, progress, "parallel", "cancelled", 5, 2, 0, 0, 3);
            scope.True(progress.WinningFlowId is null, "Cancelled execution has no winning flow",
                progress.WinningFlowId?.ToString() ?? "null");
        }
        else
        {
            scope.Equal("completed", detail.Status, "Completion winner persists completed status");
            AssertProgress(scope, progress, "parallel", "completed", 5, 3, 0, 0, 2);
            scope.Equal(201, progress.WinningFlowId, "Completion winner records flow 201");
        }
        scope.True(progress.Completed + progress.Cancelled == progress.Total,
            "Completion/cancellation race accounts for every child item",
            $"completed={progress.Completed}, cancelled={progress.Cancelled}, total={progress.Total}");
    }

    private async Task SequentialEarlyQuorumAsync(CaseScope scope)
    {
        var scenario = await StartAndEnterAsync(scope,
            RequireWorkflow(_sequentialEarlyWorkflowId, "sequential-early"), "SEQ-003");
        var tasks = (await GetAssignedTaskRecordsAsync(scope, scenario.InstanceId,
            ["seq0", "seq1", "seq2", "seq3", "seq4"])).OrderBy(t => t.Task.ItemIndex).ToList();
        var first = await CompleteTaskAsync(scope, Actor("seq0"), tasks[0].Task.Id, 201, EmptyVariables());
        AssertProgress(scope, RequireProgress(first.MultiInstance, scope),
            "sequential", "active", 5, 1, 1, 3, 0);
        var second = await CompleteTaskAsync(scope, Actor("seq1"), tasks[1].Task.Id, 201, EmptyVariables());
        var progress = RequireProgress(second.MultiInstance, scope);
        scope.Equal("completed", second.InstanceStatus, "Second sequential approval reaches early quorum");
        scope.Equal(3, second.CurrentNodeId, "Early sequential quorum reaches Approved");
        AssertProgress(scope, progress, "sequential", "completed", 5, 2, 0, 0, 3);
        scope.Equal(201, progress.WinningFlowId, "Sequential early quorum records flow 201");

        var finalTasks = new List<UserTaskDto>();
        foreach (var task in tasks)
            finalTasks.Add(await GetUserTaskAsync(scope, Actor(task.ActorName), task.Task.Id));
        scope.SequenceEqual(["completed", "completed", "cancelled", "cancelled", "cancelled"],
            finalTasks.OrderBy(t => t.ItemIndex).Select(t => t.Status).ToArray(),
            "Early quorum cancels the unprocessed pending remainder");
    }

    private async Task SequentialAfterAllAsync(CaseScope scope)
    {
        var scenario = await StartAndEnterAsync(scope,
            RequireWorkflow(_sequentialAfterAllWorkflowId, "sequential-after-all"), "SEQ-004");
        var tasks = (await GetAssignedTaskRecordsAsync(scope, scenario.InstanceId,
            ["seq0", "seq1", "seq2", "seq3", "seq4"])).OrderBy(t => t.Task.ItemIndex).ToList();
        UserTaskActionAckDto? ack = null;
        for (var index = 0; index < tasks.Count; index++)
        {
            ack = await CompleteTaskAsync(scope, Actor(tasks[index].ActorName), tasks[index].Task.Id, 201, EmptyVariables());
            if (index < tasks.Count - 1)
                scope.Equal("running", ack.InstanceStatus, $"afterAll remains running after item {index}");
            if (index == 1)
            {
                var afterQuorum = RequireProgress(ack.MultiInstance, scope);
                scope.Equal(2, afterQuorum.FlowCounts.Single(f => f.FlowId == 201).Count,
                    "Sequential approval condition is already true after two items");
                AssertProgress(scope, afterQuorum, "sequential", "active", 5, 2, 1, 2, 0);
            }
        }
        var final = RequireProgress(ack!.MultiInstance, scope);
        scope.Equal("completed", ack.InstanceStatus, "afterAll closes after the fifth item");
        AssertProgress(scope, final, "sequential", "completed", 5, 5, 0, 0, 0);
        scope.Equal(201, final.WinningFlowId, "Sequential afterAll evaluates and selects approval");
    }

    private async Task CompletionPriorityAsync(CaseScope scope)
    {
        var scenario = await StartAndEnterAsync(scope,
            RequireWorkflow(_priorityWorkflowId, "completion-priority"), "OUT-001",
            new Dictionary<string, JsonElement> { ["voters"] = JsonSerializer.SerializeToElement(3) });
        var tasks = (await GetTaskPageAsync(scope, Actor("priority1"), scenario.InstanceId, "active")).Items
            .OrderBy(t => t.ItemIndex).ToList();
        await CompleteTaskAsync(scope, Actor("priority1"), tasks[0].Id, 201, EmptyVariables());
        await CompleteTaskAsync(scope, Actor("priority2"), tasks[1].Id, 205, EmptyVariables());
        var final = await CompleteTaskAsync(scope, Actor("priority3"), tasks[2].Id, 201, EmptyVariables());
        var progress = RequireProgress(final.MultiInstance, scope);
        scope.Equal(205, progress.WinningFlowId,
            "Lower completionPriority 10 wins when both aggregate conditions are true");
        scope.Equal(7, final.CurrentNodeId, "Priority winner routes to the rejected end event");
        scope.Equal("condition", progress.CompletionReason, "Priority outcome records condition reason");
    }

    private static void SetProcessDefault(WorkflowModel model, string name, object value)
    {
        model.Variables.Single(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).DefaultValue =
            JsonSerializer.SerializeToElement(value);
    }
}
