using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class AdministrativeActionApiTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task RoleProtectedReturn_RemainsANormalManualFlow_AndBatchDiscoveryRequiresStackedRoles()
    {
        var model = CreateAdministrativeReturnModel();
        EnableAdministrativeFlowEvidence(model);
        var workflowId = await CreateWorkflowAsync(model);
        var instance = await StartAsync(workflowId);
        var review = await GetSingleActiveTaskAsync(
            instance.Id,
            "reviewer",
            "Worker");

        using (var claim = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{review.Id}/claim",
                   user: "reviewer",
                   roles: ["Worker"]))
        {
            Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        }
        using (var advance = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{review.Id}/flows/201",
                   new TakeFlowRequest(null),
                   "reviewer",
                   ["Worker"]))
        {
            Assert.Equal(HttpStatusCode.OK, advance.StatusCode);
        }

        var approval = await GetSingleActiveTaskAsync(
            instance.Id,
            "approval-owner",
            "Approver");
        Assert.Equal("approval-owner", approval.Assignee);

        using (var catalog = await SendAsync(
                   HttpMethod.Get,
                   "/api/administrative-actions/workflows",
                   user: "operator",
                   roles: ["admin", "Ops"]))
        {
            Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
            Assert.Contains(
                await ReadAsync<List<WorkflowSummaryDto>>(catalog),
                version => version.Id == workflowId);
        }
        using (var catalogWithoutFlowRole = await SendAsync(
                   HttpMethod.Get,
                   "/api/administrative-actions/workflows",
                   user: "operator",
                   roles: ["admin"]))
        {
            Assert.Equal(HttpStatusCode.OK, catalogWithoutFlowRole.StatusCode);
            Assert.DoesNotContain(
                await ReadAsync<List<WorkflowSummaryDto>>(catalogWithoutFlowRole),
                version => version.Id == workflowId);
        }
        using (var forbiddenCatalog = await SendAsync(
                   HttpMethod.Get,
                   "/api/administrative-actions/workflows",
                   user: "operator",
                   roles: ["Ops"],
                   suppressImplicitAdmin: true))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenCatalog.StatusCode);
        }

        using (var normalList = await SendAsync(
                   HttpMethod.Get,
                   $"/api/user-tasks/{approval.Id}/flows",
                   user: "operator",
                   roles: ["admin", "Ops"]))
        {
            Assert.Equal(HttpStatusCode.OK, normalList.StatusCode);
            Assert.Empty(await ReadAsync<List<SequenceFlowModel>>(normalList));
        }
        using (var ownerList = await SendAsync(
                   HttpMethod.Get,
                   $"/api/user-tasks/{approval.Id}/flows",
                   user: "approval-owner",
                   roles: ["Approver", "Ops"]))
        {
            Assert.Equal(HttpStatusCode.OK, ownerList.StatusCode);
            var flows = await ReadAsync<List<SequenceFlowModel>>(ownerList);
            Assert.Contains(flows, flow => flow.Id == 301);
            Assert.Contains(flows, flow => flow.Id == 302);
        }
        using (var normalTake = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{approval.Id}/flows/301",
                   new TakeFlowRequest(null),
                   "operator",
                   ["admin", "Ops"]))
        {
            Assert.Equal(HttpStatusCode.BadRequest, normalTake.StatusCode);
        }

        using (var definitionActions = await SendAsync(
                   HttpMethod.Get,
                   $"/api/workflows/{workflowId}/administrative-actions",
                   user: "operator",
                   roles: ["admin", "Ops"]))
        {
            Assert.Equal(HttpStatusCode.OK, definitionActions.StatusCode);
            var action = Assert.Single(
                await ReadAsync<List<AdministrativeActionSummaryDto>>(
                    definitionActions));
            Assert.Equal(workflowId, action.WorkflowDefinitionId);
            Assert.Equal(301, action.FlowId);
            Assert.Null(action.FlowExternalId);
        }

        using (var manualReturn = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{approval.Id}/flows/301",
                   new TakeFlowRequest(new Dictionary<string, JsonElement>
                   {
                       ["comment"] = JsonSerializer.SerializeToElement(
                           "Missing supporting document")
                   }),
                   "approval-owner",
                   ["Approver", "Ops"]))
        {
            Assert.Equal(HttpStatusCode.OK, manualReturn.StatusCode);
        }

        var returned = await GetSingleActiveTaskAsync(
            instance.Id,
            "approval-owner",
            "Worker");
        Assert.Equal(2, returned.NodeId);
        Assert.Equal("approval-owner", returned.ClaimedBy);

        var completed = await GetTaskAsync(
            approval.Id,
            "approval-owner",
            "Approver");
        Assert.Equal("approval-owner", completed.CompletedBy);
        Assert.Null(completed.CompletionKind);
        Assert.Null(completed.CompletionReason);
        Assert.Null(completed.AdministrativeActionBatchId);

        var detail = await GetInstanceAsync(instance.Id);
        Assert.Equal(workflowId, detail.Workflow.Id);
        Assert.Empty(detail.VersionChanges);
        var history = Assert.Single(detail.History, item =>
            item.UserTaskId == approval.Id
            && item.SequenceFlowId == 301);
        Assert.Null(history.Note);
        Assert.Null(history.Reason);
        Assert.Equal("approval-owner", history.PerformedBy);
        Assert.Null(history.AdministrativeActionBatchId);
        Assert.Equal(
            "Missing supporting document",
            history.Payload!["comment"].GetString());

        await using (var db = fixture.CreateDbContext())
        {
            var occurrence = await db.SequenceFlowOccurrences
                .AsNoTracking()
                .SingleAsync(item => item.InstanceId == instance.Id
                                     && item.SequenceFlowId == 301);
            Assert.True(occurrence.IsAction);
            Assert.True(occurrence.IsTraversal);
            Assert.Equal("userTaskAction", occurrence.Kind);
            Assert.Equal("approval-owner", occurrence.User);
            Assert.Contains("Ops", occurrence.UserRoles);

            var evidence = await db.SequenceFlowSummaries
                .AsNoTracking()
                .SingleAsync(item => item.InstanceId == instance.Id
                                     && item.SequenceFlowId == 301);
            Assert.Equal(1, evidence.ActionCount);
            Assert.Equal(1, evidence.TraversalCount);
            Assert.Equal("userTaskAction", evidence.LastActionKind);
            Assert.Equal("userTaskAction", evidence.LastTraversalKind);
        }
    }

    [Fact]
    public async Task PreviewAdministrativeBatchFlow_LoadsSettingsChecksAuthorizationAndDoesNotMutateInstance()
    {
        var settingNamespace = $"administrativeaction{Guid.NewGuid():N}";
        await CreateWorkflowSettingAsync(settingNamespace, "enabled", true);
        await CreateWorkflowSettingAsync(settingNamespace, "minimumCommentLength", 12);

        var model = CreateAdministrativeReturnModel();
        var returnFlow = model.SequenceFlows.Single(flow => flow.Id == 301);
        returnFlow.Condition = $"[setting.{settingNamespace}.enabled] == true";
        returnFlow.Variables.Single(variable => variable.Name == "comment").Validation =
            $"Length(comment) >= [setting.{settingNamespace}.minimumCommentLength]";

        var workflowId = await CreateWorkflowAsync(model);
        var instance = await StartAsync(workflowId);
        var approval = await MoveToApprovalAsync(instance.Id, "settings-reviewer");
        var selectedState = await GetInstanceAsync(instance.Id);
        var request = new AdministrativeActionRequest(
            workflowId,
            301,
            selectedState.UpdatedAt,
            "Settings-backed correction",
            new Dictionary<string, JsonElement>
            {
                ["comment"] = JsonSerializer.SerializeToElement("short")
            })
        {
            ExpectedTokenId = approval.TokenId,
            ExpectedUserTaskUpdatedAt = approval.UpdatedAt
        };
        var operatorContext = new ActorContext(
            "settings-operator",
            ["admin", "Ops"],
            new Dictionary<string, string>());

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<IWorkflowEngineService>();
        var invalid = await engine.PreviewAdministrativeBatchFlowAsync(
            approval.Id,
            request,
            operatorContext,
            CancellationToken.None);
        Assert.False(invalid.Eligible);
        Assert.Contains(invalid.Issues, issue => issue.Code == "invalidVariables");
        Assert.DoesNotContain(
            invalid.Issues,
            issue => issue.Code == "conditionNotSatisfied");

        var stale = await engine.PreviewAdministrativeBatchFlowAsync(
            approval.Id,
            request with { ExpectedTokenId = approval.TokenId + 10_000 },
            operatorContext,
            CancellationToken.None);
        Assert.False(stale.Eligible);
        Assert.Contains(stale.Issues, issue => issue.Code == "tokenChanged");

        var missingFlowRole = await engine.PreviewAdministrativeBatchFlowAsync(
            approval.Id,
            request,
            operatorContext with { Roles = ["admin"] },
            CancellationToken.None);
        Assert.False(missingFlowRole.Eligible);
        Assert.Contains(missingFlowRole.Issues, issue => issue.Code == "flowRoleRequired");

        await Assert.ThrowsAsync<WorkflowForbiddenException>(() =>
            engine.PreviewAdministrativeBatchFlowAsync(
                approval.Id,
                request,
                operatorContext with { Roles = ["Ops"] },
                CancellationToken.None));

        var valid = await engine.PreviewAdministrativeBatchFlowAsync(
            approval.Id,
            request with
            {
                Variables = new Dictionary<string, JsonElement>
                {
                    ["comment"] = JsonSerializer.SerializeToElement(
                        "Long enough correction comment")
                }
            },
            operatorContext,
            CancellationToken.None);
        Assert.True(valid.Eligible);
        Assert.Empty(valid.Issues);

        var afterPreview = await GetInstanceAsync(instance.Id);
        Assert.Equal(workflowId, afterPreview.Workflow.Id);
        Assert.Equal(selectedState.UpdatedAt, afterPreview.UpdatedAt);
        Assert.Empty(afterPreview.VersionChanges);
    }

    [Fact]
    public async Task ExactFlowMappings_RejectInvalidMappingSetsBeforeCandidateSearch()
    {
        var firstModel = CreateAdministrativeReturnModel();
        var firstWorkflowId = await CreateWorkflowAsync(firstModel);

        var incompatibleModel = CreateAdministrativeReturnModel();
        incompatibleModel.Id = firstModel.Id;
        incompatibleModel.Name = firstModel.Name;
        var incompatibleFlow = incompatibleModel.SequenceFlows.Single(flow => flow.Id == 301);
        incompatibleFlow.Id = 901;
        incompatibleFlow.Variables.Single(variable => variable.Name == "comment").Required = false;
        var incompatibleVersion = await CreateVersionAsync(
            firstWorkflowId,
            incompatibleModel);

        var unrelatedWorkflowId = await CreateWorkflowAsync(
            CreateAdministrativeReturnModel());
        var cases = new[]
        {
            new
            {
                Name = "duplicate definition mapping",
                Mappings = new AdministrativeActionFlowMappingDto[]
                {
                    new(firstWorkflowId, 301),
                    new(firstWorkflowId, 301)
                },
                Roles = new[] { "admin", "Ops" },
                ExpectedStatus = HttpStatusCode.BadRequest,
                ExpectedError = "more than one selected flow mapping"
            },
            new
            {
                Name = "missing flow",
                Mappings = new AdministrativeActionFlowMappingDto[]
                {
                    new(firstWorkflowId, 999_999)
                },
                Roles = new[] { "admin", "Ops" },
                ExpectedStatus = HttpStatusCode.BadRequest,
                ExpectedError = "not eligible for administrative batch execution"
            },
            new
            {
                Name = "existing but ineligible flow",
                Mappings = new AdministrativeActionFlowMappingDto[]
                {
                    new(firstWorkflowId, 302)
                },
                Roles = new[] { "admin", "Ops" },
                ExpectedStatus = HttpStatusCode.BadRequest,
                ExpectedError = "not eligible for administrative batch execution"
            },
            new
            {
                Name = "different workflow families",
                Mappings = new AdministrativeActionFlowMappingDto[]
                {
                    new(firstWorkflowId, 301),
                    new(unrelatedWorkflowId, 301)
                },
                Roles = new[] { "admin", "Ops" },
                ExpectedStatus = HttpStatusCode.BadRequest,
                ExpectedError = "same workflow family"
            },
            new
            {
                Name = "incompatible variable contracts",
                Mappings = new AdministrativeActionFlowMappingDto[]
                {
                    new(firstWorkflowId, 301),
                    new(incompatibleVersion.Id, 901)
                },
                Roles = new[] { "admin", "Ops" },
                ExpectedStatus = HttpStatusCode.BadRequest,
                ExpectedError = "same variable names, types, array flags, and required flags"
            },
            new
            {
                Name = "missing mapped flow role",
                Mappings = new AdministrativeActionFlowMappingDto[]
                {
                    new(firstWorkflowId, 301)
                },
                Roles = new[] { "admin" },
                ExpectedStatus = HttpStatusCode.Forbidden,
                ExpectedError = "does not have a role permitted for flow"
            }
        };

        foreach (var testCase in cases)
        {
            using var response = await SendAsync(
                HttpMethod.Post,
                "/api/administrative-actions/candidates/search",
                new AdministrativeActionCandidateSearchRequest
                {
                    FlowMappings = testCase.Mappings,
                    Page = 1,
                    PageSize = 10
                },
                $"invalid-mapping-{Guid.NewGuid():N}",
                testCase.Roles);

            Assert.True(
                response.StatusCode == testCase.ExpectedStatus,
                $"{testCase.Name}: expected {(int)testCase.ExpectedStatus} but received "
                + $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            Assert.Contains(
                testCase.ExpectedError,
                await response.Content.ReadAsStringAsync(),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task BatchCreate_RejectsCaseVariantCommonVariableDuplicatesAsBadRequest()
    {
        var workflowId = await CreateWorkflowAsync(CreateAdministrativeReturnModel());
        var variables = new Dictionary<string, JsonElement>
        {
            ["comment"] = JsonSerializer.SerializeToElement("first"),
            ["Comment"] = JsonSerializer.SerializeToElement("second")
        };
        var request = new CreateAdministrativeActionBatchRequest(
            FlowMappings(workflowId),
            "Correct duplicated variable input",
            variables,
            new AdministrativeActionBatchSelectionDto(
                AdministrativeActionBatchSelectionModes.Explicit,
                [long.MaxValue],
                null,
                null),
            null);

        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/administrative-action-batches",
            request,
            "operator",
            ["admin", "Ops"]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task BatchCreate_IdempotencyKeySerializesConcurrentRequestsAndRejectsDifferentReplay()
    {
        var workflowId = await CreateWorkflowAsync(CreateAdministrativeReturnModel());
        var instance = await StartAsync(workflowId);
        var approval = await MoveToApprovalAsync(instance.Id, "idempotency-reviewer");
        var idempotencyKey = $"batch-create-{Guid.NewGuid():N}";
        var request = new CreateAdministrativeActionBatchRequest(
            FlowMappings(workflowId),
            "Concurrent retry",
            new Dictionary<string, JsonElement>
            {
                ["comment"] = JsonSerializer.SerializeToElement("Same frozen request")
            },
            new AdministrativeActionBatchSelectionDto(
                AdministrativeActionBatchSelectionModes.Explicit,
                [approval.Id],
                null,
                null),
            idempotencyKey);

        var responseTasks = Enumerable.Range(0, 2)
            .Select(_ => SendAsync(
                HttpMethod.Post,
                "/api/administrative-action-batches",
                request,
                "idempotency-operator",
                ["admin", "Ops"]))
            .ToArray();
        var responses = await Task.WhenAll(responseTasks);
        try
        {
            Assert.All(responses, response =>
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
            var batches = new List<AdministrativeActionBatchDetailDto>();
            foreach (var response in responses)
            {
                batches.Add(await ReadAsync<AdministrativeActionBatchDetailDto>(response));
            }
            Assert.Single(batches.Select(batch => batch.Summary.Id).Distinct());
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(
                1,
                await db.AdministrativeActionBatches.CountAsync(batch =>
                    batch.PreparedBy == "idempotency-operator"
                    && batch.IdempotencyKey == idempotencyKey));
        }

        using var conflictingReplay = await SendAsync(
            HttpMethod.Post,
            "/api/administrative-action-batches",
            request with { Reason = "Different request" },
            "idempotency-operator",
            ["admin", "Ops"]);
        Assert.Equal(HttpStatusCode.Conflict, conflictingReplay.StatusCode);
    }

    [Fact]
    public async Task AllMatchingSelectionIsFrozenAtCreationAndConfiguredCapIsEnforced()
    {
        var workflowId = await CreateWorkflowAsync(CreateAdministrativeReturnModel());
        var first = await MoveToApprovalAsync(
            (await StartAsync(workflowId)).Id,
            "frozen-one");
        var second = await MoveToApprovalAsync(
            (await StartAsync(workflowId)).Id,
            "frozen-two");
        var filter = new AdministrativeActionCandidateSearchRequest
        {
            FlowMappings = FlowMappings(workflowId),
            Page = 1,
            PageSize = 1
        };
        var selection = new AdministrativeActionBatchSelectionDto(
            AdministrativeActionBatchSelectionModes.AllMatching,
            null,
            filter,
            null);
        AdministrativeActionBatchDetailDto frozenBatch;
        using (var create = await SendAsync(
                   HttpMethod.Post,
                   "/api/administrative-action-batches",
                   new CreateAdministrativeActionBatchRequest(
                       FlowMappings(workflowId),
                       "Freeze the current matching population",
                       new Dictionary<string, JsonElement>
                       {
                           ["comment"] = JsonSerializer.SerializeToElement("Frozen selection")
                       },
                       selection,
                       $"all-matching-{Guid.NewGuid():N}"),
                   "all-matching-operator",
                   ["admin", "Ops"]))
        {
            Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
            frozenBatch = await ReadAsync<AdministrativeActionBatchDetailDto>(create);
        }

        var later = await MoveToApprovalAsync(
            (await StartAsync(workflowId)).Id,
            "not-in-frozen-set");
        using (var itemsResponse = await SendAsync(
                   HttpMethod.Get,
                   $"/api/administrative-action-batches/{frozenBatch.Summary.Id}/items?page=1&pageSize=50",
                   user: "all-matching-operator",
                   roles: ["admin", "Ops"]))
        {
            Assert.Equal(HttpStatusCode.OK, itemsResponse.StatusCode);
            var items = await ReadAsync<PagedResult<AdministrativeActionBatchItemDto>>(
                itemsResponse);
            Assert.Equal(2, items.TotalCount);
            Assert.Contains(items.Items, item => item.UserTaskId == first.Id);
            Assert.Contains(items.Items, item => item.UserTaskId == second.Id);
            Assert.DoesNotContain(items.Items, item => item.UserTaskId == later.Id);
        }

        using (var cancel = await SendAsync(
                   HttpMethod.Post,
                   $"/api/administrative-action-batches/{frozenBatch.Summary.Id}/cancel",
                   new CancelAdministrativeActionBatchRequest("Test cleanup"),
                   "all-matching-operator",
                   ["admin", "Ops"]))
        {
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        }

        string originalMaxItems;
        await using (var db = fixture.CreateDbContext())
        {
            var setting = await db.EngineSettings.SingleAsync(candidate =>
                candidate.Namespace == "WorkflowBatchActions"
                && candidate.Key == "MaxItems");
            originalMaxItems = setting.Value;
            setting.Value = "1";
            setting.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        try
        {
            using var overCap = await SendAsync(
                HttpMethod.Post,
                "/api/administrative-action-batches",
                new CreateAdministrativeActionBatchRequest(
                    FlowMappings(workflowId),
                    "Reject this oversized frozen population",
                    new Dictionary<string, JsonElement>
                    {
                        ["comment"] = JsonSerializer.SerializeToElement("Over configured cap")
                    },
                    selection,
                    $"over-cap-{Guid.NewGuid():N}"),
                "all-matching-operator",
                ["admin", "Ops"]);
            Assert.Equal(HttpStatusCode.BadRequest, overCap.StatusCode);
        }
        finally
        {
            await using var db = fixture.CreateDbContext();
            var setting = await db.EngineSettings.SingleAsync(candidate =>
                candidate.Namespace == "WorkflowBatchActions"
                && candidate.Key == "MaxItems");
            setting.Value = originalMaxItems;
            setting.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Batch_FreezesPreparesConfirmsAndExecutesItemsIndependently()
    {
        var model = CreateAdministrativeReturnModel();
        EnableAdministrativeFlowEvidence(model);
        var approvalNode = model.FlowNodes.Single(node => node.Id == 3);
        approvalNode.AssigneeExpression = null;
        approvalNode.RequiresAssignment = false;
        approvalNode.AssignmentMode = AssignmentModes.Fresh;
        approvalNode.RequiresClaim = true;
        approvalNode.ClaimMode = ClaimModes.Fresh;
        var workflowId = await CreateWorkflowAsync(model);
        var firstInstance = await StartAsync(workflowId);
        var secondInstance = await StartAsync(workflowId);
        var firstApproval = await MoveToApprovalAsync(firstInstance.Id, "reviewer-one");
        var secondApproval = await MoveToApprovalAsync(secondInstance.Id, "reviewer-two");
        foreach (var approval in new[] { firstApproval, secondApproval })
        {
            using var claim = await SendAsync(
                HttpMethod.Post,
                $"/api/user-tasks/{approval.Id}/claim",
                user: "approval-owner",
                roles: ["Approver"]);
            Assert.True(
                claim.StatusCode == HttpStatusCode.OK,
                $"Expected the source task claim to succeed, but received {(int)claim.StatusCode}: "
                + await claim.Content.ReadAsStringAsync());
        }

        var search = new AdministrativeActionCandidateSearchRequest
        {
            FlowMappings = FlowMappings(workflowId),
            IncludeVariables = true,
            Page = 1,
            PageSize = 50
        };
        using (var searchResponse = await SendAsync(
                   HttpMethod.Post,
                   "/api/administrative-actions/candidates/search",
                   search,
                   "batch-operator",
                   ["admin", "Ops"]))
        {
            Assert.Equal(HttpStatusCode.OK, searchResponse.StatusCode);
            var candidates = await ReadAsync<PagedResult<AdministrativeActionCandidateDto>>(
                searchResponse);
            Assert.Contains(candidates.Items, item => item.UserTaskId == firstApproval.Id && item.Eligible);
            Assert.Contains(candidates.Items, item => item.UserTaskId == secondApproval.Id && item.Eligible);
        }

        AdministrativeActionBatchDetailDto batch;
        using (var create = await SendAsync(
                   HttpMethod.Post,
                   "/api/administrative-action-batches",
                   new CreateAdministrativeActionBatchRequest(
                       FlowMappings(workflowId),
                       "Correct two requests",
                       new Dictionary<string, JsonElement>
                       {
                           ["Comment"] = JsonSerializer.SerializeToElement("Batch correction")
                       },
                       new AdministrativeActionBatchSelectionDto(
                           AdministrativeActionBatchSelectionModes.Explicit,
                           [firstApproval.Id, secondApproval.Id],
                           null,
                           null),
                       $"batch-{Guid.NewGuid():N}"),
                   "batch-operator",
                   ["admin", "Ops"]))
        {
            Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
            batch = await ReadAsync<AdministrativeActionBatchDetailDto>(create);
        }
        Assert.Equal(AdministrativeActionBatchStatuses.Preparing, batch.Summary.Status);
        Assert.Equal(2, batch.Summary.TotalItemCount);
        await ProcessBatchJobAsync(Assert.IsType<long>(batch.PreparationJobId));

        batch = await GetBatchAsync(batch.Summary.Id);
        Assert.Equal(AdministrativeActionBatchStatuses.Ready, batch.Summary.Status);
        Assert.Equal(2, batch.Summary.EligibleItemCount);
        Assert.Equal(0, batch.Summary.IneligibleItemCount);
        var confirmation = new ConfirmAdministrativeActionBatchRequest(
            batch.Summary.EligibleItemCount,
            batch.Summary.UpdatedAt);

        using (var confirm = await SendAsync(
                   HttpMethod.Post,
                   $"/api/administrative-action-batches/{batch.Summary.Id}/confirm",
                   confirmation,
                   "batch-confirmer",
                   ["admin", "Ops"]))
        {
            Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
            batch = await ReadAsync<AdministrativeActionBatchDetailDto>(confirm);
        }
        Assert.Equal(AdministrativeActionBatchStatuses.Queued, batch.Summary.Status);
        Assert.Equal(0, batch.Summary.EligibleItemCount);
        Assert.Equal(2, batch.Summary.QueuedItemCount);
        var executionJobId = Assert.IsType<long>(batch.ExecutionJobId);
        using (var replay = await SendAsync(
                   HttpMethod.Post,
                   $"/api/administrative-action-batches/{batch.Summary.Id}/confirm",
                   confirmation,
                   "batch-confirmer",
                   ["admin", "Ops"]))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            var replayed = await ReadAsync<AdministrativeActionBatchDetailDto>(replay);
            Assert.Equal(executionJobId, replayed.ExecutionJobId);
            Assert.Equal(AdministrativeActionBatchStatuses.Queued, replayed.Summary.Status);
        }
        await ProcessBatchJobAsync(executionJobId);

        batch = await GetBatchAsync(batch.Summary.Id);
        Assert.Equal(AdministrativeActionBatchStatuses.Completed, batch.Summary.Status);
        Assert.Equal(2, batch.Summary.SucceededItemCount);
        Assert.Equal(0, batch.Summary.SkippedItemCount);
        using (var itemsResponse = await SendAsync(
                   HttpMethod.Get,
                   $"/api/administrative-action-batches/{batch.Summary.Id}/items?page=1&pageSize=20",
                   user: "batch-confirmer",
                   roles: ["admin", "Ops"]))
        {
            Assert.Equal(HttpStatusCode.OK, itemsResponse.StatusCode);
            var items = await ReadAsync<PagedResult<AdministrativeActionBatchItemDto>>(itemsResponse);
            Assert.Equal(2, items.TotalCount);
            Assert.All(items.Items, item =>
            {
                Assert.Equal(AdministrativeActionBatchItemStatuses.Succeeded, item.Status);
                Assert.NotNull(item.NewUserTaskId);
                Assert.NotNull(item.Result);
            });
        }

        var firstReturned = await GetSingleActiveTaskAsync(
            firstInstance.Id,
            "reviewer-one",
            "Worker");
        var secondReturned = await GetSingleActiveTaskAsync(
            secondInstance.Id,
            "reviewer-two",
            "Worker");
        Assert.Equal(2, firstReturned.NodeId);
        Assert.Equal("reviewer-one", firstReturned.ClaimedBy);
        Assert.Equal(2, secondReturned.NodeId);
        Assert.Equal("reviewer-two", secondReturned.ClaimedBy);
        Assert.NotEqual("batch-confirmer", firstReturned.ClaimedBy);
        Assert.NotEqual("batch-confirmer", secondReturned.ClaimedBy);

        await using var db = fixture.CreateDbContext();
        var instanceIds = new[] { firstInstance.Id, secondInstance.Id };
        var occurrences = await db.SequenceFlowOccurrences
            .AsNoTracking()
            .Where(item => instanceIds.Contains(item.InstanceId)
                           && item.SequenceFlowId == 301)
            .OrderBy(item => item.InstanceId)
            .ToListAsync();
        Assert.Equal(2, occurrences.Count);
        Assert.All(occurrences, occurrence =>
        {
            Assert.Equal(workflowId, occurrence.WorkflowDefinitionId);
            Assert.True(occurrence.IsAction);
            Assert.True(occurrence.IsTraversal);
            Assert.Equal("administrativeAction", occurrence.Kind);
            Assert.Equal("batch-confirmer", occurrence.User);
            Assert.Contains("admin", occurrence.UserRoles);
            Assert.Contains("Ops", occurrence.UserRoles);
        });

        var summaries = await db.SequenceFlowSummaries
            .AsNoTracking()
            .Where(item => instanceIds.Contains(item.InstanceId)
                           && item.SequenceFlowId == 301)
            .OrderBy(item => item.InstanceId)
            .ToListAsync();
        Assert.Equal(2, summaries.Count);
        Assert.All(summaries, summary =>
        {
            Assert.Equal(1, summary.ActionCount);
            Assert.Equal(1, summary.TraversalCount);
            Assert.Equal("administrativeAction", summary.LastActionKind);
            Assert.Equal("administrativeAction", summary.LastTraversalKind);
            Assert.Equal("batch-confirmer", summary.LastActionUser);
            Assert.Equal("batch-confirmer", summary.LastTraversalUser);
            Assert.Contains("admin", summary.LastActionUserRoles);
            Assert.Contains("Ops", summary.LastActionUserRoles);
            Assert.Contains("admin", summary.LastTraversalUserRoles);
            Assert.Contains("Ops", summary.LastTraversalUserRoles);
        });
    }

    [Fact]
    public async Task ConcurrentNormalActionAndBatchExecutionCommitExactlyOneTaskTransition()
    {
        var workflowId = await CreateWorkflowAsync(CreateAdministrativeReturnModel());
        var instance = await StartAsync(workflowId);
        var approval = await MoveToApprovalAsync(instance.Id, "race-reviewer");

        AdministrativeActionBatchDetailDto batch;
        using (var create = await SendAsync(
                   HttpMethod.Post,
                   "/api/administrative-action-batches",
                   new CreateAdministrativeActionBatchRequest(
                       FlowMappings(workflowId),
                       "Race with the ordinary approval",
                       new Dictionary<string, JsonElement>
                       {
                           ["comment"] = JsonSerializer.SerializeToElement("Concurrent action")
                       },
                       new AdministrativeActionBatchSelectionDto(
                           AdministrativeActionBatchSelectionModes.Explicit,
                           [approval.Id],
                           null,
                           null),
                       $"action-race-{Guid.NewGuid():N}"),
                   "race-preparer",
                   ["admin", "Ops"]))
        {
            Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
            batch = await ReadAsync<AdministrativeActionBatchDetailDto>(create);
        }
        await ProcessBatchJobAsync(Assert.IsType<long>(batch.PreparationJobId));
        batch = await GetBatchAsync(batch.Summary.Id);
        using (var confirm = await SendAsync(
                   HttpMethod.Post,
                   $"/api/administrative-action-batches/{batch.Summary.Id}/confirm",
                   new ConfirmAdministrativeActionBatchRequest(
                       batch.Summary.EligibleItemCount,
                       batch.Summary.UpdatedAt),
                   "race-confirmer",
                   ["admin", "Ops"]))
        {
            Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
            batch = await ReadAsync<AdministrativeActionBatchDetailDto>(confirm);
        }

        var execution = ProcessBatchJobAsync(Assert.IsType<long>(batch.ExecutionJobId));
        var ordinaryAction = SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{approval.Id}/flows/302",
            new TakeFlowRequest(null),
            "approval-owner",
            ["Approver"]);
        await Task.WhenAll(execution, ordinaryAction);
        using var ordinaryResponse = await ordinaryAction;
        Assert.Contains(
            ordinaryResponse.StatusCode,
            new[]
            {
                HttpStatusCode.OK,
                HttpStatusCode.BadRequest,
                HttpStatusCode.Conflict,
                HttpStatusCode.NotFound
            });

        batch = await GetBatchAsync(batch.Summary.Id);
        Assert.Contains(
            batch.Summary.Status,
            new[]
            {
                AdministrativeActionBatchStatuses.Completed,
                AdministrativeActionBatchStatuses.CompletedWithIssues
            });
        Assert.Equal(1, batch.Summary.SucceededItemCount + batch.Summary.SkippedItemCount);

        await using var db = fixture.CreateDbContext();
        var sourceTransitions = await db.InstanceHistory
            .AsNoTracking()
            .Where(item => item.InstanceId == instance.Id
                           && item.UserTaskId == approval.Id
                           && (item.ActionId == 301 || item.ActionId == 302))
            .ToArrayAsync();
        Assert.Single(sourceTransitions);
        var executionRecord = await db.NodeExecutions
            .AsNoTracking()
            .SingleAsync(item => item.UserTaskId == approval.Id);
        Assert.Equal(NodeExecutionStatuses.Completed, executionRecord.Status);
        Assert.Contains(
            executionRecord.CompletionReason,
            new[] { "userAction", "administrativeAction" });
    }

    [Fact]
    public async Task ExecutionJobResumesAfterCommittedItemTransitionWithoutRepeatingIt()
    {
        var workflowId = await CreateWorkflowAsync(CreateAdministrativeReturnModel());
        var instance = await StartAsync(workflowId);
        var approval = await MoveToApprovalAsync(instance.Id, "resume-reviewer");

        AdministrativeActionBatchDetailDto batch;
        using (var create = await SendAsync(
                   HttpMethod.Post,
                   "/api/administrative-action-batches",
                   new CreateAdministrativeActionBatchRequest(
                       FlowMappings(workflowId),
                       "Resume after committed item transition",
                       new Dictionary<string, JsonElement>
                       {
                           ["comment"] = JsonSerializer.SerializeToElement("Durable resume")
                       },
                       new AdministrativeActionBatchSelectionDto(
                           AdministrativeActionBatchSelectionModes.Explicit,
                           [approval.Id],
                           null,
                           null),
                       $"resume-{Guid.NewGuid():N}"),
                   "resume-preparer",
                   ["admin", "Ops"]))
        {
            batch = await ReadAsync<AdministrativeActionBatchDetailDto>(create);
        }
        await ProcessBatchJobAsync(Assert.IsType<long>(batch.PreparationJobId));
        batch = await GetBatchAsync(batch.Summary.Id);
        using (var confirm = await SendAsync(
                   HttpMethod.Post,
                   $"/api/administrative-action-batches/{batch.Summary.Id}/confirm",
                   new ConfirmAdministrativeActionBatchRequest(
                       batch.Summary.EligibleItemCount,
                       batch.Summary.UpdatedAt),
                   "resume-confirmer",
                   ["admin", "Ops"]))
        {
            batch = await ReadAsync<AdministrativeActionBatchDetailDto>(confirm);
        }
        var executionJobId = Assert.IsType<long>(batch.ExecutionJobId);
        AdministrativeActionBatchItemDto item;
        using (var itemsResponse = await SendAsync(
                   HttpMethod.Get,
                   $"/api/administrative-action-batches/{batch.Summary.Id}/items?page=1&pageSize=20",
                   user: "resume-confirmer",
                   roles: ["admin", "Ops"]))
        {
            item = Assert.Single(
                (await ReadAsync<PagedResult<AdministrativeActionBatchItemDto>>(
                    itemsResponse)).Items);
        }

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var engine = scope.ServiceProvider.GetRequiredService<IWorkflowEngineService>();
            var result = await engine.ExecuteAdministrativeBatchFlowAsync(
                item.UserTaskId,
                new AdministrativeActionRequest(
                    item.WorkflowDefinitionId,
                    item.FlowId,
                    item.CapturedInstanceUpdatedAt,
                    batch.Summary.Reason,
                    batch.CommonVariables.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value.Clone(),
                        StringComparer.OrdinalIgnoreCase))
                {
                    ExpectedTokenId = item.TokenId,
                    ExpectedUserTaskUpdatedAt = item.CapturedUserTaskUpdatedAt
                },
                new ActorContext(
                    "resume-confirmer",
                    ["admin", "Ops"],
                    new Dictionary<string, string>()),
                batch.Summary.Id,
                CancellationToken.None);
            Assert.NotNull(result);
        }

        // Simulate a worker crash immediately after the item/transition commit:
        // the durable job is still queued and must finalize without replaying it.
        await ProcessBatchJobAsync(executionJobId);
        batch = await GetBatchAsync(batch.Summary.Id);
        Assert.Equal(AdministrativeActionBatchStatuses.Completed, batch.Summary.Status);
        Assert.Equal(1, batch.Summary.SucceededItemCount);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(
            1,
            await db.InstanceHistory.CountAsync(history =>
                history.InstanceId == instance.Id
                && history.UserTaskId == approval.Id
                && history.ActionId == 301));
    }

    [Fact]
    public async Task ExplicitBatch_FreezesExistingTasksThatBecameStale_AndShowsThemIneligible()
    {
        var workflowId = await CreateWorkflowAsync(CreateAdministrativeReturnModel());
        var eligibleInstance = await StartAsync(workflowId);
        var staleInstance = await StartAsync(workflowId);
        var eligibleTask = await MoveToApprovalAsync(eligibleInstance.Id, "eligible-reviewer");
        var staleTask = await MoveToApprovalAsync(staleInstance.Id, "stale-reviewer");

        using (var completeStaleTask = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{staleTask.Id}/flows/302",
                   new TakeFlowRequest(null),
                   "approval-owner",
                   ["Approver"]))
        {
            Assert.Equal(HttpStatusCode.OK, completeStaleTask.StatusCode);
        }

        AdministrativeActionBatchDetailDto batch;
        using (var create = await SendAsync(
                   HttpMethod.Post,
                   "/api/administrative-action-batches",
                   new CreateAdministrativeActionBatchRequest(
                       FlowMappings(workflowId),
                       "Freeze a task that went stale",
                       new Dictionary<string, JsonElement>
                       {
                           ["comment"] = JsonSerializer.SerializeToElement("Stale selection test")
                       },
                       new AdministrativeActionBatchSelectionDto(
                           AdministrativeActionBatchSelectionModes.Explicit,
                           [eligibleTask.Id, staleTask.Id],
                           null,
                           null),
                       $"stale-batch-{Guid.NewGuid():N}"),
                   "batch-operator",
                   ["admin", "Ops"]))
        {
            Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
            batch = await ReadAsync<AdministrativeActionBatchDetailDto>(create);
        }

        Assert.Equal(2, batch.Summary.TotalItemCount);
        await ProcessBatchJobAsync(Assert.IsType<long>(batch.PreparationJobId));
        batch = await GetBatchAsync(batch.Summary.Id);
        Assert.Equal(AdministrativeActionBatchStatuses.Ready, batch.Summary.Status);
        Assert.Equal(1, batch.Summary.EligibleItemCount);
        Assert.Equal(1, batch.Summary.IneligibleItemCount);

        using var itemsResponse = await SendAsync(
            HttpMethod.Get,
            $"/api/administrative-action-batches/{batch.Summary.Id}/items?page=1&pageSize=20",
            user: "batch-confirmer",
            roles: ["admin", "Ops"]);
        Assert.Equal(HttpStatusCode.OK, itemsResponse.StatusCode);
        var items = await ReadAsync<PagedResult<AdministrativeActionBatchItemDto>>(
            itemsResponse);
        Assert.Equal(
            AdministrativeActionBatchItemStatuses.Eligible,
            items.Items.Single(item => item.UserTaskId == eligibleTask.Id).Status);
        Assert.Equal(
            AdministrativeActionBatchItemStatuses.Ineligible,
            items.Items.Single(item => item.UserTaskId == staleTask.Id).Status);
    }

    [Fact]
    public async Task Batch_DurableJobsPreserveAllowlistedClaimsForPreparationAndExecution()
    {
        var model = CreateAdministrativeReturnModel();
        model.SequenceFlows.Single(flow => flow.Id == 301).Condition =
            "[sys.claim.department] == 'finance'";
        var workflowId = await CreateWorkflowAsync(model);
        var instance = await StartAsync(workflowId);
        var approval = await MoveToApprovalAsync(instance.Id, "claims-reviewer");
        var financeClaim = new Dictionary<string, string>
        {
            ["department"] = "finance"
        };

        AdministrativeActionBatchDetailDto batch;
        using (var create = await SendAsync(
                   HttpMethod.Post,
                   "/api/administrative-action-batches",
                   new CreateAdministrativeActionBatchRequest(
                       FlowMappings(workflowId),
                       "Finance claim context",
                       new Dictionary<string, JsonElement>
                       {
                           ["comment"] = JsonSerializer.SerializeToElement("Finance correction")
                       },
                       new AdministrativeActionBatchSelectionDto(
                           AdministrativeActionBatchSelectionModes.Explicit,
                           [approval.Id],
                           null,
                           null),
                       $"claims-batch-{Guid.NewGuid():N}"),
                   "finance-preparer",
                   ["admin", "Ops"],
                   additionalClaims: financeClaim))
        {
            Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
            batch = await ReadAsync<AdministrativeActionBatchDetailDto>(create);
        }

        await ProcessBatchJobAsync(Assert.IsType<long>(batch.PreparationJobId));
        batch = await GetBatchAsync(batch.Summary.Id);
        Assert.Equal(1, batch.Summary.EligibleItemCount);

        using (var confirm = await SendAsync(
                   HttpMethod.Post,
                   $"/api/administrative-action-batches/{batch.Summary.Id}/confirm",
                   new ConfirmAdministrativeActionBatchRequest(
                       1,
                       batch.Summary.UpdatedAt),
                   "finance-confirmer",
                   ["admin", "Ops"],
                   additionalClaims: financeClaim))
        {
            Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
            batch = await ReadAsync<AdministrativeActionBatchDetailDto>(confirm);
        }

        await ProcessBatchJobAsync(Assert.IsType<long>(batch.ExecutionJobId));
        batch = await GetBatchAsync(batch.Summary.Id);
        Assert.Equal(AdministrativeActionBatchStatuses.Completed, batch.Summary.Status);
        Assert.Equal(1, batch.Summary.SucceededItemCount);
    }

    [Fact]
    public async Task CancellingBatch_WaitsForStartedItemToSettle_AndRefreshesFinalCounts()
    {
        var workflowId = await CreateWorkflowAsync(CreateAdministrativeReturnModel());
        var instance = await StartAsync(workflowId);
        var approval = await MoveToApprovalAsync(instance.Id, "cancel-reviewer");

        AdministrativeActionBatchDetailDto batch;
        using (var create = await SendAsync(
                   HttpMethod.Post,
                   "/api/administrative-action-batches",
                   new CreateAdministrativeActionBatchRequest(
                       FlowMappings(workflowId),
                       "Settle started item after cancellation",
                       new Dictionary<string, JsonElement>
                       {
                           ["comment"] = JsonSerializer.SerializeToElement("Cancellation race")
                       },
                       new AdministrativeActionBatchSelectionDto(
                           AdministrativeActionBatchSelectionModes.Explicit,
                           [approval.Id],
                           null,
                           null),
                       $"cancel-race-{Guid.NewGuid():N}"),
                   "cancel-preparer",
                   ["admin", "Ops"]))
        {
            Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
            batch = await ReadAsync<AdministrativeActionBatchDetailDto>(create);
        }
        await ProcessBatchJobAsync(Assert.IsType<long>(batch.PreparationJobId));
        batch = await GetBatchAsync(batch.Summary.Id);

        using (var confirm = await SendAsync(
                   HttpMethod.Post,
                   $"/api/administrative-action-batches/{batch.Summary.Id}/confirm",
                   new ConfirmAdministrativeActionBatchRequest(
                       batch.Summary.EligibleItemCount,
                       batch.Summary.UpdatedAt),
                   "cancel-confirmer",
                   ["admin", "Ops"]))
        {
            Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
            batch = await ReadAsync<AdministrativeActionBatchDetailDto>(confirm);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var item = await db.AdministrativeActionBatchItems
                .SingleAsync(candidate => candidate.BatchId == batch.Summary.Id);
            item.StartedAt = DateTimeOffset.UtcNow;
            item.UpdatedAt = item.StartedAt.Value;
            await db.SaveChangesAsync();
        }

        using (var cancel = await SendAsync(
                   HttpMethod.Post,
                   $"/api/administrative-action-batches/{batch.Summary.Id}/cancel",
                   new CancelAdministrativeActionBatchRequest("Operator stopped remaining work"),
                   "cancel-operator",
                   ["admin", "Ops"]))
        {
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
            batch = await ReadAsync<AdministrativeActionBatchDetailDto>(cancel);
        }
        Assert.Equal(AdministrativeActionBatchStatuses.Cancelled, batch.Summary.Status);
        Assert.Null(batch.Summary.CompletedAt);
        Assert.Equal(1, batch.Summary.QueuedItemCount);

        await ProcessBatchJobAsync(Assert.IsType<long>(batch.ExecutionJobId));
        batch = await GetBatchAsync(batch.Summary.Id);
        Assert.Equal(AdministrativeActionBatchStatuses.Cancelled, batch.Summary.Status);
        Assert.NotNull(batch.Summary.CompletedAt);
        Assert.Equal(0, batch.Summary.QueuedItemCount);
        Assert.Equal(1, batch.Summary.SucceededItemCount);
    }

    [Fact]
    public async Task MultiVersionBatch_UsesExactFlowMappingsWithoutChangingInstanceVersions()
    {
        var firstModel = CreateAdministrativeReturnModel();
        var firstWorkflowId = await CreateWorkflowAsync(firstModel);
        var firstInstance = await StartAsync(firstWorkflowId);
        var firstApproval = await MoveToApprovalAsync(
            firstInstance.Id,
            "version-one-reviewer");

        var secondModel = CreateAdministrativeReturnModel();
        secondModel.Id = firstModel.Id;
        secondModel.Name = firstModel.Name;
        secondModel.SequenceFlows.Single(flow => flow.Id == 301).Id = 901;
        var secondWorkflow = await CreateVersionAsync(firstWorkflowId, secondModel);
        var secondInstance = await StartAsync(secondWorkflow.Id);
        var secondApproval = await MoveToApprovalAsync(
            secondInstance.Id,
            "version-two-reviewer");
        var mappings = new AdministrativeActionFlowMappingDto[]
        {
            new(firstWorkflowId, 301),
            new(secondWorkflow.Id, 901)
        };

        using (var searchResponse = await SendAsync(
                   HttpMethod.Post,
                   "/api/administrative-actions/candidates/search",
                   new AdministrativeActionCandidateSearchRequest
                   {
                       FlowMappings = mappings,
                       Page = 1,
                       PageSize = 50
                   },
                   "version-operator",
                   ["admin", "Ops"]))
        {
            Assert.Equal(HttpStatusCode.OK, searchResponse.StatusCode);
            var candidates = await ReadAsync<PagedResult<AdministrativeActionCandidateDto>>(
                searchResponse);
            Assert.Contains(candidates.Items, item =>
                item.UserTaskId == firstApproval.Id
                && item.WorkflowDefinitionId == firstWorkflowId
                && item.FlowId == 301);
            Assert.Contains(candidates.Items, item =>
                item.UserTaskId == secondApproval.Id
                && item.WorkflowDefinitionId == secondWorkflow.Id
                && item.FlowId == 901);
        }

        AdministrativeActionBatchDetailDto batch;
        using (var create = await SendAsync(
                   HttpMethod.Post,
                   "/api/administrative-action-batches",
                   new CreateAdministrativeActionBatchRequest(
                       mappings,
                       "Mapped multi-version correction",
                       new Dictionary<string, JsonElement>
                       {
                           ["comment"] = JsonSerializer.SerializeToElement(
                               "Correct both immutable versions")
                       },
                       new AdministrativeActionBatchSelectionDto(
                           AdministrativeActionBatchSelectionModes.Explicit,
                           [firstApproval.Id, secondApproval.Id],
                           null,
                           null),
                       $"multi-version-{Guid.NewGuid():N}"),
                   "version-operator",
                   ["admin", "Ops"]))
        {
            Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
            batch = await ReadAsync<AdministrativeActionBatchDetailDto>(create);
        }
        Assert.Equal(2, batch.FlowMappings.Count);
        Assert.Contains(batch.FlowMappings, mapping =>
            mapping.WorkflowDefinitionId == firstWorkflowId && mapping.FlowId == 301);
        Assert.Contains(batch.FlowMappings, mapping =>
            mapping.WorkflowDefinitionId == secondWorkflow.Id && mapping.FlowId == 901);
        Assert.All(batch.FlowMappings, mapping => Assert.Null(mapping.FlowExternalId));

        await ProcessBatchJobAsync(Assert.IsType<long>(batch.PreparationJobId));
        batch = await GetBatchAsync(batch.Summary.Id);
        Assert.Equal(2, batch.Summary.EligibleItemCount);
        using (var confirm = await SendAsync(
                   HttpMethod.Post,
                   $"/api/administrative-action-batches/{batch.Summary.Id}/confirm",
                   new ConfirmAdministrativeActionBatchRequest(
                       2,
                       batch.Summary.UpdatedAt),
                   "version-operator",
                   ["admin", "Ops"]))
        {
            Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
            batch = await ReadAsync<AdministrativeActionBatchDetailDto>(confirm);
        }
        await ProcessBatchJobAsync(Assert.IsType<long>(batch.ExecutionJobId));
        batch = await GetBatchAsync(batch.Summary.Id);
        Assert.Equal(AdministrativeActionBatchStatuses.Completed, batch.Summary.Status);
        Assert.Equal(2, batch.Summary.SucceededItemCount);

        var firstAfter = await GetInstanceAsync(firstInstance.Id);
        var secondAfter = await GetInstanceAsync(secondInstance.Id);
        Assert.Equal(firstWorkflowId, firstAfter.Workflow.Id);
        Assert.Equal(secondWorkflow.Id, secondAfter.Workflow.Id);
        Assert.Empty(firstAfter.VersionChanges);
        Assert.Empty(secondAfter.VersionChanges);
        Assert.Equal(2, firstAfter.CurrentNodeId);
        Assert.Equal(2, secondAfter.CurrentNodeId);
        Assert.Single(firstAfter.History, row =>
            row.UserTaskId == firstApproval.Id
            && row.Note == "administrativeAction"
            && row.AdministrativeActionBatchId == batch.Summary.Id);
        Assert.Single(secondAfter.History, row =>
            row.UserTaskId == secondApproval.Id
            && row.Note == "administrativeAction"
            && row.AdministrativeActionBatchId == batch.Summary.Id);

        Assert.Equal(
            "version-one-reviewer",
            (await GetSingleActiveTaskAsync(
                firstInstance.Id,
                "version-one-reviewer",
                "Worker")).ClaimedBy);
        Assert.Equal(
            "version-two-reviewer",
            (await GetSingleActiveTaskAsync(
                secondInstance.Id,
                "version-two-reviewer",
                "Worker")).ClaimedBy);
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

    private async Task CreateWorkflowSettingAsync(
        string settingNamespace,
        string name,
        object value)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/workflow-settings",
            new CreateWorkflowSettingRequest(
                settingNamespace,
                name,
                JsonSerializer.SerializeToElement(value)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task<WorkflowDetailDto> CreateVersionAsync(
        long sourceWorkflowId,
        WorkflowModel model)
    {
        using var response = await SendAsync(
            HttpMethod.Put,
            $"/api/workflows/{sourceWorkflowId}",
            new UpdateWorkflowRequest(model, true));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<WorkflowDetailDto>(response);
    }

    private async Task<InstanceDetailDto> StartAsync(long workflowId)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(
                workflowId,
                null,
                null,
                new Dictionary<string, JsonElement>
                {
                    ["amount"] = JsonSerializer.SerializeToElement(500)
                }),
            "starter");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<InstanceDetailDto>(response);
    }

    private async Task<UserTaskDto> GetSingleActiveTaskAsync(
        long instanceId,
        string user,
        params string[] roles)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/instances/{instanceId}/user-tasks?status=active&page=1&pageSize=20",
            user: user,
            roles: roles);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.Single(
            (await ReadAsync<PagedResult<UserTaskDto>>(response)).Items);
    }

    private async Task<UserTaskDto> MoveToApprovalAsync(long instanceId, string reviewer)
    {
        var review = await GetSingleActiveTaskAsync(instanceId, reviewer, "Worker");
        using (var claim = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{review.Id}/claim",
                   user: reviewer,
                   roles: ["Worker"]))
        {
            Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        }
        using (var submit = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{review.Id}/flows/201",
                   new TakeFlowRequest(null),
                   reviewer,
                   ["Worker"]))
        {
            Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
        }
        return await GetSingleActiveTaskAsync(instanceId, "approval-owner", "Approver");
    }

    private async Task<AdministrativeActionBatchDetailDto> GetBatchAsync(long batchId)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/administrative-action-batches/{batchId}",
            user: "batch-confirmer",
            roles: ["admin", "Ops"]);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<AdministrativeActionBatchDetailDto>(response);
    }

    private async Task ProcessBatchJobAsync(long jobId)
    {
        await using (var db = fixture.CreateDbContext())
        {
            var job = await db.WorkflowJobs.SingleAsync(entity => entity.Id == jobId);
            job.Priority = 10_000;
            job.DueAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>();
        var leases = await repository.LeaseRunnableAsync(
            new WorkflowJobLeaseRequest(
                $"administrative-batch-test-{Guid.NewGuid():N}",
                MaxCount: 1,
                MaxActivityCount: 1,
                MaxPerInstance: 1,
                LeaseDuration: TimeSpan.FromMinutes(2)),
            CancellationToken.None);
        var lease = Assert.Single(leases);
        Assert.Equal(jobId, lease.Job.Id);
        var processor = scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>();
        await processor.ProcessAsync(lease, CancellationToken.None);
    }

    private async Task<UserTaskDto> GetTaskAsync(
        long taskId,
        string user,
        params string[] roles)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/user-tasks/{taskId}",
            user: user,
            roles: roles);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<UserTaskDto>(response);
    }

    private async Task<InstanceDetailDto> GetInstanceAsync(long instanceId)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/instances/{instanceId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<InstanceDetailDto>(response);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "test-admin",
        string[]? roles = null,
        bool suppressImplicitAdmin = false,
        IReadOnlyDictionary<string, string>? additionalClaims = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, user, roles ?? []);
        if (suppressImplicitAdmin)
        {
            request.Headers.TryAddWithoutValidation("X-Test-Suppress-Admin", "true");
        }
        if (additionalClaims is not null)
        {
            foreach (var claim in additionalClaims)
            {
                request.Headers.TryAddWithoutValidation(
                    $"X-Test-Claim-{claim.Key}",
                    claim.Value);
            }
        }
        return await fixture.Client.SendAsync(request);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException("Response body was empty.");

    private static IReadOnlyList<AdministrativeActionFlowMappingDto> FlowMappings(
        long workflowDefinitionId,
        int flowId = 301) =>
        [new AdministrativeActionFlowMappingDto(workflowDefinitionId, flowId)];

    private static WorkflowModel CreateAdministrativeReturnModel()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = $"administrative-return-{suffix}",
            Name = $"Administrative return {suffix}",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel
                {
                    Id = 1,
                    Name = "Start",
                    Type = BpmnFlowNodeTypes.StartEvent,
                    Variables =
                    [
                        new VariableModel
                        {
                            Id = 10,
                            Name = "amount",
                            DataType = WorkflowVariableTypes.Number,
                            Required = true
                        }
                    ]
                },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Review",
                    Type = BpmnFlowNodeTypes.UserTask,
                    Roles = ["Worker"],
                    RequiresClaim = true,
                    ClaimMode = ClaimModes.Previous
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Approval",
                    Type = BpmnFlowNodeTypes.UserTask,
                    Roles = ["Approver"],
                    AssigneeExpression = "'approval-owner'"
                },
                new FlowNodeModel
                {
                    Id = 4,
                    Name = "End",
                    Type = BpmnFlowNodeTypes.EndEvent
                }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel
                {
                    Id = 101,
                    Name = "Begin review",
                    SourceRef = 1,
                    TargetRef = 2
                },
                new SequenceFlowModel
                {
                    Id = 201,
                    Name = "Submit",
                    SourceRef = 2,
                    TargetRef = 3,
                    Roles = ["Worker"]
                },
                new SequenceFlowModel
                {
                    Id = 301,
                    Name = "Return for rework",
                    SourceRef = 3,
                    TargetRef = 2,
                    Roles = ["Ops"],
                    Condition = "amount > 100",
                    Variables =
                    [
                        new VariableModel
                        {
                            Id = 30,
                            Name = "comment",
                            DataType = WorkflowVariableTypes.String,
                            Required = true
                        }
                    ]
                },
                new SequenceFlowModel
                {
                    Id = 302,
                    Name = "Approve",
                    SourceRef = 3,
                    TargetRef = 4,
                    Roles = ["Approver"]
                }
            ]
        };
    }

    private static void EnableAdministrativeFlowEvidence(WorkflowModel model)
    {
        var terminal = model.FlowNodes.Single(node => node.Id == 4);
        terminal.Name = "Audit outcome";
        terminal.Type = BpmnFlowNodeTypes.ExclusiveGateway;
        model.FlowNodes.AddRange(
        [
            new FlowNodeModel
            {
                Id = 5,
                Name = "Returned path end",
                Type = BpmnFlowNodeTypes.EndEvent
            },
            new FlowNodeModel
            {
                Id = 6,
                Name = "Normal path end",
                Type = BpmnFlowNodeTypes.EndEvent
            }
        ]);
        model.SequenceFlows.AddRange(
        [
            new SequenceFlowModel
            {
                Id = 401,
                Name = "Administrative return was used",
                SourceRef = 4,
                TargetRef = 5,
                Condition = "FlowInfo(301, 'actions.count') > 0",
                ConditionPriority = 1
            },
            new SequenceFlowModel
            {
                Id = 402,
                Name = "No administrative return",
                SourceRef = 4,
                TargetRef = 6,
                IsDefault = true
            }
        ]);
    }
}
