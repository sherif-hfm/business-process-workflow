using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.IdentityModel.Tokens;
using WorkflowEngine.Shared.Dtos;
using WorkflowEngine.Shared.Models;

return await EntryPoint.RunAsync(args);

static class EntryPoint
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var parsed = Options.Parse(args);
            if (parsed.ShowHelp)
            {
                Options.PrintHelp();
                return 0;
            }

            var fixtureRoot = parsed.FixtureRoot is null
                ? FindFixtureRoot()
                : Path.GetFullPath(parsed.FixtureRoot);
            var reportDirectory = Path.GetFullPath(parsed.ReportDirectory
                ?? Path.Combine(fixtureRoot, "TestResults"));
            var runId = string.IsNullOrWhiteSpace(parsed.RunId)
                ? $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..22]
                : SanitizeRunId(parsed.RunId);

            var options = parsed with
            {
                ApiBase = parsed.ApiBase.TrimEnd('/'),
                FixtureRoot = fixtureRoot,
                ReportDirectory = reportDirectory,
                RunId = runId
            };

            Directory.CreateDirectory(reportDirectory);
            await using var managedApi = options.ManageApi
                ? new ManagedApiHost(options)
                : null;
            if (managedApi is not null)
                await managedApi.StartAsync();

            Func<Task>? restartApi = managedApi is null ? null : managedApi.RestartAsync;
            var suite = new MultiInstanceApiSuite(options, restartApi);
            return await suite.RunAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal test-runner error: {ex}");
            return 2;
        }
    }

    private static string FindFixtureRoot()
    {
        foreach (var seed in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(seed);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "votes-users-list.json"))
                    && File.Exists(Path.Combine(directory.FullName, "votes-sequence-users-list.json"))
                    && File.Exists(Path.Combine(directory.FullName, "votes-cardinality-approve-reject.json")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not find the workflow fixture root. Pass --fixture-root explicitly.");
    }

    private static string SanitizeRunId(string value)
    {
        var chars = value.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        var result = new string(chars).Trim('-');
        return result.Length == 0 ? Guid.NewGuid().ToString("N")[..12] : result;
    }
}

