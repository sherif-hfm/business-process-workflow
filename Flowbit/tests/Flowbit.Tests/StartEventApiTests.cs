using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class StartEventApiTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Start_RequiresExactlyOnePublishedWorkflowSelector()
    {
        var published = await CreateWorkflowAsync(CreateModel("selectors-published"), true);

        using var byId = await StartAsync(new StartInstanceRequest(published.Id, null, null, null));
        Assert.Equal(HttpStatusCode.Created, byId.StatusCode);
        Assert.StartsWith("/api/instances/", byId.Headers.Location?.OriginalString);

        using var byKey = await StartAsync(new StartInstanceRequest(null, published.WorkflowKey, null, null));
        Assert.Equal(HttpStatusCode.Created, byKey.StatusCode);

        using var both = await StartAsync(new StartInstanceRequest(
            published.Id, published.WorkflowKey, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);

        using var neither = await StartAsync(new StartInstanceRequest(null, null, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, neither.StatusCode);

        var unpublished = await CreateWorkflowAsync(CreateModel("selectors-unpublished"), false);
        using var unpublishedById = await StartAsync(new StartInstanceRequest(unpublished.Id, null, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, unpublishedById.StatusCode);
        using var unpublishedByKey = await StartAsync(new StartInstanceRequest(
            null, unpublished.WorkflowKey, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, unpublishedByKey.StatusCode);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(2, await db.WorkflowInstances.CountAsync(instance =>
            instance.WorkflowDefinitionId == published.Id));
        Assert.False(await db.WorkflowInstances.AnyAsync(instance =>
            instance.WorkflowDefinitionId == unpublished.Id));
    }

    [Fact]
    public async Task Start_StrictlyValidatesEveryVariableTypeAndRollsBackFailures()
    {
        var model = CreateModel("typed-values");
        var start = model.FlowNodes.Single(node => node.Id == 1);
        start.Variables =
        [
            Variable(1, "amount", WorkflowVariableTypes.Number, required: true),
            Variable(2, "approved", WorkflowVariableTypes.Boolean),
            Variable(3, "businessDate", WorkflowVariableTypes.Date),
            Variable(4, "receivedAt", WorkflowVariableTypes.DateTime),
            Variable(5, "tags", WorkflowVariableTypes.String, isArray: true),
            Variable(6, "metadata", WorkflowVariableTypes.Json),
            Variable(7, "threshold", WorkflowVariableTypes.Number,
                defaultValue: JsonSerializer.SerializeToElement("12.5"))
        ];
        var workflow = await CreateWorkflowAsync(model, true);

        var valid = new Dictionary<string, JsonElement>
        {
            ["amount"] = JsonSerializer.SerializeToElement(25.5),
            ["approved"] = JsonSerializer.SerializeToElement(true),
            ["businessDate"] = JsonSerializer.SerializeToElement("2026-07-17"),
            ["receivedAt"] = JsonSerializer.SerializeToElement("2026-07-17T15:00:00+03:00"),
            ["tags"] = JsonSerializer.SerializeToElement(new[] { "one", "two" }),
            ["metadata"] = JsonSerializer.SerializeToElement(new { source = "test" }),
            ["undeclared"] = JsonSerializer.SerializeToElement("ignored")
        };
        using var accepted = await StartAsync(new StartInstanceRequest(workflow.Id, null, null, valid));
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var acknowledgement = await ReadAsync<StartInstanceResultDto>(accepted);
        var detail = await GetInstanceAsync(acknowledgement.Id);
        var stored = detail.Variables.ToDictionary(variable => variable.VariableName, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(JsonValueKind.Number, stored["amount"].Value.ValueKind);
        Assert.Equal(JsonValueKind.Number, stored["threshold"].Value.ValueKind);
        Assert.Equal(JsonValueKind.Array, stored["tags"].Value.ValueKind);
        Assert.DoesNotContain("undeclared", stored.Keys, StringComparer.OrdinalIgnoreCase);

        var invalidValues = new Dictionary<string, JsonElement>[]
        {
            [],
            new() { ["amount"] = JsonSerializer.SerializeToElement("25.5") },
            new() { ["amount"] = JsonSerializer.SerializeToElement(1), ["approved"] = JsonSerializer.SerializeToElement("true") },
            new() { ["amount"] = JsonSerializer.SerializeToElement(1), ["businessDate"] = JsonSerializer.SerializeToElement("17/07/2026") },
            new() { ["amount"] = JsonSerializer.SerializeToElement(1), ["receivedAt"] = JsonSerializer.SerializeToElement("2026-07-17") },
            new() { ["amount"] = JsonSerializer.SerializeToElement(1), ["tags"] = JsonSerializer.SerializeToElement(new[] { 1, 2 }) },
            new()
            {
                ["amount"] = JsonSerializer.SerializeToElement(1),
                ["AMOUNT"] = JsonSerializer.SerializeToElement(2)
            }
        };

        foreach (var values in invalidValues)
        {
            using var rejected = await StartAsync(new StartInstanceRequest(workflow.Id, null, null, values));
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }

        await using var db = fixture.CreateDbContext();
        Assert.Equal(1, await db.WorkflowInstances.CountAsync(instance =>
            instance.WorkflowDefinitionId == workflow.Id));
    }

    [Fact]
    public async Task Start_PersistsNullableProcessVariablesAndExposesThemToJavaScript()
    {
        var model = CreateModel("nullable-process-start");
        model.Variables =
        [
            new VariableModel
            {
                Id = 1,
                Name = "creditScore",
                DataType = WorkflowVariableTypes.Number,
                Nullable = true,
                Validation = "creditScore > 0"
            },
            new VariableModel
            {
                Id = 2,
                Name = "reviewers",
                DataType = WorkflowVariableTypes.String,
                IsArray = true,
                Nullable = true
            },
            new VariableModel
            {
                Id = 3,
                Name = "observedNull",
                DataType = WorkflowVariableTypes.Boolean,
                DefaultValue = JsonSerializer.SerializeToElement(false)
            }
        ];
        InsertScriptTask(model, new FlowNodeModel
        {
            Id = 4,
            Name = "Observe null",
            Type = BpmnFlowNodeTypes.ScriptTask,
            ScriptFormat = ScriptFormats.JavaScript,
            Script = "execution.setVariable('observedNull', execution.hasVariable('creditScore') && execution.getVariable('creditScore') === null);"
        });
        var workflow = await CreateWorkflowAsync(model, true);

        using var response = await StartAsync(new StartInstanceRequest(workflow.Id, null, null, null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var acknowledgement = await ReadAsync<StartInstanceResultDto>(response);
        var detail = await GetInstanceAsync(acknowledgement.Id);

        Assert.Equal(JsonValueKind.Null, detail.Variables.Last(variable =>
            variable.VariableName == "creditScore").Value.ValueKind);
        Assert.Equal(JsonValueKind.Null, detail.Variables.Last(variable =>
            variable.VariableName == "reviewers").Value.ValueKind);
        Assert.True(detail.Variables.Last(variable =>
            variable.VariableName == "observedNull").Value.GetBoolean());
    }

    [Fact]
    public async Task Start_NCalcAndJavaScriptNullWritesRespectProcessNullability()
    {
        foreach (var scriptFormat in new[] { ScriptFormats.NCalc, ScriptFormats.JavaScript })
        {
            foreach (var nullable in new[] { true, false })
            {
                var model = CreateModel($"null-write-{scriptFormat}-{nullable}");
                model.Variables =
                [
                    new VariableModel
                    {
                        Id = 1,
                        Name = "result",
                        DataType = WorkflowVariableTypes.String,
                        Nullable = nullable,
                        DefaultValue = nullable ? null : JsonSerializer.SerializeToElement("initial"),
                        Validation = "Len(result) > 0"
                    }
                ];
                InsertScriptTask(model, new FlowNodeModel
                {
                    Id = 4,
                    Name = "Write null",
                    Type = BpmnFlowNodeTypes.ScriptTask,
                    ScriptFormat = scriptFormat,
                    Script = scriptFormat == ScriptFormats.JavaScript
                        ? "execution.setVariable('result', null);"
                        : null,
                    Assignments = scriptFormat == ScriptFormats.NCalc
                        ? [new AssignmentModel { Variable = "result", Expression = "null" }]
                        : []
                });
                var workflow = await CreateWorkflowAsync(model, true);

                using var response = await StartAsync(new StartInstanceRequest(workflow.Id, null, null, null));
                if (nullable)
                {
                    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                    var acknowledgement = await ReadAsync<StartInstanceResultDto>(response);
                    var detail = await GetInstanceAsync(acknowledgement.Id);
                    Assert.Equal(JsonValueKind.Null, detail.Variables.Last(variable =>
                        variable.VariableName == "result").Value.ValueKind);
                }
                else
                {
                    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                    var body = await response.Content.ReadAsStringAsync();
                    Assert.Contains("does not allow null", body, StringComparison.OrdinalIgnoreCase);
                    await using var db = fixture.CreateDbContext();
                    Assert.False(await db.WorkflowInstances.AnyAsync(instance =>
                        instance.WorkflowDefinitionId == workflow.Id));
                }
            }
        }
    }

    [Fact]
    public async Task Start_EnforcesRoleExplicitEntryAndClaimInheritanceSemantics()
    {
        var model = CreateModel("roles-and-history");
        model.FlowNodes.Single(node => node.Id == 1).Roles = ["Starter"];
        var task = model.FlowNodes.Single(node => node.Id == 2);
        task.RequiresClaim = true;
        task.ClaimMode = ClaimModes.Previous;
        var workflow = await CreateWorkflowAsync(model, true);

        using var unauthenticated = await fixture.Client.PostAsJsonAsync(
            "/api/instances",
            new StartInstanceRequest(workflow.Id, null, null, null),
            JsonOptions);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        using var forbidden = await StartAsync(
            new StartInstanceRequest(workflow.Id, null, null, null),
            "viewer",
            ["Viewer"]);
        Assert.Equal(HttpStatusCode.BadRequest, forbidden.StatusCode);

        model = CreateModel("explicit-entry");
        model.InitialEventId = null;
        var explicitWorkflow = await CreateWorkflowAsync(model, true);
        using var missingEntry = await StartAsync(new StartInstanceRequest(explicitWorkflow.Id, null, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, missingEntry.StatusCode);
        using var invalidEntry = await StartAsync(new StartInstanceRequest(explicitWorkflow.Id, null, 999, null));
        Assert.Equal(HttpStatusCode.BadRequest, invalidEntry.StatusCode);
        using var explicitStart = await StartAsync(new StartInstanceRequest(explicitWorkflow.Id, null, 1, null));
        Assert.Equal(HttpStatusCode.Created, explicitStart.StatusCode);

        using var allowed = await StartAsync(
            new StartInstanceRequest(workflow.Id, null, null, null),
            "starter-user",
            ["Starter"]);
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
        var acknowledgement = await ReadAsync<StartInstanceResultDto>(allowed);
        var detail = await GetInstanceAsync(acknowledgement.Id);
        var startHistory = Assert.Single(detail.History, history => history.Note == "start");
        Assert.Null(startHistory.SequenceFlowId);
        Assert.Equal("starter-user", startHistory.PerformedBy);

        await using var db = fixture.CreateDbContext();
        var workItem = await db.UserTasks.SingleAsync(item => item.InstanceId == acknowledgement.Id);
        Assert.Null(workItem.ClaimedBy);
    }

    [Fact]
    public async Task WorkflowFamily_VersionsByStableKeyAndSerializesConcurrentCreation()
    {
        var model = CreateModel("version-family");
        model.FlowNodes.Single(node => node.Id == 1).Idempotency = new IdempotencyModel
        {
            HeaderName = IdempotencyHeaders.Standard,
            Variable = "requestId"
        };
        var first = await CreateWorkflowAsync(model, true);

        var secondModel = Clone(model);
        secondModel.Name = "Renamed version family";
        var second = await CreateWorkflowAsync(secondModel, true);
        Assert.Equal(first.WorkflowKey, second.WorkflowKey);
        Assert.Equal(2, second.Version);
        Assert.False(second.IsDefault);

        var updateModel = Clone(model);
        updateModel.Id = "attempted-family-escape";
        updateModel.Name = "Renamed through PUT";
        using var updatedResponse = await SendAsync(
            HttpMethod.Put,
            $"/api/workflows/{first.Id}",
            new UpdateWorkflowRequest(updateModel, true));
        Assert.Equal(HttpStatusCode.OK, updatedResponse.StatusCode);
        var third = await ReadAsync<WorkflowDetailDto>(updatedResponse);
        Assert.Equal(first.WorkflowKey, third.WorkflowKey);
        Assert.Equal(first.WorkflowKey, third.Definition.Id);
        Assert.Equal(3, third.Version);

        var concurrentModels = new[] { Clone(model), Clone(model) };
        concurrentModels[0].Name = "Concurrent A";
        concurrentModels[1].Name = "Concurrent B";
        var concurrentResponses = await Task.WhenAll(concurrentModels.Select(candidate => SendAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(candidate, true))));
        try
        {
            Assert.All(concurrentResponses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
            var created = await Task.WhenAll(concurrentResponses.Select(ReadAsync<WorkflowDetailDto>));
            Assert.Equal([4, 5], created.Select(item => item.Version).Order().ToArray());
            Assert.All(created, item => Assert.Equal(first.WorkflowKey, item.WorkflowKey));
        }
        finally
        {
            foreach (var response in concurrentResponses) response.Dispose();
        }

        using var versionsResponse = await SendAsync(
            HttpMethod.Get,
            $"/api/workflows/{first.WorkflowKey}/versions");
        var versions = await ReadAsync<List<WorkflowSummaryDto>>(versionsResponse);
        Assert.Equal([5, 4, 3, 2, 1], versions.Select(version => version.Version).ToArray());

        using var firstStart = await StartAsync(
            new StartInstanceRequest(first.Id, null, null, null),
            additionalHeaders: new Dictionary<string, string> { [IdempotencyHeaders.Standard] = "family-retry" });
        Assert.Equal(HttpStatusCode.Created, firstStart.StatusCode);

        using var setDefault = await SendAsync(HttpMethod.Post, $"/api/workflows/{second.Id}/set-default");
        Assert.Equal(HttpStatusCode.NoContent, setDefault.StatusCode);
        using var duplicateAcrossVersions = await StartAsync(
            new StartInstanceRequest(null, first.WorkflowKey, null, null),
            additionalHeaders: new Dictionary<string, string> { [IdempotencyHeaders.Standard] = "family-retry" });
        Assert.Equal(HttpStatusCode.Conflict, duplicateAcrossVersions.StatusCode);

        using var fullStart = await StartAsync(
            new StartInstanceRequest(null, first.WorkflowKey, null, null),
            additionalHeaders: new Dictionary<string, string> { [IdempotencyHeaders.Standard] = "family-new" },
            path: "/api/instances?detail=full");
        Assert.Equal(HttpStatusCode.Created, fullStart.StatusCode);
        var detail = await ReadAsync<InstanceDetailDto>(fullStart);
        Assert.Equal(second.Id, detail.Workflow.Id);
    }

    private async Task<WorkflowDetailDto> CreateWorkflowAsync(WorkflowModel model, bool publish)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(model, publish));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<WorkflowDetailDto>(response);
    }

    private Task<HttpResponseMessage> StartAsync(
        StartInstanceRequest request,
        string user = "starter",
        string[]? roles = null,
        IReadOnlyDictionary<string, string>? additionalHeaders = null,
        string path = "/api/instances") =>
        SendAsync(HttpMethod.Post, path, request, user, roles ?? ["admin"], additionalHeaders);

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "test-admin",
        string[]? roles = null,
        IReadOnlyDictionary<string, string>? additionalHeaders = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, user, roles ?? ["admin"]);
        if (additionalHeaders is not null)
        {
            foreach (var header in additionalHeaders)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
        return await fixture.Client.SendAsync(request);
    }

    private async Task<InstanceDetailDto> GetInstanceAsync(long id)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/instances/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<InstanceDetailDto>(response);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException($"Response did not contain {typeof(T).Name}.");

    private static WorkflowModel CreateModel(string suffix)
    {
        var key = $"start-event-{suffix}-{Guid.NewGuid():N}";
        return new WorkflowModel
        {
            Id = key,
            Name = key,
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel
                {
                    Id = 1,
                    Name = "Start",
                    Type = BpmnFlowNodeTypes.StartEvent
                },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Review",
                    Type = BpmnFlowNodeTypes.UserTask
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Done",
                    Type = BpmnFlowNodeTypes.EndEvent
                }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 10, Name = "", SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 20, Name = "Complete", SourceRef = 2, TargetRef = 3 }
            ]
        };
    }

    private static void InsertScriptTask(WorkflowModel model, FlowNodeModel scriptTask)
    {
        var startFlow = model.SequenceFlows.Single(flow => flow.SourceRef == 1);
        var originalTarget = startFlow.TargetRef;
        startFlow.TargetRef = scriptTask.Id;
        model.FlowNodes.Add(scriptTask);
        model.SequenceFlows.Add(new SequenceFlowModel
        {
            Id = 15,
            Name = string.Empty,
            SourceRef = scriptTask.Id,
            TargetRef = originalTarget
        });
    }

    private static VariableModel Variable(
        int id,
        string name,
        string dataType,
        bool isArray = false,
        bool required = false,
        JsonElement? defaultValue = null) => new()
    {
        Id = id,
        Name = name,
        DataType = dataType,
        IsArray = isArray,
        Required = required,
        DefaultValue = defaultValue
    };

    private static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)
        ?? throw new InvalidOperationException("Clone failed.");
}
