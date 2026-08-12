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
public sealed class AdministrativeActionRuntimeModeApiTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(
        AdministrativeActionMultiInstanceModes.ForceParent,
        MultiInstanceRecordStatuses.Interrupted,
        UserTaskRecordStatuses.Cancelled)]
    [InlineData(
        AdministrativeActionMultiInstanceModes.CompleteAllChildren,
        MultiInstanceRecordStatuses.Completed,
        UserTaskRecordStatuses.Completed)]
    public async Task DirectMultiInstanceBatch_ClosesEveryChildAndTraversesSelectedFlowOnce(
        string mode,
        string expectedExecutionStatus,
        string expectedTaskStatus)
    {
        var workflowId = await CreateWorkflowAsync(CreateMultiInstanceDirectModel());
        var instance = await StartAsync(workflowId);
        var candidate = await GetSingleCandidateAsync(workflowId, 2, "mi-operator");

        Assert.Equal(
            AdministrativeActionPositionKinds.MultiInstanceExecution,
            candidate.PositionKind);
        Assert.Equal(3, candidate.AffectedTaskCount);

        var batch = await CreateAndExecuteBatchAsync(
            workflowId,
            sourceNodeId: 2,
            AdministrativeActionKinds.DirectFlow,
            flowId: 201,
            boundaryNodeId: null,
            multiInstanceMode: mode,
            reason: $"Administrative multi-instance {mode}",
            candidate,
            "mi-operator");

        Assert.Equal(AdministrativeActionBatchStatuses.Completed, batch.Summary.Status);
        Assert.Equal(1, batch.Summary.SucceededItemCount);
        Assert.Equal(3, batch.Summary.TotalAffectedTaskCount);

        await using var db = fixture.CreateDbContext();
        var execution = await db.MultiInstanceExecutions.SingleAsync(
            item => item.InstanceId == instance.Id);
        Assert.Equal(expectedExecutionStatus, execution.Status);
        Assert.Equal(201, execution.WinningFlowId);
        Assert.Equal(3, execution.CompletedCount + execution.CancelledCount);

        var tasks = await db.UserTasks
            .Where(task => task.InstanceId == instance.Id)
            .OrderBy(task => task.ItemIndex)
            .ToListAsync();
        Assert.Equal(3, tasks.Count);
        Assert.All(tasks, task =>
        {
            Assert.Equal(expectedTaskStatus, task.Status);
            Assert.Equal(NodeExecutionCompletionReasons.AdministrativeAction, task.CompletionKind);
            Assert.Equal(batch.Summary.Id, task.AdministrativeActionBatchId);
        });

        if (mode == AdministrativeActionMultiInstanceModes.CompleteAllChildren)
        {
            Assert.All(tasks, task =>
            {
                Assert.Equal(201, task.SelectedFlowId);
                Assert.Equal("mi-operator", task.CompletedBy);
            });
        }
        else
        {
            Assert.All(tasks, task => Assert.Null(task.SelectedFlowId));
        }

        var evidence = await db.SequenceFlowOccurrences
            .Where(occurrence => occurrence.InstanceId == instance.Id
                                 && occurrence.SequenceFlowId == 201
                                 && occurrence.Kind
                                    == NodeExecutionCompletionReasons.AdministrativeAction)
            .ToListAsync();
        Assert.Single(evidence, occurrence => occurrence.IsTraversal);
        Assert.Equal(
            mode == AdministrativeActionMultiInstanceModes.CompleteAllChildren ? 3 : 1,
            evidence.Count(occurrence => occurrence.IsAction));

        var history = await db.InstanceHistory
            .Where(item => item.InstanceId == instance.Id
                           && item.AdministrativeActionBatchId == batch.Summary.Id)
            .ToListAsync();
        Assert.NotEmpty(history);
        Assert.All(history, item => Assert.Equal(
            NodeExecutionCompletionReasons.AdministrativeAction,
            item.Note));
    }

    [Fact]
    public async Task CompleteAllChildren_InterruptingFlowCountsEveryAdministrativelyCompletedChild()
    {
        var workflowId = await CreateWorkflowAsync(CreateMultiInstanceDirectModel());
        var instance = await StartAsync(workflowId);
        var candidate = await GetSingleCandidateAsync(workflowId, 2, "mi-interrupt-operator");

        var batch = await CreateAndExecuteBatchAsync(
            workflowId,
            sourceNodeId: 2,
            AdministrativeActionKinds.DirectFlow,
            flowId: 203,
            boundaryNodeId: null,
            multiInstanceMode: AdministrativeActionMultiInstanceModes.CompleteAllChildren,
            reason: "Complete every child through the interrupting action",
            candidate,
            "mi-interrupt-operator");

        Assert.Equal(AdministrativeActionBatchStatuses.Completed, batch.Summary.Status);
        await using var db = fixture.CreateDbContext();
        var executionId = await db.MultiInstanceExecutions
            .Where(execution => execution.InstanceId == instance.Id)
            .Select(execution => execution.Id)
            .SingleAsync();
        var count = await db.MultiInstanceFlowCounts.SingleAsync(item =>
            item.ExecutionId == executionId && item.FlowId == 203);
        Assert.Equal(3, count.CompletedCount);
        Assert.Equal(3, await db.UserTasks.CountAsync(task =>
            task.InstanceId == instance.Id
            && task.SelectedFlowId == 203
            && task.CompletionKind == NodeExecutionCompletionReasons.AdministrativeAction));
    }

    [Fact]
    public async Task TimerBoundaryBatch_ForceInterruptsNonInterruptingHostAndStagesAuditForGateway()
    {
        var workflowId = await CreateWorkflowAsync(CreateNonInterruptingTimerModel());
        var instance = await StartAsync(workflowId);
        var candidate = await GetSingleCandidateAsync(workflowId, 2, "timer-operator");
        var timer = Assert.Single(candidate.TimerBoundaries);

        Assert.True(timer.Eligible);
        Assert.Equal(TimerSubscriptionStatuses.Active, timer.Status);
        Assert.NotNull(timer.TimerSubscriptionId);
        Assert.NotNull(timer.TimerJobId);

        var batch = await CreateAndExecuteBatchAsync(
            workflowId,
            sourceNodeId: 2,
            AdministrativeActionKinds.TimerBoundary,
            flowId: 401,
            boundaryNodeId: 6,
            multiInstanceMode: null,
            reason: "Do not wait for the authored timer",
            candidate,
            "timer-operator");

        Assert.Equal(AdministrativeActionBatchStatuses.Completed, batch.Summary.Status);
        Assert.Equal(1, batch.Summary.SucceededItemCount);

        await using var db = fixture.CreateDbContext();
        var task = await db.UserTasks.SingleAsync(item => item.InstanceId == instance.Id);
        Assert.Equal(UserTaskRecordStatuses.Cancelled, task.Status);
        Assert.Equal(NodeExecutionCompletionReasons.AdministrativeAction, task.CompletionKind);
        Assert.Equal("Do not wait for the authored timer", task.CompletionReason);
        Assert.Equal(batch.Summary.Id, task.AdministrativeActionBatchId);

        var subscription = await db.TimerSubscriptions.SingleAsync(
            item => item.Id == timer.TimerSubscriptionId);
        Assert.Equal(TimerSubscriptionStatuses.Completed, subscription.Status);

        var boundaryEvidence = await db.SequenceFlowOccurrences.SingleAsync(
            occurrence => occurrence.InstanceId == instance.Id
                          && occurrence.SequenceFlowId == 401);
        Assert.Equal(
            NodeExecutionCompletionReasons.AdministrativeAction,
            boundaryEvidence.Kind);
        Assert.True(boundaryEvidence.IsAction);
        Assert.True(boundaryEvidence.IsTraversal);
        Assert.Equal("timer-operator", boundaryEvidence.User);

        // The downstream gateway condition observes the administrative
        // traversal staged earlier in the same transaction.
        Assert.True(await db.SequenceFlowOccurrences.AnyAsync(
            occurrence => occurrence.InstanceId == instance.Id
                          && occurrence.SequenceFlowId == 501
                          && occurrence.IsTraversal));
        Assert.False(await db.SequenceFlowOccurrences.AnyAsync(
            occurrence => occurrence.InstanceId == instance.Id
                          && occurrence.SequenceFlowId == 502
                          && occurrence.IsTraversal));
    }

    [Fact]
    public async Task AdministrativeAction_WithoutFlowInfoReferenceStillPersistsCorrelatedEvidence()
    {
        var workflowId = await CreateWorkflowAsync(CreateNoFlowInfoDirectModel());
        var instance = await StartAsync(workflowId);
        var candidate = await GetSingleCandidateAsync(workflowId, 2, "evidence-operator");
        var batch = await CreateAndExecuteBatchAsync(
            workflowId,
            sourceNodeId: 2,
            AdministrativeActionKinds.DirectFlow,
            flowId: 201,
            boundaryNodeId: null,
            multiInstanceMode: null,
            reason: "Persist evidence without authored FlowInfo",
            candidate,
            "evidence-operator");

        await using var db = fixture.CreateDbContext();
        Assert.NotNull((await db.UserTasks.SingleAsync(task => task.InstanceId == instance.Id))
            .InboxVisibilityConditionId);
        var occurrence = await db.SequenceFlowOccurrences.SingleAsync(item =>
            item.InstanceId == instance.Id && item.SequenceFlowId == 201);
        Assert.Equal(NodeExecutionCompletionReasons.AdministrativeAction, occurrence.Kind);
        Assert.True(occurrence.IsAction);
        Assert.True(occurrence.IsTraversal);
        var metadata = Assert.IsType<JsonDocument>(occurrence.AdministrativeActionJson)
            .RootElement;
        Assert.Equal(batch.Summary.Id, metadata.GetProperty("batchId").GetInt64());
        Assert.Equal(workflowId, metadata.GetProperty("workflowDefinitionId").GetInt64());
        Assert.Equal(AdministrativeActionKinds.DirectFlow,
            metadata.GetProperty("actionKind").GetString());
        Assert.Equal(201, metadata.GetProperty("flowId").GetInt32());
        Assert.Equal("Persist evidence without authored FlowInfo",
            metadata.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task DownstreamScriptFailure_FailsItemAndRollsBackWorkflowTransition()
    {
        var workflowId = await CreateWorkflowAsync(CreateFailingScriptModel());
        var instance = await StartAsync(workflowId);
        var candidate = await GetSingleCandidateAsync(workflowId, 2, "failure-operator");
        var batch = await CreateAndExecuteBatchAsync(
            workflowId,
            sourceNodeId: 2,
            AdministrativeActionKinds.DirectFlow,
            flowId: 201,
            boundaryNodeId: null,
            multiInstanceMode: null,
            reason: "Exercise downstream rollback",
            candidate,
            "failure-operator");

        Assert.Equal(AdministrativeActionBatchStatuses.CompletedWithIssues, batch.Summary.Status);
        Assert.Equal(1, batch.Summary.FailedItemCount);
        Assert.Equal(0, batch.Summary.SkippedItemCount);
        var item = Assert.Single(await GetBatchItemsAsync(batch.Summary.Id, "failure-operator"));
        Assert.Equal(AdministrativeActionBatchItemStatuses.Failed, item.Status);
        Assert.Equal("downstream_execution_failed", item.ErrorCode);

        await using var db = fixture.CreateDbContext();
        var task = await db.UserTasks.SingleAsync(row => row.Id == candidate.UserTaskId);
        Assert.Equal(UserTaskRecordStatuses.Active, task.Status);
        Assert.Null(task.SelectedFlowId);
        var persistedInstance = await db.WorkflowInstances.SingleAsync(row => row.Id == instance.Id);
        Assert.Equal(WorkflowInstanceStatuses.Running, persistedInstance.Status);
        Assert.False(await db.SequenceFlowOccurrences.AnyAsync(row =>
            row.InstanceId == instance.Id && row.SequenceFlowId == 201));
    }

    [Fact]
    public async Task AdministrativeAsyncAfter_FinalTraversalAndHistoryRetainCorrelation()
    {
        var workflowId = await CreateWorkflowAsync(CreateNoFlowInfoDirectModel(asyncAfter: true));
        var instance = await StartAsync(workflowId);
        var candidate = await GetSingleCandidateAsync(workflowId, 2, "async-operator");
        var batch = await CreateAndExecuteBatchAsync(
            workflowId,
            sourceNodeId: 2,
            AdministrativeActionKinds.DirectFlow,
            flowId: 201,
            boundaryNodeId: null,
            multiInstanceMode: null,
            reason: "Retain async administrative correlation",
            candidate,
            "async-operator");

        long continuationJobId;
        await using (var db = fixture.CreateDbContext())
        {
            continuationJobId = await db.WorkflowJobs
                .Where(job => job.InstanceId == instance.Id
                              && job.Kind == WorkflowJobKinds.AsyncAfter
                              && job.Status == WorkflowJobStatuses.Queued)
                .Select(job => job.Id)
                .SingleAsync();
        }
        await ProcessBatchJobAsync(continuationJobId);

        await using var verification = fixture.CreateDbContext();
        var occurrences = await verification.SequenceFlowOccurrences
            .Where(item => item.InstanceId == instance.Id && item.SequenceFlowId == 201)
            .OrderBy(item => item.Id)
            .ToListAsync();
        Assert.Equal(2, occurrences.Count);
        Assert.Contains(occurrences, item => item.IsAction && !item.IsTraversal);
        Assert.Contains(occurrences, item => !item.IsAction && item.IsTraversal);
        Assert.All(occurrences, occurrence =>
        {
            Assert.Equal(NodeExecutionCompletionReasons.AdministrativeAction, occurrence.Kind);
            var metadata = Assert.IsType<JsonDocument>(occurrence.AdministrativeActionJson)
                .RootElement;
            Assert.Equal(batch.Summary.Id, metadata.GetProperty("batchId").GetInt64());
            Assert.Equal("Retain async administrative correlation",
                metadata.GetProperty("reason").GetString());
        });

        var history = await verification.InstanceHistory
            .Where(item => item.InstanceId == instance.Id
                           && item.AdministrativeActionBatchId == batch.Summary.Id)
            .ToListAsync();
        Assert.True(history.Count >= 2);
        Assert.All(history, item =>
        {
            Assert.Equal(NodeExecutionCompletionReasons.AdministrativeAction, item.Note);
            Assert.Equal("Retain async administrative correlation", item.Reason);
        });
        Assert.Equal(
            WorkflowInstanceStatuses.Completed,
            (await verification.WorkflowInstances.SingleAsync(item => item.Id == instance.Id)).Status);
    }

    private async Task<AdministrativeActionCandidateDto> GetSingleCandidateAsync(
        long workflowId,
        int sourceNodeId,
        string user)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/administrative-actions/candidates/search",
            new AdministrativeActionCandidateSearchRequest
            {
                WorkflowDefinitionId = workflowId,
                SourceNodeId = sourceNodeId,
                Page = 1,
                PageSize = 20
            },
            user);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.Single(
            (await ReadAsync<PagedResult<AdministrativeActionCandidateDto>>(response)).Items);
    }

    private async Task<AdministrativeActionBatchDetailDto> CreateAndExecuteBatchAsync(
        long workflowId,
        int sourceNodeId,
        string actionKind,
        int flowId,
        int? boundaryNodeId,
        string? multiInstanceMode,
        string? reason,
        AdministrativeActionCandidateDto candidate,
        string user)
    {
        AdministrativeActionBatchDetailDto batch;
        using (var create = await SendAsync(
                   HttpMethod.Post,
                   "/api/administrative-action-batches",
                   new CreateAdministrativeActionBatchRequest(
                       workflowId,
                       sourceNodeId,
                       actionKind,
                       flowId,
                       boundaryNodeId,
                       multiInstanceMode,
                       reason,
                       null,
                       new AdministrativeActionBatchSelectionDto(
                           AdministrativeActionBatchSelectionModes.Explicit,
                           [new AdministrativeActionPositionReferenceDto(
                               candidate.PositionKind,
                               candidate.PositionId)],
                           null,
                           null),
                       $"runtime-mode-{Guid.NewGuid():N}"),
                   user))
        {
            Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
            batch = await ReadAsync<AdministrativeActionBatchDetailDto>(create);
        }

        await ProcessBatchJobAsync(batch.PreparationJobId!.Value);
        batch = await GetBatchAsync(batch.Summary.Id, user);
        Assert.Equal(AdministrativeActionBatchStatuses.Ready, batch.Summary.Status);
        Assert.Equal(1, batch.Summary.EligibleItemCount);

        using (var confirm = await SendAsync(
                   HttpMethod.Post,
                   $"/api/administrative-action-batches/{batch.Summary.Id}/confirm",
                   new ConfirmAdministrativeActionBatchRequest(
                       batch.Summary.EligibleItemCount,
                       batch.Summary.TotalAffectedTaskCount,
                       batch.Summary.UpdatedAt),
                   user))
        {
            Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
            batch = await ReadAsync<AdministrativeActionBatchDetailDto>(confirm);
        }

        await ProcessBatchJobAsync(batch.ExecutionJobId!.Value);
        return await GetBatchAsync(batch.Summary.Id, user);
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
                $"administrative-runtime-test-{Guid.NewGuid():N}",
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

    private async Task<long> CreateWorkflowAsync(WorkflowModel model)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(model, true));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<WorkflowDetailDto>(response)).Id;
    }

    private async Task<InstanceDetailDto> StartAsync(long workflowId)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, null),
            "starter");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<InstanceDetailDto>(response);
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

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "test-operator")
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, user, []);
        request.Headers.TryAddWithoutValidation("X-Test-Suppress-Admin", "true");
        return await fixture.Client.SendAsync(request);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException("Response body was empty.");

    private static WorkflowModel CreateMultiInstanceDirectModel()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = $"administrative-mi-runtime-{suffix}",
            Name = $"Administrative MI runtime {suffix}",
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
                        CardinalityExpression = "3",
                        CompletionEvaluation = MultiInstanceCompletionEvaluations.AfterAll,
                        ResultVariable = "approvalResults"
                    }
                },
                new FlowNodeModel { Id = 3, Name = "Selected outcome", Type = BpmnFlowNodeTypes.EndEvent },
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
                    CompletionCondition = "FlowInfo(201, 'actions.count') >= 3",
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
                    Id = 203,
                    Name = "Interrupt and approve",
                    SourceRef = 2,
                    TargetRef = 3,
                    Roles = ["Approver"],
                    CancelRemainingInstances = true
                }
            ]
        };
    }

    private static WorkflowModel CreateNoFlowInfoDirectModel(bool asyncAfter = false)
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = $"administrative-no-flow-info-{suffix}",
            Name = $"Administrative no FlowInfo {suffix}",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Waiting user task",
                    Type = BpmnFlowNodeTypes.UserTask,
                    Roles = ["Worker"],
                    // Administrative batch selection/execution deliberately
                    // bypasses personal inbox visibility.
                    InboxVisibilityCondition = "false",
                    AsyncAfter = asyncAfter
                },
                new FlowNodeModel { Id = 3, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, Name = "Begin", SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel
                {
                    Id = 201,
                    Name = "Finish",
                    SourceRef = 2,
                    TargetRef = 3,
                    Roles = ["Worker"]
                }
            ]
        };
    }

    private static WorkflowModel CreateFailingScriptModel()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = $"administrative-script-failure-{suffix}",
            Name = $"Administrative script failure {suffix}",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Waiting user task",
                    Type = BpmnFlowNodeTypes.UserTask,
                    Roles = ["Worker"]
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Failing script",
                    Type = BpmnFlowNodeTypes.ScriptTask,
                    ScriptFormat = ScriptFormats.JavaScript,
                    UsesFlowInfo = false,
                    Script = "throw new Error('administrative downstream failure');"
                },
                new FlowNodeModel { Id = 4, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, Name = "Begin", SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel
                {
                    Id = 201,
                    Name = "Run failing script",
                    SourceRef = 2,
                    TargetRef = 3,
                    Roles = ["Worker"]
                },
                new SequenceFlowModel { Id = 301, Name = "Finish", SourceRef = 3, TargetRef = 4 }
            ]
        };
    }

    private static WorkflowModel CreateNonInterruptingTimerModel()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = $"administrative-timer-runtime-{suffix}",
            Name = $"Administrative timer runtime {suffix}",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Wait for approval",
                    Type = BpmnFlowNodeTypes.UserTask,
                    Roles = ["Approver"],
                    RequiresClaim = true
                },
                new FlowNodeModel { Id = 3, Name = "Approved", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 4, Name = "Timer observed", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 5, Name = "Fallback", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel
                {
                    Id = 6,
                    Name = "Approval deadline",
                    Type = BpmnFlowNodeTypes.TimerBoundaryEvent,
                    AttachedToRef = 2,
                    CancelActivity = false,
                    Timer = new TimerDefinitionModel { TimeDuration = "P2D" }
                },
                new FlowNodeModel
                {
                    Id = 7,
                    Name = "Observe timer evidence",
                    Type = BpmnFlowNodeTypes.ExclusiveGateway
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
                    Roles = ["Approver"]
                },
                new SequenceFlowModel { Id = 401, Name = "Escalate now", SourceRef = 6, TargetRef = 7 },
                new SequenceFlowModel
                {
                    Id = 501,
                    Name = "Administrative timer was observed",
                    SourceRef = 7,
                    TargetRef = 4,
                    Condition = "FlowInfo(401, 'traversals.count') == 1",
                    ConditionPriority = 1
                },
                new SequenceFlowModel
                {
                    Id = 502,
                    Name = "Fallback",
                    SourceRef = 7,
                    TargetRef = 5,
                    IsDefault = true
                }
            ]
        };
    }
}