sealed partial class MultiInstanceApiSuite
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Options _options;
    private readonly ApiTransport _api;
    private readonly Dictionary<string, ActorToken> _actors;
    private readonly RunState _state = new();
    private readonly List<TestCaseResult> _results = [];
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly Func<Task>? _restartApi;
    private bool _apiReady;
    private WorkflowModel? _parallelTemplate;
    private WorkflowModel? _sequentialTemplate;
    private WorkflowModel? _cardinalityTemplate;

    public MultiInstanceApiSuite(Options options, Func<Task>? restartApi = null)
    {
        _options = options;
        _api = new ApiTransport(options.ApiBase, JsonOptions);
        _actors = CreateActors(options);
        _restartApi = restartApi;
    }

    public async Task<int> RunAsync()
    {
        Console.WriteLine("Workflow Engine multi-instance API QA");
        Console.WriteLine($"  API:          {_options.ApiBase}");
        Console.WriteLine($"  Fixture root: {_options.FixtureRoot}");
        Console.WriteLine($"  Run ID:       {_options.RunId}");
        Console.WriteLine();

        await RunCaseAsync("ENV-001", "API and OpenAPI endpoint are ready", EnvironmentReadyAsync);
        await RunCaseAsync("DEF-001", "Create and publish unique QA workflow definitions", CreateDefinitionsAsync);
        await RunCaseAsync("DEF-002", "Reject unsupported multi-instance completion evaluation", RejectInvalidDefinitionAsync);
        await RunCaseAsync("DEF-003", "Create and publish extended boundary/race workflow definitions", CreateAdditionalDefinitionsAsync);
        await RunCaseAsync("AUTH-001", "JWT and workflow-definition authorization boundaries", AuthenticationBoundariesAsync);
        await RunCaseAsync("VAL-001", "Empty collection entry fails atomically", EmptyCollectionRollbackAsync);
        await RunCaseAsync("COL-001", "Invalid collection members are rejected atomically", InvalidCollectionMembersAsync);
        await RunCaseAsync("COL-002", "Duplicate collection usernames create distinct assigned items", DuplicateCollectionUsersAsync);
        await RunCaseAsync("PAR-001", "Parallel initialization, visibility, validation, and legacy ambiguity", ParallelInitializationAsync);
        await RunCaseAsync("PAR-002", "Parallel afterEach quorum closes with ordered evidence", ParallelEarlyQuorumAsync);
        await RunCaseAsync("PAR-003", "Concurrent parallel completions close exactly once", ParallelCompletionRaceAsync);
        await RunCaseAsync("PAR-004", "Parallel interrupt authorization, race, and loop re-entry", ParallelInterruptAsync);
        await RunCaseAsync("PAR-005", "Parallel all-items default routing", ParallelDefaultAfterAllItemsAsync);
        await RunCaseAsync("RACE-001", "Quorum completion versus parent interrupt race", CompletionVersusInterruptAsync);
        await RunCaseAsync("RACE-002", "Quorum completion versus instance cancellation race", CompletionVersusCancellationAsync);
        await RunCaseAsync("SEQ-001", "Sequential activation, pending guard, stale replay, and completion", SequentialCompletionAsync);
        await RunCaseAsync("SEQ-002", "Sequential interrupt cancels active and pending remainder", SequentialInterruptAsync);
        await RunCaseAsync("SEQ-003", "Sequential afterEach early quorum cancels pending remainder", SequentialEarlyQuorumAsync);
        await RunCaseAsync("SEQ-004", "Sequential afterAll waits for every item", SequentialAfterAllAsync);
        await RunCaseAsync("OUT-001", "Simultaneous aggregate outcomes honor completion priority", CompletionPriorityAsync);
        await RunCaseAsync("CAR-001", "Cardinality onePerActor race and afterAll approval", CardinalityApprovalAsync);
        await RunCaseAsync("CAR-002", "Cardinality afterAll reject routing", CardinalityRejectAsync);
        await RunCaseAsync("CAR-003", "Cardinality engine-only default fallback faults", CardinalityDefaultAsync);
        await RunCaseAsync("CAR-004", "Cardinality numeric and maximum-instance boundaries", CardinalityBoundariesAsync);
        await RunCaseAsync("CAR-005", "onePerActor claim, unclaim, and duplicate-claim protection", CardinalityClaimsAsync);
        await RunCaseAsync("CTX-001", "mi context flow condition and submitted-variable rollback", MultiInstanceContextAndVariablesAsync);
        await RunCaseAsync("REC-001", "Active parallel and sequential work survives API restart", RestartRecoveryAsync);
        await RunCaseAsync("LOAD-001", "One-thousand-item paging and concurrent completion", ThousandItemLoadAsync);

        var completedAt = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(_options.ReportDirectory!);
        var report = new QaReport(
            _options.RunId!,
            _options.ApiBase,
            _options.FixtureRoot!,
            Environment.MachineName,
            Environment.Version.ToString(),
            _startedAt,
            completedAt,
            _state.Identifiers,
            _results);
        var paths = await ReportWriter.WriteAsync(report, _options.ReportDirectory!, JsonOptions);

        var passed = _results.Count(r => r.Status == TestStatuses.Passed);
        var failed = _results.Count(r => r.Status == TestStatuses.Failed);
        var skipped = _results.Count(r => r.Status == TestStatuses.Skipped);
        Console.WriteLine();
        Console.WriteLine($"Result: {passed} passed, {failed} failed, {skipped} skipped");
        Console.WriteLine($"Markdown: {paths.Markdown}");
        Console.WriteLine($"JSON:     {paths.Json}");
        return failed == 0 && skipped == 0 ? 0 : 1;
    }

    private async Task RunCaseAsync(string id, string name, Func<CaseScope, Task> body)
    {
        var scope = new CaseScope(id, _api);
        var stopwatch = Stopwatch.StartNew();
        string status;
        string expected = "All assertions pass";
        string actual;
        string? details = null;

        try
        {
            await body(scope);
            status = TestStatuses.Passed;
            actual = "All assertions passed";
            Console.WriteLine($"PASS {id}  {name}");
        }
        catch (SkipTestException ex)
        {
            status = TestStatuses.Skipped;
            expected = "Prerequisites available";
            actual = ex.Message;
            details = ex.ToString();
            Console.WriteLine($"SKIP {id}  {name}: {ex.Message}");
        }
        catch (TestFailureException ex)
        {
            status = TestStatuses.Failed;
            expected = ex.Expected;
            actual = ex.Actual;
            details = ex.ToString();
            Console.WriteLine($"FAIL {id}  {name}: {ex.Message}");
        }
        catch (Exception ex)
        {
            status = TestStatuses.Failed;
            expected = "Case completes without an unexpected exception";
            actual = $"{ex.GetType().Name}: {ex.Message}";
            details = ex.ToString();
            Console.WriteLine($"FAIL {id}  {name}: {actual}");
        }

        stopwatch.Stop();
        _results.Add(new TestCaseResult(
            id,
            name,
            status,
            stopwatch.ElapsedMilliseconds,
            expected,
            actual,
            details,
            scope.Exchanges));
    }

    private async Task EnvironmentReadyAsync(CaseScope scope)
    {
        var response = await scope.SendAsync(Admin, HttpMethod.Get, "/openapi/v1.json");
        scope.ExpectStatus(response, HttpStatusCode.OK);
        scope.True(response.Body.Contains("Workflow Engine API", StringComparison.OrdinalIgnoreCase),
            "OpenAPI document contains the Workflow Engine title", Truncate(response.Body, 300));
        _apiReady = true;
    }

    private async Task CreateDefinitionsAsync(CaseScope scope)
    {
        RequireApi();
        _parallelTemplate = LoadFixture("votes-users-list.json", "parallel");
        _sequentialTemplate = LoadFixture("votes-sequence-users-list.json", "sequential");
        _cardinalityTemplate = LoadFixture("votes-cardinality-approve-reject.json", "cardinality");

        _state.ParallelWorkflowId = await CreateWorkflowAsync(scope, Clone(_parallelTemplate), "parallel");
        _state.SequentialWorkflowId = await CreateWorkflowAsync(scope, Clone(_sequentialTemplate), "sequential");
        _state.CardinalityWorkflowId = await CreateWorkflowAsync(scope, Clone(_cardinalityTemplate), "cardinality");

        var empty = Clone(_parallelTemplate);
        empty.Variables.Single(v => v.Name.Equals("voters", StringComparison.OrdinalIgnoreCase)).DefaultValue =
            JsonSerializer.SerializeToElement(Array.Empty<string>(), JsonOptions);
        _state.EmptyCollectionWorkflowId = await CreateWorkflowAsync(scope, empty, "empty-collection");

        scope.True(_state.ParallelWorkflowId > 0 && _state.SequentialWorkflowId > 0
                   && _state.CardinalityWorkflowId > 0 && _state.EmptyCollectionWorkflowId > 0,
            "Four workflow database IDs are returned", _state.DescribeWorkflowIds());
    }

    private async Task RejectInvalidDefinitionAsync(CaseScope scope)
    {
        RequireApi();
        if (_parallelTemplate is null) throw new SkipTestException("Workflow fixture setup did not complete.");
        var invalid = Clone(_parallelTemplate);
        invalid.Id = $"qa-{_options.RunId}-invalid-completion";
        invalid.Name = $"QA {_options.RunId} invalid completion";
        invalid.FlowNodes.Single(n => n.MultiInstance is not null).MultiInstance!.CompletionEvaluation = "sometimes";

        var response = await scope.SendAsync(Admin, HttpMethod.Post, "/api/workflows",
            new CreateWorkflowRequest(invalid, true));
        scope.ExpectStatus(response, HttpStatusCode.BadRequest);
        scope.True(response.Body.Contains("completionEvaluation", StringComparison.OrdinalIgnoreCase),
            "Validation error identifies completionEvaluation", Truncate(response.Body, 500));
    }

    private async Task EmptyCollectionRollbackAsync(CaseScope scope)
    {
        var workflowId = RequireWorkflow(_state.EmptyCollectionWorkflowId, "empty collection");
        var review = await StartAtReviewAsync(scope, workflowId, "VAL-001");
        var response = await scope.SendAsync(Manager, HttpMethod.Post,
            $"/api/instances/{review.Id}/flows/204", EmptyVariables());
        scope.ExpectStatus(response, HttpStatusCode.BadRequest);
        scope.True(response.Body.Contains("between 1", StringComparison.OrdinalIgnoreCase),
            "Empty collection is rejected by item-count validation", Truncate(response.Body, 500));

        var detail = await GetInstanceAsync(scope, review.Id, Admin);
        scope.Equal(5, detail.CurrentNodeId, "Instance remains on review after rollback");
        scope.Equal("running", detail.Status, "Instance remains running after rollback");
        scope.True(detail.MultiInstance is null, "No active multi-instance execution exists", "multiInstance was present");

        var tasks = await GetTaskPageAsync(scope, Manager, review.Id, "active");
        scope.Equal(1L, tasks.TotalCount, "Only the review user task remains active");
        scope.Equal(5, tasks.Items.Single().NodeId, "The surviving task is the review task");
    }

    private async Task ParallelInitializationAsync(CaseScope scope)
    {
        var workflowId = RequireWorkflow(_state.ParallelWorkflowId, "parallel");
        var scenario = await StartAndEnterAsync(scope, workflowId, "PAR-001");
        var progress = RequireProgress(scenario.Detail.MultiInstance, scope);
        AssertProgress(scope, progress, mode: "parallel", status: "active", total: 5,
            completed: 0, active: 5, pending: 0, cancelled: 0);
        scope.Equal(0, LatestResult(scope, scenario.Detail, "voteResults").Count,
            "The initialized result collection is empty");

        var assigned = await GetAssignedCollectionTasksAsync(scope, scenario.InstanceId,
            ["alice", "bob", "carol", "dave", "erin"]);
        scope.Equal(5, assigned.Select(t => t.TaskId).Distinct().Count(), "Every assignee has a distinct work item");

        var outsiderInbox = await GetInboxAsync(scope, Outsider, scenario.InstanceId);
        scope.Equal(0L, outsiderInbox.TotalCount, "An unrelated user has no assigned collection item");

        var aliceTask = assigned.Single(t => t.ActorName == "alice");
        var flowsResponse = await scope.SendAsync(Actor("alice"), HttpMethod.Get,
            $"/api/user-tasks/{aliceTask.TaskId}/flows");
        scope.ExpectStatus(flowsResponse, HttpStatusCode.OK);
        var flowIds = scope.Deserialize<List<SequenceFlowModel>>(flowsResponse).Select(f => f.Id).Order().ToArray();
        scope.SequenceEqual([201], flowIds, "Alice sees only selectable, authorized outcome flow 201");

        var missingVote = await scope.SendAsync(Actor("alice"), HttpMethod.Post,
            $"/api/user-tasks/{aliceTask.TaskId}/flows/201", EmptyVariables());
        scope.ExpectStatus(missingVote, HttpStatusCode.BadRequest);
        var hiddenDefault = await scope.SendAsync(Actor("alice"), HttpMethod.Post,
            $"/api/user-tasks/{aliceTask.TaskId}/flows/205", EmptyVariables());
        scope.ExpectStatus(hiddenDefault, HttpStatusCode.BadRequest);

        var legacy = await scope.SendAsync(Actor("alice"), HttpMethod.Post,
            $"/api/instances/{scenario.InstanceId}/flows/201", VoteVariables("yes"));
        scope.ExpectStatus(legacy, HttpStatusCode.Conflict);

        var unchanged = await GetUserTaskAsync(scope, Actor("alice"), aliceTask.TaskId);
        scope.Equal("active", unchanged.Status, "Rejected actions leave Alice's task active");
        var unchangedProgress = RequireProgress(unchanged.MultiInstance, scope);
        AssertProgress(scope, unchangedProgress, "parallel", "active", 5, 0, 5, 0, 0);
    }

    private async Task ParallelEarlyQuorumAsync(CaseScope scope)
    {
        var scenario = await StartAndEnterAsync(scope,
            RequireWorkflow(_state.ParallelWorkflowId, "parallel"), "PAR-002");
        var tasks = await GetAssignedCollectionTasksAsync(scope, scenario.InstanceId,
            ["alice", "bob", "carol", "dave", "erin"]);

        var first = await CompleteTaskAsync(scope, Actor("alice"), tasks.Single(t => t.ActorName == "alice").TaskId,
            201, VoteVariables("approve"));
        scope.Equal("running", first.InstanceStatus, "One vote does not close the instance");
        AssertProgress(scope, RequireProgress(first.MultiInstance, scope), "parallel", "active", 5, 1, 4, 0, 0);

        var second = await CompleteTaskAsync(scope, Actor("bob"), tasks.Single(t => t.ActorName == "bob").TaskId,
            201, VoteVariables("approve"));
        scope.Equal("running", second.InstanceStatus, "Two votes do not close the instance");
        AssertProgress(scope, RequireProgress(second.MultiInstance, scope), "parallel", "active", 5, 2, 3, 0, 0);

        var third = await CompleteTaskAsync(scope, Actor("carol"), tasks.Single(t => t.ActorName == "carol").TaskId,
            201, VoteVariables("approve"));
        scope.Equal("completed", third.InstanceStatus, "Third vote closes the workflow instance");
        scope.Equal(3, third.CurrentNodeId, "Winning route reaches Approved");
        var finalProgress = RequireProgress(third.MultiInstance, scope);
        AssertProgress(scope, finalProgress, "parallel", "completed", 5, 3, 0, 0, 2);
        scope.Equal(201, finalProgress.WinningFlowId, "Outcome flow 201 wins");
        scope.Equal("condition", finalProgress.CompletionReason, "Completion reason is condition");
        var count = finalProgress.FlowCounts.Single(f => f.FlowId == 201);
        scope.Equal(3, count.Count, "Flow count records three votes");
        scope.Near(60d, count.Percent, 0.001, "Flow percentage is 60%");

        var finalTasks = await GetAssignedTaskDetailsAsync(scope, tasks);
        scope.Equal(3, finalTasks.Count(t => t.Status == "completed"), "Three child tasks completed");
        scope.Equal(2, finalTasks.Count(t => t.Status == "cancelled"), "Two child tasks were cancelled");

        var detail = await GetInstanceAsync(scope, scenario.InstanceId, Admin);
        scope.Equal("completed", detail.Status, "Persisted instance status is completed");
        scope.Equal(3, detail.CurrentNodeId, "Persisted node is Approved");
        var results = LatestResult(scope, detail, "voteResults");
        AssertOrderedIndexes(scope, results, 5);
        scope.Equal(3, results.Count(r => r.Status == "completed"), "Result contains three completed entries");
        scope.Equal(2, results.Count(r => r.Status == "cancelled"), "Result contains two cancelled entries");
        foreach (var item in results.Where(r => r.Status == "completed"))
        {
            scope.True(item.Variables?.TryGetValue("vote", out var vote) == true
                       && vote.GetString() == "approve",
                "Completed result preserves submitted vote", JsonSerializer.Serialize(item, JsonOptions));
        }

        var itemHistory = detail.History.Where(h => h.MultiInstanceExecutionId == scenario.ExecutionId).ToList();
        scope.Equal(2, itemHistory.Count(h => h.Note == "multiInstanceItem"),
            "Two non-winning item history rows are written");
        scope.Equal(1, itemHistory.Count(h => h.Note == "multiInstanceComplete"),
            "One parent completion history row is written");
    }

    private async Task ParallelCompletionRaceAsync(CaseScope scope)
    {
        var scenario = await StartAndEnterAsync(scope,
            RequireWorkflow(_state.ParallelWorkflowId, "parallel"), "PAR-003");
        var tasks = await GetAssignedCollectionTasksAsync(scope, scenario.InstanceId,
            ["alice", "bob", "carol", "dave", "erin"]);

        var responses = await Task.WhenAll(tasks.Select(task => scope.SendAsync(
            Actor(task.ActorName),
            HttpMethod.Post,
            $"/api/user-tasks/{task.TaskId}/flows/201",
            VoteVariables($"race-{task.ActorName}"))));
        scope.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.OK),
            "Exactly three racing completions succeed");
        scope.Equal(2, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict),
            "Exactly two racing completions become stale conflicts");
        var closingAcks = responses.Where(r => r.StatusCode == HttpStatusCode.OK)
            .Select(scope.Deserialize<UserTaskActionAckDto>)
            .Where(a => a.InstanceStatus == "completed")
            .ToList();
        scope.Equal(1, closingAcks.Count, "Exactly one response advances the parent token");
        var progress = RequireProgress(closingAcks.Single().MultiInstance, scope);
        AssertProgress(scope, progress, "parallel", "completed", 5, 3, 0, 0, 2);

        var finalTasks = await GetAssignedTaskDetailsAsync(scope, tasks);
        scope.Equal(3, finalTasks.Count(t => t.Status == "completed"), "Race persists three completed tasks");
        scope.Equal(2, finalTasks.Count(t => t.Status == "cancelled"), "Race persists two cancelled tasks");
        var detail = await GetInstanceAsync(scope, scenario.InstanceId, Admin);
        var history = detail.History.Where(h => h.MultiInstanceExecutionId == scenario.ExecutionId).ToList();
        scope.Equal(1, history.Count(h => h.Note == "multiInstanceComplete"),
            "Race writes one parent completion history row");
        scope.Equal(3, LatestResult(scope, detail, "voteResults").Count(r => r.Status == "completed"),
            "Race result never overcounts the quorum");
    }

    private async Task ParallelInterruptAsync(CaseScope scope)
    {
        var scenario = await StartAndEnterAsync(scope,
            RequireWorkflow(_state.ParallelWorkflowId, "parallel"), "PAR-004");
        var originalTasks = await GetAssignedCollectionTasksAsync(scope, scenario.InstanceId,
            ["alice", "bob", "carol", "dave", "erin"]);

        var userFlowsResponse = await scope.SendAsync(Actor("alice"), HttpMethod.Get,
            $"/api/multi-instance-executions/{scenario.ExecutionId}/flows");
        scope.ExpectStatus(userFlowsResponse, HttpStatusCode.OK);
        scope.Equal(0, scope.Deserialize<List<SequenceFlowModel>>(userFlowsResponse).Count,
            "A normal User cannot discover manager interrupt flow 203");
        var unauthorized = await scope.SendAsync(Actor("alice"), HttpMethod.Post,
            $"/api/multi-instance-executions/{scenario.ExecutionId}/flows/203", EmptyVariables());
        scope.ExpectStatus(unauthorized, HttpStatusCode.BadRequest);

        var managerFlowsResponse = await scope.SendAsync(Manager, HttpMethod.Get,
            $"/api/multi-instance-executions/{scenario.ExecutionId}/flows");
        scope.ExpectStatus(managerFlowsResponse, HttpStatusCode.OK);
        scope.SequenceEqual([203], scope.Deserialize<List<SequenceFlowModel>>(managerFlowsResponse)
            .Select(f => f.Id).Order().ToArray(), "Manager discovers interrupt flow 203");

        var interruptResponses = await Task.WhenAll(
            scope.SendAsync(Manager, HttpMethod.Post,
                $"/api/multi-instance-executions/{scenario.ExecutionId}/flows/203", EmptyVariables()),
            scope.SendAsync(Manager, HttpMethod.Post,
                $"/api/multi-instance-executions/{scenario.ExecutionId}/flows/203", EmptyVariables()));
        scope.Equal(1, interruptResponses.Count(r => r.StatusCode == HttpStatusCode.OK),
            "Exactly one interrupt request succeeds");
        scope.Equal(1, interruptResponses.Count(r => r.StatusCode == HttpStatusCode.Conflict),
            "The duplicate interrupt returns conflict");
        var interruptedDetail = scope.Deserialize<InstanceDetailDto>(
            interruptResponses.Single(r => r.StatusCode == HttpStatusCode.OK));
        scope.Equal("running", interruptedDetail.Status, "Interrupted workflow remains running");
        scope.Equal(5, interruptedDetail.CurrentNodeId, "Interrupt routes back to review");

        var oldTaskDetails = await GetAssignedTaskDetailsAsync(scope, originalTasks);
        scope.Equal(5, oldTaskDetails.Count(t => t.Status == "cancelled"),
            "Interrupt cancels every original child task");
        var closedProgress = RequireProgress(oldTaskDetails[0].MultiInstance, scope);
        AssertProgress(scope, closedProgress, "parallel", "interrupted", 5, 0, 0, 0, 5);
        scope.Equal(203, closedProgress.WinningFlowId, "Interrupt flow 203 is recorded as winner");
        scope.Equal("interrupt", closedProgress.CompletionReason, "Completion reason is interrupt");
        var history = interruptedDetail.History.Where(h => h.MultiInstanceExecutionId == scenario.ExecutionId).ToList();
        scope.Equal(1, history.Count(h => h.Note == "multiInstanceInterrupt"),
            "Exactly one interrupt history row is written");
        scope.Equal(5, LatestResult(scope, interruptedDetail, "voteResults").Count,
            "Interrupted execution materializes all five ordered result entries");

        var reenter = await scope.SendAsync(Manager, HttpMethod.Post,
            $"/api/instances/{scenario.InstanceId}/flows/204", EmptyVariables());
        scope.ExpectStatus(reenter, HttpStatusCode.OK);
        var freshDetail = scope.Deserialize<InstanceDetailDto>(reenter);
        var freshProgress = RequireProgress(freshDetail.MultiInstance, scope);
        scope.True(freshProgress.ExecutionId != scenario.ExecutionId,
            "Loop re-entry creates a new multi-instance execution ID",
            $"old={scenario.ExecutionId}, new={freshProgress.ExecutionId}");
        AssertProgress(scope, freshProgress, "parallel", "active", 5, 0, 5, 0, 0);
        scope.Equal(0, LatestResult(scope, freshDetail, "voteResults").Count,
            "Loop re-entry resets the result collection for the fresh execution");
        var freshTasks = await GetAssignedCollectionTasksAsync(scope, scenario.InstanceId,
            ["alice", "bob", "carol", "dave", "erin"]);
        scope.Equal(0, freshTasks.Select(t => t.TaskId).Intersect(originalTasks.Select(t => t.TaskId)).Count(),
            "Fresh execution uses a new child-task set");
        var oldTasksAfterReentry = await GetAssignedTaskDetailsAsync(scope, originalTasks);
        scope.Equal(5, oldTasksAfterReentry.Count(t => t.Status == "cancelled"),
            "Loop re-entry does not mutate the previous execution's cancelled tasks");
        var oldProgressAfterReentry = RequireProgress(oldTasksAfterReentry[0].MultiInstance, scope);
        AssertProgress(scope, oldProgressAfterReentry, "parallel", "interrupted", 5, 0, 0, 0, 5);
    }

    private async Task SequentialCompletionAsync(CaseScope scope)
    {
        var scenario = await StartAndEnterAsync(scope,
            RequireWorkflow(_state.SequentialWorkflowId, "sequential"), "SEQ-001");
        AssertProgress(scope, RequireProgress(scenario.Detail.MultiInstance, scope),
            "sequential", "active", 3, 0, 1, 2, 0);

        var assigned = await GetAssignedTaskRecordsAsync(scope, scenario.InstanceId,
            ["alice", "alice1", "alice2"]);
        var ordered = assigned.OrderBy(t => t.Task.ItemIndex).ToList();
        scope.SequenceEqual([0, 1, 2], ordered.Select(t => t.Task.ItemIndex!.Value).ToArray(),
            "Sequential tasks retain authored item order");
        scope.SequenceEqual(["active", "pending", "pending"], ordered.Select(t => t.Task.Status).ToArray(),
            "Only item zero starts active");
        scope.Equal(1L, (await GetInboxAsync(scope, Actor("alice"), scenario.InstanceId)).TotalCount,
            "First assignee has one inbox item");
        scope.Equal(0L, (await GetInboxAsync(scope, Actor("alice1"), scenario.InstanceId)).TotalCount,
            "Second assignee has no inbox item while pending");

        var earlyFlows = await scope.SendAsync(Actor("alice1"), HttpMethod.Get,
            $"/api/user-tasks/{ordered[1].Task.Id}/flows");
        scope.ExpectStatus(earlyFlows, HttpStatusCode.OK);
        scope.Equal(0, scope.Deserialize<List<SequenceFlowModel>>(earlyFlows).Count,
            "Pending sequential task exposes no actions");
        var earlyAction = await scope.SendAsync(Actor("alice1"), HttpMethod.Post,
            $"/api/user-tasks/{ordered[1].Task.Id}/flows/201", EmptyVariables());
        scope.ExpectStatus(earlyAction, HttpStatusCode.Conflict);

        var first = await CompleteTaskAsync(scope, Actor("alice"), ordered[0].Task.Id, 201, EmptyVariables());
        AssertProgress(scope, RequireProgress(first.MultiInstance, scope),
            "sequential", "active", 3, 1, 1, 1, 0);
        var afterFirst = await GetInstanceAsync(scope, scenario.InstanceId, Admin);
        var historyCountBeforeReplay = afterFirst.History.Count;
        var replay = await scope.SendAsync(Actor("alice"), HttpMethod.Post,
            $"/api/user-tasks/{ordered[0].Task.Id}/flows/201", EmptyVariables());
        scope.ExpectStatus(replay, HttpStatusCode.Conflict);
        var afterReplay = await GetInstanceAsync(scope, scenario.InstanceId, Admin);
        scope.Equal(historyCountBeforeReplay, afterReplay.History.Count,
            "Stale replay does not append history");

        var secondTask = await GetUserTaskAsync(scope, Actor("alice1"), ordered[1].Task.Id);
        scope.Equal("active", secondTask.Status, "Second item activates after item zero completes");
        var second = await CompleteTaskAsync(scope, Actor("alice1"), secondTask.Id, 201, EmptyVariables());
        AssertProgress(scope, RequireProgress(second.MultiInstance, scope),
            "sequential", "active", 3, 2, 1, 0, 0);

        var thirdTask = await GetUserTaskAsync(scope, Actor("alice2"), ordered[2].Task.Id);
        scope.Equal("active", thirdTask.Status, "Third item activates after item one completes");
        var third = await CompleteTaskAsync(scope, Actor("alice2"), thirdTask.Id, 201, EmptyVariables());
        scope.Equal("completed", third.InstanceStatus, "Final sequential item completes the instance");
        scope.Equal(3, third.CurrentNodeId, "Sequential winner reaches Approved");
        var finalProgress = RequireProgress(third.MultiInstance, scope);
        AssertProgress(scope, finalProgress, "sequential", "completed", 3, 3, 0, 0, 0);
        scope.Equal(201, finalProgress.WinningFlowId, "Sequential flow 201 wins");
        scope.Equal("condition", finalProgress.CompletionReason, "Sequential completion reason is condition");

        var finalTasks = new List<UserTaskDto>();
        foreach (var item in ordered)
            finalTasks.Add(await GetUserTaskAsync(scope, Actor(item.ActorName), item.Task.Id));
        scope.Equal(3, finalTasks.Count(t => t.Status == "completed"),
            "All sequential tasks are completed");
        var finalDetail = await GetInstanceAsync(scope, scenario.InstanceId, Admin);
        var results = LatestResult(scope, finalDetail, "voteResults");
        AssertOrderedIndexes(scope, results, 3);
        scope.SequenceEqual(["alice", "alice1", "alice2"], results.Select(r => r.CompletedBy!).ToArray(),
            "Sequential result actors follow item order");
        var miHistory = finalDetail.History.Where(h => h.MultiInstanceExecutionId == scenario.ExecutionId).ToList();
        scope.Equal(2, miHistory.Count(h => h.Note == "multiInstanceItem"),
            "Two intermediate sequential item rows are written");
        scope.Equal(1, miHistory.Count(h => h.Note == "multiInstanceComplete"),
            "One sequential completion row is written");
    }

    private async Task SequentialInterruptAsync(CaseScope scope)
    {
        var scenario = await StartAndEnterAsync(scope,
            RequireWorkflow(_state.SequentialWorkflowId, "sequential"), "SEQ-002");
        var assigned = (await GetAssignedTaskRecordsAsync(scope, scenario.InstanceId,
            ["alice", "alice1", "alice2"])).OrderBy(t => t.Task.ItemIndex).ToList();
        await CompleteTaskAsync(scope, Actor("alice"), assigned[0].Task.Id, 201, EmptyVariables());

        var interrupt = await scope.SendAsync(Manager, HttpMethod.Post,
            $"/api/multi-instance-executions/{scenario.ExecutionId}/flows/203", EmptyVariables());
        scope.ExpectStatus(interrupt, HttpStatusCode.OK);
        var detail = scope.Deserialize<InstanceDetailDto>(interrupt);
        scope.Equal(5, detail.CurrentNodeId, "Sequential interrupt returns to review");

        var finalTasks = new List<UserTaskDto>();
        foreach (var item in assigned)
            finalTasks.Add(await GetUserTaskAsync(scope, Actor(item.ActorName), item.Task.Id));
        scope.SequenceEqual(["completed", "cancelled", "cancelled"],
            finalTasks.OrderBy(t => t.ItemIndex).Select(t => t.Status).ToArray(),
            "Sequential interrupt preserves first completion and cancels active/pending remainder");
        var progress = RequireProgress(finalTasks[0].MultiInstance, scope);
        AssertProgress(scope, progress, "sequential", "interrupted", 3, 1, 0, 0, 2);
        scope.Equal(203, progress.WinningFlowId, "Sequential interrupt flow is winner");
    }

    private async Task CardinalityApprovalAsync(CaseScope scope)
    {
        var scenario = await StartAndEnterAsync(scope,
            RequireWorkflow(_state.CardinalityWorkflowId, "cardinality"), "CAR-001",
            new Dictionary<string, JsonElement> { ["voters"] = JsonSerializer.SerializeToElement(3) });
        AssertProgress(scope, RequireProgress(scenario.Detail.MultiInstance, scope),
            "parallel", "active", 3, 0, 3, 0, 0);

        var caseUser = Actor("CaseUser");
        var tasks = (await GetTaskPageAsync(scope, caseUser, scenario.InstanceId, "active")).Items
            .OrderBy(t => t.ItemIndex).ToList();
        scope.Equal(3, tasks.Count, "Cardinality creates three active unassigned tasks");
        scope.True(tasks.All(t => t.Assignee is null), "Cardinality tasks are unassigned", "An assignee was present");
        scope.Equal(1L, (await GetInboxAsync(scope, caseUser, scenario.InstanceId)).TotalCount,
            "onePerActor projects one representative inbox item");
        scope.Equal(1L, (await GetInboxAsync(scope, Actor("voter2"), scenario.InstanceId)).TotalCount,
            "A second actor also receives one representative item");

        var sameActorRace = await Task.WhenAll(
            scope.SendAsync(Actor("CaseUser"), HttpMethod.Post,
                $"/api/user-tasks/{tasks[0].Id}/flows/201", EmptyVariables()),
            scope.SendAsync(Actor("caseuser"), HttpMethod.Post,
                $"/api/user-tasks/{tasks[1].Id}/flows/201", EmptyVariables()));
        scope.Equal(1, sameActorRace.Count(r => r.StatusCode == HttpStatusCode.OK),
            "Case-insensitive actor race permits one completion");
        scope.Equal(1, sameActorRace.Count(r => r.StatusCode == HttpStatusCode.Conflict),
            "Case-insensitive actor race rejects the duplicate completion");
        scope.Equal(0L, (await GetInboxAsync(scope, Actor("caseuser"), scenario.InstanceId)).TotalCount,
            "Completed actor has no further representative inbox item");

        var activeForSecond = (await GetTaskPageAsync(scope, Actor("voter2"), scenario.InstanceId, "active")).Items;
        scope.Equal(2, activeForSecond.Count, "Two tasks remain active after the actor race");
        var second = await CompleteTaskAsync(scope, Actor("voter2"), activeForSecond[0].Id, 201, EmptyVariables());
        scope.Equal("running", second.InstanceStatus,
            "afterAll keeps the instance running after the approval condition becomes true");
        var afterTwo = RequireProgress(second.MultiInstance, scope);
        AssertProgress(scope, afterTwo, "parallel", "active", 3, 2, 1, 0, 0);
        scope.Equal(2, afterTwo.FlowCounts.Single(f => f.FlowId == 201).Count,
            "Two approval selections are counted before final evaluation");

        var activeForThird = (await GetTaskPageAsync(scope, Actor("voter3"), scenario.InstanceId, "active")).Items;
        scope.Equal(1, activeForThird.Count, "One final cardinality task remains");
        var third = await CompleteTaskAsync(scope, Actor("voter3"), activeForThird[0].Id, 205, EmptyVariables());
        scope.Equal("completed", third.InstanceStatus, "Final item closes the afterAll execution");
        scope.Equal(3, third.CurrentNodeId, "Two approvals route to Approved");
        var final = RequireProgress(third.MultiInstance, scope);
        AssertProgress(scope, final, "parallel", "completed", 3, 3, 0, 0, 0);
        scope.Equal(201, final.WinningFlowId, "Approval aggregate flow wins after all items complete");
        scope.Equal(2, final.FlowCounts.Single(f => f.FlowId == 201).Count, "Approval count is two");
        scope.Equal(1, final.FlowCounts.Single(f => f.FlowId == 205).Count, "Reject count is one");
    }

    private async Task CardinalityRejectAsync(CaseScope scope)
    {
        var scenario = await StartAndEnterAsync(scope,
            RequireWorkflow(_state.CardinalityWorkflowId, "cardinality"), "CAR-002",
            new Dictionary<string, JsonElement> { ["voters"] = JsonSerializer.SerializeToElement(3) });
        var tasks = (await GetTaskPageAsync(scope, Actor("rejecter1"), scenario.InstanceId, "active")).Items
            .OrderBy(t => t.ItemIndex).ToList();
        scope.Equal(3, tasks.Count, "Reject scenario starts with three tasks");
        var first = await CompleteTaskAsync(scope, Actor("rejecter1"), tasks[0].Id, 205, EmptyVariables());
        scope.Equal("running", first.InstanceStatus, "First reject does not close afterAll execution");
        var second = await CompleteTaskAsync(scope, Actor("rejecter2"), tasks[1].Id, 205, EmptyVariables());
        scope.Equal("running", second.InstanceStatus, "Second reject still waits for all items");
        var third = await CompleteTaskAsync(scope, Actor("approver"), tasks[2].Id, 201, EmptyVariables());
        scope.Equal("completed", third.InstanceStatus, "Third item closes reject scenario");
        scope.Equal(7, third.CurrentNodeId, "Reject aggregate routes to rejected end event");
        var progress = RequireProgress(third.MultiInstance, scope);
        scope.Equal(205, progress.WinningFlowId, "Reject flow 205 wins");
        scope.Equal(2, progress.FlowCounts.Single(f => f.FlowId == 205).Count, "Reject count is two");
        scope.Equal(1, progress.FlowCounts.Single(f => f.FlowId == 201).Count, "Approval count is one");
    }

    private async Task CardinalityDefaultAsync(CaseScope scope)
    {
        var scenario = await StartAndEnterAsync(scope,
            RequireWorkflow(_state.CardinalityWorkflowId, "cardinality"), "CAR-003",
            new Dictionary<string, JsonElement> { ["voters"] = JsonSerializer.SerializeToElement(1) });
        var task = (await GetTaskPageAsync(scope, Actor("solo"), scenario.InstanceId, "active")).Items.Single();
        var flowsResponse = await scope.SendAsync(Actor("solo"), HttpMethod.Get,
            $"/api/user-tasks/{task.Id}/flows");
        scope.ExpectStatus(flowsResponse, HttpStatusCode.OK);
        scope.SequenceEqual([201, 205], scope.Deserialize<List<SequenceFlowModel>>(flowsResponse)
            .Select(f => f.Id).Order().ToArray(),
            "User sees selectable outcomes but not engine-only default 207");
        var hiddenAttempt = await scope.SendAsync(Actor("solo"), HttpMethod.Post,
            $"/api/user-tasks/{task.Id}/flows/207", EmptyVariables());
        scope.ExpectStatus(hiddenAttempt, HttpStatusCode.BadRequest);

        var completed = await CompleteTaskAsync(scope, Actor("solo"), task.Id, 201, EmptyVariables());
        scope.Equal("faulted", completed.InstanceStatus, "Default route reaches an error end and faults the instance");
        scope.Equal(6, completed.CurrentNodeId, "Default route reaches Error node");
        var progress = RequireProgress(completed.MultiInstance, scope);
        AssertProgress(scope, progress, "parallel", "completed", 1, 1, 0, 0, 0);
        scope.Equal(207, progress.WinningFlowId, "Engine-only default 207 wins");
        scope.Equal("all", progress.CompletionReason, "Default completion reason is all");
        scope.Equal(1, progress.FlowCounts.Single(f => f.FlowId == 201).Count,
            "The selected approval remains recorded separately from the default winner");
    }

    private WorkflowModel LoadFixture(string fileName, string suffix)
    {
        var path = Path.Combine(_options.FixtureRoot!, fileName);
        var model = JsonSerializer.Deserialize<WorkflowModel>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException($"Fixture '{path}' deserialized to null.");
        model.Id = $"qa-{_options.RunId}-{suffix}";
        model.Name = $"QA {_options.RunId} {suffix}";
        return model;
    }

    private async Task<long> CreateWorkflowAsync(CaseScope scope, WorkflowModel model, string label)
    {
        model.Id = $"qa-{_options.RunId}-{label}";
        model.Name = $"QA {_options.RunId} {label}";
        var response = await scope.SendAsync(Admin, HttpMethod.Post, "/api/workflows",
            new CreateWorkflowRequest(model, true));
        scope.ExpectStatus(response, HttpStatusCode.Created);
        var detail = scope.Deserialize<WorkflowDetailDto>(response);
        scope.True(detail.IsPublished, $"{label} workflow is published", $"workflow #{detail.Id} was not published");
        scope.Equal(model.Id, detail.WorkflowKey, $"{label} workflow key round-trips");
        _state.Identifiers[$"workflow.{label}.id"] = detail.Id.ToString();
        _state.Identifiers[$"workflow.{label}.key"] = detail.WorkflowKey;
        return detail.Id;
    }

    private async Task<InstanceDetailDto> StartAtReviewAsync(
        CaseScope scope,
        long workflowId,
        string label,
        Dictionary<string, JsonElement>? variables = null)
    {
        var response = await scope.SendAsync(Manager, HttpMethod.Post, "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, variables));
        scope.ExpectStatus(response, HttpStatusCode.Created);
        var detail = scope.Deserialize<InstanceDetailDto>(response);
        scope.Equal(5, detail.CurrentNodeId, "Started instance rests on manager review");
        scope.Equal("running", detail.Status, "Started instance is running");
        _state.Identifiers[$"instance.{label}"] = detail.Id.ToString();
        return detail;
    }

    private async Task<MiScenario> StartAndEnterAsync(
        CaseScope scope,
        long workflowId,
        string label,
        Dictionary<string, JsonElement>? variables = null)
    {
        var review = await StartAtReviewAsync(scope, workflowId, label, variables);
        var response = await scope.SendAsync(Manager, HttpMethod.Post,
            $"/api/instances/{review.Id}/flows/204", EmptyVariables());
        scope.ExpectStatus(response, HttpStatusCode.OK);
        var detail = scope.Deserialize<InstanceDetailDto>(response);
        scope.Equal(2, detail.CurrentNodeId, "Manager flow 204 enters the multi-instance task");
        var progress = RequireProgress(detail.MultiInstance, scope);
        _state.Identifiers[$"execution.{label}"] = progress.ExecutionId.ToString();
        return new MiScenario(review.Id, progress.ExecutionId, detail);
    }

    private async Task<InstanceDetailDto> GetInstanceAsync(CaseScope scope, long instanceId, ActorToken actor)
    {
        var response = await scope.SendAsync(actor, HttpMethod.Get, $"/api/instances/{instanceId}");
        scope.ExpectStatus(response, HttpStatusCode.OK);
        return scope.Deserialize<InstanceDetailDto>(response);
    }

    private async Task<UserTaskDto> GetUserTaskAsync(CaseScope scope, ActorToken actor, long taskId)
    {
        var response = await scope.SendAsync(actor, HttpMethod.Get, $"/api/user-tasks/{taskId}");
        scope.ExpectStatus(response, HttpStatusCode.OK);
        return scope.Deserialize<UserTaskDto>(response);
    }

    private async Task<PagedResult<UserTaskDto>> GetTaskPageAsync(
        CaseScope scope,
        ActorToken actor,
        long instanceId,
        string? status = null)
    {
        var statusQuery = string.IsNullOrWhiteSpace(status) ? string.Empty : $"&status={Uri.EscapeDataString(status)}";
        var response = await scope.SendAsync(actor, HttpMethod.Get,
            $"/api/instances/{instanceId}/user-tasks?page=1&pageSize=200{statusQuery}");
        scope.ExpectStatus(response, HttpStatusCode.OK);
        return scope.Deserialize<PagedResult<UserTaskDto>>(response);
    }

    private async Task<PagedResult<InboxItemDto>> GetInboxAsync(
        CaseScope scope,
        ActorToken actor,
        long instanceId)
    {
        var response = await scope.SendAsync(actor, HttpMethod.Get,
            $"/api/instances/inbox?instanceId={instanceId}&page=1&pageSize=200");
        scope.ExpectStatus(response, HttpStatusCode.OK);
        return scope.Deserialize<PagedResult<InboxItemDto>>(response);
    }

    private async Task<List<AssignedTask>> GetAssignedCollectionTasksAsync(
        CaseScope scope,
        long instanceId,
        IReadOnlyList<string> actorNames)
    {
        var tasks = new List<AssignedTask>();
        foreach (var actorName in actorNames)
        {
            var page = await GetInboxAsync(scope, Actor(actorName), instanceId);
            scope.Equal(1L, page.TotalCount, $"{actorName} has exactly one assigned active inbox item");
            var item = page.Items.Single();
            scope.Equal(actorName, item.Assignee, $"{actorName}'s inbox item is directly assigned");
            scope.True(item.ItemValue?.ValueKind == JsonValueKind.String
                       && item.ItemValue.Value.GetString() == actorName,
                $"{actorName}'s item snapshot matches the collection value",
                item.ItemValue?.ToString() ?? "null");
            tasks.Add(new AssignedTask(actorName, item.UserTaskId, item.ItemIndex));
        }
        return tasks;
    }

    private async Task<List<AssignedTaskRecord>> GetAssignedTaskRecordsAsync(
        CaseScope scope,
        long instanceId,
        IReadOnlyList<string> actorNames)
    {
        var records = new List<AssignedTaskRecord>();
        foreach (var actorName in actorNames)
        {
            var page = await GetTaskPageAsync(scope, Actor(actorName), instanceId);
            var task = page.Items.Single(t => t.MultiInstance is not null);
            scope.Equal(actorName, task.Assignee, $"{actorName}'s task is directly assigned");
            records.Add(new AssignedTaskRecord(actorName, task));
        }
        return records;
    }

    private async Task<List<UserTaskDto>> GetAssignedTaskDetailsAsync(
        CaseScope scope,
        IReadOnlyList<AssignedTask> tasks)
    {
        var details = new List<UserTaskDto>();
        foreach (var task in tasks)
            details.Add(await GetUserTaskAsync(scope, Actor(task.ActorName), task.TaskId));
        return details;
    }

    private async Task<UserTaskActionAckDto> CompleteTaskAsync(
        CaseScope scope,
        ActorToken actor,
        long taskId,
        int flowId,
        TakeFlowRequest request)
    {
        var response = await scope.SendAsync(actor, HttpMethod.Post,
            $"/api/user-tasks/{taskId}/flows/{flowId}", request);
        scope.ExpectStatus(response, HttpStatusCode.OK);
        return scope.Deserialize<UserTaskActionAckDto>(response);
    }

    private static TakeFlowRequest EmptyVariables() => new(new Dictionary<string, JsonElement>());

    private static TakeFlowRequest VoteVariables(string vote) => new(new Dictionary<string, JsonElement>
    {
        ["vote"] = JsonSerializer.SerializeToElement(vote)
    });

    private static MultiInstanceProgressDto RequireProgress(MultiInstanceProgressDto? progress, CaseScope scope)
    {
        scope.True(progress is not null, "Multi-instance progress is present", "multiInstance was null");
        return progress!;
    }

    private static void AssertProgress(
        CaseScope scope,
        MultiInstanceProgressDto progress,
        string mode,
        string status,
        int total,
        int completed,
        int active,
        int pending,
        int cancelled)
    {
        scope.Equal(mode, progress.Mode, "Multi-instance mode");
        scope.Equal(status, progress.Status, "Multi-instance status");
        scope.Equal(total, progress.Total, "Multi-instance total");
        scope.Equal(completed, progress.Completed, "Multi-instance completed count");
        scope.Equal(active, progress.Active, "Multi-instance active count");
        scope.Equal(pending, progress.Pending, "Multi-instance pending count");
        scope.Equal(cancelled, progress.Cancelled, "Multi-instance cancelled count");
    }

    private static List<MultiResultItem> LatestResult(
        CaseScope scope,
        InstanceDetailDto detail,
        string variableName)
    {
        var variable = detail.Variables.Where(v => v.VariableName.Equals(variableName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(v => v.Id)
            .LastOrDefault();
        scope.True(variable is not null, $"Result variable '{variableName}' exists", "Variable was absent");
        var result = variable!.Value.Deserialize<List<MultiResultItem>>(JsonOptions);
        scope.True(result is not null, $"Result variable '{variableName}' is a JSON array", variable.Value.ToString());
        return result!;
    }

    private static void AssertOrderedIndexes(CaseScope scope, IReadOnlyList<MultiResultItem> results, int count)
    {
        scope.Equal(count, results.Count, "Result collection contains every item");
        scope.SequenceEqual(Enumerable.Range(0, count).ToArray(), results.Select(r => r.Index).ToArray(),
            "Result collection is ordered by item index");
    }

    private long RequireWorkflow(long? id, string label)
    {
        RequireApi();
        return id ?? throw new SkipTestException($"The {label} workflow was not created.");
    }

    private void RequireApi()
    {
        if (!_apiReady) throw new SkipTestException("The API readiness case failed.");
    }

    private ActorToken Actor(string name) => _actors.TryGetValue(name, out var actor)
        ? actor
        : throw new InvalidOperationException($"Actor '{name}' is not configured.");

    private ActorToken Admin => Actor("admin");
    private ActorToken Manager => Actor("manager");
    private ActorToken Outsider => Actor("outsider");

    private static Dictionary<string, ActorToken> CreateActors(Options options)
    {
        var definitions = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["admin"] = ["admin", "sysAdmin", "Manager", "User", "Requester"],
            ["manager"] = ["Manager"],
            ["outsider"] = ["User"],
            ["alice"] = ["User"],
            ["bob"] = ["User"],
            ["carol"] = ["User"],
            ["dave"] = ["User"],
            ["erin"] = ["User"],
            ["alice1"] = ["User"],
            ["alice2"] = ["User"],
            ["CaseUser"] = ["User"],
            ["caseuser"] = ["User"],
            ["voter2"] = ["User"],
            ["voter3"] = ["User"],
            ["rejecter1"] = ["User"],
            ["rejecter2"] = ["User"],
            ["approver"] = ["User"],
            ["solo"] = ["User"],
            ["dupe"] = ["User"],
            ["DUPE"] = ["User"],
            ["seq0"] = ["User"],
            ["seq1"] = ["User"],
            ["seq2"] = ["User"],
            ["seq3"] = ["User"],
            ["seq4"] = ["User"],
            ["priority1"] = ["User"],
            ["priority2"] = ["User"],
            ["priority3"] = ["User"],
            ["ClaimUser"] = ["User"],
            ["claimuser"] = ["User"],
            ["loaduser"] = ["User"],
            ["no-role"] = [],
            ["missing-token"] = [],
            ["malformed-token"] = [],
            ["expired-token"] = []
        };

        var actors = definitions.ToDictionary(
            pair => pair.Key,
            pair => new ActorToken(pair.Key, pair.Value,
                CreateToken(pair.Key, pair.Value, options.JwtKey)),
            StringComparer.Ordinal);
        actors["missing-token"] = actors["missing-token"] with { Token = null };
        actors["malformed-token"] = actors["malformed-token"] with { Token = "not-a-jwt" };
        actors["expired-token"] = actors["expired-token"] with
        {
            Token = CreateToken("expired-token", [], options.JwtKey,
                DateTime.UtcNow.AddHours(-2), DateTime.UtcNow.AddHours(-1))
        };
        return actors;
    }

    private static string CreateToken(
        string name,
        IReadOnlyList<string> roles,
        string jwtKey,
        DateTime? notBefore = null,
        DateTime? expires = null)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, name),
            new(JwtRegisteredClaimNames.Sub, name)
        };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        var token = new JwtSecurityToken(
            issuer: Defaults.Issuer,
            audience: Defaults.Audience,
            claims: claims,
            notBefore: notBefore ?? DateTime.UtcNow.AddMinutes(-1),
            expires: expires ?? DateTime.UtcNow.AddHours(8),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static WorkflowModel Clone(WorkflowModel source) =>
        JsonSerializer.Deserialize<WorkflowModel>(JsonSerializer.Serialize(source, JsonOptions), JsonOptions)
        ?? throw new InvalidOperationException("Workflow clone failed.");

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";
}

