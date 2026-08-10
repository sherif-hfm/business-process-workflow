using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class InstanceVariableUpdateApiTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Patch_RequiresAuthenticationAndWorkflowAdministratorRole()
    {
        var instance = await StartInstanceAsync();
        var body = new UpdateInstanceVariablesRequest(
            [new InstanceVariableWriteDto(
                "probe",
                JsonSerializer.SerializeToElement(true))],
            null,
            null);

        using var anonymous = await SendAnonymousAsync(
            HttpMethod.Patch,
            $"/api/instances/{instance.Id}/variables",
            body);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var forbidden = await SendAsync(
            HttpMethod.Patch,
            $"/api/instances/{instance.Id}/variables",
            body,
            user: "worker",
            roles: ["worker"],
            suppressDefaultAdmin: true);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task Patch_AppendsAuditedRawValues_PreservesCasingAndReplaysIdempotently()
    {
        var instance = await StartInstanceAsync();
        var firstKey = $"variable-update-{Guid.NewGuid():N}";
        var firstRequest = new UpdateInstanceVariablesRequest(
            [
                new InstanceVariableWriteDto(
                    "MixedCase",
                    JsonSerializer.SerializeToElement(new { approved = true })),
                new InstanceVariableWriteDto(
                    "textValue",
                    JsonSerializer.SerializeToElement("hello")),
                new InstanceVariableWriteDto(
                    "arrayValue",
                    JsonSerializer.SerializeToElement(new object[] { 1, "two" })),
                new InstanceVariableWriteDto(
                    "boolValue",
                    JsonSerializer.SerializeToElement(true))
            ],
            "  initial correction  ",
            firstKey);
        var first = await PatchAsync(instance.Id, firstRequest, "operator");
        Assert.Equal(4, first.Variables.Count);
        Assert.Equal("MixedCase", first.Variables[0].Name);
        Assert.Equal("added", first.Variables[0].Outcome);
        Assert.Equal("initial correction", firstRequest.Reason?.Trim());

        var secondKey = $"variable-update-{Guid.NewGuid():N}";
        var secondRequest = new UpdateInstanceVariablesRequest(
            [
                new InstanceVariableWriteDto(
                    "mixedcase",
                    JsonSerializer.SerializeToElement(42)),
                new InstanceVariableWriteDto(
                    "nullableValue",
                    JsonSerializer.SerializeToElement<object?>(null))
            ],
            null,
            secondKey);
        var second = await PatchAsync(instance.Id, secondRequest, "operator");
        Assert.Equal(["updated", "added"],
            second.Variables.Select(variable => variable.Outcome));
        Assert.Equal("MixedCase", second.Variables[0].Name);
        Assert.Equal(JsonValueKind.Null, second.Variables[1].Value.ValueKind);
        Assert.True(second.UpdatedAt >= first.UpdatedAt);

        var replay = await PatchAsync(instance.Id, secondRequest, "operator");
        Assert.Equal(second.OperationId, replay.OperationId);
        Assert.Equal(second.UpdatedAt, replay.UpdatedAt);

        using (var conflictingReplay = await SendAsync(
                   HttpMethod.Patch,
                   $"/api/instances/{instance.Id}/variables",
                   secondRequest with
                   {
                       Variables =
                       [
                           new InstanceVariableWriteDto(
                               "mixedcase",
                               JsonSerializer.SerializeToElement(43))
                       ]
                   },
                   user: "operator"))
        {
            Assert.Equal(HttpStatusCode.Conflict, conflictingReplay.StatusCode);
        }

        using (var reservedName = await SendAsync(
                   HttpMethod.Patch,
                   $"/api/instances/{instance.Id}/variables",
                   new UpdateInstanceVariablesRequest(
                       [new InstanceVariableWriteDto(
                           "sys.user",
                           JsonSerializer.SerializeToElement("spoof"))],
                       null,
                       null)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, reservedName.StatusCode);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var history = await db.InstanceVariables
                .Where(variable => variable.InstanceId == instance.Id)
                .OrderBy(variable => variable.Id)
                .ToListAsync();
            Assert.Equal(6, history.Count);
            Assert.All(history, variable =>
                Assert.NotNull(variable.InstanceVariableUpdateAuditId));
            Assert.Equal(2, await db.InstanceVariableUpdates.CountAsync(
                update => update.InstanceId == instance.Id));

            var current = await db.InstanceVariableCurrentValues.SingleAsync(
                value => value.InstanceId == instance.Id
                    && value.VariableName == "MixedCase");
            Assert.Equal(42, current.ValueJson.RootElement.GetInt32());
        }

        using (var detailResponse = await SendAsync(
                   HttpMethod.Get,
                   $"/api/instances/{instance.Id}"))
        {
            Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
            var detail = await ReadAsync<InstanceDetailDto>(detailResponse);
            Assert.Equal(2, detail.VariableUpdates.Count);
            Assert.Equal("initial correction", detail.VariableUpdates[0].Reason);
            Assert.All(detail.VariableUpdates, audit =>
                Assert.NotEmpty(audit.Variables));
            Assert.Equal(6, detail.Variables.Count(variable =>
                variable.InstanceVariableUpdateAuditId is not null));
        }

        using (var cancel = await SendAsync(
                   HttpMethod.Post,
                   $"/api/instances/{instance.Id}/cancel"))
        {
            Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        }
        using (var terminal = await SendAsync(
                   HttpMethod.Patch,
                   $"/api/instances/{instance.Id}/variables",
                   new UpdateInstanceVariablesRequest(
                       [new InstanceVariableWriteDto(
                           "afterCancel",
                           JsonSerializer.SerializeToElement(true))],
                       null,
                       null)))
        {
            Assert.Equal(HttpStatusCode.Conflict, terminal.StatusCode);
        }

        using var missing = await SendAsync(
            HttpMethod.Patch,
            $"/api/instances/{long.MaxValue}/variables",
            new UpdateInstanceVariablesRequest(
                [new InstanceVariableWriteDto(
                    "missing",
                    JsonSerializer.SerializeToElement(true))],
                null,
                null));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private async Task<InstanceDetailDto> StartInstanceAsync()
    {
        var model = CreateModel();
        using var create = await SendAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(model, true));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var workflow = await ReadAsync<WorkflowDetailDto>(create);

        using var start = await SendAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(workflow.Id, null, null, null));
        Assert.Equal(HttpStatusCode.Created, start.StatusCode);
        return await ReadAsync<InstanceDetailDto>(start);
    }

    private async Task<UpdateInstanceVariablesResultDto> PatchAsync(
        long instanceId,
        UpdateInstanceVariablesRequest request,
        string user)
    {
        using var response = await SendAsync(
            HttpMethod.Patch,
            $"/api/instances/{instanceId}/variables",
            request,
            user);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<UpdateInstanceVariablesResultDto>(response);
    }

    private Task<HttpResponseMessage> SendAnonymousAsync(
        HttpMethod method,
        string path,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        return fixture.Client.SendAsync(request);
    }

    private Task<HttpResponseMessage> SendAsync(
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
        ApiTestAuth.Authorize(request, user, roles ?? ["admin"]);
        if (suppressDefaultAdmin)
        {
            request.Headers.TryAddWithoutValidation(
                "X-Test-Suppress-Admin",
                "true");
        }
        return fixture.Client.SendAsync(request);
    }

    private static WorkflowModel CreateModel()
    {
        var key = $"variable-update-{Guid.NewGuid():N}";
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
                new SequenceFlowModel
                {
                    Id = 10,
                    Name = "Start review",
                    SourceRef = 1,
                    TargetRef = 2
                },
                new SequenceFlowModel
                {
                    Id = 20,
                    Name = "Complete",
                    SourceRef = 2,
                    TargetRef = 3
                }
            ]
        };
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException(
            $"Response did not contain {typeof(T).Name}.");
}
