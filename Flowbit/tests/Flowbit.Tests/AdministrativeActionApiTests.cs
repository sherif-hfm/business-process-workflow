using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
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
    public async Task AdministrativeAction_IsHiddenFromNormalActions_BypassesOnlyTaskOwnership_AndDoesNotInheritOperator()
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

        using (var forbiddenGlobal = await SendAsync(
                   HttpMethod.Get,
                   $"/api/user-tasks/{approval.Id}/administrative-actions?targetWorkflowId={workflowId}",
                   user: "operator",
                   roles: ["Ops"],
                   suppressImplicitAdmin: true))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenGlobal.StatusCode);
        }

        using (var missingFlowRole = await SendAsync(
                   HttpMethod.Get,
                   $"/api/user-tasks/{approval.Id}/administrative-actions?targetWorkflowId={workflowId}",
                   user: "operator",
                   roles: ["admin"]))
        {
            Assert.Equal(HttpStatusCode.OK, missingFlowRole.StatusCode);
            Assert.Empty(await ReadAsync<List<AdministrativeActionSummaryDto>>(
                missingFlowRole));
        }

        using (var ordinaryTaskView = await SendAsync(
                   HttpMethod.Get,
                   $"/api/user-tasks/{approval.Id}",
                   user: "operator",
                   roles: ["admin", "Ops"]))
        {
            Assert.Equal(HttpStatusCode.BadRequest, ordinaryTaskView.StatusCode);
        }
        using (var privilegedContext = await SendAsync(
                   HttpMethod.Get,
                   $"/api/user-tasks/{approval.Id}/administrative-context",
                   user: "operator",
                   roles: ["admin", "Ops"]))
        {
            Assert.Equal(HttpStatusCode.OK, privilegedContext.StatusCode);
            var context = await ReadAsync<AdministrativeActionTaskContextDto>(
                privilegedContext);
            Assert.Equal(approval.Id, context.UserTaskId);
            Assert.Equal(instance.Id, context.InstanceId);
            Assert.Equal(workflowId, context.SourceWorkflowId);
            Assert.Contains(context.TargetVersions, version => version.Id == workflowId);
        }

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
                   $"/api/workflows/{workflowId}/administrative-actions?batchableOnly=true",
                   user: "operator",
                   roles: ["admin", "Ops"]))
        {
            Assert.Equal(HttpStatusCode.OK, definitionActions.StatusCode);
            var action = Assert.Single(
                await ReadAsync<List<AdministrativeActionSummaryDto>>(
                    definitionActions));
            Assert.Equal("RETURN_FOR_REWORK", action.FlowExternalId);
            Assert.True(action.IsBatchable);
        }

        using (var taskActions = await SendAsync(
                   HttpMethod.Get,
                   $"/api/user-tasks/{approval.Id}/administrative-actions?targetWorkflowId={workflowId}",
                   user: "operator",
                   roles: ["admin", "Ops"]))
        {
            Assert.Equal(HttpStatusCode.OK, taskActions.StatusCode);
            Assert.Equal(
                301,
                Assert.Single(await ReadAsync<List<AdministrativeActionSummaryDto>>(
                    taskActions)).FlowId);
        }

        var current = await GetInstanceAsync(instance.Id);
        var request = new AdministrativeActionRequest(
            workflowId,
            workflowId,
            current.UpdatedAt,
            "return_for_rework",
            "  compliance correction  ",
            new Dictionary<string, JsonElement>
            {
                ["comment"] = JsonSerializer.SerializeToElement(
                    "Missing supporting document")
            })
        {
            ExpectedTokenId = approval.TokenId,
            ExpectedUserTaskUpdatedAt = approval.UpdatedAt
        };

        using (var staleTokenPreview = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{approval.Id}/administrative-actions/preview",
                   request with { ExpectedTokenId = approval.TokenId + 10_000 },
                   "operator",
                   ["admin", "Ops"]))
        {
            Assert.Equal(HttpStatusCode.OK, staleTokenPreview.StatusCode);
            var eligibility = await ReadAsync<AdministrativeActionEligibilityDto>(
                staleTokenPreview);
            Assert.False(eligibility.Eligible);
            Assert.Contains(eligibility.Issues, issue => issue.Code == "tokenChanged");
        }

        using (var preview = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{approval.Id}/administrative-actions/preview",
                   request,
                   "operator",
                   ["admin", "Ops"]))
        {
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            var eligibility = await ReadAsync<AdministrativeActionEligibilityDto>(preview);
            Assert.True(eligibility.Eligible);
            Assert.Empty(eligibility.Issues);
        }

        AdministrativeActionResultDto result;
        using (var execute = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{approval.Id}/administrative-actions",
                   request,
                   "operator",
                   ["admin", "Ops"]))
        {
            Assert.Equal(HttpStatusCode.OK, execute.StatusCode);
            result = await ReadAsync<AdministrativeActionResultDto>(execute);
        }

        Assert.Equal(approval.Id, result.CompletedUserTaskId);
        Assert.Null(result.VersionChange);
        Assert.Null(result.AdministrativeActionBatchId);
        Assert.Equal(2, result.Instance.CurrentNodeId);
        var returned = Assert.IsType<UserTaskDto>(result.NewUserTask);
        Assert.Equal(2, returned.NodeId);
        Assert.Equal("reviewer", returned.ClaimedBy);
        Assert.NotEqual("operator", returned.ClaimedBy);

        var completed = await GetTaskAsync(
            approval.Id,
            "approval-owner",
            "Approver");
        Assert.Equal("operator", completed.CompletedBy);
        Assert.Equal("administrativeAction", completed.CompletionKind);
        Assert.Equal("compliance correction", completed.CompletionReason);
        Assert.Null(completed.AdministrativeActionBatchId);

        var detail = await GetInstanceAsync(instance.Id);
        var history = Assert.Single(detail.History, item =>
            item.UserTaskId == approval.Id
            && item.SequenceFlowId == 301);
        Assert.Equal("administrativeAction", history.Note);
        Assert.Equal("compliance correction", history.Reason);
        Assert.Equal("operator", history.PerformedBy);
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
            Assert.Equal("administrativeAction", occurrence.Kind);
            Assert.Equal("operator", occurrence.User);
            Assert.Contains("Ops", occurrence.UserRoles);

            var evidence = await db.SequenceFlowSummaries
                .AsNoTracking()
                .SingleAsync(item => item.InstanceId == instance.Id
                                     && item.SequenceFlowId == 301);
            Assert.Equal(1, evidence.ActionCount);
            Assert.Equal(1, evidence.TraversalCount);
            Assert.Equal("administrativeAction", evidence.LastActionKind);
            Assert.Equal("administrativeAction", evidence.LastTraversalKind);
        }
    }

    [Fact]
    public async Task DiscoveryAndPreview_LoadWorkflowSettingsForConditionsAndVariableValidation()
    {
        var settingNamespace = $"administrativeaction{Guid.NewGuid():N}";
        await CreateWorkflowSettingAsync(settingNamespace, "enabled", true);
        await CreateWorkflowSettingAsync(settingNamespace, "minimumCommentLength", 12);

        var model = CreateAdministrativeReturnModel();
        var administrativeFlow = model.SequenceFlows.Single(flow => flow.IsAdministrative);
        administrativeFlow.Condition = $"[setting.{settingNamespace}.enabled] == true";
        administrativeFlow.Variables.Single(variable => variable.Name == "comment").Validation =
            $"Length(comment) >= [setting.{settingNamespace}.minimumCommentLength]";

        var workflowId = await CreateWorkflowAsync(model);
        var instance = await StartAsync(workflowId);
        var approval = await MoveToApprovalAsync(instance.Id, "settings-reviewer");

        using (var discovery = await SendAsync(
                   HttpMethod.Get,
                   $"/api/user-tasks/{approval.Id}/administrative-actions?targetWorkflowId={workflowId}",
                   user: "settings-operator",
                   roles: ["admin", "Ops"]))
        {
            Assert.Equal(HttpStatusCode.OK, discovery.StatusCode);
            Assert.Equal(
                301,
                Assert.Single(await ReadAsync<List<AdministrativeActionSummaryDto>>(
                    discovery)).FlowId);
        }

        var selectedState = await GetInstanceAsync(instance.Id);
        var request = new AdministrativeActionRequest(
            workflowId,
            workflowId,
            selectedState.UpdatedAt,
            "RETURN_FOR_REWORK",
            "Settings-backed correction",
            new Dictionary<string, JsonElement>
            {
                ["comment"] = JsonSerializer.SerializeToElement("short")
            })
        {
            ExpectedTokenId = approval.TokenId,
            ExpectedUserTaskUpdatedAt = approval.UpdatedAt
        };

        using (var invalidPreview = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{approval.Id}/administrative-actions/preview",
                   request,
                   "settings-operator",
                   ["admin", "Ops"]))
        {
            Assert.Equal(HttpStatusCode.OK, invalidPreview.StatusCode);
            var eligibility = await ReadAsync<AdministrativeActionEligibilityDto>(
                invalidPreview);
            Assert.False(eligibility.Eligible);
            Assert.Contains(eligibility.Issues, issue => issue.Code == "invalidVariables");
            Assert.DoesNotContain(
                eligibility.Issues,
                issue => issue.Code == "conditionNotSatisfied");
        }

        request = request with
        {
            Variables = new Dictionary<string, JsonElement>
            {
                ["comment"] = JsonSerializer.SerializeToElement(
                    "Long enough correction comment")
            }
        };
        using (var validPreview = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{approval.Id}/administrative-actions/preview",
                   request,
                   "settings-operator",
                   ["admin", "Ops"]))
        {
            Assert.Equal(HttpStatusCode.OK, validPreview.StatusCode);
            var eligibility = await ReadAsync<AdministrativeActionEligibilityDto>(
                validPreview);
            Assert.True(eligibility.Eligible);
            Assert.Empty(eligibility.Issues);
        }

        using var execute = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{approval.Id}/administrative-actions",
            request,
            "settings-operator",
            ["admin", "Ops"]);
        Assert.Equal(HttpStatusCode.OK, execute.StatusCode);
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
            workflowId,
            "RETURN_FOR_REWORK",
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
            workflowId,
            "RETURN_FOR_REWORK",
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
            TargetWorkflowId = workflowId,
            FlowExternalId = "RETURN_FOR_REWORK",
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
                       workflowId,
                       "RETURN_FOR_REWORK",
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
                    workflowId,
                    "RETURN_FOR_REWORK",
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
        var workflowId = await CreateWorkflowAsync(CreateAdministrativeReturnModel());
        var firstInstance = await StartAsync(workflowId);
        var secondInstance = await StartAsync(workflowId);
        var firstApproval = await MoveToApprovalAsync(firstInstance.Id, "reviewer-one");
        var secondApproval = await MoveToApprovalAsync(secondInstance.Id, "reviewer-two");

        var search = new AdministrativeActionCandidateSearchRequest
        {
            TargetWorkflowId = workflowId,
            FlowExternalId = "RETURN_FOR_REWORK",
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
                       workflowId,
                       "RETURN_FOR_REWORK",
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
                       workflowId,
                       "RETURN_FOR_REWORK",
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
                       workflowId,
                       "RETURN_FOR_REWORK",
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
            var result = await engine.ExecuteUserTaskAdministrativeActionAsync(
                item.UserTaskId,
                new AdministrativeActionRequest(
                    batch.Summary.TargetWorkflowId,
                    item.SourceWorkflowId,
                    item.CapturedInstanceUpdatedAt,
                    batch.Summary.FlowExternalId,
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
                CancellationToken.None,
                batch.Summary.Id);
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
                       workflowId,
                       "RETURN_FOR_REWORK",
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
        model.SequenceFlows.Single(flow => flow.IsAdministrative).Condition =
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
                       workflowId,
                       "RETURN_FOR_REWORK",
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
                       workflowId,
                       "RETURN_FOR_REWORK",
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
    public async Task CrossVersionAction_RollsBackIncompatibleTransitionThenChangesVersionAndReturnsAtomically()
    {
        var sourceModel = CreateAdministrativeReturnModel();
        sourceModel.SequenceFlows.RemoveAll(flow => flow.IsAdministrative);
        var sourceWorkflowId = await CreateWorkflowAsync(sourceModel);
        var instance = await StartAsync(sourceWorkflowId);
        var approval = await MoveToApprovalAsync(instance.Id, "version-reviewer");
        var selectedState = await GetInstanceAsync(instance.Id);

        var guardedTargetModel = CreateAdministrativeReturnModel();
        guardedTargetModel.Id = sourceModel.Id;
        guardedTargetModel.Name = sourceModel.Name;
        guardedTargetModel.SequenceFlows.Single(flow => flow.IsAdministrative).Condition =
            "amount > 1000";
        var guardedTarget = await CreateVersionAsync(
            sourceWorkflowId,
            guardedTargetModel);
        var guardedRequest = new AdministrativeActionRequest(
            guardedTarget.Id,
            sourceWorkflowId,
            selectedState.UpdatedAt,
            "RETURN_FOR_REWORK",
            "Condition should roll back",
            new Dictionary<string, JsonElement>
            {
                ["comment"] = JsonSerializer.SerializeToElement("Guarded")
            })
        {
            ExpectedTokenId = approval.TokenId,
            ExpectedUserTaskUpdatedAt = approval.UpdatedAt
        };

        using (var rejected = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{approval.Id}/administrative-actions",
                   guardedRequest,
                   "version-operator",
                   ["admin", "Ops"]))
        {
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }

        var afterRejected = await GetInstanceAsync(instance.Id);
        Assert.Equal(sourceWorkflowId, afterRejected.Workflow.Id);
        Assert.Equal(selectedState.UpdatedAt, afterRejected.UpdatedAt);
        Assert.Empty(afterRejected.VersionChanges);
        Assert.Equal(
            approval.Id,
            (await GetSingleActiveTaskAsync(
                instance.Id,
                "approval-owner",
                "Approver")).Id);

        var targetModel = CreateAdministrativeReturnModel();
        targetModel.Id = sourceModel.Id;
        targetModel.Name = sourceModel.Name;
        var target = await CreateVersionAsync(sourceWorkflowId, targetModel);
        var request = guardedRequest with
        {
            TargetWorkflowId = target.Id,
            Reason = "Approved cross-version correction"
        };

        AdministrativeActionResultDto result;
        using (var response = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{approval.Id}/administrative-actions",
                   request,
                   "version-operator",
                   ["admin", "Ops"]))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            result = await ReadAsync<AdministrativeActionResultDto>(response);
        }

        Assert.Equal(target.Id, result.Instance.Workflow.Id);
        Assert.Equal(3, result.Instance.Workflow.Version);
        Assert.Equal(2, result.Instance.CurrentNodeId);
        Assert.Equal("version-reviewer", result.NewUserTask?.ClaimedBy);
        var versionChange = Assert.IsType<InstanceVersionChangeAuditDto>(
            result.VersionChange);
        Assert.Equal(sourceWorkflowId, versionChange.SourceWorkflow.Id);
        Assert.Equal(target.Id, versionChange.TargetWorkflow.Id);
        Assert.Equal("Approved cross-version correction", versionChange.Reason);
        Assert.Equal("version-operator", versionChange.ChangedBy);

        var detail = await GetInstanceAsync(instance.Id);
        Assert.Equal(versionChange.Id, Assert.Single(detail.VersionChanges).Id);
        Assert.Single(detail.History, row =>
            row.UserTaskId == approval.Id
            && row.Note == "administrativeAction");
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
                    ExternalId = "RETURN_FOR_REWORK",
                    SourceRef = 3,
                    TargetRef = 2,
                    Roles = ["Ops"],
                    Condition = "amount > 100",
                    IsAdministrative = true,
                    IsBatchable = true,
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