sealed class CaseScope
{
    private readonly ApiTransport _api;
    private readonly List<HttpExchange> _exchanges = [];
    private readonly object _exchangeLock = new();

    public CaseScope(string id, ApiTransport api)
    {
        Id = id;
        _api = api;
    }

    public string Id { get; }
    public IReadOnlyList<HttpExchange> Exchanges
    {
        get
        {
            lock (_exchangeLock) return _exchanges.ToList();
        }
    }

    public async Task<ApiResponse> SendAsync(
        ActorToken actor,
        HttpMethod method,
        string path,
        object? body = null)
    {
        var response = await _api.SendAsync(actor, method, path, body);
        lock (_exchangeLock)
        {
            _exchanges.Add(new HttpExchange(
                method.Method,
                path,
                actor.Name,
                (int)response.StatusCode,
                response.DurationMilliseconds,
                Truncate(response.Body, 8000)));
        }
        return response;
    }

    public void ExpectStatus(ApiResponse response, HttpStatusCode expected)
    {
        if (response.StatusCode != expected)
        {
            throw new TestFailureException(
                $"Expected HTTP {(int)expected} but received {(int)response.StatusCode} for {response.Method} {response.Path}.",
                $"HTTP {(int)expected} {expected}",
                $"HTTP {(int)response.StatusCode} {response.StatusCode}: {Truncate(response.Body, 1500)}");
        }
    }

