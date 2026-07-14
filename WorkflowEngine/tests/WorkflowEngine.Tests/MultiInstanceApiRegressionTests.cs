using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WorkflowEngine.Infrastructure.Entities;
using WorkflowEngine.Shared.Dtos;
using WorkflowEngine.Shared.Models;
using Xunit;

namespace WorkflowEngine.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class MultiInstanceApiRegressionTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] AdminRoles = ["admin", "Manager", "User"];

    [Fact]
    public async Task DefinitionApi_CanonicalizesKnownCasingAndRejectsTyposAndDuplicates()
    {
        var canonical = LoadUniqueModel("votes-users-list.json", "canonical");
        var multi = canonical.FlowNodes.Single(node => node.Id == 2).MultiInstance!;
        multi.Mode = "SeQuEnTiAl";
        multi.Source = "CoLlEcTiOn";
        multi.CompletionEvaluation = "AfTeRaLl";

        using var accepted = await SendAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(canonical, false));
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var detail = await ReadAsync<WorkflowDetailDto>(accepted);
        var saved = detail.Definition.FlowNodes.Single(node => node.Id == 2).MultiInstance!;
        Assert.Equal("sequential", saved.Mode);
        Assert.Equal("collection", saved.Source);
        Assert.Equal("afterAll", saved.CompletionEvaluation);

        var typo = LoadUniqueModel("votes-users-list.json", "typo");
        typo.FlowNodes.Single(node => node.Id == 2).MultiInstance!.Mode = "sequentual";
        using var typoResponse = await SendAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(typo, false));
        Assert.Equal(HttpStatusCode.BadRequest, typoResponse.StatusCode);

        var duplicate = LoadUniqueModel("votes-users-list.json", "duplicate");
        duplicate.SequenceFlows.Add(Clone(duplicate.SequenceFlows[0]));
        using var duplicateResponse = await SendAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(duplicate, false));
        Assert.Equal(HttpStatusCode.BadRequest, duplicateResponse.StatusCode);

        var duplicateNode = LoadUniqueModel("votes-users-list.json", "duplicate-node");
        duplicateNode.FlowNodes.Add(Clone(duplicateNode.FlowNodes[0]));
        using var duplicateNodeResponse = await SendAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(duplicateNode, false));
        Assert.Equal(HttpStatusCode.BadRequest, duplicateNodeResponse.StatusCode);

        var duplicateVariable = LoadUniqueModel("votes-users-list.json", "duplicate-variable");
        duplicateVariable.Variables.Add(new VariableModel
        {
            Id = 99,
            Name = "VOTERS",
            DataType = WorkflowVariableTypes.String,
            IsArray = true,
            DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<string>())
        });
        using var duplicateVariableResponse = await SendAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(duplicateVariable, false));
        Assert.Equal(HttpStatusCode.BadRequest, duplicateVariableResponse.StatusCode);

        var nullMode = LoadUniqueModel("votes-users-list.json", "null-mode");
        nullMode.FlowNodes.Single(node => node.Id == 2).MultiInstance!.Mode = null!;
        using var nullModeResponse = await SendAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(nullMode, false));
        Assert.Equal(HttpStatusCode.BadRequest, nullModeResponse.StatusCode);
    }

    [Fact]
    public async Task CardinalityAndCollectionBoundsFailAtomicallyBeforeFanOut()
    {
        var cardinalityWorkflow = await CreateWorkflowAsync(
            LoadUniqueModel("votes-cardinality-approve-reject.json", "bounds-cardinality"));
        foreach (var value in new[]
                 {
                     JsonSerializer.SerializeToElement(int.MaxValue),
                     JsonSerializer.SerializeToElement((long)int.MinValue - 1),
                     JsonSerializer.SerializeToElement(1.5m),
                     JsonSerializer.SerializeToElement(1001)
                 })
        {
            var review = await StartAtReviewAsync(
                cardinalityWorkflow,
                new Dictionary<string, JsonElement> { ["voters"] = value });
            var stopwatch = Stopwatch.StartNew();
            using var response = await SendAsync(
                HttpMethod.Post,
                "/api/instances/" + review.Id + "/flows/204",
                new TakeFlowRequest(null));
            stopwatch.Stop();
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                "Invalid cardinality should fail before allocating child items.");
            await AssertEntryRolledBackAsync(review.Id);
        }

        var collectionWorkflow = await CreateWorkflowAsync(
            LoadUniqueModel("votes-users-list.json", "bounds-collection"));
        var collections = new[]
        {
            Enumerable.Range(0, 1001).Select(index => "user-" + index).ToArray(),
            new[] { new string('x', UserTaskConstraints.MaxActorNameLength + 1) }
        };
        foreach (var users in collections)
        {
            var review = await StartAtReviewAsync(collectionWorkflow);
            using var response = await SendAsync(
                HttpMethod.Post,
                "/api/instances/" + review.Id + "/flows/204",
                new TakeFlowRequest(new Dictionary<string, JsonElement>
                {
                    ["voters"] = JsonSerializer.SerializeToElement(users)
                }));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertEntryRolledBackAsync(review.Id);
        }
    }

    [Fact]
    public async Task ParentInterruptPersistsResolvedVariablesBeforeGatewayRouting()
    {
        var model = LoadUniqueModel("votes-users-list.json", "interrupt-variable");
        model.Variables.Add(new VariableModel
        {
            Id = 50,
            Name = "interruptReason",
            DataType = WorkflowVariableTypes.String,
            DefaultValue = JsonSerializer.SerializeToElement("")
        });
        model.Variables.Add(new VariableModel
        {
            Id = 52,
            Name = "interruptCategory",
            DataType = WorkflowVariableTypes.String,
            DefaultValue = JsonSerializer.SerializeToElement("")
        });
        var interrupt = model.SequenceFlows.Single(flow => flow.Id == 203);
        interrupt.TargetRef = 8;
        interrupt.Variables =
        [
            new VariableModel
            {
                Id = 51,
                Name = "interruptReason",
                DataType = WorkflowVariableTypes.String,
                Required = true
            },
            new VariableModel
            {
                Id = 52,
                Name = "interruptCategory",
                DataType = WorkflowVariableTypes.String,
                DefaultValue = JsonSerializer.SerializeToElement("system")
            }
        ];
        model.FlowNodes.AddRange(
        [
            new FlowNodeModel { Id = 8, Name = "Route interrupt", Type = BpmnFlowNodeTypes.ExclusiveGateway },
            new FlowNodeModel { Id = 9, Name = "Urgent interrupt", Type = BpmnFlowNodeTypes.EndEvent },
            new FlowNodeModel { Id = 10, Name = "Other interrupt", Type = BpmnFlowNodeTypes.EndEvent }
        ]);
        model.SequenceFlows.AddRange(
        [
            new SequenceFlowModel
            {
                Id = 301,
                Name = "Urgent",
                SourceRef = 8,
                TargetRef = 9,
                Condition = "interruptReason == 'urgent' and interruptCategory == 'system'"
            },
            new SequenceFlowModel
            {
                Id = 302,
                Name = "Other",
                SourceRef = 8,
                TargetRef = 10,
                IsDefault = true
            }
        ]);

        var workflowId = await CreateWorkflowAsync(model);
        var scenario = await StartAndEnterAsync(workflowId);
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/multi-instance-executions/" + scenario.MultiInstance!.ExecutionId + "/flows/203",
            new TakeFlowRequest(new Dictionary<string, JsonElement>
            {
                ["interruptReason"] = JsonSerializer.SerializeToElement("urgent")
            }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await ReadAsync<InstanceDetailDto>(response);

        Assert.Equal(9, detail.CurrentNodeId);
        Assert.Equal("completed", detail.Status);
        var stored = detail.Variables.Last(variable =>
            variable.VariableName.Equals("interruptReason", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("urgent", stored.Value.GetString());
        Assert.Equal(203, stored.SourceFlowId);
        var defaulted = detail.Variables.Last(variable =>
            variable.VariableName.Equals("interruptCategory", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("system", defaulted.Value.GetString());
        Assert.Equal(203, defaulted.SourceFlowId);
    }

    [Fact]
    public async Task WorkSummaryIsAccurateAndChildClaimsTouchParentTimestamp()
    {
        var cardinality = LoadUniqueModel("votes-cardinality-approve-reject.json", "work-summary");
        cardinality.FlowNodes.Single(node => node.Id == 2).RequiresClaim = true;
        var workflowId = await CreateWorkflowAsync(cardinality);
        var scenario = await StartAndEnterAsync(
            workflowId,
            new Dictionary<string, JsonElement>
            {
                ["voters"] = JsonSerializer.SerializeToElement(3)
            });
        var before = scenario.UpdatedAt;
        var tasks = await ListTasksAsync(scenario.Id, "alice", "User");
        Assert.Equal(3, tasks.Items.Count);
        var orderingPeer = await StartAndEnterAsync(
            workflowId,
            new Dictionary<string, JsonElement>
            {
                ["voters"] = JsonSerializer.SerializeToElement(3)
            });

        await Task.Delay(10);
        using var aliceClaim = await SendAsync(
            HttpMethod.Post,
            "/api/user-tasks/" + tasks.Items[0].Id + "/claim",
            null,
            "alice",
            "User");
        Assert.Equal(HttpStatusCode.OK, aliceClaim.StatusCode);
        using var bobClaim = await SendAsync(
            HttpMethod.Post,
            "/api/user-tasks/" + tasks.Items[1].Id + "/claim",
            null,
            "bob",
            "User");
        Assert.Equal(HttpStatusCode.OK, bobClaim.StatusCode);

        var detail = await GetInstanceAsync(scenario.Id);
        Assert.NotNull(detail.UserTasks);
        Assert.True(detail.UserTasks.IsMultiInstance);
        Assert.Equal(3, detail.UserTasks.ActiveCount);
        Assert.Equal(0, detail.UserTasks.PendingCount);
        Assert.Equal(2, detail.UserTasks.ClaimedCount);
        Assert.Null(detail.UserTasks.SoleClaimedBy);
        Assert.True(detail.UpdatedAt > before);

        using var listResponse = await SendAsync(
            HttpMethod.Get,
            "/api/instances?instanceId=" + scenario.Id);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listed = await ReadAsync<PagedResult<InstanceSummaryDto>>(listResponse);
        var summary = Assert.Single(listed.Items);
        Assert.Equal(2, summary.UserTasks!.ClaimedCount);

        using var orderedListResponse = await SendAsync(
            HttpMethod.Get,
            $"/api/instances?workflowId={workflowId}&pageSize=200");
        Assert.Equal(HttpStatusCode.OK, orderedListResponse.StatusCode);
        var orderedList = await ReadAsync<PagedResult<InstanceSummaryDto>>(orderedListResponse);
        Assert.Contains(orderedList.Items, item => item.Id == orderingPeer.Id);
        Assert.Equal(scenario.Id, orderedList.Items[0].Id);

        var soleScenario = await StartAndEnterAsync(
            workflowId,
            new Dictionary<string, JsonElement>
            {
                ["voters"] = JsonSerializer.SerializeToElement(1)
            });
        var soleTask = Assert.Single((await ListTasksAsync(soleScenario.Id, "alice", "User")).Items);
        using var soleClaim = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{soleTask.Id}/claim",
            null,
            "alice",
            "User");
        Assert.Equal(HttpStatusCode.OK, soleClaim.StatusCode);
        var soleOwner = await GetInstanceAsync(soleScenario.Id);
        Assert.Equal("alice", soleOwner.UserTasks!.SoleClaimedBy);

        var sequentialWorkflow = await CreateWorkflowAsync(
            LoadUniqueModel("votes-sequence-users-list.json", "sequential-summary"));
        var sequential = await StartAndEnterAsync(sequentialWorkflow);
        Assert.Equal(1, sequential.UserTasks!.ActiveCount);
        Assert.Equal(2, sequential.UserTasks.PendingCount);
        Assert.Equal("alice", sequential.UserTasks.SoleAssignee);
    }

    [Fact]
    public async Task ConcurrentClaimsCompletionsInterruptAndCancellationSerializeWithoutDoubleAdvance()
    {
        var claimModel = LoadUniqueModel("votes-cardinality-approve-reject.json", "concurrent-claim");
        claimModel.FlowNodes.Single(node => node.Id == 2).RequiresClaim = true;
        var claimWorkflow = await CreateWorkflowAsync(claimModel);
        var claimScenario = await StartAndEnterAsync(
            claimWorkflow,
            new Dictionary<string, JsonElement>
            {
                ["voters"] = JsonSerializer.SerializeToElement(2)
            });
        var claimable = await ListTasksAsync(claimScenario.Id, "alice", "User");
        var claimTaskId = claimable.Items[0].Id;
        var competingClaims = await Task.WhenAll(
                SendAsync(HttpMethod.Post, $"/api/user-tasks/{claimTaskId}/claim", null, "alice", "User"),
                SendAsync(HttpMethod.Post, $"/api/user-tasks/{claimTaskId}/claim", null, "bob", "User"))
            .WaitAsync(TimeSpan.FromSeconds(10));
        using (competingClaims[0])
        using (competingClaims[1])
        {
            Assert.Equal(1, competingClaims.Count(response => response.StatusCode == HttpStatusCode.OK));
            Assert.Equal(1, competingClaims.Count(response => response.StatusCode == HttpStatusCode.Conflict));
        }

        var parallelModel = LoadUniqueModel("votes-users-list.json", "concurrent-completion");
        parallelModel.Variables.Single(variable => variable.Name == "requiredApprovals").DefaultValue =
            JsonSerializer.SerializeToElement(2);
        var parallelWorkflow = await CreateWorkflowAsync(parallelModel);
        var parallel = await StartAndEnterAsync(parallelWorkflow);
        var aliceTask = Assert.Single((await ListTasksAsync(parallel.Id, "alice", "User")).Items);
        var bobTask = Assert.Single((await ListTasksAsync(parallel.Id, "bob", "User")).Items);
        var completionPayload = new TakeFlowRequest(new Dictionary<string, JsonElement>
        {
            ["vote"] = JsonSerializer.SerializeToElement("approve")
        });
        var completions = await Task.WhenAll(
                SendAsync(HttpMethod.Post, $"/api/user-tasks/{aliceTask.Id}/flows/201", completionPayload, "alice", "User"),
                SendAsync(HttpMethod.Post, $"/api/user-tasks/{bobTask.Id}/flows/201", completionPayload, "bob", "User"))
            .WaitAsync(TimeSpan.FromSeconds(10));
        using (completions[0])
        using (completions[1])
        {
            Assert.All(completions, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        }

        var completedDetail = await GetInstanceAsync(parallel.Id);
        Assert.Equal("completed", completedDetail.Status);
        Assert.Equal(3, completedDetail.CurrentNodeId);
        await using (var db = fixture.CreateDbContext())
        {
            var execution = await db.MultiInstanceExecutions.SingleAsync(item => item.InstanceId == parallel.Id);
            Assert.Equal(MultiInstanceExecutionStatuses.Completed, execution.Status);
            Assert.Equal(2, execution.CompletedCount);
            Assert.Equal(201, execution.WinningFlowId);
            Assert.Equal(1, await db.InstanceHistory.CountAsync(history =>
                history.InstanceId == parallel.Id && history.Note == "multiInstanceComplete"));
        }

        var interruptWorkflow = await CreateWorkflowAsync(
            LoadUniqueModel("votes-users-list.json", "interrupt-cancel-race"));
        var interruptScenario = await StartAndEnterAsync(interruptWorkflow);
        var interruptAndCancel = await Task.WhenAll(
                SendAsync(HttpMethod.Post,
                    $"/api/multi-instance-executions/{interruptScenario.MultiInstance!.ExecutionId}/flows/203",
                    new TakeFlowRequest(null), "manager", "Manager"),
                SendAsync(HttpMethod.Post, $"/api/instances/{interruptScenario.Id}/cancel", null,
                    "test-admin", "admin", "Manager"))
            .WaitAsync(TimeSpan.FromSeconds(10));
        using (interruptAndCancel[0])
        using (interruptAndCancel[1])
        {
            Assert.Contains(interruptAndCancel[0].StatusCode,
                new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
            Assert.Equal(HttpStatusCode.NoContent, interruptAndCancel[1].StatusCode);
        }

        var cancelledDetail = await GetInstanceAsync(interruptScenario.Id);
        Assert.Equal("cancelled", cancelledDetail.Status);
        await using (var db = fixture.CreateDbContext())
        {
            Assert.False(await db.MultiInstanceExecutions.AnyAsync(execution =>
                execution.InstanceId == interruptScenario.Id
                && execution.Status == MultiInstanceExecutionStatuses.Active));
            Assert.False(await db.UserTasks.AnyAsync(task =>
                task.InstanceId == interruptScenario.Id
                && (task.Status == UserTaskStatuses.Active || task.Status == UserTaskStatuses.Pending)));
        }
    }

    [Fact]
    public async Task SequentialModeActivatesOneItemAndInterruptCancelsActiveAndPendingRemainders()
    {
        var workflow = await CreateWorkflowAsync(
            LoadUniqueModel("votes-sequence-users-list.json", "sequential-lifecycle"));
        var scenario = await StartAndEnterAsync(workflow);
        var executionId = scenario.MultiInstance!.ExecutionId;
        Assert.Equal(1, scenario.UserTasks!.ActiveCount);
        Assert.Equal(2, scenario.UserTasks.PendingCount);

        var first = Assert.Single((await ListTasksAsync(scenario.Id, "alice", "User")).Items);
        using var completion = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{first.Id}/flows/201",
            new TakeFlowRequest(null),
            "alice",
            "User");
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);

        var afterFirst = await GetInstanceAsync(scenario.Id);
        Assert.Equal(1, afterFirst.UserTasks!.ActiveCount);
        Assert.Equal(1, afterFirst.UserTasks.PendingCount);
        Assert.Equal("alice1", afterFirst.UserTasks.SoleAssignee);

        using var interrupt = await SendAsync(
            HttpMethod.Post,
            $"/api/multi-instance-executions/{executionId}/flows/203",
            new TakeFlowRequest(null),
            "manager",
            "Manager");
        Assert.Equal(HttpStatusCode.OK, interrupt.StatusCode);
        var interrupted = await ReadAsync<InstanceDetailDto>(interrupt);
        Assert.Equal("running", interrupted.Status);
        Assert.Equal(5, interrupted.CurrentNodeId);

        await using var db = fixture.CreateDbContext();
        var statuses = await db.UserTasks.AsNoTracking()
            .Where(task => task.MultiInstanceExecutionId == executionId)
            .GroupBy(task => task.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Status, item => item.Count);
        Assert.Equal(1, statuses[UserTaskStatuses.Completed]);
        Assert.Equal(2, statuses[UserTaskStatuses.Cancelled]);
        Assert.False(statuses.ContainsKey(UserTaskStatuses.Active));
        Assert.False(statuses.ContainsKey(UserTaskStatuses.Pending));
    }

    [Fact]
    public async Task EntryAndCancellationRaceNeverLeavesOpenMultiInstanceWork()
    {
        var workflowId = await CreateWorkflowAsync(
            LoadUniqueModel("votes-users-list.json", "cancel-entry-race"));

        for (var attempt = 0; attempt < 12; attempt++)
        {
            var review = await StartAtReviewAsync(workflowId);
            await using var barrierConnection = new NpgsqlConnection(fixture.ConnectionString);
            await barrierConnection.OpenAsync();
            await using var barrierTransaction = await barrierConnection.BeginTransactionAsync();
            await using (var barrierCommand = new NpgsqlCommand(
                             "SELECT \"Id\" FROM workflow_instances WHERE \"Id\" = @id FOR UPDATE",
                             barrierConnection,
                             barrierTransaction))
            {
                barrierCommand.Parameters.AddWithValue("id", review.Id);
                await barrierCommand.ExecuteScalarAsync();
            }
            var enterTask = SendAsync(
                HttpMethod.Post,
                "/api/instances/" + review.Id + "/flows/204",
                new TakeFlowRequest(null));
            var cancelTask = SendAsync(
                HttpMethod.Post,
                "/api/instances/" + review.Id + "/cancel",
                null);
            await Task.Delay(25);
            await barrierTransaction.CommitAsync();
            var responses = await Task.WhenAll(enterTask, cancelTask)
                .WaitAsync(TimeSpan.FromSeconds(10));
            using var enter = responses[0];
            using var cancel = responses[1];
            Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
            Assert.Contains(enter.StatusCode,
                new[] { HttpStatusCode.OK, HttpStatusCode.BadRequest, HttpStatusCode.Conflict });

            var detail = await GetInstanceAsync(review.Id);
            Assert.Equal("cancelled", detail.Status);
            await using var db = fixture.CreateDbContext();
            Assert.False(await db.MultiInstanceExecutions.AnyAsync(execution =>
                execution.InstanceId == review.Id &&
                execution.Status == MultiInstanceExecutionStatuses.Active));
            Assert.False(await db.UserTasks.AnyAsync(task =>
                task.InstanceId == review.Id &&
                (task.Status == UserTaskStatuses.Active || task.Status == UserTaskStatuses.Pending)));
        }
    }

    private async Task AssertEntryRolledBackAsync(long instanceId)
    {
        var detail = await GetInstanceAsync(instanceId);
        Assert.Equal(5, detail.CurrentNodeId);
        Assert.Equal("running", detail.Status);
        Assert.Null(detail.MultiInstance);
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.MultiInstanceExecutions.AnyAsync(execution =>
            execution.InstanceId == instanceId));
    }

    private async Task<long> CreateWorkflowAsync(WorkflowModel model)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(model, true));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<WorkflowDetailDto>(response)).Id;
    }

    private async Task<InstanceDetailDto> StartAtReviewAsync(
        long workflowId,
        Dictionary<string, JsonElement>? variables = null)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, variables));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = await ReadAsync<InstanceDetailDto>(response);
        Assert.Equal(5, detail.CurrentNodeId);
        return detail;
    }

    private async Task<InstanceDetailDto> StartAndEnterAsync(
        long workflowId,
        Dictionary<string, JsonElement>? variables = null)
    {
        var review = await StartAtReviewAsync(workflowId, variables);
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/instances/" + review.Id + "/flows/204",
            new TakeFlowRequest(null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await ReadAsync<InstanceDetailDto>(response);
        Assert.Equal(2, detail.CurrentNodeId);
        Assert.NotNull(detail.MultiInstance);
        return detail;
    }

    private async Task<InstanceDetailDto> GetInstanceAsync(long instanceId)
    {
        using var response = await SendAsync(HttpMethod.Get, "/api/instances/" + instanceId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<InstanceDetailDto>(response);
    }

    private async Task<PagedResult<UserTaskDto>> ListTasksAsync(
        long instanceId,
        string user,
        params string[] roles)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            "/api/instances/" + instanceId + "/user-tasks?status=active&page=1&pageSize=200",
            null,
            user,
            roles);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<PagedResult<UserTaskDto>>(response);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "test-admin",
        params string[] roles)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, user, roles.Length == 0 ? AdminRoles : roles);
        return await fixture.Client.SendAsync(request);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException("Response body was empty.");

    private static WorkflowModel LoadUniqueModel(string fileName, string label)
    {
        var model = DefinitionValidationTests.LoadModel(fileName);
        var suffix = Guid.NewGuid().ToString("N");
        model.Id = "tests-" + label + "-" + suffix;
        model.Name = "Tests " + label + " " + suffix;
        return model;
    }

    private static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))
        ?? throw new InvalidOperationException("Fixture clone failed.");
}
