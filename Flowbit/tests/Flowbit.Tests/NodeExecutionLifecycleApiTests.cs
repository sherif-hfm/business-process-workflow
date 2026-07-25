using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class NodeExecutionLifecycleApiTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task NormalUserTaskLifecycleTracksVisitsActorsPointerAndExecutionLocalVariables()
    {
        var model = CreateNormalUserTaskModel("normal");
        var workflowId = await CreateWorkflowAsync(model);
        var startValues = new Dictionary<string, JsonElement>
        {
            ["requestId"] = JsonSerializer.SerializeToElement("REQ-42")
        };

        using var startResponse = await SendAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, startValues),
            "starter",
            ["Starter"],
            suppressDefaultAdmin: true);
        Assert.Equal(HttpStatusCode.Created, startResponse.StatusCode);
        var started = await ReadAsync<InstanceDetailDto>(startResponse);
        Assert.Equal(2, started.CurrentNodeId);

        long taskId;
        long startExecutionId;
        long taskExecutionId;
        await using (var db = fixture.CreateDbContext())
        {
            var executions = await db.NodeExecutions.AsNoTracking()
                .Where(execution => execution.InstanceId == started.Id)
                .OrderBy(execution => execution.Id)
                .ToListAsync();
            Assert.Equal(2, executions.Count);

            var start = Assert.Single(executions, execution => execution.NodeId == 1);
            Assert.Equal(NodeExecutionStatuses.Completed, start.Status);
            Assert.Equal(NodeExecutionCompletionReasons.Normal, start.CompletionReason);
            Assert.Equal("starter", start.TriggeredBy);
            Assert.Equal("starter", start.CompletedBy);
            Assert.Equal(["Starter"], StringList(start.TriggeredByRolesJson));
            Assert.Equal(["Starter"], StringList(start.CompletedByRolesJson));
            Assert.Null(start.EnteredViaFlowId);
            Assert.Equal(101, start.ExitedViaFlowId);
            startExecutionId = start.Id;

            var task = Assert.Single(executions, execution => execution.NodeId == 2);
            Assert.Equal(NodeExecutionStatuses.Active, task.Status);
            Assert.Equal(NodeExecutionKinds.Node, task.ExecutionKind);
            Assert.Equal(101, task.EnteredViaFlowId);
            Assert.Equal(["Reviewer"], StringList(task.NodeRolesJson));
            Assert.Equal("starter", task.TriggeredBy);
            Assert.Equal(["Starter"], StringList(task.TriggeredByRolesJson));
            Assert.Null(task.CompletedAt);
            taskExecutionId = task.Id;
            taskId = Assert.IsType<long>(task.UserTaskId);

            var token = await db.ExecutionTokens.AsNoTracking()
                .SingleAsync(token => token.InstanceId == started.Id);
            Assert.Equal(taskExecutionId, token.CurrentNodeExecutionId);

            var requestVariable = await db.InstanceVariables.AsNoTracking()
                .SingleAsync(variable =>
                    variable.InstanceId == started.Id
                    && variable.VariableName == "requestId");
            Assert.Equal(startExecutionId, requestVariable.NodeExecutionId);
        }

        var submitted = new Dictionary<string, JsonElement>
        {
            ["decision"] = JsonSerializer.SerializeToElement("approved")
        };
        using var actionResponse = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{taskId}/flows/201",
            new TakeFlowRequest(submitted),
            "finisher",
            ["Reviewer"],
            suppressDefaultAdmin: true);
        Assert.Equal(HttpStatusCode.OK, actionResponse.StatusCode);

        await using (var db = fixture.CreateDbContext())
        {
            var executions = await db.NodeExecutions.AsNoTracking()
                .Where(execution => execution.InstanceId == started.Id)
                .OrderBy(execution => execution.Id)
                .ToListAsync();
            Assert.Equal(3, executions.Count);
            Assert.Single(executions.Select(execution => execution.ExecutionTokenId).Distinct());

            var task = Assert.Single(executions, execution => execution.NodeId == 2);
            Assert.Equal(taskExecutionId, task.Id);
            Assert.Equal(NodeExecutionStatuses.Completed, task.Status);
            Assert.Equal(NodeExecutionCompletionReasons.UserAction, task.CompletionReason);
            Assert.Equal(201, task.SelectedFlowId);
            Assert.Equal(201, task.ExitedViaFlowId);
            Assert.Equal("finisher", task.CompletedBy);
            Assert.Equal(["Reviewer"], StringList(task.CompletedByRolesJson));
            Assert.NotNull(task.CompletedAt);

            var end = Assert.Single(executions, execution => execution.NodeId == 3);
            Assert.Equal(NodeExecutionStatuses.Completed, end.Status);
            Assert.Equal(NodeExecutionCompletionReasons.NormalEnd, end.CompletionReason);
            Assert.Equal(201, end.EnteredViaFlowId);
            Assert.Equal("finisher", end.TriggeredBy);
            Assert.Equal("finisher", end.CompletedBy);

            var token = await db.ExecutionTokens.AsNoTracking()
                .SingleAsync(candidate => candidate.InstanceId == started.Id);
            Assert.Equal(ExecutionTokenStatuses.Completed, token.Status);
            Assert.Null(token.CurrentNodeExecutionId);

            var decisionVariable = await db.InstanceVariables.AsNoTracking()
                .SingleAsync(variable =>
                    variable.InstanceId == started.Id
                    && variable.VariableName == "decision");
            Assert.Equal(taskExecutionId, decisionVariable.NodeExecutionId);
        }

        using var detailResponse = await SendAsync(
            HttpMethod.Get,
            $"/api/node-executions/{taskExecutionId}",
            user: "activity-reader");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detail = await ReadAsync<NodeExecutionDetailDto>(detailResponse);
        Assert.Equal("starter", detail.StartedBy);
        Assert.Equal(["Starter"], detail.StartedByRoles);
        Assert.Equal("finisher", detail.CompletedBy);
        Assert.Equal(["Reviewer"], detail.CompletedByRoles);
        Assert.Equal(["Reviewer"], detail.NodeRoles);
        var change = Assert.Single(detail.VariableChanges);
        Assert.Equal("decision", change.VariableName);
        Assert.Equal(201, change.SourceActionId);
        Assert.Equal("approved", change.Value.GetString());
    }

    [Fact]
    public async Task SequentialMultiInstanceTracksOnlyChildVisitsAndActivatesThenCancelsRemainders()
    {
        var model = DefinitionValidationTests.LoadModel("votes-sequence-users-list.json");
        var suffix = Guid.NewGuid().ToString("N");
        model.Id = "node-execution-mi-" + suffix;
        model.Name = "Node execution MI " + suffix;
        var workflowId = await CreateWorkflowAsync(model);

        using var startResponse = await SendAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, null),
            "manager",
            ["Manager"],
            suppressDefaultAdmin: true);
        Assert.Equal(HttpStatusCode.Created, startResponse.StatusCode);
        var started = await ReadAsync<InstanceDetailDto>(startResponse);
        Assert.Equal(5, started.CurrentNodeId);

        long reviewTaskId;
        await using (var db = fixture.CreateDbContext())
        {
            reviewTaskId = await db.UserTasks.AsNoTracking()
                .Where(task =>
                    task.InstanceId == started.Id
                    && task.NodeId == 5
                    && task.Status == UserTaskStatuses.Active)
                .Select(task => task.Id)
                .SingleAsync();
        }

        using var enterResponse = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{reviewTaskId}/flows/204",
            new TakeFlowRequest(null),
            "manager",
            ["Manager"],
            suppressDefaultAdmin: true);
        Assert.Equal(HttpStatusCode.OK, enterResponse.StatusCode);

        long multiInstanceId;
        long firstTaskId;
        await using (var db = fixture.CreateDbContext())
        {
            multiInstanceId = await db.MultiInstanceExecutions.AsNoTracking()
                .Where(execution => execution.InstanceId == started.Id)
                .Select(execution => execution.Id)
                .SingleAsync();
            var children = await db.NodeExecutions.AsNoTracking()
                .Where(execution =>
                    execution.InstanceId == started.Id
                    && execution.NodeId == 2)
                .OrderBy(execution => execution.ItemIndex)
                .ToListAsync();

            Assert.Equal(3, children.Count);
            Assert.All(children, child =>
            {
                Assert.Equal(NodeExecutionKinds.UserTaskItem, child.ExecutionKind);
                Assert.Equal(multiInstanceId, child.MultiInstanceExecutionId);
                Assert.NotNull(child.UserTaskId);
            });
            Assert.Equal([0, 1, 2], children.Select(child => child.ItemIndex));
            Assert.Equal(3, children.Select(child => child.UserTaskId).Distinct().Count());
            Assert.Equal(NodeExecutionStatuses.Active, children[0].Status);
            Assert.NotNull(children[0].StartedAt);
            Assert.All(children.Skip(1), child =>
            {
                Assert.Equal(NodeExecutionStatuses.Pending, child.Status);
                Assert.Null(child.StartedAt);
            });

            var token = await db.ExecutionTokens.AsNoTracking()
                .SingleAsync(candidate => candidate.InstanceId == started.Id);
            Assert.Null(token.CurrentNodeExecutionId);
            firstTaskId = children[0].UserTaskId!.Value;
        }

        using var firstCompletion = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{firstTaskId}/flows/201",
            new TakeFlowRequest(null),
            "alice",
            ["User"],
            suppressDefaultAdmin: true);
        Assert.Equal(HttpStatusCode.OK, firstCompletion.StatusCode);

        await using (var db = fixture.CreateDbContext())
        {
            var children = await db.NodeExecutions.AsNoTracking()
                .Where(execution => execution.MultiInstanceExecutionId == multiInstanceId)
                .OrderBy(execution => execution.ItemIndex)
                .ToListAsync();
            Assert.Equal(NodeExecutionStatuses.Completed, children[0].Status);
            Assert.Equal(NodeExecutionCompletionReasons.MultiInstanceItem, children[0].CompletionReason);
            Assert.Equal(201, children[0].SelectedFlowId);
            Assert.Null(children[0].ExitedViaFlowId);
            Assert.Equal("alice", children[0].CompletedBy);
            Assert.Equal(["User"], StringList(children[0].CompletedByRolesJson));

            Assert.Equal(NodeExecutionStatuses.Active, children[1].Status);
            Assert.NotNull(children[1].StartedAt);
            Assert.Equal("alice", children[1].TriggeredBy);
            Assert.Equal(["User"], StringList(children[1].TriggeredByRolesJson));
            Assert.Equal(NodeExecutionStatuses.Pending, children[2].Status);
            Assert.Null(children[2].StartedAt);
        }

        using var interruptResponse = await SendAsync(
            HttpMethod.Post,
            $"/api/multi-instance-executions/{multiInstanceId}/flows/203",
            new TakeFlowRequest(null),
            "manager",
            ["Manager"],
            suppressDefaultAdmin: true);
        Assert.Equal(HttpStatusCode.OK, interruptResponse.StatusCode);

        await using (var db = fixture.CreateDbContext())
        {
            var children = await db.NodeExecutions.AsNoTracking()
                .Where(execution => execution.MultiInstanceExecutionId == multiInstanceId)
                .OrderBy(execution => execution.ItemIndex)
                .ToListAsync();
            Assert.Equal(3, children.Count);
            Assert.Equal(3, children.Select(child => (child.MultiInstanceExecutionId, child.ItemIndex)).Distinct().Count());
            Assert.Equal(NodeExecutionStatuses.Completed, children[0].Status);
            Assert.All(children.Skip(1), child =>
            {
                Assert.Equal(NodeExecutionStatuses.Cancelled, child.Status);
                Assert.Equal(NodeExecutionCompletionReasons.MultiInstanceInterrupt, child.CompletionReason);
                Assert.Equal("manager", child.CompletedBy);
                Assert.Equal(["Manager"], StringList(child.CompletedByRolesJson));
                Assert.NotNull(child.CompletedAt);
            });
            Assert.DoesNotContain(children, child =>
                child.Status is NodeExecutionStatuses.Active or NodeExecutionStatuses.Pending);

            // A multi-instance parent token has no duplicate visit row. The only
            // rows for node #2 are the three child work-item executions.
            Assert.Equal(3, await db.NodeExecutions.CountAsync(execution =>
                execution.InstanceId == started.Id
                && execution.NodeId == 2));
            Assert.False(await db.NodeExecutions.AnyAsync(execution =>
                execution.InstanceId == started.Id
                && execution.NodeId == 2
                && execution.ExecutionKind == NodeExecutionKinds.Node));
        }
    }

    [Fact]
    public async Task ParallelForkAndJoinCreatePerTokenVisitsAndMergeOnlyJoinLosers()
    {
        var workflowId = await CreateWorkflowAsync(
            ParallelGatewayApiTests.CreateParallelWorkflow());
        using var startResponse = await SendAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, null),
            "starter",
            ["Manager"],
            suppressDefaultAdmin: true);
        Assert.Equal(HttpStatusCode.Created, startResponse.StatusCode);
        var started = await ReadAsync<InstanceDetailDto>(startResponse);

        long managerTaskId;
        await using (var db = fixture.CreateDbContext())
        {
            managerTaskId = await db.UserTasks.AsNoTracking()
                .Where(task =>
                    task.InstanceId == started.Id
                    && task.NodeId == 3
                    && task.Status == UserTaskStatuses.Active)
                .Select(task => task.Id)
                .SingleAsync();

            var branchVisits = await db.NodeExecutions.AsNoTracking()
                .Where(execution =>
                    execution.InstanceId == started.Id
                    && (execution.NodeId == 3
                        || execution.NodeId == 4
                        || execution.NodeId == 5))
                .ToListAsync();
            Assert.Equal(3, branchVisits.Count);
            Assert.Equal(3, branchVisits.Select(visit => visit.ExecutionTokenId).Distinct().Count());
            Assert.Equal(3, branchVisits.Select(visit => visit.EntryParallelBranchId).Distinct().Count());
        }

        using var managerCompletion = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{managerTaskId}/flows/301",
            new TakeFlowRequest(null),
            "manager",
            ["Manager"],
            suppressDefaultAdmin: true);
        Assert.Equal(HttpStatusCode.OK, managerCompletion.StatusCode);

        await using var finalDb = fixture.CreateDbContext();
        var joins = await finalDb.NodeExecutions.AsNoTracking()
            .Where(execution =>
                execution.InstanceId == started.Id
                && execution.NodeId == 6)
            .OrderBy(execution => execution.ExecutionTokenId)
            .ToListAsync();
        Assert.Equal(3, joins.Count);
        Assert.Equal(3, joins.Select(join => join.ExecutionTokenId).Distinct().Count());
        Assert.Equal(3, joins.Select(join => join.EntryParallelBranchId).Distinct().Count());

        var survivor = Assert.Single(joins, join => join.Status == NodeExecutionStatuses.Completed);
        Assert.Equal(NodeExecutionCompletionReasons.ParallelJoin, survivor.CompletionReason);
        Assert.Equal(601, survivor.ExitedViaFlowId);
        Assert.Equal("manager", survivor.CompletedBy);
        var losers = joins.Where(join => join.Status == NodeExecutionStatuses.Merged).ToList();
        Assert.Equal(2, losers.Count);
        Assert.All(losers, loser =>
        {
            Assert.Equal(NodeExecutionCompletionReasons.ParallelJoinMerged, loser.CompletionReason);
            Assert.Equal("manager", loser.CompletedBy);
            Assert.Null(loser.ExitedViaFlowId);
        });

        var tokens = await finalDb.ExecutionTokens.AsNoTracking()
            .Where(token => token.InstanceId == started.Id)
            .OrderBy(token => token.Id)
            .ToListAsync();
        Assert.Equal(2, tokens.Count(token => token.Status == ExecutionTokenStatuses.Merged));
        var active = Assert.Single(tokens, token => token.Status == ExecutionTokenStatuses.Active);
        Assert.Equal(7, active.NodeId);
        Assert.NotNull(active.CurrentNodeExecutionId);
        Assert.All(tokens.Where(token => token.Status == ExecutionTokenStatuses.Merged),
            token => Assert.Null(token.CurrentNodeExecutionId));
    }

    [Theory]
    [InlineData(BpmnFlowNodeTypes.ServiceTask)]
    [InlineData(BpmnFlowNodeTypes.ScriptTask)]
    public async Task CaughtAutomaticFailureCommitsFaultedHostBoundaryAndErrorEnd(string hostType)
    {
        var workflowId = await CreateWorkflowAsync(
            CreateBoundaryFailureModel(hostType, withBoundary: true));
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, null),
            "automation",
            ["Automation"],
            suppressDefaultAdmin: true);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = await ReadAsync<InstanceDetailDto>(response);
        Assert.Equal(WorkflowInstanceStatuses.Faulted, detail.Status);

        await using var db = fixture.CreateDbContext();
        var executions = await db.NodeExecutions.AsNoTracking()
            .Where(execution => execution.InstanceId == detail.Id)
            .OrderBy(execution => execution.Id)
            .ToListAsync();
        Assert.Equal(4, executions.Count);

        var host = Assert.Single(executions, execution => execution.NodeId == 2);
        Assert.Equal(NodeExecutionStatuses.Faulted, host.Status);
        Assert.Equal(NodeExecutionCompletionReasons.BoundaryCaught, host.CompletionReason);
        Assert.NotNull(host.ErrorDescription);
        Assert.Equal("automation", host.CompletedBy);
        Assert.NotNull(host.CompletedAt);

        var boundary = Assert.Single(executions, execution => execution.NodeId == 4);
        Assert.Equal(NodeExecutionStatuses.Completed, boundary.Status);
        Assert.Equal(NodeExecutionCompletionReasons.Normal, boundary.CompletionReason);

        var errorEnd = Assert.Single(executions, execution => execution.NodeId == 5);
        Assert.Equal(NodeExecutionStatuses.Faulted, errorEnd.Status);
        Assert.Equal(NodeExecutionCompletionReasons.ErrorEnd, errorEnd.CompletionReason);
        Assert.Equal("AUTOMATION_FAILED", errorEnd.ErrorCode);
        Assert.Equal("The automatic task failed.", errorEnd.ErrorDescription);

        var caughtError = await db.InstanceVariables.AsNoTracking()
            .SingleAsync(variable =>
                variable.InstanceId == detail.Id
                && variable.VariableName == "caughtError");
        Assert.Equal(host.Id, caughtError.NodeExecutionId);
    }

    [Fact]
    public async Task CaughtFailureWithLongDiagnosticStillCommitsAndBoundsExecutionDetail()
    {
        var model = CreateBoundaryFailureModel(
            BpmnFlowNodeTypes.ScriptTask,
            withBoundary: true);
        var diagnostic = new string('x', ErrorEndConstraints.MaxDescriptionLength + 200);
        model.FlowNodes.Single(node => node.Id == 2).Script =
            $"throw new Error('{diagnostic}');";
        var workflowId = await CreateWorkflowAsync(model);

        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, null),
            "automation",
            ["Automation"],
            suppressDefaultAdmin: true);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = await ReadAsync<InstanceDetailDto>(response);
        Assert.Equal(WorkflowInstanceStatuses.Faulted, detail.Status);

        await using var db = fixture.CreateDbContext();
        var host = await db.NodeExecutions.AsNoTracking()
            .SingleAsync(execution =>
                execution.InstanceId == detail.Id
                && execution.NodeId == 2);
        Assert.Equal(NodeExecutionStatuses.Faulted, host.Status);
        Assert.Equal(NodeExecutionCompletionReasons.BoundaryCaught, host.CompletionReason);
        Assert.NotNull(host.ErrorDescription);
        Assert.Equal(
            ErrorEndConstraints.MaxDescriptionLength,
            host.ErrorDescription.EnumerateRunes().Count());
    }

    [Fact]
    public async Task UncaughtAutomaticFailureRollsBackTheInstanceAndEveryExecution()
    {
        var workflowId = await CreateWorkflowAsync(
            CreateBoundaryFailureModel(BpmnFlowNodeTypes.ScriptTask, withBoundary: false));

        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, null),
            "automation",
            ["Automation"],
            suppressDefaultAdmin: true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var db = fixture.CreateDbContext();
        Assert.False(await db.WorkflowInstances.AsNoTracking()
            .AnyAsync(instance => instance.WorkflowDefinitionId == workflowId));
        Assert.False(await db.NodeExecutions.AsNoTracking()
            .AnyAsync(execution =>
                execution.Instance != null
                && execution.Instance.WorkflowDefinitionId == workflowId));
    }

    [Fact]
    public async Task InstanceCancellationClosesTheOpenVisitOnceWithTheCancellingActor()
    {
        var model = CreateNormalUserTaskModel("cancel");
        model.FlowNodes[0].Variables = [];
        model.SequenceFlows.Single(flow => flow.Id == 201).Variables = [];
        var workflowId = await CreateWorkflowAsync(model);

        using var startResponse = await SendAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, null),
            "starter");
        Assert.Equal(HttpStatusCode.Created, startResponse.StatusCode);
        var started = await ReadAsync<InstanceDetailDto>(startResponse);

        using var cancelResponse = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{started.Id}/cancel",
            user: "canceller");
        Assert.Equal(HttpStatusCode.NoContent, cancelResponse.StatusCode);

        long cancelledExecutionId;
        DateTimeOffset completedAt;
        await using (var db = fixture.CreateDbContext())
        {
            var execution = await db.NodeExecutions.AsNoTracking()
                .SingleAsync(candidate =>
                    candidate.InstanceId == started.Id
                    && candidate.NodeId == 2);
            Assert.Equal(NodeExecutionStatuses.Cancelled, execution.Status);
            Assert.Equal(NodeExecutionCompletionReasons.InstanceCancelled, execution.CompletionReason);
            Assert.Equal("canceller", execution.CompletedBy);
            Assert.Equal(["admin"], StringList(execution.CompletedByRolesJson));
            cancelledExecutionId = execution.Id;
            completedAt = Assert.IsType<DateTimeOffset>(execution.CompletedAt);

            var token = await db.ExecutionTokens.AsNoTracking()
                .SingleAsync(candidate => candidate.InstanceId == started.Id);
            Assert.Equal(ExecutionTokenStatuses.Cancelled, token.Status);
            Assert.Null(token.CurrentNodeExecutionId);
        }

        using var duplicate = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{started.Id}/cancel",
            user: "canceller");
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        await using var finalDb = fixture.CreateDbContext();
        var persisted = await finalDb.NodeExecutions.AsNoTracking()
            .SingleAsync(execution => execution.Id == cancelledExecutionId);
        Assert.Equal(completedAt, persisted.CompletedAt);
        Assert.Equal(2, await finalDb.NodeExecutions.CountAsync(execution =>
            execution.InstanceId == started.Id));
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

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "test-admin",
        string[]? roles = null,
        bool suppressDefaultAdmin = false)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, user, roles ?? []);
        if (suppressDefaultAdmin)
        {
            request.Headers.TryAddWithoutValidation("X-Test-Suppress-Admin", "true");
        }
        return await fixture.Client.SendAsync(request);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException("Response body was empty.");

    private static IReadOnlyList<string> StringList(JsonDocument? document) =>
        document?.RootElement.EnumerateArray()
            .Select(item => item.GetString()!)
            .ToList()
        ?? [];

    private static WorkflowModel CreateNormalUserTaskModel(string label)
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = $"node-execution-{label}-{suffix}",
            Name = $"Node execution {label} {suffix}",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel
                {
                    Id = 1,
                    Name = "Start",
                    ExternalId = "start",
                    Type = BpmnFlowNodeTypes.StartEvent,
                    Variables =
                    [
                        new VariableModel
                        {
                            Id = 1,
                            Name = "requestId",
                            DataType = WorkflowVariableTypes.String,
                            Required = true
                        }
                    ]
                },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Review",
                    ExternalId = "review",
                    Type = BpmnFlowNodeTypes.UserTask,
                    Roles = ["Reviewer"]
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "End",
                    ExternalId = "end",
                    Type = BpmnFlowNodeTypes.EndEvent
                }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel
                {
                    Id = 101,
                    Name = "Review",
                    SourceRef = 1,
                    TargetRef = 2
                },
                new SequenceFlowModel
                {
                    Id = 201,
                    Name = "Complete",
                    SourceRef = 2,
                    TargetRef = 3,
                    Roles = ["Reviewer"],
                    Variables =
                    [
                        new VariableModel
                        {
                            Id = 2,
                            Name = "decision",
                            DataType = WorkflowVariableTypes.String,
                            Required = true
                        }
                    ]
                }
            ]
        };
    }

    private static WorkflowModel CreateBoundaryFailureModel(string hostType, bool withBoundary)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var host = new FlowNodeModel
        {
            Id = 2,
            Name = "Failing activity",
            Type = hostType
        };
        if (hostType == BpmnFlowNodeTypes.ServiceTask)
        {
            host.Service = new ServiceTaskModel
            {
                Url = "https://tests.local/typed-output-invalid",
                OutputMappings =
                [
                    new ServiceOutputMappingModel
                    {
                        Variable = "score",
                        Path = "result.score",
                        Required = true,
                        DataType = WorkflowVariableTypes.Number
                    }
                ]
            };
        }
        else
        {
            host.ScriptFormat = ScriptFormats.JavaScript;
            host.UsesFlowInfo = false;
            host.Script = "execution.setVariable('staged', 'must roll back'); " +
                          "throw new Error('node execution failure');";
        }

        var nodes = new List<FlowNodeModel>
        {
            new() { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            host,
            new() { Id = 3, Name = "Normal end", Type = BpmnFlowNodeTypes.EndEvent }
        };
        var flows = new List<SequenceFlowModel>
        {
            new() { Id = 101, Name = "Run", SourceRef = 1, TargetRef = 2 },
            new() { Id = 201, Name = "Success", SourceRef = 2, TargetRef = 3 }
        };
        if (withBoundary)
        {
            nodes.Add(new FlowNodeModel
            {
                Id = 4,
                Name = "Catch failure",
                Type = BpmnFlowNodeTypes.ErrorBoundaryEvent,
                AttachedToRef = 2,
                ErrorVariable = "caughtError"
            });
            nodes.Add(new FlowNodeModel
            {
                Id = 5,
                Name = "Error end",
                Type = BpmnFlowNodeTypes.ErrorEndEvent,
                ErrorCode = "AUTOMATION_FAILED",
                ErrorDescription = "The automatic task failed."
            });
            flows.Add(new SequenceFlowModel
            {
                Id = 401,
                Name = "Failure",
                SourceRef = 4,
                TargetRef = 5
            });
        }

        return new WorkflowModel
        {
            Id = "node-execution-boundary-" + suffix,
            Name = "Node execution boundary " + suffix,
            InitialEventId = 1,
            Variables = hostType == BpmnFlowNodeTypes.ScriptTask
                ?
                [
                    new VariableModel
                    {
                        Id = 1,
                        Name = "staged",
                        DataType = WorkflowVariableTypes.String,
                        DefaultValue = JsonSerializer.SerializeToElement("initial")
                    }
                ]
                : [],
            FlowNodes = nodes,
            SequenceFlows = flows
        };
    }
}
