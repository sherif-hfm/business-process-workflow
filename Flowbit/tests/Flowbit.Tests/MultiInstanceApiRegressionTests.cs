using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Flowbit.Infrastructure.Entities;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

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
    public async Task BusinessKeyPoliciesAreAtomicCaseSensitiveAndFilterable()
    {
        var activeModel = LoadUniqueModel("votes-users-list.json", "business-key-active");
        ConfigureBusinessKey(activeModel, BusinessKeyUniqueness.Active);
        var activeWorkflow = await CreateWorkflowAsync(activeModel);

        using var first = await StartWithBusinessKeyAsync(activeWorkflow, "  V-42  ");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstAck = await ReadAsync<StartInstanceResultDto>(first);
        Assert.Equal("V-42", firstAck.BusinessKey);
        Assert.Equal(BusinessKeyUniqueness.Active, firstAck.BusinessKeyUniqueness);

        using var duplicate = await StartWithBusinessKeyAsync(activeWorkflow, "V-42");
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using (var conflict = JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync()))
        {
            Assert.Equal("business_key_conflict", conflict.RootElement.GetProperty("code").GetString());
            Assert.Equal(firstAck.Id, conflict.RootElement.GetProperty("existingInstanceId").GetInt64());
        }

        using var differentCase = await StartWithBusinessKeyAsync(activeWorkflow, "v-42");
        Assert.Equal(HttpStatusCode.Created, differentCase.StatusCode);

        using var filtered = await SendAsync(HttpMethod.Get, "/api/instances?businessKey=V-42&pageSize=20");
        Assert.Equal(HttpStatusCode.OK, filtered.StatusCode);
        var page = await ReadAsync<PagedResult<InstanceSummaryDto>>(filtered);
        Assert.Single(page.Items);
        Assert.Equal(firstAck.Id, page.Items[0].Id);

        using var inboxFiltered = await SendAsync(HttpMethod.Get, "/api/instances/inbox?businessKey=V-42&pageSize=20");
        Assert.Equal(HttpStatusCode.OK, inboxFiltered.StatusCode);
        var inboxPage = await ReadAsync<PagedResult<InboxItemDto>>(inboxFiltered);
        Assert.Single(inboxPage.Items);
        Assert.Equal("V-42", inboxPage.Items[0].BusinessKey);

        var detail = await GetInstanceAsync(firstAck.Id);
        Assert.Equal("V-42", detail.BusinessKey);
        Assert.Equal(BusinessKeyUniqueness.Active, detail.BusinessKeyUniqueness);

        using var cancelled = await SendAsync(HttpMethod.Post, $"/api/instances/{firstAck.Id}/cancel");
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        using var reused = await StartWithBusinessKeyAsync(activeWorkflow, "V-42");
        Assert.Equal(HttpStatusCode.Created, reused.StatusCode);

        var allModel = LoadUniqueModel("votes-users-list.json", "business-key-all");
        ConfigureBusinessKey(allModel, BusinessKeyUniqueness.All);
        var allWorkflow = await CreateWorkflowAsync(allModel);
        using var permanent = await StartWithBusinessKeyAsync(allWorkflow, "PERMANENT-1");
        var permanentAck = await ReadAsync<StartInstanceResultDto>(permanent);
        using var permanentCancelled = await SendAsync(HttpMethod.Post, $"/api/instances/{permanentAck.Id}/cancel");
        Assert.Equal(HttpStatusCode.NoContent, permanentCancelled.StatusCode);
        using var permanentDuplicate = await StartWithBusinessKeyAsync(allWorkflow, "PERMANENT-1");
        Assert.Equal(HttpStatusCode.Conflict, permanentDuplicate.StatusCode);
    }

    [Fact]
    public async Task ConcurrentBusinessKeyStartsCreateExactlyOneInstance()
    {
        var model = LoadUniqueModel("votes-users-list.json", "business-key-concurrency");
        ConfigureBusinessKey(model, BusinessKeyUniqueness.Active);
        var workflow = await CreateWorkflowAsync(model);

        var starts = await Task.WhenAll(
            StartWithBusinessKeyAsync(workflow, "RACE-1"),
            StartWithBusinessKeyAsync(workflow, "RACE-1"));
        try
        {
            Assert.Single(starts, response => response.StatusCode == HttpStatusCode.Created);
            Assert.Single(starts, response => response.StatusCode == HttpStatusCode.Conflict);
        }
        finally
        {
            foreach (var response in starts) response.Dispose();
        }
    }

    [Fact]
    public async Task MessageStartDuplicateKeysReturnConflictWithExistingInstanceId()
    {
        var model = LoadUniqueModel("votes-users-list.json", "business-key-message");
        var start = model.FlowNodes.Single(node => node.Id == model.InitialEventId);
        start.Type = BpmnFlowNodeTypes.MessageStartEvent;
        start.Roles = [];
        start.Variables = [];
        start.BusinessKey = new BusinessKeyModel
        {
            Variable = "violationId",
            Uniqueness = BusinessKeyUniqueness.Active
        };
        start.Idempotency = new IdempotencyModel
        {
            HeaderName = IdempotencyHeaders.Standard,
            Variable = "requestId"
        };
        start.Message = new MessageCatchModel
        {
            ClientId = "tests-client",
            ClientSecret = "tests-secret",
            HeaderName = "X-Correlation",
            HeaderValue = "accepted",
            OutputMappings =
            [
                new MessageOutputMappingModel
                {
                    Variable = "violationId",
                    Path = "violationId",
                    Required = true,
                    DataType = WorkflowVariableTypes.String,
                    IsArray = false
                }
            ]
        };
        model.InitialEventId = null;
        await CreateWorkflowAsync(model);

        using var first = await SendMessageStartAsync(model.Id, "REQUEST-1", "V-1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstAck = await ReadAsync<MessageStartAckDto>(first);

        using var retry = await SendMessageStartAsync(model.Id, "REQUEST-1", "V-1");
        await AssertMessageStartConflictAsync(
            retry,
            "idempotency_conflict",
            firstAck.InstanceId);

        using var malformedRetry = await SendMessageStartPayloadAsync(
            model.Id,
            new { },
            "REQUEST-1");
        await AssertMessageStartConflictAsync(
            malformedRetry,
            "idempotency_conflict",
            firstAck.InstanceId);

        using var domainDuplicate = await SendMessageStartAsync(model.Id, "REQUEST-2", "V-1");
        await AssertMessageStartConflictAsync(
            domainDuplicate,
            "business_key_conflict",
            firstAck.InstanceId);
        await using (var conflictDb = fixture.CreateDbContext())
        {
            Assert.False(await conflictDb.WorkflowIdempotencyClaims.AnyAsync(claim =>
                claim.WorkflowKey == model.Id && claim.IdempotencyKey == "REQUEST-2"));
        }

        using var mismatchedRetry = await SendMessageStartAsync(model.Id, "REQUEST-1", "V-2");
        await AssertMessageStartConflictAsync(
            mismatchedRetry,
            "idempotency_conflict",
            firstAck.InstanceId);

        using var cancelled = await SendAsync(HttpMethod.Post, $"/api/instances/{firstAck.InstanceId}/cancel");
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        using var reused = await SendMessageStartAsync(model.Id, "REQUEST-3", "V-1");
        Assert.Equal(HttpStatusCode.OK, reused.StatusCode);
        var reusedAck = await ReadAsync<MessageStartAckDto>(reused);
        Assert.NotEqual(firstAck.InstanceId, reusedAck.InstanceId);

        using var oldRetry = await SendMessageStartAsync(model.Id, "REQUEST-1", "V-1");
        await AssertMessageStartConflictAsync(
            oldRetry,
            "idempotency_conflict",
            firstAck.InstanceId);

        var messageRace = await Task.WhenAll(
            SendMessageStartAsync(model.Id, "REQUEST-RACE", "V-RACE"),
            SendMessageStartAsync(model.Id, "REQUEST-RACE", "V-RACE"));
        try
        {
            Assert.Single(messageRace, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(messageRace, response => response.StatusCode == HttpStatusCode.Conflict);
        }
        finally
        {
            foreach (var response in messageRace) response.Dispose();
        }
    }

    [Fact]
    public async Task AuthenticatedStartIdempotencyIsPermanentTrimmedExactAndAliasAware()
    {
        var model = LoadUniqueModel("votes-users-list.json", "start-idempotency");
        var start = model.FlowNodes.Single(node => node.Id == model.InitialEventId);
        start.Idempotency = new IdempotencyModel
        {
            HeaderName = IdempotencyHeaders.Standard,
            Variable = "requestId"
        };
        var workflow = await CreateWorkflowAsync(model);

        using var first = await StartWithIdempotencyAsync(workflow, "  REQUEST-1  ");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstAck = await ReadAsync<StartInstanceResultDto>(first);
        var firstDetail = await GetInstanceAsync(firstAck.Id);
        Assert.Equal("REQUEST-1", firstDetail.Variables.Single(variable =>
            variable.VariableName == "requestId").Value.GetString());

        using var duplicate = await StartWithIdempotencyAsync(workflow, "REQUEST-1");
        await AssertMessageStartConflictAsync(duplicate, "idempotency_conflict", firstAck.Id);

        using var differentCase = await StartWithIdempotencyAsync(workflow, "request-1");
        Assert.Equal(HttpStatusCode.Created, differentCase.StatusCode);

        using var alias = await StartWithIdempotencyAsync(
            workflow,
            "ALIAS-1",
            IdempotencyHeaders.LegacyAlias);
        Assert.Equal(HttpStatusCode.Created, alias.StatusCode);

        using var bothEqual = await StartWithIdempotencyHeadersAsync(
            workflow,
            (IdempotencyHeaders.Standard, new[] { "BOTH-1" }),
            (IdempotencyHeaders.LegacyAlias, new[] { "BOTH-1" }));
        Assert.Equal(HttpStatusCode.Created, bothEqual.StatusCode);

        using var bothDifferent = await StartWithIdempotencyHeadersAsync(
            workflow,
            (IdempotencyHeaders.Standard, new[] { "ONE" }),
            (IdempotencyHeaders.LegacyAlias, new[] { "TWO" }));
        Assert.Equal(HttpStatusCode.BadRequest, bothDifferent.StatusCode);

        using var repeated = await StartWithIdempotencyHeadersAsync(
            workflow,
            (IdempotencyHeaders.Standard, new[] { "A", "A" }));
        Assert.Equal(HttpStatusCode.BadRequest, repeated.StatusCode);

        using var missing = await StartWithIdempotencyHeadersAsync(workflow);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        using var oversized = await StartWithIdempotencyAsync(workflow, new string('x', 301));
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);

        var normalRace = await Task.WhenAll(
            StartWithIdempotencyAsync(workflow, "NORMAL-RACE"),
            StartWithIdempotencyAsync(workflow, "NORMAL-RACE"));
        try
        {
            Assert.Single(normalRace, response => response.StatusCode == HttpStatusCode.Created);
            Assert.Single(normalRace, response => response.StatusCode == HttpStatusCode.Conflict);
        }
        finally
        {
            foreach (var response in normalRace) response.Dispose();
        }

        using var cancelled = await SendAsync(HttpMethod.Post, $"/api/instances/{firstAck.Id}/cancel");
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        using var afterCancellation = await StartWithIdempotencyAsync(workflow, "REQUEST-1");
        await AssertMessageStartConflictAsync(afterCancellation, "idempotency_conflict", firstAck.Id);

        var customModel = LoadUniqueModel("votes-users-list.json", "custom-idempotency-header");
        customModel.FlowNodes.Single(node => node.Id == customModel.InitialEventId).Idempotency =
            new IdempotencyModel { HeaderName = "X-Request-Id", Variable = "requestId" };
        var customWorkflow = await CreateWorkflowAsync(customModel);
        using var standardDoesNotAliasCustom = await StartWithIdempotencyAsync(customWorkflow, "CUSTOM-1");
        Assert.Equal(HttpStatusCode.BadRequest, standardDoesNotAliasCustom.StatusCode);
        using var custom = await StartWithIdempotencyAsync(customWorkflow, "CUSTOM-1", "X-Request-Id");
        Assert.Equal(HttpStatusCode.Created, custom.StatusCode);

        var unlimitedModel = LoadUniqueModel("votes-users-list.json", "unconfigured-idempotency");
        var unlimitedWorkflow = await CreateWorkflowAsync(unlimitedModel);
        using var ignored = await StartWithIdempotencyHeadersAsync(
            unlimitedWorkflow,
            (IdempotencyHeaders.Standard, new[] { "A", "B" }));
        Assert.Equal(HttpStatusCode.Created, ignored.StatusCode);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(5, await db.WorkflowIdempotencyClaims.CountAsync(claim =>
            claim.WorkflowKey == model.Id));
    }

    [Fact]
    public async Task IdempotencyScopeSpansStartRoutesAndVersionsButNotWorkflowFamilies()
    {
        var model = LoadUniqueModel("votes-users-list.json", "idempotency-scope");
        var normalStart = model.FlowNodes.Single(node => node.Id == model.InitialEventId);
        normalStart.Idempotency = new IdempotencyModel
        {
            HeaderName = IdempotencyHeaders.Standard,
            Variable = "normalRequestId"
        };
        var target = model.SequenceFlows.Single(flow => flow.SourceRef == normalStart.Id).TargetRef;
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 90,
            Name = "Webhook start",
            Type = BpmnFlowNodeTypes.MessageStartEvent,
            Idempotency = new IdempotencyModel
            {
                HeaderName = IdempotencyHeaders.Standard,
                Variable = "messageRequestId"
            },
            Message = new MessageCatchModel
            {
                ClientId = "tests-client",
                ClientSecret = "tests-secret",
                HeaderName = "X-Correlation",
                HeaderValue = "accepted"
            }
        });
        model.SequenceFlows.Add(new SequenceFlowModel
        {
            Id = 990,
            SourceRef = 90,
            TargetRef = target
        });
        var workflow = await CreateWorkflowAsync(model);

        using var normal = await StartWithIdempotencyAsync(workflow, "CROSS-ROUTE-1");
        Assert.Equal(HttpStatusCode.Created, normal.StatusCode);
        var normalAck = await ReadAsync<StartInstanceResultDto>(normal);

        using var messageConflict = await SendMessageStartPayloadAsync(
            model.Id,
            new { },
            "CROSS-ROUTE-1");
        await AssertMessageStartConflictAsync(
            messageConflict,
            "idempotency_conflict",
            normalAck.Id);

        var updated = Clone(model);
        updated.Name += " v2";
        using var createVersion = await SendAsync(
            HttpMethod.Put,
            $"/api/workflows/{workflow}",
            new UpdateWorkflowRequest(updated, true));
        Assert.Equal(HttpStatusCode.OK, createVersion.StatusCode);
        var version = await ReadAsync<WorkflowDetailDto>(createVersion);
        using var versionConflict = await StartWithIdempotencyAsync(version.Id, "CROSS-ROUTE-1");
        await AssertMessageStartConflictAsync(
            versionConflict,
            "idempotency_conflict",
            normalAck.Id);

        var otherFamily = Clone(model);
        otherFamily.Id += "-other";
        otherFamily.Name += " other";
        var otherWorkflow = await CreateWorkflowAsync(otherFamily);
        using var reusedElsewhere = await StartWithIdempotencyAsync(otherWorkflow, "CROSS-ROUTE-1");
        Assert.Equal(HttpStatusCode.Created, reusedElsewhere.StatusCode);

        var raceKey = "CROSS-RACE-" + Guid.NewGuid().ToString("N");
        var race = await Task.WhenAll(
            StartWithIdempotencyAsync(workflow, raceKey),
            SendMessageStartPayloadAsync(model.Id, new { }, raceKey));
        try
        {
            Assert.Single(race, response => response.StatusCode == HttpStatusCode.Conflict);
            Assert.Single(race, response => response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK);
        }
        finally
        {
            foreach (var response in race) response.Dispose();
        }
    }

    [Fact]
    public async Task TypedMessageStartMappingsResolveDefaultsValidateTypesAndPersistAtomically()
    {
        var model = LoadUniqueModel("votes-users-list.json", "typed-message-start");
        var start = model.FlowNodes.Single(node => node.Id == model.InitialEventId);
        start.Type = BpmnFlowNodeTypes.MessageStartEvent;
        start.Roles = [];
        start.Variables = [];
        start.BusinessKey = new BusinessKeyModel
        {
            Variable = "violationId",
            Uniqueness = BusinessKeyUniqueness.Active
        };
        start.Idempotency = new IdempotencyModel
        {
            HeaderName = IdempotencyHeaders.Standard,
            Variable = "requestId"
        };
        start.Message = new MessageCatchModel
        {
            ClientId = "tests-client",
            ClientSecret = "tests-secret",
            HeaderName = "X-Correlation",
            HeaderValue = "accepted",
            OutputMappings =
            [
                TypedMapping("violationId", "violationId", WorkflowVariableTypes.String, required: true),
                TypedMapping("amount", "order.amount", WorkflowVariableTypes.Number, required: true,
                    validation: "amount > 0 and country == 'SA'"),
                TypedMapping("country", "order.country", WorkflowVariableTypes.String,
                    defaultValue: JsonSerializer.SerializeToElement("SA")),
                TypedMapping("tags", "order.tags", WorkflowVariableTypes.String, required: true, isArray: true),
                TypedMapping("businessDate", "order.businessDate", WorkflowVariableTypes.Date, required: true),
                TypedMapping("receivedAt", "order.receivedAt", WorkflowVariableTypes.DateTime, required: true),
                TypedMapping("metadata", "order.metadata", WorkflowVariableTypes.Json),
                TypedMapping("region", string.Empty, WorkflowVariableTypes.String,
                    defaultValue: JsonSerializer.SerializeToElement("central")),
                TypedMapping("note", "order.note", WorkflowVariableTypes.String)
            ]
        };
        model.InitialEventId = null;
        await CreateWorkflowAsync(model);

        using var valid = await SendMessageStartPayloadAsync(model.Id, new
        {
            violationId = "TYPED-1",
            order = new
            {
                amount = 12.5,
                tags = new[] { "safety", "priority" },
                businessDate = "2026-07-15",
                receivedAt = "2026-07-15T10:30:00+03:00",
                metadata = new { source = "camera" }
            }
        }, "TYPED-VALID");
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        var ack = await ReadAsync<MessageStartAckDto>(valid);
        var detail = await GetInstanceAsync(ack.InstanceId);
        var values = detail.Variables.ToDictionary(variable => variable.VariableName, variable => variable.Value);
        Assert.Equal("TYPED-1", values["violationId"].GetString());
        Assert.Equal(12.5, values["amount"].GetDouble());
        Assert.Equal("SA", values["country"].GetString());
        Assert.Equal("central", values["region"].GetString());
        Assert.Equal(2, values["tags"].GetArrayLength());
        Assert.Equal("camera", values["metadata"].GetProperty("source").GetString());
        Assert.DoesNotContain("note", values.Keys);

        using var wrongType = await SendMessageStartPayloadAsync(model.Id, new
        {
            violationId = "BAD-TYPE",
            order = new
            {
                amount = "12.5",
                tags = new[] { "safety" },
                businessDate = "2026-07-15",
                receivedAt = "2026-07-15T10:30:00Z"
            }
        }, "BAD-TYPE-IDEMPOTENCY");
        Assert.Equal(HttpStatusCode.BadRequest, wrongType.StatusCode);

        using var invalidDate = await SendMessageStartPayloadAsync(model.Id, new
        {
            violationId = "BAD-DATE",
            order = new
            {
                amount = 1,
                tags = new[] { "safety" },
                businessDate = "15/07/2026",
                receivedAt = "2026-07-15T10:30:00Z"
            }
        }, "BAD-DATE-IDEMPOTENCY");
        Assert.Equal(HttpStatusCode.BadRequest, invalidDate.StatusCode);

        using var failedValidation = await SendMessageStartPayloadAsync(model.Id, new
        {
            violationId = "BAD-VALIDATION",
            order = new
            {
                amount = -1,
                tags = new[] { "safety" },
                businessDate = "2026-07-15",
                receivedAt = "2026-07-15T10:30:00Z"
            }
        }, "BAD-VALIDATION-IDEMPOTENCY");
        Assert.Equal(HttpStatusCode.BadRequest, failedValidation.StatusCode);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(1, await db.WorkflowInstances.CountAsync(instance => instance.WorkflowKey == model.Id));
        Assert.False(await db.WorkflowBusinessKeyClaims.AnyAsync(claim =>
            claim.WorkflowKey == model.Id && claim.BusinessKey.StartsWith("BAD-")));
        Assert.False(await db.WorkflowIdempotencyClaims.AnyAsync(claim =>
            claim.WorkflowKey == model.Id && claim.IdempotencyKey.StartsWith("BAD-")));
    }

    [Fact]
    public async Task LegacyIntermediateMessageCatchMappingsNormalizeAndRemainCreateOrUpdateWrites()
    {
        var model = CreateRawMessageCatchModel();
        var workflowId = await CreateWorkflowAsync(model);
        using var started = await SendAsync(
            HttpMethod.Post,
            "/api/instances",
            new StartInstanceRequest(workflowId, null, null, null));
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var startAck = await ReadAsync<StartInstanceResultDto>(started);
        Assert.Equal(2, startAck.CurrentNodeId);

        using var first = await SendCatchMessageAsync(startAck.Id, "new");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(3, (await ReadAsync<MessageDeliveryAckDto>(first)).CurrentNodeId);

        using var second = await SendCatchMessageAsync(startAck.Id, "updated");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("completed", (await ReadAsync<MessageDeliveryAckDto>(second)).Status);

        var detail = await GetInstanceAsync(startAck.Id);
        var statuses = detail.Variables.Where(variable => variable.VariableName == "externalStatus").ToList();
        Assert.Equal(2, statuses.Count);
        Assert.Equal("updated", statuses[^1].Value.GetString());
    }

    [Fact]
    public async Task TypedMessageCatchMappingsResolveValidateAndWriteAtomically()
    {
        var model = CreateTypedMessageCatchModel();
        var workflowId = await CreateWorkflowAsync(model);

        var validStart = await StartWorkflowAsync(workflowId);
        using (var valid = await SendCatchPayloadAsync(validStart.Id, new
        {
            result = new { decision = "approved", score = 12 },
            tags = new[] { "safe", "priority" },
            businessDate = "2026-07-15",
            approved = true,
            receivedAt = "2026-07-15T10:30:00+03:00",
            metadata = new { source = "webhook" },
            ratings = new[] { 1.5, 2.5 }
        }))
        {
            Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
            Assert.Equal("completed", (await ReadAsync<MessageDeliveryAckDto>(valid)).Status);
        }
        var validDetail = await GetInstanceAsync(validStart.Id);
        var latest = validDetail.Variables
            .GroupBy(variable => variable.VariableName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("approved", latest["decision"].GetString());
        Assert.Equal(12, latest["score"].GetInt32());
        Assert.Equal("SA", latest["country"].GetString());
        Assert.Equal("SA-central", latest["region"].GetString());
        Assert.Equal(2, latest["tags"].GetArrayLength());
        Assert.Equal("2026-07-15", latest["businessDate"].GetString());
        Assert.True(latest["approved"].GetBoolean());
        Assert.Equal("2026-07-15T10:30:00+03:00", latest["receivedAt"].GetString());
        Assert.Equal("webhook", latest["metadata"].GetProperty("source").GetString());
        Assert.Equal(2, latest["ratings"].GetArrayLength());
        Assert.DoesNotContain("note", latest.Keys);

        var wrongTypeStart = await StartWorkflowAsync(workflowId);
        using (var wrongType = await SendCatchPayloadAsync(wrongTypeStart.Id, new
        {
            result = new { decision = "approved", score = "12" },
            tags = new[] { "safe" },
            businessDate = "2026-07-15"
        }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, wrongType.StatusCode);
        }
        await AssertCatchFailureLeftNoOutputsAsync(wrongTypeStart.Id);

        var staleRequiredStart = await StartWorkflowAsync(workflowId);
        using (var missingRequired = await SendCatchPayloadAsync(staleRequiredStart.Id, new
        {
            result = new { score = 12 },
            tags = new[] { "safe" },
            businessDate = "2026-07-15"
        }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missingRequired.StatusCode);
        }
        await AssertCatchFailureLeftNoOutputsAsync(staleRequiredStart.Id);

        var mappingValidationStart = await StartWorkflowAsync(workflowId);
        using (var invalidMapping = await SendCatchPayloadAsync(mappingValidationStart.Id, new
        {
            result = new { decision = "approved", score = -1 },
            tags = new[] { "safe" },
            businessDate = "2026-07-15"
        }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalidMapping.StatusCode);
        }
        await AssertCatchFailureLeftNoOutputsAsync(mappingValidationStart.Id);

        var processValidationStart = await StartWorkflowAsync(workflowId);
        using (var invalidProcess = await SendCatchPayloadAsync(processValidationStart.Id, new
        {
            result = new { decision = "blocked", score = 12 },
            tags = new[] { "safe" },
            businessDate = "2026-07-15"
        }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalidProcess.StatusCode);
        }
        await AssertCatchFailureLeftNoOutputsAsync(processValidationStart.Id);
    }

    [Fact]
    public async Task TypedServiceMappingsSucceedOrFailWithoutPartialWrites()
    {
        var successModel = CreateTypedServiceModel("success", "typed-output-success", withBoundary: false);
        var successWorkflow = await CreateWorkflowAsync(successModel);
        using (var started = await SendAsync(
            HttpMethod.Post,
            "/api/instances",
            new StartInstanceRequest(successWorkflow, null, null, null)))
        {
            Assert.Equal(HttpStatusCode.Created, started.StatusCode);
            Assert.Equal("completed", (await ReadAsync<StartInstanceResultDto>(started)).Status);
        }
        var successPage = await ListWorkflowInstancesAsync(successModel.Id);
        var successDetail = await GetInstanceAsync(Assert.Single(successPage.Items).Id);
        var successValues = successDetail.Variables
            .GroupBy(variable => variable.VariableName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("approved", successValues["decision"].GetString());
        Assert.Equal(12, successValues["score"].GetInt32());
        Assert.Equal("SA", successValues["country"].GetString());
        Assert.Equal("SA-central", successValues["region"].GetString());
        Assert.Equal(2, successValues["tags"].GetArrayLength());
        Assert.True(successValues["approved"].GetBoolean());
        Assert.Equal("2026-07-15T10:30:00+03:00", successValues["receivedAt"].GetString());
        Assert.Equal("service", successValues["metadata"].GetProperty("source").GetString());
        Assert.Equal(2, successValues["ratings"].GetArrayLength());
        Assert.Equal(200, successValues["serviceStatus"].GetInt32());

        var uncaughtModel = CreateTypedServiceModel("uncaught", "typed-output-invalid", withBoundary: false);
        var uncaughtWorkflow = await CreateWorkflowAsync(uncaughtModel);
        using (var failed = await SendAsync(
            HttpMethod.Post,
            "/api/instances",
            new StartInstanceRequest(uncaughtWorkflow, null, null, null)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
        }
        Assert.Empty((await ListWorkflowInstancesAsync(uncaughtModel.Id)).Items);

        var boundaryModel = CreateTypedServiceModel("boundary", "typed-output-invalid", withBoundary: true);
        var boundaryWorkflow = await CreateWorkflowAsync(boundaryModel);
        using (var caught = await SendAsync(
            HttpMethod.Post,
            "/api/instances",
            new StartInstanceRequest(boundaryWorkflow, null, null, null)))
        {
            Assert.Equal(HttpStatusCode.Created, caught.StatusCode);
            Assert.Equal(5, (await ReadAsync<StartInstanceResultDto>(caught)).CurrentNodeId);
        }
        var boundaryPage = await ListWorkflowInstancesAsync(boundaryModel.Id);
        var boundaryDetail = await GetInstanceAsync(Assert.Single(boundaryPage.Items).Id);
        Assert.Single(boundaryDetail.Variables, variable => variable.VariableName == "decision");
        Assert.DoesNotContain(boundaryDetail.Variables, variable =>
            variable.VariableName is "score" or "country" or "region" or "tags" or "businessDate"
                or "approved" or "receivedAt" or "metadata" or "ratings");
        Assert.Equal(200, boundaryDetail.Variables.Last(variable => variable.VariableName == "serviceStatus").Value.GetInt32());
        Assert.Contains("must be number", boundaryDetail.Variables.Last(variable => variable.VariableName == "serviceError").Value.GetString());

        var processModel = CreateTypedServiceModel("process-validation", "typed-output-blocked", withBoundary: false);
        var processWorkflow = await CreateWorkflowAsync(processModel);
        using (var processFailure = await SendAsync(
            HttpMethod.Post,
            "/api/instances",
            new StartInstanceRequest(processWorkflow, null, null, null)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, processFailure.StatusCode);
        }
        Assert.Empty((await ListWorkflowInstancesAsync(processModel.Id)).Items);
    }

    [Fact]
    public async Task BusinessKeyFamilyActivationBlocksUnkeyedVersionsAndPrematureKeyedStarts()
    {
        var unkeyed = LoadUniqueModel("votes-users-list.json", "business-key-family");
        var oldWorkflowId = await CreateWorkflowAsync(unkeyed);
        var keyed = Clone(unkeyed);
        ConfigureBusinessKey(keyed, BusinessKeyUniqueness.Active);

        using var createKeyed = await SendAsync(
            HttpMethod.Put,
            $"/api/workflows/{oldWorkflowId}",
            new UpdateWorkflowRequest(keyed, true));
        Assert.Equal(HttpStatusCode.OK, createKeyed.StatusCode);
        var keyedVersion = await ReadAsync<WorkflowDetailDto>(createKeyed);

        using var premature = await StartWithBusinessKeyAsync(keyedVersion.Id, "FAMILY-1");
        Assert.Equal(HttpStatusCode.BadRequest, premature.StatusCode);

        using var activate = await SendAsync(
            HttpMethod.Post,
            $"/api/workflows/{keyedVersion.Id}/set-default");
        Assert.Equal(HttpStatusCode.NoContent, activate.StatusCode);

        using var oldStart = await SendAsync(
            HttpMethod.Post,
            "/api/instances",
            new StartInstanceRequest(oldWorkflowId, null, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, oldStart.StatusCode);

        using var createUnkeyed = await SendAsync(
            HttpMethod.Put,
            $"/api/workflows/{oldWorkflowId}",
            new UpdateWorkflowRequest(unkeyed, false));
        Assert.Equal(HttpStatusCode.BadRequest, createUnkeyed.StatusCode);
    }

    [Theory]
    [InlineData(BpmnFlowNodeTypes.EndEvent, "completed")]
    [InlineData(BpmnFlowNodeTypes.ErrorEndEvent, "faulted")]
    public async Task ImmediateTerminalStartsReleaseActiveBusinessKey(string endType, string expectedStatus)
    {
        var model = CreateImmediateBusinessKeyModel("business-key-" + expectedStatus, endType);
        var workflow = await CreateWorkflowAsync(model);

        using var first = await StartWithBusinessKeyAsync(workflow, "TERMINAL-1");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(expectedStatus, (await ReadAsync<StartInstanceResultDto>(first)).Status);

        using var reused = await StartWithBusinessKeyAsync(workflow, "TERMINAL-1");
        Assert.Equal(HttpStatusCode.Created, reused.StatusCode);
    }

    [Fact]
    public async Task FailedPassThroughRollsBackBusinessKeyReservation()
    {
        var model = CreateImmediateBusinessKeyModel("business-key-rollback", BpmnFlowNodeTypes.EndEvent);
        model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsStart(node.Type)).Idempotency =
            new IdempotencyModel
            {
                HeaderName = IdempotencyHeaders.Standard,
                Variable = "requestId"
            };
        var end = model.FlowNodes.Single(node => node.Id == 2);
        end.Id = 3;
        model.SequenceFlows.Single().TargetRef = 2;
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 2,
            Name = "Failing script",
            Type = BpmnFlowNodeTypes.ScriptTask,
            ScriptFormat = ScriptFormats.JavaScript,
            Script = "throw new Error('business-key rollback');"
        });
        model.SequenceFlows.Add(new SequenceFlowModel
        {
            Id = 102,
            Name = "After script",
            SourceRef = 2,
            TargetRef = 3
        });
        var workflow = await CreateWorkflowAsync(model);

        using var failed = await StartWithBusinessAndIdempotencyAsync(
            workflow,
            "ROLLBACK-1",
            "TRANSPORT-ROLLBACK-1");
        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);

        using var repeated = await StartWithBusinessAndIdempotencyAsync(
            workflow,
            "ROLLBACK-1",
            "TRANSPORT-ROLLBACK-1");
        Assert.Equal(HttpStatusCode.BadRequest, repeated.StatusCode);

        await using var db = fixture.CreateDbContext();
        Assert.False(await db.WorkflowBusinessKeyClaims.AnyAsync(claim =>
            claim.WorkflowKey == model.Id && claim.BusinessKey == "ROLLBACK-1"));
        Assert.False(await db.WorkflowInstances.AnyAsync(instance =>
            instance.WorkflowKey == model.Id && instance.BusinessKey == "ROLLBACK-1"));
        Assert.False(await db.WorkflowIdempotencyClaims.AnyAsync(claim =>
            claim.WorkflowKey == model.Id));
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
    public async Task CardinalityDefault_EnteringErrorEndEventFaultsAndClosesAllWork()
    {
        var model = LoadUniqueModel("votes-cardinality-approve-reject.json", "error-end-default");
        var workflowId = await CreateWorkflowAsync(model);
        var scenario = await StartAndEnterAsync(
            workflowId,
            new Dictionary<string, JsonElement>
            {
                ["voters"] = JsonSerializer.SerializeToElement(1)
            });
        var task = Assert.Single((await ListTasksAsync(scenario.Id, "solo", "User")).Items);

        using (var flows = await SendAsync(
                   HttpMethod.Get,
                   $"/api/user-tasks/{task.Id}/flows",
                   null,
                   "solo",
                   "User"))
        {
            Assert.Equal(HttpStatusCode.OK, flows.StatusCode);
            Assert.Equal(new[] { 201, 205 }, (await ReadAsync<List<SequenceFlowModel>>(flows))
                .Select(flow => flow.Id)
                .Order()
                .ToArray());
        }

        using (var hiddenDefault = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{task.Id}/flows/207",
                   new TakeFlowRequest(null),
                   "solo",
                   "User"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, hiddenDefault.StatusCode);
        }

        using var completed = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{task.Id}/flows/201",
            new TakeFlowRequest(null),
            "solo",
            "User");
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        var ack = await ReadAsync<UserTaskActionAckDto>(completed);
        Assert.Equal("faulted", ack.InstanceStatus);
        Assert.Equal(6, ack.CurrentNodeId);
        Assert.Equal(UserTaskStatuses.Completed, ack.TaskStatus);
        Assert.NotNull(ack.MultiInstance);
        Assert.Equal(MultiInstanceExecutionStatuses.Completed, ack.MultiInstance.Status);
        Assert.Equal(207, ack.MultiInstance.WinningFlowId);
        Assert.Equal("all", ack.MultiInstance.CompletionReason);
        Assert.Equal(1, Assert.Single(ack.MultiInstance.FlowCounts, count => count.FlowId == 201).Count);

        var detail = await GetInstanceAsync(scenario.Id);
        Assert.Equal("faulted", detail.Status);
        Assert.Equal(6, detail.CurrentNodeId);
        Assert.Null(detail.UserTasks);
        Assert.Single(detail.History, entry =>
            entry.Note == "multiInstanceComplete" && entry.SequenceFlowId == 207);
        Assert.Empty((await ListTasksAsync(scenario.Id, "solo", "User")).Items);

        await using var db = fixture.CreateDbContext();
        var token = await db.ExecutionTokens.SingleAsync(row => row.InstanceId == scenario.Id);
        Assert.Equal(BpmnFlowNodeTypes.ErrorEndEvent, token.NodeType);
        Assert.Equal(ExecutionTokenStatuses.Faulted, token.Status);
        var execution = await db.MultiInstanceExecutions.SingleAsync(row => row.InstanceId == scenario.Id);
        Assert.Equal(MultiInstanceExecutionStatuses.Completed, execution.Status);
        Assert.Equal(207, execution.WinningFlowId);
        Assert.Equal("all", execution.CompletionReason);
        Assert.NotNull(execution.CompletedAt);
        Assert.False(await db.UserTasks.AnyAsync(row =>
            row.InstanceId == scenario.Id
            && (row.Status == UserTaskStatuses.Active || row.Status == UserTaskStatuses.Pending)));
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
    public async Task OnePerActorConcurrentCompletionsAllowOnlyOneCaseInsensitiveActorCompletion()
    {
        var model = LoadUniqueModel("votes-cardinality-approve-reject.json", "one-per-actor-completion");
        var multi = model.FlowNodes.Single(node => node.Id == 2).MultiInstance!;
        Assert.True(multi.OnePerActor);

        var workflowId = await CreateWorkflowAsync(model);
        var scenario = await StartAndEnterAsync(
            workflowId,
            new Dictionary<string, JsonElement>
            {
                ["voters"] = JsonSerializer.SerializeToElement(3)
            });
        var executionId = scenario.MultiInstance!.ExecutionId;
        var tasks = (await ListTasksAsync(scenario.Id, "CaseUser", "User")).Items
            .OrderBy(task => task.ItemIndex)
            .ToList();
        Assert.Equal(3, tasks.Count);

        var completions = await Task.WhenAll(
                SendAsync(
                    HttpMethod.Post,
                    $"/api/user-tasks/{tasks[0].Id}/flows/201",
                    new TakeFlowRequest(null),
                    "CaseUser",
                    "User"),
                SendAsync(
                    HttpMethod.Post,
                    $"/api/user-tasks/{tasks[1].Id}/flows/201",
                    new TakeFlowRequest(null),
                    "caseuser",
                    "User"))
            .WaitAsync(TimeSpan.FromSeconds(10));
        using (completions[0])
        using (completions[1])
        {
            Assert.Equal(1, completions.Count(response => response.StatusCode == HttpStatusCode.OK));
            Assert.Equal(1, completions.Count(response => response.StatusCode == HttpStatusCode.Conflict));
        }

        await using var db = fixture.CreateDbContext();
        var execution = await db.MultiInstanceExecutions.AsNoTracking()
            .SingleAsync(item => item.Id == executionId);
        Assert.True(execution.OnePerActor);
        Assert.Equal(MultiInstanceExecutionStatuses.Active, execution.Status);
        Assert.Equal(1, execution.CompletedCount);
        Assert.Equal(1, await db.UserTasks.AsNoTracking().CountAsync(task =>
            task.MultiInstanceExecutionId == executionId
            && task.Status == UserTaskStatuses.Completed
            && task.CompletedBy != null
            && task.CompletedBy.ToLower() == "caseuser"));
        Assert.Equal(1, await db.MultiInstanceFlowCounts.AsNoTracking()
            .Where(count => count.ExecutionId == executionId && count.FlowId == 201)
            .Select(count => count.CompletedCount)
            .SingleAsync());
    }

    [Fact]
    public async Task OnePerActorConcurrentClaimsAllowOnlyOneClaimPerCaseInsensitiveActor()
    {
        var model = LoadUniqueModel("votes-cardinality-approve-reject.json", "one-per-actor-claim");
        var node = model.FlowNodes.Single(item => item.Id == 2);
        node.RequiresClaim = true;
        Assert.True(node.MultiInstance!.OnePerActor);

        var workflowId = await CreateWorkflowAsync(model);
        var scenario = await StartAndEnterAsync(
            workflowId,
            new Dictionary<string, JsonElement>
            {
                ["voters"] = JsonSerializer.SerializeToElement(2)
            });
        var executionId = scenario.MultiInstance!.ExecutionId;
        var tasks = (await ListTasksAsync(scenario.Id, "CaseUser", "User")).Items
            .OrderBy(task => task.ItemIndex)
            .ToList();
        Assert.Equal(2, tasks.Count);

        var claims = await Task.WhenAll(
                SendAsync(
                    HttpMethod.Post,
                    $"/api/user-tasks/{tasks[0].Id}/claim",
                    null,
                    "CaseUser",
                    "User"),
                SendAsync(
                    HttpMethod.Post,
                    $"/api/user-tasks/{tasks[1].Id}/claim",
                    null,
                    "caseuser",
                    "User"))
            .WaitAsync(TimeSpan.FromSeconds(10));
        using (claims[0])
        using (claims[1])
        {
            Assert.Equal(1, claims.Count(response => response.StatusCode == HttpStatusCode.OK));
            Assert.Equal(1, claims.Count(response => response.StatusCode == HttpStatusCode.Conflict));
        }

        await using var db = fixture.CreateDbContext();
        Assert.Equal(1, await db.UserTasks.AsNoTracking().CountAsync(task =>
            task.MultiInstanceExecutionId == executionId
            && task.Status == UserTaskStatuses.Active
            && task.ClaimedBy != null
            && task.ClaimedBy.ToLower() == "caseuser"));
        Assert.Equal(0, await db.UserTasks.AsNoTracking().CountAsync(task =>
            task.MultiInstanceExecutionId == executionId
            && task.Status == UserTaskStatuses.Completed));
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

    private async Task<StartInstanceResultDto> StartWorkflowAsync(long workflowId)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/instances",
            new StartInstanceRequest(workflowId, null, null, null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<StartInstanceResultDto>(response);
    }

    private async Task<PagedResult<InstanceSummaryDto>> ListWorkflowInstancesAsync(string workflowKey)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            "/api/instances?workflowKey=" + Uri.EscapeDataString(workflowKey) + "&pageSize=50");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<PagedResult<InstanceSummaryDto>>(response);
    }

    private async Task AssertCatchFailureLeftNoOutputsAsync(long instanceId)
    {
        var detail = await GetInstanceAsync(instanceId);
        Assert.Equal("running", detail.Status);
        Assert.Equal(2, detail.CurrentNodeId);
        var decisionRows = detail.Variables.Where(variable => variable.VariableName == "decision").ToList();
        Assert.Single(decisionRows);
        Assert.Equal("pending", decisionRows[0].Value.GetString());
        Assert.DoesNotContain(detail.Variables, variable =>
            variable.VariableName is "score" or "country" or "region" or "tags" or "businessDate" or "note"
                or "approved" or "receivedAt" or "metadata" or "ratings");
    }

    private Task<HttpResponseMessage> StartWithBusinessKeyAsync(long workflowId, string value) =>
        SendAsync(
            HttpMethod.Post,
            "/api/instances",
            new StartInstanceRequest(
                workflowId,
                null,
                null,
                new Dictionary<string, JsonElement>
                {
                    ["violationId"] = JsonSerializer.SerializeToElement(value)
                }));

    private async Task<HttpResponseMessage> StartWithBusinessAndIdempotencyAsync(
        long workflowId,
        string businessKey,
        string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/instances")
        {
            Content = JsonContent.Create(
                new StartInstanceRequest(
                    workflowId,
                    null,
                    null,
                    new Dictionary<string, JsonElement>
                    {
                        ["violationId"] = JsonSerializer.SerializeToElement(businessKey)
                    }),
                options: JsonOptions)
        };
        request.Headers.Add(IdempotencyHeaders.Standard, idempotencyKey);
        ApiTestAuth.Authorize(request, "test-admin", AdminRoles);
        return await fixture.Client.SendAsync(request);
    }

    private Task<HttpResponseMessage> StartWithIdempotencyAsync(
        long workflowId,
        string key,
        string headerName = IdempotencyHeaders.Standard) =>
        StartWithIdempotencyHeadersAsync(workflowId, (headerName, new[] { key }));

    private async Task<HttpResponseMessage> StartWithIdempotencyHeadersAsync(
        long workflowId,
        params (string Name, string[] Values)[] headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/instances")
        {
            Content = JsonContent.Create(
                new StartInstanceRequest(workflowId, null, null, null),
                options: JsonOptions)
        };
        foreach (var (name, values) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, values);
        }
        ApiTestAuth.Authorize(request, "test-admin", AdminRoles);
        return await fixture.Client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendMessageStartAsync(
        string workflowKey,
        string idempotencyKey,
        string businessKey)
    {
        return await SendMessageStartPayloadAsync(
            workflowKey,
            new { violationId = businessKey },
            idempotencyKey);
    }

    private async Task<HttpResponseMessage> SendMessageStartPayloadAsync(
        string workflowKey,
        object payload,
        string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/workflows/" + Uri.EscapeDataString(workflowKey) + "/message-start");
        request.Headers.Add("X-Client-Id", "tests-client");
        request.Headers.Add("X-Client-Secret", "tests-secret");
        request.Headers.Add("X-Correlation", "accepted");
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        return await fixture.Client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendCatchMessageAsync(long instanceId, string status)
        => await SendCatchPayloadAsync(instanceId, new { status });

    private async Task<HttpResponseMessage> SendCatchPayloadAsync(long instanceId, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/instances/{instanceId}/message");
        request.Headers.Add("X-Client-Id", "tests-client");
        request.Headers.Add("X-Client-Secret", "tests-secret");
        request.Headers.Add("X-Correlation", "accepted");
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        return await fixture.Client.SendAsync(request);
    }

    private static MessageOutputMappingModel TypedMapping(
        string variable,
        string path,
        string dataType,
        bool required = false,
        bool isArray = false,
        JsonElement? defaultValue = null,
        string? validation = null) => new()
    {
        Variable = variable,
        Path = path,
        DataType = dataType,
        IsArray = isArray,
        Required = required,
        DefaultValue = defaultValue,
        Validation = validation
    };

    private static ServiceOutputMappingModel ServiceTypedMapping(
        string variable,
        string path,
        string dataType,
        bool required = false,
        bool isArray = false,
        JsonElement? defaultValue = null,
        string? validation = null) => new()
    {
        Variable = variable,
        Path = path,
        DataType = dataType,
        IsArray = isArray,
        Required = required,
        DefaultValue = defaultValue,
        Validation = validation
    };

    private static WorkflowModel CreateTypedMessageCatchModel()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = "typed-message-catch-" + suffix,
            Name = "Typed message catch " + suffix,
            InitialEventId = 1,
            Variables =
            [
                new VariableModel
                {
                    Id = 1,
                    Name = "decision",
                    DataType = WorkflowVariableTypes.String,
                    DefaultValue = JsonSerializer.SerializeToElement("pending"),
                    Validation = "decision != 'blocked'"
                }
            ],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Message",
                    Type = BpmnFlowNodeTypes.IntermediateMessageCatchEvent,
                    Message = new MessageCatchModel
                    {
                        ClientId = "tests-client",
                        ClientSecret = "tests-secret",
                        HeaderName = "X-Correlation",
                        HeaderValue = "accepted",
                        OutputMappings =
                        [
                            TypedMapping("DECISION", "result.decision", WorkflowVariableTypes.String, required: true,
                                validation: "score > 0 and country == 'SA'"),
                            TypedMapping("score", "result.score", WorkflowVariableTypes.Number, required: true),
                            TypedMapping("country", "details.country", WorkflowVariableTypes.String,
                                defaultValue: JsonSerializer.SerializeToElement("SA")),
                            TypedMapping("region", string.Empty, WorkflowVariableTypes.String,
                                defaultValue: JsonSerializer.SerializeToElement("${country}-central"),
                                validation: "StartsWith(region, 'SA-')"),
                            TypedMapping("tags", "tags", WorkflowVariableTypes.String, required: true, isArray: true),
                            TypedMapping("businessDate", "businessDate", WorkflowVariableTypes.Date, required: true),
                            TypedMapping("approved", "approved", WorkflowVariableTypes.Boolean),
                            TypedMapping("receivedAt", "receivedAt", WorkflowVariableTypes.DateTime),
                            TypedMapping("metadata", "metadata", WorkflowVariableTypes.Json),
                            TypedMapping("ratings", "ratings", WorkflowVariableTypes.Number, isArray: true),
                            TypedMapping("note", "note", WorkflowVariableTypes.String)
                        ]
                    }
                },
                new FlowNodeModel { Id = 3, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 }
            ]
        };
    }

    private static WorkflowModel CreateTypedServiceModel(
        string label,
        string responseName,
        bool withBoundary)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var model = new WorkflowModel
        {
            Id = "typed-service-" + label + "-" + suffix,
            Name = "Typed service " + label + " " + suffix,
            InitialEventId = 1,
            Variables =
            [
                new VariableModel
                {
                    Id = 1,
                    Name = "decision",
                    DataType = WorkflowVariableTypes.String,
                    DefaultValue = JsonSerializer.SerializeToElement("pending"),
                    Validation = "decision != 'blocked'"
                }
            ],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Service",
                    Type = BpmnFlowNodeTypes.ServiceTask,
                    Service = new ServiceTaskModel
                    {
                        Url = "https://tests.local/" + responseName,
                        StatusVariable = "serviceStatus",
                        OutputMappings =
                        [
                            ServiceTypedMapping("DECISION", "result.decision", WorkflowVariableTypes.String, required: true,
                                validation: "score > 0 and country == 'SA'"),
                            ServiceTypedMapping("score", "result.score", WorkflowVariableTypes.Number, required: true),
                            ServiceTypedMapping("country", "details.country", WorkflowVariableTypes.String,
                                defaultValue: JsonSerializer.SerializeToElement("SA")),
                            ServiceTypedMapping("region", string.Empty, WorkflowVariableTypes.String,
                                defaultValue: JsonSerializer.SerializeToElement("${country}-central"),
                                validation: "StartsWith(region, 'SA-')"),
                            ServiceTypedMapping("tags", "tags", WorkflowVariableTypes.String, required: true, isArray: true),
                            ServiceTypedMapping("businessDate", "businessDate", WorkflowVariableTypes.Date, required: true),
                            ServiceTypedMapping("approved", "approved", WorkflowVariableTypes.Boolean),
                            ServiceTypedMapping("receivedAt", "receivedAt", WorkflowVariableTypes.DateTime),
                            ServiceTypedMapping("metadata", "metadata", WorkflowVariableTypes.Json),
                            ServiceTypedMapping("ratings", "ratings", WorkflowVariableTypes.Number, isArray: true)
                        ]
                    }
                },
                new FlowNodeModel { Id = 3, Name = "Normal end", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 }
            ]
        };

        if (withBoundary)
        {
            model.FlowNodes.Add(new FlowNodeModel
            {
                Id = 4,
                Name = "Service error",
                Type = BpmnFlowNodeTypes.ErrorBoundaryEvent,
                AttachedToRef = 2,
                ErrorVariable = "serviceError"
            });
            model.FlowNodes.Add(new FlowNodeModel
            {
                Id = 5,
                Name = "Handle error",
                Type = BpmnFlowNodeTypes.UserTask
            });
            model.FlowNodes.Add(new FlowNodeModel
            {
                Id = 6,
                Name = "Handled end",
                Type = BpmnFlowNodeTypes.EndEvent
            });
            model.SequenceFlows.Add(new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 5 });
            model.SequenceFlows.Add(new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 6 });
        }

        return model;
    }

    private static WorkflowModel CreateRawMessageCatchModel()
    {
        var suffix = Guid.NewGuid().ToString("N");
        MessageCatchModel Message() => new()
        {
            ClientId = "tests-client",
            ClientSecret = "tests-secret",
            HeaderName = "X-Correlation",
            HeaderValue = "accepted",
            OutputMappings =
            [
                new MessageOutputMappingModel
                {
                    Variable = "externalStatus",
                    Path = "status",
                    Required = true
                }
            ]
        };
        return new WorkflowModel
        {
            Id = "message-catch-raw-" + suffix,
            Name = "message-catch-raw-" + suffix,
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "First message", Type = BpmnFlowNodeTypes.IntermediateMessageCatchEvent, Message = Message() },
                new FlowNodeModel { Id = 3, Name = "Second message", Type = BpmnFlowNodeTypes.IntermediateMessageCatchEvent, Message = Message() },
                new FlowNodeModel { Id = 4, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4 }
            ]
        };
    }

    private static void ConfigureBusinessKey(WorkflowModel model, string uniqueness)
    {
        var start = model.FlowNodes.Single(node => node.Id == model.InitialEventId);
        start.Variables.Add(new VariableModel
        {
            Id = 90,
            Name = "violationId",
            DataType = WorkflowVariableTypes.String,
            Required = true
        });
        start.BusinessKey = new BusinessKeyModel
        {
            Variable = "violationId",
            Uniqueness = uniqueness
        };
    }

    private static WorkflowModel CreateImmediateBusinessKeyModel(string label, string endType)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var start = new FlowNodeModel
        {
            Id = 1,
            Name = "Start",
            Type = BpmnFlowNodeTypes.StartEvent,
            Variables =
            [
                new VariableModel
                {
                    Id = 90,
                    Name = "violationId",
                    DataType = WorkflowVariableTypes.String,
                    Required = true
                }
            ],
            BusinessKey = new BusinessKeyModel
            {
                Variable = "violationId",
                Uniqueness = BusinessKeyUniqueness.Active
            }
        };
        return new WorkflowModel
        {
            Id = "tests-" + label + "-" + suffix,
            Name = "Tests " + label + " " + suffix,
            InitialEventId = 1,
            FlowNodes =
            [
                start,
                new FlowNodeModel { Id = 2, Name = "End", Type = endType }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel
                {
                    Id = 101,
                    Name = "Finish",
                    SourceRef = 1,
                    TargetRef = 2
                }
            ]
        };
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

    private static async Task AssertMessageStartConflictAsync(
        HttpResponseMessage response,
        string expectedCode,
        long expectedInstanceId)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal($"/api/instances/{expectedInstanceId}", response.Headers.Location?.OriginalString);
        var conflict = await ReadAsync<StartConflictDto>(response);
        Assert.Equal(expectedCode, conflict.Code);
        Assert.Equal(expectedInstanceId, conflict.InstanceId);
    }

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
