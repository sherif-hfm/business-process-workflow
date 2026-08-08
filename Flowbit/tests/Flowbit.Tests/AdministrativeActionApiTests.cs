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
    public async Task AdministrativeCatalog_RequiresAuthenticationButDoesNotRequireRoles()
    {
        var workflowId = await CreateWorkflowAsync(CreateAdministrativeReturnModel());

        using (var unauthenticated = await fixture.Client.GetAsync(
                   "/api/administrative-actions/workflows"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        }

        using (var catalog = await SendAsync(
                   HttpMethod.Get,
                   "/api/administrative-actions/workflows",
                   user: "plain-user",
                   suppressImplicitAdmin: true))
        {
            Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
            Assert.Contains(
                await ReadAsync<List<WorkflowSummaryDto>>(catalog),
                version => version.Id == workflowId);
        }

        using (var nodes = await SendAsync(
                   HttpMethod.Get,
                   $"/api/workflows/{workflowId}/administrative-actions/nodes",
                   user: "plain-user",
                   suppressImplicitAdmin: true))
        {
            Assert.Equal(HttpStatusCode.OK, nodes.StatusCode);
            var sources = await ReadAsync<List<AdministrativeActionSourceNodeDto>>(nodes);
            Assert.Contains(sources, node => node.NodeId == 2 && !node.IsMultiInstance);
            Assert.Contains(sources, node => node.NodeId == 3 && !node.IsMultiInstance);
        }

        using (var actions = await SendAsync(
                   HttpMethod.Get,
                   $"/api/workflows/{workflowId}/nodes/3/administrative-actions",
                   user: "plain-user",
                   suppressImplicitAdmin: true))
        {
            Assert.Equal(HttpStatusCode.OK, actions.StatusCode);
            var result = await ReadAsync<List<AdministrativeActionSummaryDto>>(actions);
            Assert.Contains(result, action =>
                action.ActionKind == AdministrativeActionKinds.DirectFlow
                && action.FlowId == 301
                && action.Condition == "amount > 1000"
                && action.Roles.Contains("Ops"));
            Assert.Contains(result, action => action.FlowId == 302);
        }
    }

    [Fact]
    public async Task DirectBatch_FreezesPositionAndBypassesRolesAssignmentClaimAndCondition()
    {
        var workflowId = await CreateWorkflowAsync(CreateAdministrativeReturnModel());
        var instance = await StartAsync(
            workflowId,
            new Dictionary<string, JsonElement>
            {
                ["amount"] = JsonSerializer.SerializeToElement(500)
            });
        var approval = await MoveToApprovalAsync(instance.Id, "reviewer");

        var candidates = await SearchCandidatesAsync(
            workflowId,
            3,
            "batch-operator");
        var candidate = Assert.Single(candidates.Items);
        Assert.Equal(AdministrativeActionPositionKinds.UserTask, candidate.PositionKind);
        Assert.Equal(approval.Id, candidate.UserTaskId);
        Assert.Equal(1, candidate.AffectedTaskCount);

        var selection = new AdministrativeActionBatchSelectionDto(
            AdministrativeActionBatchSelectionModes.Explicit,
            [new AdministrativeActionPositionReferenceDto(
                candidate.PositionKind,
                candidate.PositionId)],
            null,
            null);
        AdministrativeActionBatchDetailDto batch;
        using (var create = await SendAsync(
                   HttpMethod.Post,
                   "/api/administrative-action-batches",
                   new CreateAdministrativeActionBatchRequest(
                       workflowId,
                       3,
                       AdministrativeActionKinds.DirectFlow,
                       301,
                       null,
                       null,
                       null,
                       new Dictionary<string, JsonElement>
                       {
                           ["comment"] = JsonSerializer.SerializeToElement("Return without normal permission")
                       },
                       selection,
                       $"direct-{Guid.NewGuid():N}"),
                   "batch-operator",
                   suppressImplicitAdmin: true))
        {
            Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
            batch = await ReadAsync<AdministrativeActionBatchDetailDto>(create);
        }
        Assert.Null(batch.Summary.Reason);
        Assert.Equal(1, batch.Summary.TotalAffectedTaskCount);
        await ProcessBatchJobAsync(batch.PreparationJobId!.Value);
        batch = await GetBatchAsync(batch.Summary.Id, "batch-operator");
        Assert.Equal(AdministrativeActionBatchStatuses.Ready, batch.Summary.Status);
        Assert.Equal(1, batch.Summary.EligibleItemCount);

        using (var confirm = await SendAsync(
                   HttpMethod.Post,
                   $"/api/administrative-action-batches/{batch.Summary.Id}/confirm",
                   new ConfirmAdministrativeActionBatchRequest(
                       batch.Summary.EligibleItemCount,
                       batch.Summary.TotalAffectedTaskCount,
                       batch.Summary.UpdatedAt),
                   "batch-operator",
                   suppressImplicitAdmin: true))
        {
            Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
            batch = await ReadAsync<AdministrativeActionBatchDetailDto>(confirm);
        }
        await ProcessBatchJobAsync(batch.ExecutionJobId!.Value);
        batch = await GetBatchAsync(batch.Summary.Id, "batch-operator");
        Assert.Equal(AdministrativeActionBatchStatuses.Completed, batch.Summary.Status);
        Assert.Equal(1, batch.Summary.SucceededItemCount);

        var returned = await GetSingleActiveTaskAsync(instance.Id, "reviewer", "Worker");
        Assert.Equal(2, returned.NodeId);
        Assert.Equal("reviewer", returned.ClaimedBy);
        Assert.NotEqual("batch-operator", returned.ClaimedBy);

        using var sourceTaskResponse = await SendAsync(
            HttpMethod.Get,
            $"/api/user-tasks/{approval.Id}",
            user: "approval-owner",
            roles: ["Approver"]);
        Assert.Equal(HttpStatusCode.OK, sourceTaskResponse.StatusCode);
        var sourceTask = await ReadAsync<UserTaskDto>(sourceTaskResponse);
        Assert.Equal("administrativeAction", sourceTask.CompletionKind);
        Assert.Equal(batch.Summary.Id, sourceTask.AdministrativeActionBatchId);
        Assert.Equal("batch-operator", sourceTask.CompletedBy);

        using var itemsResponse = await SendAsync(
            HttpMethod.Get,
            $"/api/administrative-action-batches/{batch.Summary.Id}/items",
            user: "batch-operator",
            suppressImplicitAdmin: true);
        var item = Assert.Single(
            (await ReadAsync<PagedResult<AdministrativeActionBatchItemDto>>(itemsResponse)).Items);
        Assert.Equal(AdministrativeActionBatchItemStatuses.Succeeded, item.Status);
        Assert.Equal(candidate.TokenActivationId, item.TokenActivationId);
    }

    [Fact]
    public async Task TimerBoundaryBatch_InterruptsMultiInstanceEvenWhenAuthoredNonInterrupting()
    {
        var workflowId = await CreateWorkflowAsync(CreateMultiInstanceTimerModel());
        var instance = await StartAsync(workflowId, null);
        var candidates = await SearchCandidatesAsync(workflowId, 2, "timer-operator");
        var candidate = Assert.Single(candidates.Items);
        Assert.Equal(
            AdministrativeActionPositionKinds.MultiInstanceExecution,
            candidate.PositionKind);
        Assert.Equal(2, candidate.AffectedTaskCount);
        var timer = Assert.Single(candidate.TimerBoundaries);
        Assert.True(timer.Eligible);
        Assert.Equal(TimerSubscriptionStatuses.Active, timer.Status);

        using (var actionsResponse = await SendAsync(
                   HttpMethod.Get,
                   $"/api/workflows/{workflowId}/nodes/2/administrative-actions",
                   user: "timer-operator",
                   suppressImplicitAdmin: true))
        {
            var actions = await ReadAsync<List<AdministrativeActionSummaryDto>>(actionsResponse);
            var action = Assert.Single(actions, item =>
                item.ActionKind == AdministrativeActionKinds.TimerBoundary);
            Assert.Equal(6, action.BoundaryNodeId);
            Assert.Equal(401, action.FlowId);
            Assert.False(action.AuthoredCancelActivity);
        }

        AdministrativeActionBatchDetailDto batch;
        using (var create = await SendAsync(
                   HttpMethod.Post,
                   "/api/administrative-action-batches",
                   new CreateAdministrativeActionBatchRequest(
                       workflowId,
                       2,
                       AdministrativeActionKinds.TimerBoundary,
                       401,
                       6,
                       null,
                       "Skip the remaining approval wait",
                       null,
                       new AdministrativeActionBatchSelectionDto(
                           AdministrativeActionBatchSelectionModes.Explicit,
                           [new AdministrativeActionPositionReferenceDto(
                               candidate.PositionKind,
                               candidate.PositionId)],
                           null,
                           null),
                       $"timer-{Guid.NewGuid():N}"),
                   "timer-operator",
                   suppressImplicitAdmin: true))
        {
            Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
            batch = await ReadAsync<AdministrativeActionBatchDetailDto>(create);
        }
        await ProcessBatchJobAsync(batch.PreparationJobId!.Value);
        batch = await GetBatchAsync(batch.Summary.Id, "timer-operator");
        Assert.Equal(2, batch.Summary.TotalAffectedTaskCount);
        Assert.Equal(1, batch.Summary.EligibleItemCount);

        using (var confirm = await SendAsync(
                   HttpMethod.Post,
                   $"/api/administrative-action-batches/{batch.Summary.Id}/confirm",
                   new ConfirmAdministrativeActionBatchRequest(
                       1,
                       2,
                       batch.Summary.UpdatedAt),
                   "timer-operator",
                   suppressImplicitAdmin: true))
        {
            Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
            batch = await ReadAsync<AdministrativeActionBatchDetailDto>(confirm);
        }
        await ProcessBatchJobAsync(batch.ExecutionJobId!.Value);
        batch = await GetBatchAsync(batch.Summary.Id, "timer-operator");
        Assert.Equal(AdministrativeActionBatchStatuses.Completed, batch.Summary.Status);

        var detail = await GetInstanceAsync(instance.Id);
        Assert.Equal(WorkflowInstanceStatuses.Completed, detail.Status);
        await using var db = fixture.CreateDbContext();
        Assert.All(
            await db.UserTasks.Where(task => task.InstanceId == instance.Id).ToListAsync(),
            task => Assert.Equal(UserTaskRecordStatuses.Cancelled, task.Status));
        var execution = await db.MultiInstanceExecutions.SingleAsync(
            item => item.InstanceId == instance.Id);
        Assert.Equal(MultiInstanceRecordStatuses.Cancelled, execution.Status);
        var subscription = await db.TimerSubscriptions.SingleAsync(
            item => item.InstanceId == instance.Id && item.TimerNodeId == 6);
        Assert.Equal(TimerSubscriptionStatuses.Completed, subscription.Status);
    }

    [Fact]
    public async Task EveryAdministrativeActionEndpoint_RequiresAuthentication()
    {
        var candidateSearch = new AdministrativeActionCandidateSearchRequest
        {
            WorkflowDefinitionId = 1,
            SourceNodeId = 2,
            Page = 1,
            PageSize = 20
        };
        var selection = new AdministrativeActionBatchSelectionDto(
            AdministrativeActionBatchSelectionModes.Explicit,
            [new AdministrativeActionPositionReferenceDto(
                AdministrativeActionPositionKinds.UserTask,
                1)],
            null,
            null);
        var create = new CreateAdministrativeActionBatchRequest(
            1,
            2,
            AdministrativeActionKinds.DirectFlow,
            201,
            null,
            null,
            null,
            null,
            selection,
            null);

        var calls = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Get, "/api/administrative-actions/workflows", null),
            (HttpMethod.Get, "/api/workflows/1/administrative-actions/nodes", null),
            (HttpMethod.Get, "/api/workflows/1/nodes/2/administrative-actions", null),
            (HttpMethod.Post, "/api/administrative-actions/candidates/search", candidateSearch),
            (HttpMethod.Get, "/api/administrative-action-batches", null),
            (HttpMethod.Get, "/api/administrative-action-batches/1", null),
            (HttpMethod.Get, "/api/administrative-action-batches/1/items", null),
            (HttpMethod.Post, "/api/administrative-action-batches", create),
            (HttpMethod.Post, "/api/administrative-action-batches/1/confirm",
                new ConfirmAdministrativeActionBatchRequest(0, 0, DateTimeOffset.UtcNow)),
            (HttpMethod.Post, "/api/administrative-action-batches/1/cancel",
                new CancelAdministrativeActionBatchRequest(null))
        };

        foreach (var call in calls)
        {
            using var request = new HttpRequestMessage(call.Method, call.Path);
            if (call.Body is not null)
            {
                request.Content = JsonContent.Create(call.Body, options: JsonOptions);
            }
            using var response = await fixture.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task BatchCreation_RejectsInexactDefinitionSourceActionBoundaryAndOrdinaryMode()
    {
        var workflowId = await CreateWorkflowAsync(CreateAdministrativeReturnModel());
        var instance = await StartAsync(
            workflowId,
            new Dictionary<string, JsonElement>
            {
                ["amount"] = JsonSerializer.SerializeToElement(500)
            });
        await MoveToApprovalAsync(instance.Id, "contract-reviewer");
        var candidate = Assert.Single(
            (await SearchCandidatesAsync(workflowId, 3, "contract-operator")).Items);
        var variables = new Dictionary<string, JsonElement>
        {
            ["comment"] = JsonSerializer.SerializeToElement("valid comment")
        };

        var requests = new[]
        {
            BatchRequest(
                long.MaxValue,
                3,
                AdministrativeActionKinds.DirectFlow,
                301,
                null,
                null,
                variables,
                candidate),
            BatchRequest(
                workflowId,
                1,
                AdministrativeActionKinds.DirectFlow,
                301,
                null,
                null,
                variables,
                candidate),
            BatchRequest(
                workflowId,
                3,
                "moveAnything",
                301,
                null,
                null,
                variables,
                candidate),
            BatchRequest(
                workflowId,
                3,
                AdministrativeActionKinds.DirectFlow,
                999_999,
                null,
                null,
                variables,
                candidate),
            BatchRequest(
                workflowId,
                3,
                AdministrativeActionKinds.DirectFlow,
                301,
                6,
                null,
                variables,
                candidate),
            BatchRequest(
                workflowId,
                3,
                AdministrativeActionKinds.TimerBoundary,
                301,
                null,
                null,
                variables,
                candidate),
            BatchRequest(
                workflowId,
                3,
                AdministrativeActionKinds.DirectFlow,
                301,
                null,
                AdministrativeActionMultiInstanceModes.ForceParent,
                variables,
                candidate)
        };

        foreach (var request in requests)
        {
            await AssertCreateBatchBadRequestAsync(request, "contract-operator");
        }
    }

    [Fact]
    public async Task BatchCreation_RequiresAnExactMultiInstanceModeOnlyForDirectMiActions()
    {
        var workflowId = await CreateWorkflowAsync(CreateMultiInstanceTimerModel());
        _ = await StartAsync(workflowId, null);
        var candidate = Assert.Single(
            (await SearchCandidatesAsync(workflowId, 2, "mode-operator")).Items);

        var requests = new[]
        {
            BatchRequest(
                workflowId,
                2,
                AdministrativeActionKinds.DirectFlow,
                201,
                null,
                null,
                null,
                candidate),
            BatchRequest(
                workflowId,
                2,
                AdministrativeActionKinds.DirectFlow,
                201,
                null,
                "finishSomeChildren",
                null,
                candidate),
            BatchRequest(
                workflowId,
                2,
                AdministrativeActionKinds.TimerBoundary,
                401,
                6,
                AdministrativeActionMultiInstanceModes.ForceParent,
                null,
                candidate),
            BatchRequest(
                workflowId,
                2,
                AdministrativeActionKinds.TimerBoundary,
                401,
                999_999,
                null,
                null,
                candidate)
        };

        foreach (var request in requests)
        {
            await AssertCreateBatchBadRequestAsync(request, "mode-operator");
        }
    }

    [Fact]
    public async Task BatchReason_IsOptionalAndBoundedToOneThousandCharacters()
    {
        var workflowId = await CreateWorkflowAsync(CreateAdministrativeReturnModel());
        var instance = await StartAsync(
            workflowId,
            new Dictionary<string, JsonElement>
            {
                ["amount"] = JsonSerializer.SerializeToElement(500)
            });
        await MoveToApprovalAsync(instance.Id, "reason-reviewer");
        var candidate = Assert.Single(
            (await SearchCandidatesAsync(workflowId, 3, "reason-operator")).Items);
        var values = new Dictionary<string, JsonElement>
        {
            ["comment"] = JsonSerializer.SerializeToElement("valid comment")
        };

        var maximumReason = new string('r', AdministrativeActionConstraints.MaxReasonLength);
        AdministrativeActionBatchDetailDto accepted;
        using (var response = await SendAsync(
                   HttpMethod.Post,
                   "/api/administrative-action-batches",
                   BatchRequest(
                       workflowId,
                       3,
                       AdministrativeActionKinds.DirectFlow,
                       301,
                       null,
                       null,
                       values,
                       candidate,
                       maximumReason),
                   "reason-operator",
                   suppressImplicitAdmin: true))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            accepted = await ReadAsync<AdministrativeActionBatchDetailDto>(response);
        }
        Assert.Equal(maximumReason, accepted.Summary.Reason);

        await AssertCreateBatchBadRequestAsync(
            BatchRequest(
                workflowId,
                3,
                AdministrativeActionKinds.DirectFlow,
                301,
                null,
                null,
                values,
                candidate,
                maximumReason + "x"),
            "reason-operator");
    }

    [Fact]
    public async Task DirectVariables_RejectMissingRequiredAndPrepareTypeArrayAndValidationFailuresAsIneligible()
    {
        var model = CreateAdministrativeReturnModel();
        model.SequenceFlows.Single(flow => flow.Id == 301).Variables =
        [
            new VariableModel
            {
                Id = 30,
                Name = "comment",
                DataType = WorkflowVariableTypes.String,
                Required = true,
                Validation = "Len(comment) >= 5"
            },
            new VariableModel
            {
                Id = 31,
                Name = "tags",
                DataType = WorkflowVariableTypes.String,
                IsArray = true,
                Required = true
            }
        ];
        var workflowId = await CreateWorkflowAsync(model);
        var instance = await StartAsync(
            workflowId,
            new Dictionary<string, JsonElement>
            {
                ["amount"] = JsonSerializer.SerializeToElement(500)
            });
        await MoveToApprovalAsync(instance.Id, "variable-reviewer");
        var candidate = Assert.Single(
            (await SearchCandidatesAsync(workflowId, 3, "variable-operator")).Items);

        await AssertCreateBatchBadRequestAsync(
            BatchRequest(
                workflowId,
                3,
                AdministrativeActionKinds.DirectFlow,
                301,
                null,
                null,
                new Dictionary<string, JsonElement>
                {
                    ["comment"] = JsonSerializer.SerializeToElement("valid comment")
                },
                candidate),
            "variable-operator");

        var cases = new[]
        {
            (Values: new Dictionary<string, JsonElement>
            {
                ["comment"] = JsonSerializer.SerializeToElement(123),
                ["tags"] = JsonSerializer.SerializeToElement(new[] { "urgent" })
            }, ExpectedMessage: "Variable 'comment' must be"),
            (Values: new Dictionary<string, JsonElement>
            {
                ["comment"] = JsonSerializer.SerializeToElement("valid comment"),
                ["tags"] = JsonSerializer.SerializeToElement("urgent")
            }, ExpectedMessage: "Variable 'tags' must be"),
            (Values: new Dictionary<string, JsonElement>
            {
                ["comment"] = JsonSerializer.SerializeToElement("bad"),
                ["tags"] = JsonSerializer.SerializeToElement(new[] { "urgent" })
            }, ExpectedMessage: "Variable 'comment' failed validation")
        };

        foreach (var testCase in cases)
        {
            AdministrativeActionBatchDetailDto batch;
            using (var create = await SendAsync(
                       HttpMethod.Post,
                       "/api/administrative-action-batches",
                       BatchRequest(
                           workflowId,
                           3,
                           AdministrativeActionKinds.DirectFlow,
                           301,
                           null,
                           null,
                           testCase.Values,
                           candidate),
                       "variable-operator",
                       suppressImplicitAdmin: true))
            {
                Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
                batch = await ReadAsync<AdministrativeActionBatchDetailDto>(create);
            }

            await ProcessBatchJobAsync(batch.PreparationJobId!.Value);
            batch = await GetBatchAsync(batch.Summary.Id, "variable-operator");
            Assert.Equal(AdministrativeActionBatchStatuses.Ready, batch.Summary.Status);
            Assert.Equal(0, batch.Summary.EligibleItemCount);
            Assert.Equal(1, batch.Summary.IneligibleItemCount);

            var item = await GetSingleBatchItemAsync(batch.Summary.Id, "variable-operator");
            Assert.Equal(AdministrativeActionBatchItemStatuses.Ineligible, item.Status);
            Assert.Contains(
                testCase.ExpectedMessage,
                item.Issues?.GetRawText() ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task TimerBoundaryBatch_RejectsSubmittedVariables()
    {
        var workflowId = await CreateWorkflowAsync(CreateMultiInstanceTimerModel());
        _ = await StartAsync(workflowId, null);
        var candidate = Assert.Single(
            (await SearchCandidatesAsync(workflowId, 2, "timer-variable-operator")).Items);

        await AssertCreateBatchBadRequestAsync(
            BatchRequest(
                workflowId,
                2,
                AdministrativeActionKinds.TimerBoundary,
                401,
                6,
                null,
                new Dictionary<string, JsonElement>
                {
                    ["ignored"] = JsonSerializer.SerializeToElement("not allowed")
                },
                candidate),
            "timer-variable-operator");
    }

    [Fact]
    public async Task TimerBoundaryBatch_BecomesIneligibleWhenFrozenSubscriptionTurnsTerminal()
    {
        var workflowId = await CreateWorkflowAsync(CreateMultiInstanceTimerModel());
        _ = await StartAsync(workflowId, null);
        var candidate = Assert.Single(
            (await SearchCandidatesAsync(workflowId, 2, "terminal-timer-operator")).Items);
        var timer = Assert.Single(candidate.TimerBoundaries);
        Assert.Equal(TimerSubscriptionStatuses.Active, timer.Status);

        AdministrativeActionBatchDetailDto batch;
        using (var create = await SendAsync(
                   HttpMethod.Post,
                   "/api/administrative-action-batches",
                   BatchRequest(
                       workflowId,
                       2,
                       AdministrativeActionKinds.TimerBoundary,
                       401,
                       6,
                       null,
                       null,
                       candidate),
                   "terminal-timer-operator",
                   suppressImplicitAdmin: true))
        {
            Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
            batch = await ReadAsync<AdministrativeActionBatchDetailDto>(create);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var subscription = await db.TimerSubscriptions.SingleAsync(
                item => item.Id == timer.TimerSubscriptionId);
            var completedAt = subscription.UpdatedAt.AddTicks(10);
            subscription.Status = TimerSubscriptionStatuses.Completed;
            subscription.CompletedAt = completedAt;
            subscription.UpdatedAt = completedAt;
            await db.SaveChangesAsync();
        }

        await ProcessBatchJobAsync(batch.PreparationJobId!.Value);
        batch = await GetBatchAsync(batch.Summary.Id, "terminal-timer-operator");
        Assert.Equal(AdministrativeActionBatchStatuses.Ready, batch.Summary.Status);
        Assert.Equal(0, batch.Summary.EligibleItemCount);
        Assert.Equal(1, batch.Summary.IneligibleItemCount);
        var item = await GetSingleBatchItemAsync(
            batch.Summary.Id,
            "terminal-timer-operator");
        Assert.Equal(AdministrativeActionBatchItemStatuses.Ineligible, item.Status);
    }

    [Fact]
    public async Task Preparation_RechecksAffectedTaskCapAfterMultiInstanceFanOutGrows()
    {
        EngineSettingRecord? previousSetting = null;
        try
        {
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var settings = scope.ServiceProvider.GetRequiredService<IEngineSettingsRepository>();
                previousSetting = await settings.GetByKeyAsync(
                    AdministrativeActionConstraints.BatchMaxAffectedTasksSetting,
                    CancellationToken.None);
                await settings.SetAsync(
                    AdministrativeActionConstraints.BatchMaxAffectedTasksSetting,
                    "2",
                    CancellationToken.None);
            }

            var workflowId = await CreateWorkflowAsync(CreateMultiInstanceTimerModel());
            var instance = await StartAsync(workflowId, null);
            var candidate = Assert.Single(
                (await SearchCandidatesAsync(workflowId, 2, "cap-operator")).Items);
            Assert.Equal(2, candidate.AffectedTaskCount);

            AdministrativeActionBatchDetailDto batch;
            using (var create = await SendAsync(
                       HttpMethod.Post,
                       "/api/administrative-action-batches",
                       BatchRequest(
                           workflowId,
                           2,
                           AdministrativeActionKinds.DirectFlow,
                           201,
                           null,
                           AdministrativeActionMultiInstanceModes.ForceParent,
                           null,
                           candidate),
                       "cap-operator",
                       suppressImplicitAdmin: true))
            {
                Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
                batch = await ReadAsync<AdministrativeActionBatchDetailDto>(create);
            }
            Assert.Equal(2, batch.Summary.TotalAffectedTaskCount);

            await AddUnfinishedMultiInstanceChildWithoutTouchingPositionAsync(instance.Id);
            await ProcessBatchJobAsync(batch.PreparationJobId!.Value);

            batch = await GetBatchAsync(batch.Summary.Id, "cap-operator");
            Assert.Equal(AdministrativeActionBatchStatuses.Failed, batch.Summary.Status);
            Assert.Equal(3, batch.Summary.TotalAffectedTaskCount);
            Assert.Equal(1, batch.Summary.FailedItemCount);
            Assert.Contains(
                "affected_task_limit_exceeded",
                batch.Issues?.GetRawText() ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<IEngineSettingsRepository>();
            if (previousSetting is null)
            {
                await settings.DeleteAsync(
                    AdministrativeActionConstraints.BatchMaxAffectedTasksSetting,
                    CancellationToken.None);
            }
            else
            {
                await settings.SetAsync(
                    AdministrativeActionConstraints.BatchMaxAffectedTasksSetting,
                    previousSetting.Value,
                    CancellationToken.None);
            }
        }
    }

    private static CreateAdministrativeActionBatchRequest BatchRequest(
        long workflowDefinitionId,
        int sourceNodeId,
        string actionKind,
        int flowId,
        int? boundaryNodeId,
        string? multiInstanceMode,
        Dictionary<string, JsonElement>? variables,
        AdministrativeActionCandidateDto candidate,
        string? reason = null) =>
        new(
            workflowDefinitionId,
            sourceNodeId,
            actionKind,
            flowId,
            boundaryNodeId,
            multiInstanceMode,
            reason,
            variables,
            new AdministrativeActionBatchSelectionDto(
                AdministrativeActionBatchSelectionModes.Explicit,
                [new AdministrativeActionPositionReferenceDto(
                    candidate.PositionKind,
                    candidate.PositionId)],
                null,
                null),
            $"contract-{Guid.NewGuid():N}");

    private async Task AssertCreateBatchBadRequestAsync(
        CreateAdministrativeActionBatchRequest request,
        string user)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/administrative-action-batches",
            request,
            user,
            suppressImplicitAdmin: true);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<AdministrativeActionBatchItemDto> GetSingleBatchItemAsync(
        long batchId,
        string user)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/administrative-action-batches/{batchId}/items?page=1&pageSize=20",
            user: user,
            suppressImplicitAdmin: true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.Single(
            (await ReadAsync<PagedResult<AdministrativeActionBatchItemDto>>(response)).Items);
    }

    private async Task AddUnfinishedMultiInstanceChildWithoutTouchingPositionAsync(
        long instanceId)
    {
        await using var db = fixture.CreateDbContext();
        var execution = await db.MultiInstanceExecutions.SingleAsync(
            item => item.InstanceId == instanceId);
        var template = await db.UserTasks
            .Where(task => task.MultiInstanceExecutionId == execution.Id)
            .OrderBy(task => task.ItemIndex)
            .FirstAsync();
        var nextIndex = await db.UserTasks
            .Where(task => task.MultiInstanceExecutionId == execution.Id)
            .MaxAsync(task => task.ItemIndex) + 1;
        var now = DateTimeOffset.UtcNow;
        var child = new UserTaskEntity
        {
            InstanceId = template.InstanceId,
            TokenId = template.TokenId,
            NodeId = template.NodeId,
            NodeName = template.NodeName,
            NodeExternalId = template.NodeExternalId,
            Roles = [.. template.Roles],
            RequiresClaim = template.RequiresClaim,
            RequiresAssignment = template.RequiresAssignment,
            Status = UserTaskStatuses.Active,
            MultiInstanceExecutionId = execution.Id,
            ItemIndex = nextIndex,
            Assignee = template.Assignee,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.UserTasks.Add(child);
        db.NodeExecutions.Add(new NodeExecutionEntity
        {
            InstanceId = template.InstanceId,
            WorkflowDefinitionId = await db.WorkflowInstances
                .Where(instance => instance.Id == instanceId)
                .Select(instance => instance.WorkflowDefinitionId)
                .SingleAsync(),
            ExecutionTokenId = template.TokenId,
            UserTask = child,
            MultiInstanceExecutionId = execution.Id,
            ItemIndex = nextIndex,
            NodeId = template.NodeId,
            NodeName = template.NodeName,
            NodeExternalId = template.NodeExternalId,
            NodeType = BpmnFlowNodeTypes.UserTask,
            ExecutionKind = NodeExecutionKinds.UserTaskItem,
            Status = NodeExecutionStatuses.Active,
            CreatedAt = now,
            StartedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
    }

    private async Task<PagedResult<AdministrativeActionCandidateDto>> SearchCandidatesAsync(
        long workflowId,
        int nodeId,
        string user)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/administrative-actions/candidates/search",
            new AdministrativeActionCandidateSearchRequest
            {
                WorkflowDefinitionId = workflowId,
                SourceNodeId = nodeId,
                IncludeVariables = true,
                Page = 1,
                PageSize = 20
            },
            user,
            suppressImplicitAdmin: true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<PagedResult<AdministrativeActionCandidateDto>>(response);
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

    private async Task<InstanceDetailDto> StartAsync(
        long workflowId,
        Dictionary<string, JsonElement>? variables)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, variables),
            "starter");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<InstanceDetailDto>(response);
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
        return Assert.Single((await ReadAsync<PagedResult<UserTaskDto>>(response)).Items);
    }

    private async Task<AdministrativeActionBatchDetailDto> GetBatchAsync(
        long batchId,
        string user)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/administrative-action-batches/{batchId}",
            user: user,
            suppressImplicitAdmin: true);
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

    private async Task<InstanceDetailDto> GetInstanceAsync(long instanceId)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/instances/{instanceId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<InstanceDetailDto>(response);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "test-admin",
        string[]? roles = null,
        bool suppressImplicitAdmin = false)
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
                new FlowNodeModel { Id = 4, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, Name = "Begin", SourceRef = 1, TargetRef = 2 },
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
                    Condition = "amount > 1000",
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

    private static WorkflowModel CreateMultiInstanceTimerModel()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = $"administrative-mi-timer-{suffix}",
            Name = $"Administrative MI timer {suffix}",
            InitialEventId = 1,
            Variables =
            [
                new VariableModel
                {
                    Id = 1,
                    Name = "approvalResults",
                    DataType = WorkflowVariableTypes.Json,
                    DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<object>())
                }
            ],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Parallel approvals",
                    Type = BpmnFlowNodeTypes.UserTask,
                    Roles = ["Approver"],
                    MultiInstance = new MultiInstanceModel
                    {
                        Mode = MultiInstanceModes.Parallel,
                        Source = MultiInstanceSources.Cardinality,
                        CardinalityExpression = "2",
                        CompletionEvaluation = MultiInstanceCompletionEvaluations.AfterAll,
                        ResultVariable = "approvalResults"
                    }
                },
                new FlowNodeModel { Id = 3, Name = "Approved", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 4, Name = "Fallback", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 5, Name = "Escalated", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel
                {
                    Id = 6,
                    Name = "Approval deadline",
                    Type = BpmnFlowNodeTypes.TimerBoundaryEvent,
                    AttachedToRef = 2,
                    CancelActivity = false,
                    Timer = new TimerDefinitionModel { TimeDuration = "P2D" }
                }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, Name = "Begin", SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel
                {
                    Id = 201,
                    Name = "Approve",
                    SourceRef = 2,
                    TargetRef = 3,
                    Roles = ["Approver"],
                    CompletionCondition = "CountFlow(201) == 2",
                    CompletionPriority = 1
                },
                new SequenceFlowModel
                {
                    Id = 202,
                    Name = "No outcome",
                    SourceRef = 2,
                    TargetRef = 4,
                    IsDefault = true,
                    IsSelectable = false
                },
                new SequenceFlowModel
                {
                    Id = 401,
                    Name = "Escalate now",
                    SourceRef = 6,
                    TargetRef = 5
                }
            ]
        };
    }
}
