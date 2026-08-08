using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class AdministrativeActionBatchLifecycleApiTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task CreateBatch_IdempotencyReplaysSameDraftAndRejectsDifferentPayload()
    {
        var workflowId = await CreateWorkflowAsync(CreateOrdinaryBatchModel());
        await StartAsync(workflowId, "idempotency-instance");
        var candidate = Assert.Single((await SearchCandidatesAsync(workflowId)).Items);
        var idempotencyKey = $"batch-replay-{Guid.NewGuid():N}";
        var request = DirectRequest(
            workflowId,
            ExplicitSelection(candidate),
            idempotencyKey,
            reason: "Replay this exact request");

        var first = await CreateBatchAsync(request, "idempotent-operator");
        var replay = await CreateBatchAsync(request, "idempotent-operator");

        Assert.Equal(first.Summary.Id, replay.Summary.Id);
        Assert.Equal(first.PreparationJobId, replay.PreparationJobId);
        Assert.Equal(first.Summary.TotalItemCount, replay.Summary.TotalItemCount);

        using (var conflict = await SendAsync(
                   HttpMethod.Post,
                   "/api/administrative-action-batches",
                   request with { Reason = "A materially different replay" },
                   "idempotent-operator"))
        {
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        }

        await CancelBatchAsync(first.Summary.Id, "idempotent-operator", "test cleanup");
        await ProcessBatchJobAsync(first.PreparationJobId!.Value);
    }

    [Fact]
    public async Task AllMatching_FreezesSelectionHonorsExclusionsAndEnforcesAffectedTaskCap()
    {
        var originalLimit = await SetMaxAffectedTasksAsync("2");
        try
        {
            var workflowId = await CreateWorkflowAsync(CreateOrdinaryBatchModel());
            var initialInstances = new[]
            {
                await StartAsync(workflowId, "frozen-1"),
                await StartAsync(workflowId, "frozen-2"),
                await StartAsync(workflowId, "frozen-3")
            };
            var candidates = (await SearchCandidatesAsync(workflowId)).Items;
            Assert.Equal(3, candidates.Count);
            var excluded = candidates[0];
            var selection = new AdministrativeActionBatchSelectionDto(
                AdministrativeActionBatchSelectionModes.AllMatching,
                null,
                new AdministrativeActionCandidateSearchRequest
                {
                    WorkflowDefinitionId = workflowId,
                    SourceNodeId = 2,
                    Page = 1,
                    PageSize = 200
                },
                [new AdministrativeActionPositionReferenceDto(
                    excluded.PositionKind,
                    excluded.PositionId)]);

            var batch = await CreateBatchAsync(
                DirectRequest(workflowId, selection, $"freeze-{Guid.NewGuid():N}"),
                "freeze-operator");
            Assert.Equal(2, batch.Summary.TotalItemCount);
            Assert.Equal(2, batch.Summary.TotalAffectedTaskCount);

            var lateInstance = await StartAsync(workflowId, "frozen-late");
            await ProcessBatchJobAsync(batch.PreparationJobId!.Value);
            batch = await GetBatchAsync(batch.Summary.Id, "freeze-operator");
            Assert.Equal(AdministrativeActionBatchStatuses.Ready, batch.Summary.Status);
            Assert.Equal(2, batch.Summary.EligibleItemCount);

            batch = await ConfirmBatchAsync(batch, "freeze-operator");
            await ProcessBatchJobAsync(batch.ExecutionJobId!.Value);
            batch = await GetBatchAsync(batch.Summary.Id, "freeze-operator");
            Assert.Equal(AdministrativeActionBatchStatuses.Completed, batch.Summary.Status);
            Assert.Equal(2, batch.Summary.SucceededItemCount);

            await using (var db = fixture.CreateDbContext())
            {
                var statuses = await db.UserTasks
                    .Where(task => initialInstances.Select(instance => instance.Id)
                        .Append(lateInstance.Id)
                        .Contains(task.InstanceId))
                    .ToDictionaryAsync(task => task.InstanceId, task => task.Status);
                Assert.Equal(UserTaskRecordStatuses.Active, statuses[excluded.InstanceId]);
                Assert.Equal(UserTaskRecordStatuses.Active, statuses[lateInstance.Id]);
                Assert.All(
                    candidates.Where(candidate => candidate.PositionId != excluded.PositionId),
                    selected => Assert.Equal(
                        UserTaskRecordStatuses.Completed,
                        statuses[selected.InstanceId]));
            }

            var multiInstanceWorkflowId = await CreateWorkflowAsync(CreateMultiInstanceCapModel());
            await StartAsync(multiInstanceWorkflowId, "affected-cap");
            var multiInstance = Assert.Single(
                (await SearchCandidatesAsync(multiInstanceWorkflowId)).Items);
            Assert.Equal(
                AdministrativeActionPositionKinds.MultiInstanceExecution,
                multiInstance.PositionKind);
            Assert.Equal(3, multiInstance.AffectedTaskCount);

            using var overLimit = await SendAsync(
                HttpMethod.Post,
                "/api/administrative-action-batches",
                DirectRequest(
                    multiInstanceWorkflowId,
                    ExplicitSelection(multiInstance),
                    $"over-cap-{Guid.NewGuid():N}",
                    multiInstanceMode: AdministrativeActionMultiInstanceModes.ForceParent),
                "cap-operator");
            Assert.Equal(HttpStatusCode.BadRequest, overLimit.StatusCode);
        }
        finally
        {
            await SetMaxAffectedTasksAsync(originalLimit);
        }
    }

    [Fact]
    public async Task PreparedItems_ExecuteIndependentlyWhenNormalActionRacesBatch()
    {
        var workflowId = await CreateWorkflowAsync(CreateOrdinaryBatchModel());
        await StartAsync(workflowId, "race-1");
        await StartAsync(workflowId, "race-2");
        var candidates = (await SearchCandidatesAsync(workflowId)).Items;
        Assert.Equal(2, candidates.Count);
        var raced = candidates[0];
        var unaffected = candidates[1];

        var batch = await CreateBatchAsync(
            DirectRequest(
                workflowId,
                ExplicitSelection(candidates),
                $"race-{Guid.NewGuid():N}"),
            "race-operator");
        await ProcessBatchJobAsync(batch.PreparationJobId!.Value);
        batch = await GetBatchAsync(batch.Summary.Id, "race-operator");
        Assert.Equal(2, batch.Summary.EligibleItemCount);
        batch = await ConfirmBatchAsync(batch, "race-operator");

        var manualAction = TakeNormalFlowAsync(raced.UserTaskId!.Value, "normal-racer");
        var batchExecution = ProcessBatchJobAsync(batch.ExecutionJobId!.Value);
        await Task.WhenAll(manualAction, batchExecution);
        var manualStatus = await manualAction;
        Assert.True(
            manualStatus is HttpStatusCode.OK or HttpStatusCode.Conflict,
            $"Unexpected normal-action response: {(int)manualStatus} {manualStatus}.");

        batch = await GetBatchAsync(batch.Summary.Id, "race-operator");
        var items = await GetBatchItemsAsync(batch.Summary.Id, "race-operator");
        var racedItem = Assert.Single(items, item => item.PositionId == raced.PositionId);
        var unaffectedItem = Assert.Single(items, item => item.PositionId == unaffected.PositionId);
        Assert.Equal(AdministrativeActionBatchItemStatuses.Succeeded, unaffectedItem.Status);
        Assert.Equal(
            manualStatus == HttpStatusCode.OK
                ? AdministrativeActionBatchItemStatuses.Skipped
                : AdministrativeActionBatchItemStatuses.Succeeded,
            racedItem.Status);
        Assert.Equal(2, batch.Summary.SucceededItemCount + batch.Summary.SkippedItemCount);

        await using var db = fixture.CreateDbContext();
        foreach (var candidate in candidates)
        {
            var occurrence = Assert.Single(await db.SequenceFlowOccurrences
                .Where(item => item.InstanceId == candidate.InstanceId
                               && item.SequenceFlowId == 201
                               && item.IsTraversal)
                .ToListAsync());
            Assert.Equal(
                candidate.PositionId == raced.PositionId && manualStatus == HttpStatusCode.OK
                    ? "userTaskAction"
                    : NodeExecutionCompletionReasons.AdministrativeAction,
                occurrence.Kind);
        }
    }

    [Fact]
    public async Task ExplicitStalePosition_IsRetainedAndShownAsIneligible()
    {
        var workflowId = await CreateWorkflowAsync(CreateOrdinaryBatchModel());
        await StartAsync(workflowId, "stale-explicit");
        var candidate = Assert.Single((await SearchCandidatesAsync(workflowId)).Items);
        Assert.Equal(
            HttpStatusCode.OK,
            await TakeNormalFlowAsync(candidate.UserTaskId!.Value, "normal-before-freeze"));

        var batch = await CreateBatchAsync(
            DirectRequest(
                workflowId,
                ExplicitSelection(candidate),
                $"stale-{Guid.NewGuid():N}"),
            "stale-operator");
        Assert.Equal(1, batch.Summary.TotalItemCount);
        await ProcessBatchJobAsync(batch.PreparationJobId!.Value);
        batch = await GetBatchAsync(batch.Summary.Id, "stale-operator");

        Assert.Equal(AdministrativeActionBatchStatuses.Ready, batch.Summary.Status);
        Assert.Equal(0, batch.Summary.EligibleItemCount);
        Assert.Equal(1, batch.Summary.IneligibleItemCount);
        var item = Assert.Single(await GetBatchItemsAsync(batch.Summary.Id, "stale-operator"));
        Assert.Equal(AdministrativeActionBatchItemStatuses.Ineligible, item.Status);
        Assert.NotNull(item.Issues);

        batch = await ConfirmBatchAsync(batch, "stale-operator");
        Assert.Equal(AdministrativeActionBatchStatuses.CompletedWithIssues, batch.Summary.Status);
        Assert.Null(batch.ExecutionJobId);
    }

    [Fact]
    public async Task ConfirmationAndCancellation_AreIdempotentAndDoNotMoveUnstartedPositions()
    {
        var workflowId = await CreateWorkflowAsync(CreateOrdinaryBatchModel());
        var instances = new[]
        {
            await StartAsync(workflowId, "cancel-1"),
            await StartAsync(workflowId, "cancel-2")
        };
        var candidates = (await SearchCandidatesAsync(workflowId)).Items;
        var batch = await CreateBatchAsync(
            DirectRequest(
                workflowId,
                ExplicitSelection(candidates),
                $"cancel-{Guid.NewGuid():N}"),
            "cancel-operator");
        await ProcessBatchJobAsync(batch.PreparationJobId!.Value);
        batch = await GetBatchAsync(batch.Summary.Id, "cancel-operator");
        var confirmation = new ConfirmAdministrativeActionBatchRequest(
            batch.Summary.EligibleItemCount,
            batch.Summary.TotalAffectedTaskCount,
            batch.Summary.UpdatedAt);

        var firstConfirmation = await ConfirmBatchAsync(
            batch.Summary.Id,
            confirmation,
            "cancel-operator");
        var replayedConfirmation = await ConfirmBatchAsync(
            batch.Summary.Id,
            confirmation,
            "cancel-operator");
        Assert.Equal(firstConfirmation.ExecutionJobId, replayedConfirmation.ExecutionJobId);
        Assert.Equal(AdministrativeActionBatchStatuses.Queued, replayedConfirmation.Summary.Status);

        var cancelled = await CancelBatchAsync(
            batch.Summary.Id,
            "cancel-operator",
            "Do not execute this selection");
        var cancellationReplay = await CancelBatchAsync(
            batch.Summary.Id,
            "cancel-operator",
            "This replay must not replace the first reason");
        Assert.Equal(AdministrativeActionBatchStatuses.Cancelled, cancellationReplay.Summary.Status);
        Assert.Equal("Do not execute this selection", cancellationReplay.CancellationReason);
        Assert.Equal(2, cancellationReplay.Summary.CancelledItemCount);
        Assert.All(
            await GetBatchItemsAsync(batch.Summary.Id, "cancel-operator"),
            item => Assert.Equal(AdministrativeActionBatchItemStatuses.Cancelled, item.Status));

        await ProcessBatchJobAsync(cancelled.ExecutionJobId!.Value);
        await using var db = fixture.CreateDbContext();
        var tasks = await db.UserTasks
            .Where(task => instances.Select(instance => instance.Id).Contains(task.InstanceId))
            .ToListAsync();
        Assert.Equal(2, tasks.Count);
        Assert.All(tasks, task => Assert.Equal(UserTaskRecordStatuses.Active, task.Status));
    }

    [Fact]
    public async Task ExecutionRetry_ResumesQueuedItemsWithoutRepeatingCommittedTransition()
    {
        var workflowId = await CreateWorkflowAsync(CreateOrdinaryBatchModel());
        await StartAsync(workflowId, "resume-1");
        await StartAsync(workflowId, "resume-2");
        var candidates = (await SearchCandidatesAsync(workflowId)).Items;
        var batch = await CreateBatchAsync(
            DirectRequest(
                workflowId,
                ExplicitSelection(candidates),
                $"resume-{Guid.NewGuid():N}"),
            "resume-operator");
        await ProcessBatchJobAsync(batch.PreparationJobId!.Value);
        batch = await GetBatchAsync(batch.Summary.Id, "resume-operator");
        batch = await ConfirmBatchAsync(batch, "resume-operator");

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider
                .GetRequiredService<IAdministrativeActionBatchRepository>();
            var record = await repository.GetAsync(batch.Summary.Id, false, CancellationToken.None)
                ?? throw new InvalidOperationException("Batch disappeared.");
            var queued = await repository.ListItemsForProcessingAsync(
                record.Id,
                [AdministrativeActionBatchItemStatuses.Queued],
                100,
                CancellationToken.None);
            var committed = queued[0];
            var engine = scope.ServiceProvider.GetRequiredService<IWorkflowEngineService>();
            var result = await engine.ExecuteAdministrativeBatchActionAsync(
                BuildRequest(record, committed),
                new ActorContext(
                    "resume-operator",
                    [],
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
                CancellationToken.None);
            Assert.NotNull(result);
        }

        await ProcessBatchJobAsync(batch.ExecutionJobId!.Value);
        batch = await GetBatchAsync(batch.Summary.Id, "resume-operator");
        Assert.Equal(AdministrativeActionBatchStatuses.Completed, batch.Summary.Status);
        Assert.Equal(2, batch.Summary.SucceededItemCount);

        var completedItems = await GetBatchItemsAsync(batch.Summary.Id, "resume-operator");
        Assert.All(completedItems, item =>
        {
            var result = Assert.IsType<JsonElement>(item.Result);
            Assert.Equal(
                WorkflowInstanceStatuses.Completed,
                result.GetProperty("instanceStatus").GetString());
            var positions = result.GetProperty("executionPositions").EnumerateArray().ToArray();
            var position = Assert.Single(positions);
            Assert.Equal(4, position.GetProperty("NodeId").GetInt32());
            Assert.NotEqual(3, position.GetProperty("NodeId").GetInt32());
            Assert.Equal(4, result.GetProperty("completion").GetProperty("NodeId").GetInt32());
        });

        await using var db = fixture.CreateDbContext();
        foreach (var candidate in candidates)
        {
            Assert.Single(await db.SequenceFlowOccurrences
                .Where(item => item.InstanceId == candidate.InstanceId
                               && item.SequenceFlowId == 201
                               && item.IsTraversal)
                .ToListAsync());
        }
    }

    private static AdministrativeActionRequest BuildRequest(
        AdministrativeActionBatchRecord batch,
        AdministrativeActionBatchItemRecord item) =>
        new()
        {
            BatchId = batch.Id,
            BatchItemId = item.Id,
            ExpectedWorkflowDefinitionId = item.WorkflowDefinitionId,
            SourceNodeId = item.SourceNodeId,
            ActionKind = batch.ActionKind,
            FlowId = item.FlowId,
            BoundaryNodeId = batch.BoundaryNodeId,
            MultiInstanceMode = batch.MultiInstanceMode,
            PositionKind = item.PositionKind,
            PositionId = item.PositionId,
            UserTaskId = item.UserTaskId,
            MultiInstanceExecutionId = item.MultiInstanceExecutionId,
            ExpectedTokenId = item.TokenId,
            ExpectedTokenActivationId = item.TokenActivationId,
            ExpectedPositionUpdatedAt = item.CapturedPositionUpdatedAt,
            ExpectedTimerSubscriptionId = item.TimerSubscriptionId,
            ExpectedTimerJobId = item.TimerJobId,
            ExpectedTimerOccurrence = item.CapturedTimerOccurrence,
            ExpectedTimerStatus = item.CapturedTimerStatus,
            ExpectedTimerSubscriptionUpdatedAt = item.CapturedTimerSubscriptionUpdatedAt,
            Reason = batch.Reason,
            Variables = batch.CommonVariables.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Clone(),
                StringComparer.OrdinalIgnoreCase)
        };

    private static CreateAdministrativeActionBatchRequest DirectRequest(
        long workflowId,
        AdministrativeActionBatchSelectionDto selection,
        string idempotencyKey,
        string? reason = null,
        string? multiInstanceMode = null) =>
        new(
            workflowId,
            2,
            AdministrativeActionKinds.DirectFlow,
            201,
            null,
            multiInstanceMode,
            reason,
            null,
            selection,
            idempotencyKey);

    private static AdministrativeActionBatchSelectionDto ExplicitSelection(
        params AdministrativeActionCandidateDto[] candidates) =>
        ExplicitSelection((IReadOnlyCollection<AdministrativeActionCandidateDto>)candidates);

    private static AdministrativeActionBatchSelectionDto ExplicitSelection(
        IReadOnlyCollection<AdministrativeActionCandidateDto> candidates) =>
        new(
            AdministrativeActionBatchSelectionModes.Explicit,
            candidates.Select(candidate => new AdministrativeActionPositionReferenceDto(
                    candidate.PositionKind,
                    candidate.PositionId))
                .ToArray(),
            null,
            null);

    private async Task<AdministrativeActionBatchDetailDto> CreateBatchAsync(
        CreateAdministrativeActionBatchRequest request,
        string user)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/administrative-action-batches",
            request,
            user);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await ReadAsync<AdministrativeActionBatchDetailDto>(response);
    }

    private async Task<AdministrativeActionBatchDetailDto> ConfirmBatchAsync(
        AdministrativeActionBatchDetailDto batch,
        string user) =>
        await ConfirmBatchAsync(
            batch.Summary.Id,
            new ConfirmAdministrativeActionBatchRequest(
                batch.Summary.EligibleItemCount,
                batch.Summary.TotalAffectedTaskCount,
                batch.Summary.UpdatedAt),
            user);

    private async Task<AdministrativeActionBatchDetailDto> ConfirmBatchAsync(
        long batchId,
        ConfirmAdministrativeActionBatchRequest request,
        string user)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/administrative-action-batches/{batchId}/confirm",
            request,
            user);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<AdministrativeActionBatchDetailDto>(response);
    }

    private async Task<AdministrativeActionBatchDetailDto> CancelBatchAsync(
        long batchId,
        string user,
        string reason)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/administrative-action-batches/{batchId}/cancel",
            new CancelAdministrativeActionBatchRequest(reason),
            user);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<AdministrativeActionBatchDetailDto>(response);
    }

    private async Task<AdministrativeActionBatchDetailDto> GetBatchAsync(
        long batchId,
        string user)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/administrative-action-batches/{batchId}",
            user: user);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<AdministrativeActionBatchDetailDto>(response);
    }

    private async Task<IReadOnlyList<AdministrativeActionBatchItemDto>> GetBatchItemsAsync(
        long batchId,
        string user)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/administrative-action-batches/{batchId}/items?page=1&pageSize=200",
            user: user);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadAsync<PagedResult<AdministrativeActionBatchItemDto>>(response)).Items;
    }

    private async Task<PagedResult<AdministrativeActionCandidateDto>> SearchCandidatesAsync(
        long workflowId)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/administrative-actions/candidates/search",
            new AdministrativeActionCandidateSearchRequest
            {
                WorkflowDefinitionId = workflowId,
                SourceNodeId = 2,
                Page = 1,
                PageSize = 200
            },
            "candidate-reader");
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

    private async Task<InstanceDetailDto> StartAsync(long workflowId, string businessKey)
    {
        _ = businessKey; // Human-readable test label; workflow starts do not require a business key.
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, null),
            "starter");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<InstanceDetailDto>(response);
    }

    private async Task<HttpStatusCode> TakeNormalFlowAsync(long userTaskId, string user)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{userTaskId}/flows/201",
            new TakeFlowRequest(null),
            user,
            ["Worker"]);
        return response.StatusCode;
    }

    private async Task<string> SetMaxAffectedTasksAsync(string value)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<IEngineSettingsRepository>();
        var existing = await settings.GetByKeyAsync(
            AdministrativeActionConstraints.BatchMaxAffectedTasksSetting,
            CancellationToken.None);
        await settings.SetAsync(
            AdministrativeActionConstraints.BatchMaxAffectedTasksSetting,
            value,
            CancellationToken.None);
        return existing?.Value ?? AdministrativeActionConstraints.MaxAffectedTasks.ToString();
    }

    private async Task ProcessBatchJobAsync(long jobId)
    {
        await using (var db = fixture.CreateDbContext())
        {
            var job = await db.WorkflowJobs.SingleAsync(entity => entity.Id == jobId);
            job.Priority = 1_000_000;
            job.DueAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>();
        var leases = await repository.LeaseRunnableAsync(
            new WorkflowJobLeaseRequest(
                $"administrative-lifecycle-test-{Guid.NewGuid():N}",
                MaxCount: 1,
                MaxActivityCount: 1,
                MaxPerInstance: 1,
                LeaseDuration: TimeSpan.FromMinutes(2)),
            CancellationToken.None);
        var lease = Assert.Single(leases);
        Assert.Equal(jobId, lease.Job.Id);
        await scope.ServiceProvider
            .GetRequiredService<IWorkflowJobProcessor>()
            .ProcessAsync(lease, CancellationToken.None);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "test-operator",
        string[]? roles = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, user, roles ?? []);
        request.Headers.TryAddWithoutValidation("X-Test-Suppress-Admin", "true");
        return await fixture.Client.SendAsync(request);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException("Response body was empty.");

    private static WorkflowModel CreateOrdinaryBatchModel()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = $"administrative-batch-lifecycle-{suffix}",
            Name = $"Administrative batch lifecycle {suffix}",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Wait for action",
                    Type = BpmnFlowNodeTypes.UserTask,
                    Roles = ["Worker"]
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Observe exact traversal",
                    Type = BpmnFlowNodeTypes.ExclusiveGateway
                },
                new FlowNodeModel { Id = 4, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 5, Name = "Fallback", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, Name = "Begin", SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel
                {
                    Id = 201,
                    Name = "Finish selected position",
                    SourceRef = 2,
                    TargetRef = 3,
                    Roles = ["Worker"]
                },
                new SequenceFlowModel
                {
                    Id = 301,
                    Name = "Observed",
                    SourceRef = 3,
                    TargetRef = 4,
                    Condition = "FlowInfo(201, 'traversals.count') >= 1",
                    ConditionPriority = 1
                },
                new SequenceFlowModel
                {
                    Id = 302,
                    Name = "Fallback",
                    SourceRef = 3,
                    TargetRef = 5,
                    IsDefault = true
                }
            ]
        };
    }

    private static WorkflowModel CreateMultiInstanceCapModel()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = $"administrative-affected-cap-{suffix}",
            Name = $"Administrative affected cap {suffix}",
            InitialEventId = 1,
            Variables =
            [
                new VariableModel
                {
                    Id = 1,
                    Name = "results",
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
                    Name = "Three approvals",
                    Type = BpmnFlowNodeTypes.UserTask,
                    Roles = ["Approver"],
                    MultiInstance = new MultiInstanceModel
                    {
                        Mode = MultiInstanceModes.Parallel,
                        Source = MultiInstanceSources.Cardinality,
                        CardinalityExpression = "3",
                        CompletionEvaluation = MultiInstanceCompletionEvaluations.AfterAll,
                        ResultVariable = "results"
                    }
                },
                new FlowNodeModel { Id = 3, Name = "Selected", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 4, Name = "Fallback", Type = BpmnFlowNodeTypes.EndEvent }
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
                    CompletionCondition = "CountFlow(201) >= 3",
                    CompletionPriority = 1
                },
                new SequenceFlowModel
                {
                    Id = 202,
                    Name = "No result",
                    SourceRef = 2,
                    TargetRef = 4,
                    IsDefault = true,
                    IsSelectable = false
                }
            ]
        };
    }
}