    public T Deserialize<T>(ApiResponse response)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(response.Body, MultiInstanceApiSuiteJson.Options)
                   ?? throw new JsonException("Response deserialized to null.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new TestFailureException(
                $"Could not deserialize {response.Method} {response.Path} as {typeof(T).Name}.",
                typeof(T).Name,
                $"{ex.Message}; body={Truncate(response.Body, 1500)}");
        }
    }

    public void True(bool condition, string expected, string actual)
    {
        if (!condition) throw new TestFailureException(expected, expected, actual);
    }

    public void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new TestFailureException(message, Format(expected), Format(actual));
    }

    public void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string message)
    {
        var expectedList = expected.ToList();
        var actualList = actual.ToList();
        if (!expectedList.SequenceEqual(actualList))
            throw new TestFailureException(message, Format(expectedList), Format(actualList));
    }

    public void Near(double expected, double actual, double tolerance, string message)
    {
        if (Math.Abs(expected - actual) > tolerance)
            throw new TestFailureException(message, $"{expected} +/- {tolerance}", actual.ToString("G17"));
    }

    private static string Format<T>(T value) =>
        value is null ? "null" : value is string text ? text : JsonSerializer.Serialize(value);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";
}

static class MultiInstanceApiSuiteJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

sealed class ApiTransport : IDisposable
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _jsonOptions;

    public ApiTransport(string apiBase, JsonSerializerOptions jsonOptions)
    {
        _jsonOptions = jsonOptions;
        _http = new HttpClient
        {
            BaseAddress = new Uri(apiBase.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(10)
        };
    }

    public async Task<ApiResponse> SendAsync(
        ActorToken actor,
        HttpMethod method,
        string path,
        object? body)
    {
        using var request = new HttpRequestMessage(method, path.TrimStart('/'));
        if (!string.IsNullOrWhiteSpace(actor.Token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", actor.Token);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: _jsonOptions);
        var stopwatch = Stopwatch.StartNew();
        using var response = await _http.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();
        stopwatch.Stop();
        return new ApiResponse(method.Method, path, response.StatusCode, content, stopwatch.ElapsedMilliseconds);
    }

    public void Dispose() => _http.Dispose();
}

static class ReportWriter
{
    public static async Task<ReportPaths> WriteAsync(
        QaReport report,
        string directory,
        JsonSerializerOptions jsonOptions)
    {
        var jsonPath = Path.Combine(directory, $"multi-instance-api-{report.RunId}.json");
        var markdownPath = Path.Combine(directory, $"multi-instance-api-{report.RunId}.md");
        var indented = new JsonSerializerOptions(jsonOptions) { WriteIndented = true };
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, indented));
        await File.WriteAllTextAsync(markdownPath, BuildMarkdown(report));
        return new ReportPaths(markdownPath, jsonPath);
    }

    private static string BuildMarkdown(QaReport report)
    {
        var builder = new StringBuilder();
        var passed = report.Cases.Count(c => c.Status == TestStatuses.Passed);
        var failed = report.Cases.Count(c => c.Status == TestStatuses.Failed);
        var skipped = report.Cases.Count(c => c.Status == TestStatuses.Skipped);
        builder.AppendLine("# Multi-Instance API QA Report");
        builder.AppendLine();
        builder.AppendLine($"- Run ID: `{report.RunId}`");
        builder.AppendLine($"- API: `{report.ApiBase}`");
        builder.AppendLine($"- Started: `{report.StartedAt:O}`");
        builder.AppendLine($"- Completed: `{report.CompletedAt:O}`");
        builder.AppendLine($"- Result: **{passed} passed, {failed} failed, {skipped} skipped**");
        builder.AppendLine();
        builder.AppendLine("## Created records");
        builder.AppendLine();
        builder.AppendLine("| Name | Value |");
        builder.AppendLine("| --- | --- |");
        foreach (var pair in report.Identifiers.OrderBy(p => p.Key))
            builder.AppendLine($"| `{Escape(pair.Key)}` | `{Escape(pair.Value)}` |");
        builder.AppendLine();
        builder.AppendLine("## Cases");
        builder.AppendLine();
        builder.AppendLine("| ID | Status | Duration | Case |");
        builder.AppendLine("| --- | --- | ---: | --- |");
        foreach (var result in report.Cases)
            builder.AppendLine($"| {result.Id} | **{result.Status}** | {result.DurationMilliseconds} ms | {Escape(result.Name)} |");

        foreach (var result in report.Cases.Where(c => c.Status != TestStatuses.Passed))
        {
            builder.AppendLine();
            builder.AppendLine($"### {result.Id}: {Escape(result.Name)}");
            builder.AppendLine();
            builder.AppendLine($"- Expected: `{Escape(result.Expected)}`");
            builder.AppendLine($"- Actual: `{Escape(result.Actual)}`");
            if (!string.IsNullOrWhiteSpace(result.Details))
            {
                builder.AppendLine();
                builder.AppendLine("```text");
                builder.AppendLine(Truncate(result.Details, 6000));
                builder.AppendLine("```");
            }
            if (result.Exchanges.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("| Method | Path | Actor | Status | Duration |");
                builder.AppendLine("| --- | --- | --- | ---: | ---: |");
                foreach (var exchange in result.Exchanges)
                    builder.AppendLine($"| {exchange.Method} | `{Escape(exchange.Path)}` | `{Escape(exchange.Actor)}` | {exchange.StatusCode} | {exchange.DurationMilliseconds} ms |");
            }
        }

        return builder.ToString();
    }

    private static string Escape(string value) => value.Replace("|", "\\|").Replace("`", "'");
    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "...";
}

