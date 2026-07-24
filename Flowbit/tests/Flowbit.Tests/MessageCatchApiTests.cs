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
public sealed class MessageCatchApiTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] AdminRoles = ["admin"];

    [Fact]
    public async Task ConfiguredDeliveryIdempotencyIsPermanentPerInstanceAndConcurrentSafe()
    {
        var workflowId = await CreateWorkflowAsync(CreateModel(idempotent: true, withMapping: true));
        var firstInstance = await StartAsync(workflowId);

        using var first = await SendCatchAsync(
            firstInstance.Id,
            JsonContent.Create(new { status = "accepted" }, options: JsonOptions),
            idempotencyKey: " DELIVERY-1 ");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("completed", (await ReadAsync<MessageDeliveryAckDto>(first)).Status);

        using var duplicate = await SendCatchAsync(
            firstInstance.Id,
            JsonContent.Create(new { status = "changed" }, options: JsonOptions),
            idempotencyKey: "DELIVERY-1");
        await AssertConflictAsync(duplicate, "idempotency_conflict", firstInstance.Id, 2);

        using var wrongCredential = await SendCatchAsync(
            firstInstance.Id,
            JsonContent.Create(new { status = "changed" }, options: JsonOptions),
            idempotencyKey: "DELIVERY-1",
            clientSecret: "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, wrongCredential.StatusCode);

        var otherInstance = await StartAsync(workflowId);
        using var reusedOnAnotherInstance = await SendCatchAsync(
            otherInstance.Id,
            JsonContent.Create(new { status = "accepted" }, options: JsonOptions),
            idempotencyKey: "DELIVERY-1");
        Assert.Equal(HttpStatusCode.OK, reusedOnAnotherInstance.StatusCode);

        var concurrentInstance = await StartAsync(workflowId);
        var responses = await Task.WhenAll(
            SendCatchAsync(
                concurrentInstance.Id,
                JsonContent.Create(new { status = "accepted" }, options: JsonOptions),
                idempotencyKey: "CONCURRENT-1"),
            SendCatchAsync(
                concurrentInstance.Id,
                JsonContent.Create(new { status = "accepted" }, options: JsonOptions),
                idempotencyKey: "CONCURRENT-1"));
        try
        {
            Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
            Assert.Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }

        await using var db = fixture.CreateDbContext();
        Assert.Single(await db.MessageDeliveryReceipts
            .Where(receipt => receipt.InstanceId == firstInstance.Id)
            .ToListAsync());
        Assert.Equal(1, await db.InstanceHistory.CountAsync(history =>
            history.InstanceId == firstInstance.Id && history.Note == "message"));
        Assert.Equal(1, await db.InstanceVariables.CountAsync(variable =>
            variable.InstanceId == firstInstance.Id && variable.VariableName == "status"));
        Assert.Equal(1, await db.InstanceHistory.CountAsync(history =>
            history.InstanceId == concurrentInstance.Id && history.Note == "message"));
    }

    [Fact]
    public async Task IdempotencyHeaderContractAndSequentialCatchesPreventRetryConsumption()
    {
        var workflowId = await CreateWorkflowAsync(CreateConsecutiveCatchModel());

        var missingKeyInstance = await StartAsync(workflowId);
        using (var missing = await SendCatchAsync(
                   missingKeyInstance.Id,
                   JsonContent.Create(new { }, options: JsonOptions)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        }

        var mismatchedAliasInstance = await StartAsync(workflowId);
        var mismatchedRequest = CreateCatchRequest(
            mismatchedAliasInstance.Id,
            JsonContent.Create(new { }, options: JsonOptions));
        mismatchedRequest.Headers.Add(IdempotencyHeaders.Standard, "ONE");
        mismatchedRequest.Headers.Add(IdempotencyHeaders.LegacyAlias, "TWO");
        using (mismatchedRequest)
        using (var mismatched = await fixture.Client.SendAsync(mismatchedRequest))
        {
            Assert.Equal(HttpStatusCode.BadRequest, mismatched.StatusCode);
        }

        var aliasInstance = await StartAsync(workflowId);
        var aliasRequest = CreateCatchRequest(
            aliasInstance.Id,
            JsonContent.Create(new { }, options: JsonOptions));
        aliasRequest.Headers.Add(IdempotencyHeaders.LegacyAlias, "SEQUENTIAL-1");
        using (aliasRequest)
        using (var first = await fixture.Client.SendAsync(aliasRequest))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.Equal(3, (await ReadAsync<MessageDeliveryAckDto>(first)).CurrentNodeId);
        }

        using (var retry = await SendCatchAsync(
                   aliasInstance.Id,
                   JsonContent.Create(new { }, options: JsonOptions),
                   idempotencyKey: "SEQUENTIAL-1"))
        {
            await AssertConflictAsync(retry, "idempotency_conflict", aliasInstance.Id, 2);
        }

        using var second = await SendCatchAsync(
            aliasInstance.Id,
            JsonContent.Create(new { }, options: JsonOptions),
            idempotencyKey: "SEQUENTIAL-2");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("completed", (await ReadAsync<MessageDeliveryAckDto>(second)).Status);
    }

    [Fact]
    public async Task CustomIdempotencyHeaderIsRequiredAndCommittedRetriesRemainConflicts()
    {
        const string customHeader = "X-Message-Delivery-Id";
        var model = CreateModel(idempotent: true, withMapping: false);
        model.FlowNodes.Single(node => node.Id == 2).Message!.DeliveryIdempotencyHeaderName = customHeader;
        var workflowId = await CreateWorkflowAsync(model);

        var missingCustomHeader = await StartAsync(workflowId);
        using (var standardOnly = await SendCatchAsync(
                   missingCustomHeader.Id,
                   JsonContent.Create(new { }, options: JsonOptions),
                   idempotencyKey: "CUSTOM-1"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, standardOnly.StatusCode);
        }

        var aliasOnly = await StartAsync(workflowId);
        using (var aliasRequest = CreateCatchRequest(
                   aliasOnly.Id,
                   JsonContent.Create(new { }, options: JsonOptions)))
        {
            aliasRequest.Headers.Add(IdempotencyHeaders.LegacyAlias, "CUSTOM-2");
            using var aliasResponse = await fixture.Client.SendAsync(aliasRequest);
            Assert.Equal(HttpStatusCode.BadRequest, aliasResponse.StatusCode);
        }

        var delivered = await StartAsync(workflowId);
        using (var first = await SendCatchAsync(
                   delivered.Id,
                   JsonContent.Create(new { }, options: JsonOptions),
                   idempotencyKey: "CUSTOM-3",
                   idempotencyHeaderName: customHeader))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using var duplicate = await SendCatchAsync(
            delivered.Id,
            JsonContent.Create(new { }, options: JsonOptions),
            idempotencyKey: "CUSTOM-3",
            idempotencyHeaderName: customHeader);
        await AssertConflictAsync(duplicate, "idempotency_conflict", delivered.Id, 2);
    }

    [Fact]
    public async Task RequestBodyContractRejectsMalformedUnsupportedAndOversizedPayloadsAtomically()
    {
        var workflowId = await CreateWorkflowAsync(CreateModel(idempotent: false, withMapping: false));
        var instance = await StartAsync(workflowId);

        using (var malformed = await SendCatchAsync(
                   instance.Id,
                   new StringContent("{", Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        }

        using (var unsupported = await SendCatchAsync(
                   instance.Id,
                   new StringContent("{}", Encoding.UTF8, "text/plain")))
        {
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, unsupported.StatusCode);
        }

        var oversizedContent = new ByteArrayContent(new byte[1_048_577]);
        oversizedContent.Headers.ContentType = new("application/json");
        using (var oversized = await SendCatchAsync(instance.Id, oversizedContent))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        }

        var beforeSuccess = await GetInstanceAsync(instance.Id);
        Assert.Equal("running", beforeSuccess.Status);
        Assert.Equal(2, beforeSuccess.CurrentNodeId);
        Assert.DoesNotContain(beforeSuccess.History, history => history.Note == "message");

        using (var empty = await SendCatchAsync(instance.Id, new ByteArrayContent([])))
        {
            Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        }

        var nullInstance = await StartAsync(workflowId);
        using (var explicitNull = await SendCatchAsync(
                   nullInstance.Id,
                   new StringContent("null", Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.OK, explicitNull.StatusCode);
        }

        var streamingInstance = await StartAsync(workflowId);
        using var streaming = await SendCatchAsync(
            streamingInstance.Id,
            new UnknownLengthJsonContent("{}"));
        Assert.Equal(HttpStatusCode.OK, streaming.StatusCode);
    }

    [Fact]
    public async Task InstanceDetailsRedactMessageSecretsAndSlimAckHasExactContract()
    {
        var workflowId = await CreateWorkflowAsync(CreateModel(idempotent: false, withMapping: false));
        var instance = await StartAsync(workflowId);

        using var detailResponse = await SendAuthorizedAsync(
            HttpMethod.Get,
            $"/api/instances/{instance.Id}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        using var detailJson = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
        var catchNode = detailJson.RootElement
            .GetProperty("workflow")
            .GetProperty("definition")
            .GetProperty("flowNodes")
            .EnumerateArray()
            .Single(node => node.GetProperty("id").GetInt32() == 2);
        Assert.Equal("[redacted]", catchNode.GetProperty("message").GetProperty("clientSecret").GetString());
        Assert.Equal("[redacted]", catchNode.GetProperty("message").GetProperty("headerValue").GetString());

        using var workflowResponse = await SendAuthorizedAsync(
            HttpMethod.Get,
            $"/api/workflows/{workflowId}");
        var workflowJson = await workflowResponse.Content.ReadAsStringAsync();
        Assert.Contains("tests-secret", workflowJson, StringComparison.Ordinal);
        Assert.Contains("accepted", workflowJson, StringComparison.Ordinal);

        using var delivered = await SendCatchAsync(instance.Id, JsonContent.Create(new { }, options: JsonOptions));
        Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);
        using var ackJson = JsonDocument.Parse(await delivered.Content.ReadAsStringAsync());
        Assert.Equal(
            ["completion", "currentNodeExternalId", "currentNodeId", "currentNodeName", "executionPositions", "id", "status", "updatedAt"],
            ackJson.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
    }

    [Fact]
    public async Task CredentialTemplatesFailClosedAndObserveImmediateSettingRotation()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var settingNamespace = "catch-" + suffix;
        await SetSettingAsync(settingNamespace, "secret", "old-secret");

        var model = CreateModel(idempotent: false, withMapping: false);
        model.FlowNodes.Single(node => node.Id == 2).Message!.ClientSecret =
            $"${{setting.{settingNamespace}.secret}}";
        var workflowId = await CreateWorkflowAsync(model);

        var first = await StartAsync(workflowId);
        using (var accepted = await SendCatchAsync(
                   first.Id,
                   JsonContent.Create(new { }, options: JsonOptions),
                   clientSecret: "old-secret"))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        await SetSettingAsync(settingNamespace, "secret", "new-secret");
        var second = await StartAsync(workflowId);
        using (var stale = await SendCatchAsync(
                   second.Id,
                   JsonContent.Create(new { }, options: JsonOptions),
                   clientSecret: "old-secret"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        }
        using (var rotated = await SendCatchAsync(
                   second.Id,
                   JsonContent.Create(new { }, options: JsonOptions),
                   clientSecret: "new-secret"))
        {
            Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        }

        var missingModel = CreateModel(idempotent: false, withMapping: false);
        missingModel.FlowNodes.Single(node => node.Id == 2).Message!.ClientSecret =
            $"${{setting.missing-{suffix}.secret}}";
        var missingWorkflow = await CreateWorkflowAsync(missingModel);
        var missing = await StartAsync(missingWorkflow);
        using var unresolved = await SendCatchAsync(
            missing.Id,
            JsonContent.Create(new { }, options: JsonOptions),
            clientSecret: "");
        Assert.Equal(HttpStatusCode.Unauthorized, unresolved.StatusCode);
        var unchanged = await GetInstanceAsync(missing.Id);
        Assert.Equal(2, unchanged.CurrentNodeId);
    }

    [Fact]
    public async Task CatchEvidenceIsVisibleDownstreamAndDownstreamFailureRollsEverythingBack()
    {
        var successfulWorkflow = await CreateWorkflowAsync(CreateFlowInfoGatewayModel(failDownstream: false));
        var successful = await StartAsync(successfulWorkflow);
        using (var delivered = await SendCatchAsync(
                   successful.Id,
                   JsonContent.Create(new { status = "approved" }, options: JsonOptions),
                   idempotencyKey: "FLOWINFO-SUCCESS"))
        {
            Assert.True(
                delivered.StatusCode == HttpStatusCode.OK,
                $"Expected 200 but received {(int)delivered.StatusCode}: {await delivered.Content.ReadAsStringAsync()}");
            Assert.Equal("completed", (await ReadAsync<MessageDeliveryAckDto>(delivered)).Status);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var catchSummary = await db.SequenceFlowSummaries.SingleAsync(summary =>
                summary.InstanceId == successful.Id && summary.SequenceFlowId == 201);
            Assert.Equal(1, catchSummary.TraversalCount);
            Assert.Equal("messageCatch", catchSummary.LastTraversalKind);
        }

        var failingWorkflow = await CreateWorkflowAsync(CreateFlowInfoGatewayModel(failDownstream: true));
        var failing = await StartAsync(failingWorkflow);
        using (var rejected = await SendCatchAsync(
                   failing.Id,
                   JsonContent.Create(new { status = "approved" }, options: JsonOptions),
                   idempotencyKey: "FLOWINFO-ROLLBACK"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }

        var detail = await GetInstanceAsync(failing.Id);
        Assert.Equal("running", detail.Status);
        Assert.Equal(2, detail.CurrentNodeId);
        Assert.DoesNotContain(detail.Variables, variable => variable.VariableName == "status");
        Assert.DoesNotContain(detail.History, history => history.Note == "message");
        await using (var db = fixture.CreateDbContext())
        {
            Assert.False(await db.SequenceFlowSummaries.AnyAsync(summary =>
                summary.InstanceId == failing.Id && summary.SequenceFlowId != 101));
            Assert.False(await db.MessageDeliveryReceipts.AnyAsync(receipt =>
                receipt.InstanceId == failing.Id));
        }
    }

    [Fact]
    public async Task DefinitionRejectsConditionalCatchFlowAndNullMapping()
    {
        var conditional = CreateModel(idempotent: false, withMapping: false);
        conditional.SequenceFlows.Single(flow => flow.SourceRef == 2).Condition = "true";
        using (var response = await SendAuthorizedAsync(
                   HttpMethod.Post,
                   "/api/workflows",
                   new CreateWorkflowRequest(conditional, true)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var nullMapping = CreateModel(idempotent: false, withMapping: false);
        nullMapping.FlowNodes.Single(node => node.Id == 2).Message!.OutputMappings.Add(null!);
        using var nullResponse = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(nullMapping, true));
        Assert.Equal(HttpStatusCode.BadRequest, nullResponse.StatusCode);

        var reservedHeader = CreateModel(idempotent: true, withMapping: false);
        reservedHeader.FlowNodes.Single(node => node.Id == 2).Message!
            .DeliveryIdempotencyHeaderName = "X-Client-Id";
        using (var reservedResponse = await SendAuthorizedAsync(
                   HttpMethod.Post,
                   "/api/workflows",
                   new CreateWorkflowRequest(reservedHeader, true)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, reservedResponse.StatusCode);
        }

        var collidingHeader = CreateModel(idempotent: true, withMapping: false);
        collidingHeader.FlowNodes.Single(node => node.Id == 2).Message!
            .DeliveryIdempotencyHeaderName = "X-Correlation";
        using var collisionResponse = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(collidingHeader, true));
        Assert.Equal(HttpStatusCode.BadRequest, collisionResponse.StatusCode);
    }

    private async Task<long> CreateWorkflowAsync(WorkflowModel model)
    {
        using var response = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(model, true));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<WorkflowDetailDto>(response)).Id;
    }

    private async Task<StartInstanceResultDto> StartAsync(long workflowId)
    {
        using var response = await SendAuthorizedAsync(
            HttpMethod.Post,
            "/api/instances",
            new StartInstanceRequest(workflowId, null, null, null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<StartInstanceResultDto>(response);
    }

    private async Task<InstanceDetailDto> GetInstanceAsync(long instanceId)
    {
        using var response = await SendAuthorizedAsync(
            HttpMethod.Get,
            $"/api/instances/{instanceId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<InstanceDetailDto>(response);
    }

    private async Task<HttpResponseMessage> SendCatchAsync(
        long instanceId,
        HttpContent content,
        string? idempotencyKey = null,
        string clientSecret = "tests-secret",
        string idempotencyHeaderName = IdempotencyHeaders.Standard)
    {
        var request = CreateCatchRequest(instanceId, content, clientSecret);
        if (idempotencyKey is not null)
        {
            request.Headers.Add(idempotencyHeaderName, idempotencyKey);
        }
        return await fixture.Client.SendAsync(request);
    }

    private static HttpRequestMessage CreateCatchRequest(
        long instanceId,
        HttpContent content,
        string clientSecret = "tests-secret")
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/instances/{instanceId}/message")
        {
            Content = content
        };
        request.Headers.Add("X-Client-Id", "tests-client");
        if (!string.IsNullOrEmpty(clientSecret))
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
        ApiTestAuth.Authorize(request, "message-catch-admin", AdminRoles);
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

    private static async Task AssertConflictAsync(
        HttpResponseMessage response,
        string code,
        long instanceId,
        int sourceNodeId)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal($"/api/instances/{instanceId}", response.Headers.Location?.OriginalString);
        var conflict = await ReadAsync<MessageDeliveryConflictDto>(response);
        Assert.Equal(code, conflict.Code);
        Assert.Equal(instanceId, conflict.InstanceId);
        Assert.Equal(sourceNodeId, conflict.SourceNodeId);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException("Response body was empty.");

    private static WorkflowModel CreateModel(bool idempotent, bool withMapping)
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = "message-catch-tests-" + suffix,
            Name = "Message catch tests " + suffix,
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
                    Name = "Wait for message",
                    ExternalId = "wait-for-message",
                    Type = BpmnFlowNodeTypes.IntermediateMessageCatchEvent,
                    Message = new MessageCatchModel
                    {
                        ClientId = "tests-client",
                        ClientSecret = "tests-secret",
                        HeaderName = "X-Correlation",
                        HeaderValue = "accepted",
                        DeliveryIdempotency = idempotent,
                        OutputMappings = withMapping
                            ?
                            [
                                new MessageOutputMappingModel
                                {
                                    Variable = "status",
                                    Path = "status",
                                    DataType = WorkflowVariableTypes.String,
                                    Required = true
                                }
                            ]
                            : []
                    }
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "End",
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

    private static WorkflowModel CreateConsecutiveCatchModel()
    {
        var model = CreateModel(idempotent: true, withMapping: false);
        var originalEnd = model.FlowNodes.Single(node => node.Id == 3);
        originalEnd.Id = 4;
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 3,
            Name = "Wait for second message",
            ExternalId = "wait-for-second-message",
            Type = BpmnFlowNodeTypes.IntermediateMessageCatchEvent,
            Message = new MessageCatchModel
            {
                ClientId = "tests-client",
                ClientSecret = "tests-secret",
                HeaderName = "X-Correlation",
                HeaderValue = "accepted",
                DeliveryIdempotency = true
            }
        });
        model.SequenceFlows.Single(flow => flow.Id == 201).TargetRef = 3;
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4 });
        return model;
    }

    private static WorkflowModel CreateFlowInfoGatewayModel(bool failDownstream)
    {
        var model = CreateModel(idempotent: true, withMapping: true);
        model.FlowNodes.RemoveAll(node => node.Id == 3);
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 3,
            Name = "Route",
            Type = BpmnFlowNodeTypes.ExclusiveGateway
        });
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 4,
            Name = failDownstream ? "Failing service" : "Approved end",
            Type = failDownstream ? BpmnFlowNodeTypes.ServiceTask : BpmnFlowNodeTypes.EndEvent,
            Service = failDownstream
                ? new ServiceTaskModel { Url = "https://tests.local/unconfigured-failure" }
                : null
        });
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 5,
            Name = "Default end",
            Type = BpmnFlowNodeTypes.EndEvent
        });
        if (failDownstream)
        {
            model.FlowNodes.Add(new FlowNodeModel
            {
                Id = 6,
                Name = "Unreachable end",
                Type = BpmnFlowNodeTypes.EndEvent
            });
        }

        model.SequenceFlows.Single(flow => flow.Id == 201).TargetRef = 3;
        model.SequenceFlows.Add(new SequenceFlowModel
        {
            Id = 301,
            SourceRef = 3,
            TargetRef = 4,
            Condition = "FlowInfo(201, 'traversals.count') == 1 and status == 'approved'",
            ConditionPriority = 1
        });
        model.SequenceFlows.Add(new SequenceFlowModel
        {
            Id = 302,
            SourceRef = 3,
            TargetRef = 5,
            IsDefault = true
        });
        if (failDownstream)
        {
            model.SequenceFlows.Add(new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 6 });
        }
        return model;
    }

    private sealed class UnknownLengthJsonContent : HttpContent
    {
        private readonly byte[] _body;

        public UnknownLengthJsonContent(string json)
        {
            _body = Encoding.UTF8.GetBytes(json);
            Headers.ContentType = new("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return stream.WriteAsync(_body).AsTask();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
