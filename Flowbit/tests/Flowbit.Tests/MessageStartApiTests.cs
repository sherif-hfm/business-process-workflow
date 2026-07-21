using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Flowbit.Infrastructure.Entities;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class MessageStartApiTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] AdminRoles = ["admin"];

    [Fact]
    public async Task AuthenticationFailsClosedRequiresSingleHeadersAndUsesFreshSettings()
    {
        var missingSuffix = Guid.NewGuid().ToString("N");
        var missingModel = CreateModel("missing-secret");
        missingModel.FlowNodes.Single(node => BpmnFlowNodeTypes.IsMessageStart(node.Type))
            .Message!.ClientSecret = $"${{setting.missing-{missingSuffix}.secret}}";
        await CreateWorkflowAsync(missingModel);

        using (var unresolved = await SendMessageStartAsync(
                   missingModel.Id,
                   clientSecret: null))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unresolved.StatusCode);
        }

        var literalModel = CreateModel("single-headers");
        await CreateWorkflowAsync(literalModel);

        using (var missingClientId = CreateMessageStartRequest(literalModel.Id))
        {
            missingClientId.Headers.Remove("X-Client-Id");
            using var response = await fixture.Client.SendAsync(missingClientId);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using (var repeatedClientId = CreateMessageStartRequest(literalModel.Id))
        {
            repeatedClientId.Headers.Remove("X-Client-Id");
            repeatedClientId.Headers.TryAddWithoutValidation(
                "X-Client-Id",
                ["tests-client", "tests-client"]);
            using var response = await fixture.Client.SendAsync(repeatedClientId);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using (var repeatedCredentials = CreateMessageStartRequest(literalModel.Id))
        {
            repeatedCredentials.Headers.Remove("X-Client-Secret");
            repeatedCredentials.Headers.TryAddWithoutValidation(
                "X-Client-Secret",
                ["tests-secret", "tests-secret"]);
            using var response = await fixture.Client.SendAsync(repeatedCredentials);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using (var repeatedCorrelation = CreateMessageStartRequest(literalModel.Id))
        {
            repeatedCorrelation.Headers.Remove("X-Correlation");
            repeatedCorrelation.Headers.TryAddWithoutValidation(
                "X-Correlation",
                ["accepted", "accepted"]);
            using var response = await fixture.Client.SendAsync(repeatedCorrelation);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using (var missingCorrelation = CreateMessageStartRequest(literalModel.Id))
        {
            missingCorrelation.Headers.Remove("X-Correlation");
            using var response = await fixture.Client.SendAsync(missingCorrelation);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var invalidValidation = CreateModel("header-validation");
        invalidValidation.FlowNodes.Single(node => BpmnFlowNodeTypes.IsMessageStart(node.Type))
            .Message!.HeaderValidation = "header == 'different'";
        await CreateWorkflowAsync(invalidValidation);
        using (var rejected = await SendMessageStartAsync(invalidValidation.Id))
        {
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }

        var unresolvedCorrelation = CreateModel("unresolved-correlation");
        unresolvedCorrelation.FlowNodes.Single(node => BpmnFlowNodeTypes.IsMessageStart(node.Type))
            .Message!.HeaderValue = $"${{setting.missing-{missingSuffix}.correlation}}";
        await CreateWorkflowAsync(unresolvedCorrelation);
        using (var rejected = await SendMessageStartAsync(unresolvedCorrelation.Id))
        {
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }

        await using (var db = fixture.CreateDbContext())
        {
            Assert.False(await db.WorkflowInstances.AnyAsync(instance =>
                instance.WorkflowKey == missingModel.Id
                || instance.WorkflowKey == literalModel.Id
                || instance.WorkflowKey == invalidValidation.Id
                || instance.WorkflowKey == unresolvedCorrelation.Id));
        }

        var settingNamespace = "message-start-" + Guid.NewGuid().ToString("N");
        await SetSettingAsync(settingNamespace, "secret", "old-secret");
        var rotatingModel = CreateModel("rotating-secret");
        rotatingModel.FlowNodes.Single(node => BpmnFlowNodeTypes.IsMessageStart(node.Type))
            .Message!.ClientSecret = $"${{setting.{settingNamespace}.secret}}";
        await CreateWorkflowAsync(rotatingModel);

        using (var accepted = await SendMessageStartAsync(rotatingModel.Id, clientSecret: "old-secret"))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        await SetSettingAsync(settingNamespace, "secret", "new-secret");
        using (var stale = await SendMessageStartAsync(rotatingModel.Id, clientSecret: "old-secret"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        }
        using (var rotated = await SendMessageStartAsync(rotatingModel.Id, clientSecret: "new-secret"))
        {
            Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        }
    }

    [Fact]
    public async Task DynamicCorrelationHeaderCannotUseTheIdempotencyAlias()
    {
        var settingNamespace = "message-start-header-" + Guid.NewGuid().ToString("N");
        await SetSettingAsync(settingNamespace, "name", IdempotencyHeaders.LegacyAlias);
        var model = CreateModel("dynamic-reserved-header");
        var start = model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsMessageStart(node.Type));
        start.Message!.HeaderName = $"${{setting.{settingNamespace}.name}}";
        start.Idempotency = new IdempotencyModel
        {
            HeaderName = IdempotencyHeaders.Standard,
            Variable = "requestId"
        };
        await CreateWorkflowAsync(model);

        using var request = CreateMessageStartRequest(model.Id);
        request.Headers.Add(IdempotencyHeaders.LegacyAlias, "accepted");
        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.WorkflowInstances.AnyAsync(instance => instance.WorkflowKey == model.Id));
        Assert.False(await db.WorkflowIdempotencyClaims.AnyAsync(claim => claim.WorkflowKey == model.Id));
    }

    [Fact]
    public async Task RequestBodyContractRejectsMalformedUnsupportedAndOversizedPayloadsAtomically()
    {
        var model = CreateModel("payload-contract");
        await CreateWorkflowAsync(model);

        using (var malformed = await SendMessageStartAsync(
                   model.Id,
                   new StringContent("{", Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        }

        using (var unsupported = await SendMessageStartAsync(
                   model.Id,
                   new StringContent("{}", Encoding.UTF8, "text/plain")))
        {
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, unsupported.StatusCode);
        }

        var oversizedContent = new ByteArrayContent(new byte[1_048_577]);
        oversizedContent.Headers.ContentType = new("application/json");
        using (var oversized = await SendMessageStartAsync(model.Id, oversizedContent))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        }

        using (var streamingOversized = await SendMessageStartAsync(
                   model.Id,
                   new UnknownLengthJsonContent(new string(' ', 1_048_577))))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, streamingOversized.StatusCode);
        }

        await using (var db = fixture.CreateDbContext())
        {
            Assert.False(await db.WorkflowInstances.AnyAsync(instance => instance.WorkflowKey == model.Id));
        }

        using (var empty = await SendMessageStartAsync(model.Id, new ByteArrayContent([])))
        {
            Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        }
        using (var explicitNull = await SendMessageStartAsync(
                   model.Id,
                   new StringContent("null", Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.OK, explicitNull.StatusCode);
        }
        using (var streaming = await SendMessageStartAsync(
                   model.Id,
                   new UnknownLengthJsonContent("{}")))
        {
            Assert.Equal(HttpStatusCode.OK, streaming.StatusCode);
        }

        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(3, await db.WorkflowInstances.CountAsync(instance => instance.WorkflowKey == model.Id));
        }
    }

    [Fact]
    public async Task SelectorIsSingleValuedCaseSensitiveAndMessageStartsRemainSystemOnly()
    {
        var model = CreateMultipleStartModel();
        var workflow = await CreateWorkflowAsync(model);

        using (var ambiguous = await SendMessageStartAsync(model.Id))
        {
            Assert.Equal(HttpStatusCode.BadRequest, ambiguous.StatusCode);
        }
        using (var repeated = await SendMessageStartAsync(model.Id, selector: "alpha&startEvent=beta"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, repeated.StatusCode);
        }
        using (var unknown = await SendMessageStartAsync(model.Id, selector: "missing"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        }
        using (var wrongCase = await SendMessageStartAsync(model.Id, selector: "ALPHA"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrongCase.StatusCode);
        }

        using (var alpha = await SendMessageStartAsync(model.Id, selector: "alpha"))
        {
            Assert.Equal(HttpStatusCode.OK, alpha.StatusCode);
            Assert.Equal(2, (await ReadAsync<MessageStartAckDto>(alpha)).CurrentNodeId);
        }
        using (var beta = await SendMessageStartAsync(model.Id, selector: "beta"))
        {
            Assert.Equal(HttpStatusCode.OK, beta.StatusCode);
            Assert.Equal(5, (await ReadAsync<MessageStartAckDto>(beta)).CurrentNodeId);
        }

        using var normalStart = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/api/instances",
            new StartInstanceRequest(workflow.Id, null, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, normalStart.StatusCode);
    }

    [Fact]
    public async Task MessageStartUsesPublishedDefaultInsteadOfNewerPublishedVersion()
    {
        var defaultModel = CreateModel("published-default");
        defaultModel.FlowNodes.Single(node => node.Id == 2).Name = "Default task";
        var first = await CreateWorkflowAsync(defaultModel);

        var newerModel = Clone(defaultModel);
        newerModel.Name = "Newer published version";
        newerModel.FlowNodes.Single(node => node.Id == 2).Name = "Newer task";
        var second = await CreateWorkflowAsync(newerModel);
        Assert.True(first.IsDefault);
        Assert.False(second.IsDefault);

        using (var start = await SendMessageStartAsync(defaultModel.Id))
        {
            Assert.Equal(HttpStatusCode.OK, start.StatusCode);
            Assert.Equal("Default task", (await ReadAsync<MessageStartAckDto>(start)).CurrentNodeName);
        }

        using (var setDefault = await SendAuthorizedAsync(
                   HttpMethod.Post,
                   $"/api/workflows/{second.Id}/set-default"))
        {
            Assert.Equal(HttpStatusCode.NoContent, setDefault.StatusCode);
        }
        using (var start = await SendMessageStartAsync(defaultModel.Id))
        {
            Assert.Equal(HttpStatusCode.OK, start.StatusCode);
            Assert.Equal("Newer task", (await ReadAsync<MessageStartAckDto>(start)).CurrentNodeName);
        }

        await using var db = fixture.CreateDbContext();
        Assert.Equal(
            [first.Id, second.Id],
            await db.WorkflowInstances
                .Where(instance => instance.WorkflowKey == defaultModel.Id)
                .OrderBy(instance => instance.Id)
                .Select(instance => instance.WorkflowDefinitionId)
                .ToArrayAsync());
    }

    [Fact]
    public async Task SuccessfulStartReturnsSlimAckAndRecordsVerifiedActorAndUserTask()
    {
        var model = CreateModel("successful-contract", withMapping: true);
        await CreateWorkflowAsync(model);

        using var response = await SendMessageStartAsync(
            model.Id,
            JsonContent.Create(new { status = "ready" }, options: JsonOptions));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responseBody = await response.Content.ReadAsStringAsync();
        using var responseJson = JsonDocument.Parse(responseBody);
        Assert.Equal(
            ["createdAt", "currentNodeExternalId", "currentNodeId", "currentNodeName", "instanceId", "status"],
            responseJson.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.DoesNotContain("tests-secret", responseBody, StringComparison.Ordinal);

        var ack = responseJson.RootElement.Deserialize<MessageStartAckDto>(JsonOptions)!;
        Assert.Equal("running", ack.Status);
        Assert.Equal(2, ack.CurrentNodeId);
        Assert.Equal("review", ack.CurrentNodeExternalId);

        var detail = await GetInstanceAsync(ack.InstanceId);
        Assert.Equal("tests-client", detail.StartedBy);
        Assert.Contains(detail.Variables, variable =>
            variable.VariableName == "status" && variable.Value.GetString() == "ready");
        var history = Assert.Single(detail.History, item => item.Note == "messageStart");
        Assert.Equal("tests-client", history.PerformedBy);

        await using var db = fixture.CreateDbContext();
        var task = await db.UserTasks.SingleAsync(item => item.InstanceId == ack.InstanceId);
        Assert.Equal("active", task.Status);
    }

    [Fact]
    public async Task DownstreamFailureRollsBackInstanceVariablesHistoryAndOwnershipClaims()
    {
        var model = CreateFailingStartModel();
        await CreateWorkflowAsync(model);
        await using var beforeDb = fixture.CreateDbContext();
        var variablesBefore = await beforeDb.InstanceVariables.CountAsync();
        var historyBefore = await beforeDb.InstanceHistory.CountAsync();
        var evidenceBefore = await beforeDb.SequenceFlowSummaries.CountAsync();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var rejected = await SendMessageStartAsync(
                model.Id,
                JsonContent.Create(new { caseId = "CASE-1" }, options: JsonOptions),
                idempotencyKey: "REQUEST-1");
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }

        await using var db = fixture.CreateDbContext();
        Assert.False(await db.WorkflowInstances.AnyAsync(instance => instance.WorkflowKey == model.Id));
        Assert.False(await db.WorkflowIdempotencyClaims.AnyAsync(claim => claim.WorkflowKey == model.Id));
        Assert.False(await db.WorkflowBusinessKeyClaims.AnyAsync(claim => claim.WorkflowKey == model.Id));
        Assert.Equal(variablesBefore, await db.InstanceVariables.CountAsync());
        Assert.Equal(historyBefore, await db.InstanceHistory.CountAsync());
        Assert.Equal(evidenceBefore, await db.SequenceFlowSummaries.CountAsync());
    }

    [Fact]
    public async Task InvalidCredentialsTakePrecedenceOverCommittedIdempotencyConflict()
    {
        var model = CreateModel("authenticated-conflict");
        model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsMessageStart(node.Type)).Idempotency =
            new IdempotencyModel
            {
                HeaderName = IdempotencyHeaders.Standard,
                Variable = "requestId"
            };
        await CreateWorkflowAsync(model);

        using (var first = await SendMessageStartAsync(model.Id, idempotencyKey: "REQUEST-1"))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }
        using (var unauthorized = await SendMessageStartAsync(
                   model.Id,
                   idempotencyKey: "REQUEST-1",
                   clientSecret: "wrong"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        }
        using (var duplicate = await SendMessageStartAsync(model.Id, idempotencyKey: "REQUEST-1"))
        {
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        }
    }

    private async Task<WorkflowDetailDto> CreateWorkflowAsync(WorkflowModel model)
    {
        using var response = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(model, true));
        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"Expected 201 but received {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await ReadAsync<WorkflowDetailDto>(response);
    }

    private async Task<InstanceDetailDto> GetInstanceAsync(long instanceId)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Get, $"/api/instances/{instanceId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<InstanceDetailDto>(response);
    }

    private Task<HttpResponseMessage> SendMessageStartAsync(
        string workflowKey,
        HttpContent? content = null,
        string? selector = null,
        string? idempotencyKey = null,
        string? clientId = "tests-client",
        string? clientSecret = "tests-secret")
    {
        var request = CreateMessageStartRequest(
            workflowKey,
            content,
            selector,
            clientId,
            clientSecret);
        if (idempotencyKey is not null)
        {
            request.Headers.Add(IdempotencyHeaders.Standard, idempotencyKey);
        }
        return fixture.Client.SendAsync(request);
    }

    private static HttpRequestMessage CreateMessageStartRequest(
        string workflowKey,
        HttpContent? content = null,
        string? selector = null,
        string? clientId = "tests-client",
        string? clientSecret = "tests-secret")
    {
        var path = "/api/workflows/" + Uri.EscapeDataString(workflowKey) + "/message-start";
        if (selector is not null)
        {
            path += "?startEvent=" + selector;
        }

        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = content ?? JsonContent.Create(new { }, options: JsonOptions)
        };
        if (clientId is not null)
        {
            request.Headers.Add("X-Client-Id", clientId);
        }
        if (clientSecret is not null)
        {
            request.Headers.Add("X-Client-Secret", clientSecret);
        }
        request.Headers.Add("X-Correlation", "accepted");
        return request;
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(
        HttpMethod method,
        string path,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, "message-start-admin", AdminRoles);
        return await fixture.Client.SendAsync(request);
    }

    private async Task SetSettingAsync(string settingNamespace, string name, string value)
    {
        await using var db = fixture.CreateDbContext();
        var setting = await db.WorkflowSettings.SingleOrDefaultAsync(candidate =>
            candidate.Namespace == settingNamespace && candidate.Name == name);
        if (setting is null)
        {
            db.WorkflowSettings.Add(new WorkflowSettingEntity
            {
                Namespace = settingNamespace,
                Name = name,
                Value = JsonSerializer.SerializeToElement(value)
            });
        }
        else
        {
            setting.Value = JsonSerializer.SerializeToElement(value);
            setting.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync();
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException("Response body was empty.");

    private static WorkflowModel CreateModel(string label, bool withMapping = false)
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = $"message-start-{label}-{suffix}",
            Name = $"Message start {label} {suffix}",
            InitialEventId = null,
            FlowNodes =
            [
                new FlowNodeModel
                {
                    Id = 1,
                    Name = "Message start",
                    ExternalId = "message-start",
                    Type = BpmnFlowNodeTypes.MessageStartEvent,
                    Message = new MessageCatchModel
                    {
                        ClientId = "tests-client",
                        ClientSecret = "tests-secret",
                        HeaderName = "X-Correlation",
                        HeaderValue = "accepted",
                        OutputMappings = withMapping
                            ?
                            [
                                new MessageOutputMappingModel
                                {
                                    Variable = "status",
                                    Path = "status",
                                    DataType = WorkflowVariableTypes.String,
                                    IsArray = false,
                                    Required = true
                                }
                            ]
                            : []
                    }
                },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Review",
                    ExternalId = "review",
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
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 }
            ]
        };
    }

    private static WorkflowModel CreateMultipleStartModel()
    {
        var model = CreateModel("selectors");
        model.FlowNodes.Single(node => node.Id == 1).ExternalId = "alpha";
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 4,
            Name = "Second message start",
            ExternalId = "beta",
            Type = BpmnFlowNodeTypes.MessageStartEvent,
            Message = new MessageCatchModel
            {
                ClientId = "tests-client",
                ClientSecret = "tests-secret",
                HeaderName = "X-Correlation",
                HeaderValue = "accepted"
            }
        });
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 5,
            Name = "Second review",
            ExternalId = "second-review",
            Type = BpmnFlowNodeTypes.UserTask
        });
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 6,
            Name = "Second done",
            Type = BpmnFlowNodeTypes.EndEvent
        });
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 5 });
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 6 });
        return model;
    }

    private static WorkflowModel CreateFailingStartModel()
    {
        var model = CreateModel("rollback");
        var start = model.FlowNodes.Single(node => node.Id == 1);
        start.Message!.OutputMappings =
        [
            new MessageOutputMappingModel
            {
                Variable = "caseId",
                Path = "caseId",
                DataType = WorkflowVariableTypes.String,
                IsArray = false,
                Required = true
            }
        ];
        start.Idempotency = new IdempotencyModel
        {
            HeaderName = IdempotencyHeaders.Standard,
            Variable = "requestId"
        };
        start.BusinessKey = new BusinessKeyModel
        {
            Variable = "caseId",
            Uniqueness = BusinessKeyUniqueness.All
        };
        var service = model.FlowNodes.Single(node => node.Id == 2);
        service.Name = "Failing service";
        service.Type = BpmnFlowNodeTypes.ServiceTask;
        service.Service = new ServiceTaskModel
        {
            Url = "https://tests.local/unconfigured-failure"
        };
        return model;
    }

    private static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))
        ?? throw new InvalidOperationException("Clone failed.");

    private sealed class UnknownLengthJsonContent : HttpContent
    {
        private readonly byte[] _body;

        public UnknownLengthJsonContent(string json)
        {
            _body = Encoding.UTF8.GetBytes(json);
            Headers.ContentType = new("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_body).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