sealed record Options(
    string ApiBase = Defaults.ApiBase,
    string JwtKey = Defaults.JwtKey,
    string? FixtureRoot = null,
    string? ReportDirectory = null,
    string? RunId = null,
    bool ManageApi = false,
    string? ApiProject = null,
    bool ShowHelp = false)
{
    public static Options Parse(string[] args)
    {
        var result = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--api" when i + 1 < args.Length:
                    result = result with { ApiBase = args[++i] };
                    break;
                case "--jwt-key" when i + 1 < args.Length:
                    result = result with { JwtKey = args[++i] };
                    break;
                case "--fixture-root" when i + 1 < args.Length:
                    result = result with { FixtureRoot = args[++i] };
                    break;
                case "--report-dir" when i + 1 < args.Length:
                    result = result with { ReportDirectory = args[++i] };
                    break;
                case "--run-id" when i + 1 < args.Length:
                    result = result with { RunId = args[++i] };
                    break;
                case "--manage-api":
                    result = result with { ManageApi = true };
                    break;
                case "--api-project" when i + 1 < args.Length:
                    result = result with { ApiProject = args[++i] };
                    break;
                case "--help" or "-h":
                    result = result with { ShowHelp = true };
                    break;
                default:
                    throw new ArgumentException($"Unknown or incomplete argument '{args[i]}'. Use --help.");
            }
        }
        return result;
    }

    public static void PrintHelp() => Console.WriteLine("""
        Usage: dotnet run --project tools/MultiInstanceApiTests -- [options]

          --api <url>          API base URL (default: http://localhost:5017)
          --jwt-key <key>      Development JWT signing key
          --fixture-root <dir> Directory containing workflow fixture JSON files
          --report-dir <dir>   Markdown/JSON report output directory
          --run-id <id>        Workflow/report run identifier
          --manage-api         Start, restart, and stop the API as a child process
          --api-project <path> API csproj used by --manage-api
          -h, --help           Show help
        """);
}

static class Defaults
{
    public const string ApiBase = "http://localhost:5017";
    public const string JwtKey = "dev-only-symmetric-signing-key-change-me-please-32+";
    public const string Issuer = "workflow-engine-dev";
    public const string Audience = "workflow-engine-api";
}

static class TestStatuses
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
}

sealed class RunState
{
    public long? ParallelWorkflowId { get; set; }
    public long? SequentialWorkflowId { get; set; }
    public long? CardinalityWorkflowId { get; set; }
    public long? EmptyCollectionWorkflowId { get; set; }
    public Dictionary<string, string> Identifiers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string DescribeWorkflowIds() =>
        $"parallel={ParallelWorkflowId}, sequential={SequentialWorkflowId}, cardinality={CardinalityWorkflowId}, empty={EmptyCollectionWorkflowId}";
}

sealed class TestFailureException : Exception
{
    public TestFailureException(string message, string expected, string actual) : base(message)
    {
        Expected = expected;
        Actual = actual;
    }

    public TestFailureException(string expected, string actual) : this(expected, expected, actual) { }
    public string Expected { get; }
    public string Actual { get; }
}

sealed class SkipTestException(string message) : Exception(message);

sealed record ActorToken(string Name, IReadOnlyList<string> Roles, string? Token);
sealed record ApiResponse(string Method, string Path, HttpStatusCode StatusCode, string Body, long DurationMilliseconds);
sealed record AssignedTask(string ActorName, long TaskId, int? ItemIndex);
sealed record AssignedTaskRecord(string ActorName, UserTaskDto Task);
sealed record MiScenario(long InstanceId, long ExecutionId, InstanceDetailDto Detail);
sealed record ReportPaths(string Markdown, string Json);

sealed record MultiResultItem(
    int Index,
    JsonElement? Item,
    long UserTaskId,
    string Status,
    int? SelectedFlowId,
    string? CompletedBy,
    DateTimeOffset? CompletedAt,
    Dictionary<string, JsonElement>? Variables);

sealed record HttpExchange(
    string Method,
    string Path,
    string Actor,
    int StatusCode,
    long DurationMilliseconds,
    string Body);

sealed record TestCaseResult(
    string Id,
    string Name,
    string Status,
    long DurationMilliseconds,
    string Expected,
    string Actual,
    string? Details,
    IReadOnlyList<HttpExchange> Exchanges);

sealed record QaReport(
    string RunId,
    string ApiBase,
    string FixtureRoot,
    string MachineName,
    string RuntimeVersion,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyDictionary<string, string> Identifiers,
    IReadOnlyList<TestCaseResult> Cases);
