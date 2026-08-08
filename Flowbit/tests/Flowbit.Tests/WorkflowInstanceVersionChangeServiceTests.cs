using System.Reflection;
using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Flowbit.Tests;

public sealed class WorkflowInstanceVersionChangeServiceTests
{
    private static readonly ActorContext Admin = new(
        "workflow-admin",
        ["admin", "operations"],
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    [Fact]
    public async Task Preview_ReturnsCompatibleVersionSummariesAndConcurrencyValues()
    {
        var harness = new Harness(sourceVersion: 1, targetVersion: 4);

        var preview = await harness.Service.PreviewInstanceVersionChangeAsync(
            harness.Instance.Id,
            harness.Target.Id,
            Admin,
            CancellationToken.None);

        Assert.NotNull(preview);
        Assert.True(preview.Compatible);
        Assert.Empty(preview.Blockers);
        Assert.Equal(InstanceVersionChangeDirections.Upgrade, preview.Direction);
        Assert.Equal(harness.Source.Id, preview.SourceWorkflow.Id);
        Assert.Equal(1, preview.SourceWorkflow.Version);
        Assert.Equal(harness.Target.Id, preview.TargetWorkflow.Id);
        Assert.Equal(4, preview.TargetWorkflow.Version);
        Assert.Equal(harness.Source.Id, preview.ExpectedSourceWorkflowId);
        Assert.Equal(harness.Instance.UpdatedAt, preview.ExpectedUpdatedAt);
        Assert.Equal(0, harness.ChangeCalls);
    }

    [Theory]
    [InlineData(1, 2, InstanceVersionChangeDirections.Upgrade)]
    [InlineData(3, 2, InstanceVersionChangeDirections.Downgrade)]
    [InlineData(1, 5, InstanceVersionChangeDirections.Upgrade)]
    public async Task Change_SupportsUpgradeDowngradeAndNonAdjacentVersions(
        int sourceVersion,
        int targetVersion,
        string expectedDirection)
    {
        var harness = new Harness(sourceVersion, targetVersion);
        var sourceUpdatedAt = harness.Instance.UpdatedAt;

        var result = await harness.Service.ChangeInstanceVersionAsync(
            harness.Instance.Id,
            new ChangeInstanceVersionRequest(
                harness.Target.Id,
                harness.Source.Id,
                sourceUpdatedAt,
                "  approved production correction  "),
            Admin,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(harness.Target.Id, result.Instance.Workflow.Id);
        Assert.Equal(targetVersion, result.Instance.Workflow.Version);
        Assert.Equal(expectedDirection, result.VersionChange.Direction);
        Assert.Equal(harness.Source.Id, result.VersionChange.SourceWorkflow.Id);
        Assert.Equal(harness.Target.Id, result.VersionChange.TargetWorkflow.Id);
        Assert.Equal("workflow-admin", result.VersionChange.ChangedBy);
        Assert.Equal(["admin", "operations"], result.VersionChange.ChangedByRoles);
        Assert.Equal("approved production correction", result.VersionChange.Reason);
        Assert.True(result.Instance.UpdatedAt > sourceUpdatedAt);

        var detailAudit = Assert.Single(result.Instance.VersionChanges);
        Assert.Equal(result.VersionChange, detailAudit);
        Assert.Equal(expectedDirection, detailAudit.Direction);
        Assert.Equal(sourceVersion, detailAudit.SourceWorkflow.Version);
        Assert.Equal(targetVersion, detailAudit.TargetWorkflow.Version);
        Assert.Equal(1, harness.ChangeCalls);
        Assert.Equal(1, harness.SaveCalls);
        Assert.True(harness.Transaction.Committed);
    }

    [Fact]
    public async Task Change_RejectsMalformedReasonSourceAndTimestampBeforeMutation()
    {
        var harness = new Harness(sourceVersion: 1, targetVersion: 2);

        var blankReason = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            harness.Service.ChangeInstanceVersionAsync(
                harness.Instance.Id,
                new ChangeInstanceVersionRequest(
                    harness.Target.Id,
                    harness.Source.Id,
                    harness.Instance.UpdatedAt,
                    " \t \r\n "),
                Admin,
                CancellationToken.None));
        Assert.Contains("Reason", blankReason.Message, StringComparison.Ordinal);

        var source = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            harness.Service.ChangeInstanceVersionAsync(
                harness.Instance.Id,
                new ChangeInstanceVersionRequest(
                    harness.Target.Id,
                    0,
                    harness.Instance.UpdatedAt,
                    "valid reason"),
                Admin,
                CancellationToken.None));
        Assert.Contains("ExpectedSourceWorkflowId", source.Message, StringComparison.Ordinal);

        var timestamp = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            harness.Service.ChangeInstanceVersionAsync(
                harness.Instance.Id,
                new ChangeInstanceVersionRequest(
                    harness.Target.Id,
                    harness.Source.Id,
                    default,
                    "valid reason"),
                Admin,
                CancellationToken.None));
        Assert.Contains("ExpectedUpdatedAt", timestamp.Message, StringComparison.Ordinal);
        Assert.Equal(0, harness.ChangeCalls);
        Assert.Equal(0, harness.SaveCalls);
    }

    [Fact]
    public async Task Change_RejectsTerminalInstanceAsConflict()
    {
        var harness = new Harness(
            sourceVersion: 1,
            targetVersion: 2,
            instanceStatus: WorkflowInstanceStatuses.Completed);

        var previewError = await Assert.ThrowsAsync<WorkflowConflictException>(() =>
            harness.Service.PreviewInstanceVersionChangeAsync(
                harness.Instance.Id,
                harness.Target.Id,
                Admin,
                CancellationToken.None));
        Assert.Contains("running", previewError.Message, StringComparison.OrdinalIgnoreCase);

        var error = await Assert.ThrowsAsync<WorkflowConflictException>(() =>
            harness.ChangeAsync());
        Assert.Contains("running", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.ChangeCalls);
    }

    [Fact]
    public async Task Change_RejectsStaleSourceAndTimestampAsConflicts()
    {
        var harness = new Harness(sourceVersion: 1, targetVersion: 2);

        var staleSource = await Assert.ThrowsAsync<WorkflowConflictException>(() =>
            harness.Service.ChangeInstanceVersionAsync(
                harness.Instance.Id,
                new ChangeInstanceVersionRequest(
                    harness.Target.Id,
                    harness.Source.Id + 99,
                    harness.Instance.UpdatedAt,
                    "stale source"),
                Admin,
                CancellationToken.None));
        Assert.Contains("source version changed", staleSource.Message, StringComparison.OrdinalIgnoreCase);

        var staleTimestamp = await Assert.ThrowsAsync<WorkflowConflictException>(() =>
            harness.Service.ChangeInstanceVersionAsync(
                harness.Instance.Id,
                new ChangeInstanceVersionRequest(
                    harness.Target.Id,
                    harness.Source.Id,
                    harness.Instance.UpdatedAt.AddTicks(-1),
                    "stale timestamp"),
                Admin,
                CancellationToken.None));
        Assert.Contains("changed after preview", staleTimestamp.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.ChangeCalls);
    }

    [Fact]
    public async Task Change_RejectsIncompatibleActiveNodeAsConflict()
    {
        var harness = new Harness(
            sourceVersion: 1,
            targetVersion: 2,
            targetNodeType: BpmnFlowNodeTypes.ServiceTask);

        var preview = await harness.Service.PreviewInstanceVersionChangeAsync(
            harness.Instance.Id,
            harness.Target.Id,
            Admin,
            CancellationToken.None);
        Assert.NotNull(preview);
        Assert.False(preview.Compatible);
        var blocker = Assert.Single(
            preview.Blockers,
            issue => issue.Code == WorkflowVersionCompatibilityCodes.ActiveNodeTypeChanged);
        Assert.Equal(7, blocker.NodeId);

        var error = await Assert.ThrowsAsync<WorkflowConflictException>(() =>
            harness.ChangeAsync());
        Assert.Contains(
            WorkflowVersionCompatibilityCodes.ActiveNodeTypeChanged,
            error.Message,
            StringComparison.Ordinal);
        Assert.Equal(0, harness.ChangeCalls);
    }

    [Fact]
    public async Task Change_RejectsUnpublishedTargetAsConflict()
    {
        var harness = new Harness(
            sourceVersion: 1,
            targetVersion: 2,
            targetPublished: false);

        var previewError = await Assert.ThrowsAsync<WorkflowConflictException>(() =>
            harness.Service.PreviewInstanceVersionChangeAsync(
                harness.Instance.Id,
                harness.Target.Id,
                Admin,
                CancellationToken.None));
        Assert.Contains(
            "no longer published",
            previewError.Message,
            StringComparison.OrdinalIgnoreCase);

        var error = await Assert.ThrowsAsync<WorkflowConflictException>(() =>
            harness.ChangeAsync());
        Assert.Contains("no longer published", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.ChangeCalls);
        Assert.False(harness.Transaction.Committed);
    }

    [Fact]
    public async Task BatchExecute_UsesTheFrozenFenceAndCorrelatesTheAtomicAudit()
    {
        var harness = new Harness(sourceVersion: 1, targetVersion: 4);
        var request = harness.BatchRequest(
            batchId: 301,
            batchItemId: 907,
            reason: "  approved batch correction  ");

        var outcome = await harness.Service.ExecuteInstanceVersionChangeBatchItemAsync(
            request,
            Admin,
            CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal(901, outcome.VersionChangeId);
        Assert.Null(outcome.Code);
        Assert.Empty(outcome.Blockers);
        Assert.Equal(1, harness.BatchChangeCalls);
        Assert.Equal(0, harness.DirectChangeCalls);
        Assert.Equal(1, harness.SaveCalls);
        Assert.True(harness.Transaction.Committed);
        Assert.Equal(301, harness.LastBatchId);
        Assert.Equal(907, harness.LastBatchItemId);
        Assert.Equal(harness.Source.Id, harness.LastExpectedSourceWorkflowId);
        Assert.Equal(request.ExpectedUpdatedAt, harness.LastExpectedUpdatedAt);
        Assert.Equal(harness.Target.Id, harness.LastTargetWorkflowId);
        Assert.Equal("approved batch correction", harness.LastReason);
        Assert.Equal("workflow-admin", harness.LastActor?.User);
        Assert.Equal(["admin", "operations"], harness.LastActor?.Roles);

        var audit = Assert.Single(harness.Audits);
        Assert.Equal(301, audit.BatchId);
        Assert.Equal(907, audit.BatchItemId);
    }

    [Fact]
    public async Task BatchExecute_RevalidatesFenceAfterTheInstanceLock()
    {
        var harness = new Harness(sourceVersion: 1, targetVersion: 2);
        var request = harness.BatchRequest();
        harness.LockedInstanceOverride = harness.Instance with
        {
            UpdatedAt = harness.Instance.UpdatedAt.AddSeconds(1)
        };

        var outcome = await harness.Service.ExecuteInstanceVersionChangeBatchItemAsync(
            request,
            Admin,
            CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal("stale_since_preparation", outcome.Code);
        Assert.Equal(0, harness.BatchChangeCalls);
        Assert.Equal(0, harness.SaveCalls);
        Assert.False(harness.Transaction.Committed);
    }

    [Fact]
    public async Task BatchExecute_RevalidatesTargetPublicationAfterTheFamilyLock()
    {
        var harness = new Harness(sourceVersion: 1, targetVersion: 2);
        harness.PublishedTargetAvailable = false;

        var outcome = await harness.Service.ExecuteInstanceVersionChangeBatchItemAsync(
            harness.BatchRequest(),
            Admin,
            CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(WorkflowVersionCompatibilityCodes.TargetNotPublished, outcome.Code);
        Assert.Equal(0, harness.BatchChangeCalls);
        Assert.Equal(0, harness.SaveCalls);
        Assert.False(harness.Transaction.Committed);
    }

    [Fact]
    public async Task BatchExecute_RevalidatesFullCompatibilityUnderRuntimeLocks()
    {
        var harness = new Harness(
            sourceVersion: 1,
            targetVersion: 2,
            targetNodeType: BpmnFlowNodeTypes.ServiceTask);

        var outcome = await harness.Service.ExecuteInstanceVersionChangeBatchItemAsync(
            harness.BatchRequest(),
            Admin,
            CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal("incompatible", outcome.Code);
        Assert.Contains(
            outcome.Blockers,
            issue => issue.Code == WorkflowVersionCompatibilityCodes.ActiveNodeTypeChanged);
        Assert.Equal(0, harness.BatchChangeCalls);
        Assert.Equal(0, harness.SaveCalls);
        Assert.False(harness.Transaction.Committed);
    }

    [Fact]
    public async Task BatchExecute_DoesNotCommitWhenAtomicMutationFails()
    {
        var harness = new Harness(sourceVersion: 1, targetVersion: 2)
        {
            BatchMutationException = new InvalidOperationException("database write failed")
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Service.ExecuteInstanceVersionChangeBatchItemAsync(
                harness.BatchRequest(),
                Admin,
                CancellationToken.None));

        Assert.Equal("database write failed", exception.Message);
        Assert.Equal(1, harness.BatchChangeCalls);
        Assert.Equal(0, harness.SaveCalls);
        Assert.False(harness.Transaction.Committed);
    }

    [Fact]
    public async Task BatchExecute_DoesNotCommitWhenTheFinalSaveFails()
    {
        var harness = new Harness(sourceVersion: 1, targetVersion: 2)
        {
            SaveException = new InvalidOperationException("save failed")
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Service.ExecuteInstanceVersionChangeBatchItemAsync(
                harness.BatchRequest(),
                Admin,
                CancellationToken.None));

        Assert.Equal("save failed", exception.Message);
        Assert.Equal(1, harness.BatchChangeCalls);
        Assert.Equal(1, harness.SaveCalls);
        Assert.False(harness.Transaction.Committed);
    }

    private sealed class Harness
    {
        private readonly Dictionary<long, WorkflowDefinitionRecord> definitions;
        private readonly List<WorkflowInstanceVersionChangeRecord> audits = [];
        private readonly IReadOnlyList<ExecutionTokenRecord> tokens;

        public Harness(
            int sourceVersion,
            int targetVersion,
            string instanceStatus = WorkflowInstanceStatuses.Running,
            bool targetPublished = true,
            string targetNodeType = BpmnFlowNodeTypes.UserTask)
        {
            var now = DateTimeOffset.Parse("2026-08-02T11:30:00Z");
            Source = Definition(1000 + sourceVersion, sourceVersion, true, BpmnFlowNodeTypes.UserTask, now.AddDays(-2));
            Target = Definition(2000 + targetVersion, targetVersion, targetPublished, targetNodeType, now.AddDays(-1));
            definitions = new Dictionary<long, WorkflowDefinitionRecord>
            {
                [Source.Id] = Source,
                [Target.Id] = Target
            };
            Instance = new WorkflowInstanceRecord(
                42,
                Source.Id,
                Source.WorkflowKey,
                null,
                "ORDER-42",
                "active",
                88,
                7,
                null,
                instanceStatus,
                null,
                "starter",
                now.AddHours(-1),
                now);
            tokens =
            [
                new ExecutionTokenRecord(
                    Id: 88,
                    InstanceId: Instance.Id,
                    NodeId: 7,
                    NodeName: "Review order",
                    NodeExternalId: "REVIEW_ORDER",
                    NodeType: BpmnFlowNodeTypes.UserTask,
                    FaultCode: null,
                    FaultDescription: null,
                    Status: instanceStatus == WorkflowInstanceStatuses.Running
                        ? ExecutionTokenRecordStatuses.Active
                        : ExecutionTokenRecordStatuses.Completed,
                    GatewayBranchId: null,
                    ArrivedViaFlowId: null,
                    ComplexGatewayStateId: null,
                    ComplexGatewayCycle: null,
                    ComplexDrainStateIds: [],
                    TerminationReason: null,
                    CreatedAt: now.AddHours(-1),
                    UpdatedAt: now)
            ];

            Transaction = new TestTransaction();
            var definitionRepository = Proxy<IWorkflowDefinitionRepository>(DefinitionCall);
            var runtimeRepository = Proxy<IWorkflowRuntimeRepository>(RuntimeCall);
            var jobRepository = Proxy<IWorkflowJobRepository>(JobCall);
            var timerRepository = Proxy<ITimerSubscriptionRepository>(TimerCall);
            var unitOfWork = Proxy<IUnitOfWork>(UnitOfWorkCall);
            var settings = Proxy<IWorkflowSettingsRepository>(SettingsCall);

            Service = new WorkflowEngineService(
                definitionRepository,
                runtimeRepository,
                jobRepository,
                timerRepository,
                Proxy<IUserDelegationRepository>(Unexpected),
                unitOfWork,
                Proxy<IServiceTaskInvoker>(Unexpected),
                Proxy<IScriptEvaluator>(Unexpected),
                new WorkflowContextOptions(),
                TimeProvider.System,
                settings,
                Proxy<IEngineSettingsRepository>(Unexpected),
                NullLogger<WorkflowEngineService>.Instance);
        }

        public WorkflowEngineService Service { get; }
        public WorkflowDefinitionRecord Source { get; }
        public WorkflowDefinitionRecord Target { get; }
        public WorkflowInstanceRecord Instance { get; private set; }
        public TestTransaction Transaction { get; }
        public int ChangeCalls { get; private set; }
        public int DirectChangeCalls { get; private set; }
        public int BatchChangeCalls { get; private set; }
        public int SaveCalls { get; private set; }
        public IReadOnlyList<WorkflowInstanceVersionChangeRecord> Audits => audits;
        public WorkflowInstanceRecord? LockedInstanceOverride { get; set; }
        public bool PublishedTargetAvailable { get; set; } = true;
        public Exception? BatchMutationException { get; set; }
        public Exception? SaveException { get; set; }
        public long? LastBatchId { get; private set; }
        public long? LastBatchItemId { get; private set; }
        public long? LastExpectedSourceWorkflowId { get; private set; }
        public DateTimeOffset? LastExpectedUpdatedAt { get; private set; }
        public long? LastTargetWorkflowId { get; private set; }
        public string? LastReason { get; private set; }
        public NodeExecutionActorRecord? LastActor { get; private set; }

        public Task<ChangeInstanceVersionResultDto?> ChangeAsync() =>
            Service.ChangeInstanceVersionAsync(
                Instance.Id,
                new ChangeInstanceVersionRequest(
                    Target.Id,
                    Source.Id,
                    Instance.UpdatedAt,
                    "approved change"),
                Admin,
                CancellationToken.None);

        public InstanceVersionChangeBatchExecutionRequest BatchRequest(
            long batchId = 301,
            long batchItemId = 907,
            string reason = "approved batch correction") =>
            new(
                batchId,
                batchItemId,
                Instance.Id,
                Source.Id,
                Instance.UpdatedAt,
                Target.Id,
                reason);

        private object? DefinitionCall(MethodInfo method, object?[] arguments) => method.Name switch
        {
            nameof(IWorkflowDefinitionRepository.GetAsync) => Task.FromResult(
                definitions.GetValueOrDefault((long)arguments[0]!) as WorkflowDefinitionRecord),
            nameof(IWorkflowDefinitionRepository.GetPublishedAsync) => Task.FromResult(
                PublishedTargetAvailable
                    && definitions.GetValueOrDefault((long)arguments[0]!) is { IsPublished: true } found
                    ? found
                    : null),
            nameof(IWorkflowDefinitionRepository.GetManyAsync) => Task.FromResult<IReadOnlyDictionary<long, WorkflowDefinitionRecord>>(
                ((IReadOnlyCollection<long>)arguments[0]!)
                    .Where(definitions.ContainsKey)
                    .ToDictionary(id => id, id => definitions[id])),
            nameof(IWorkflowDefinitionRepository.LockFamilyForStartAsync) => Task.CompletedTask,
            _ => Unexpected(method, arguments)
        };

        private object? RuntimeCall(MethodInfo method, object?[] arguments) => method.Name switch
        {
            nameof(IWorkflowRuntimeRepository.GetInstanceAsync) =>
                Task.FromResult<WorkflowInstanceRecord?>(Instance),
            nameof(IWorkflowRuntimeRepository.GetInstanceForUpdateAsync) =>
                Task.FromResult<WorkflowInstanceRecord?>(LockedInstanceOverride ?? Instance),
            nameof(IWorkflowRuntimeRepository.ListExecutionTokensAsync) =>
                Task.FromResult(tokens),
            nameof(IWorkflowRuntimeRepository.ListUserTasksAsync) =>
                Task.FromResult<IReadOnlyList<UserTaskRecord>>([]),
            nameof(IWorkflowRuntimeRepository.ListMultiInstancesAsync) =>
                Task.FromResult<IReadOnlyList<MultiInstanceExecutionRecord>>([]),
            nameof(IWorkflowRuntimeRepository.ListGatewayExecutionsAsync) =>
                Task.FromResult<IReadOnlyList<GatewayExecutionRecord>>([]),
            nameof(IWorkflowRuntimeRepository.ListGatewayBranchesForInstanceAsync) =>
                Task.FromResult<IReadOnlyList<GatewayBranchRecord>>([]),
            nameof(IWorkflowRuntimeRepository.ListComplexGatewayStatesAsync) =>
                Task.FromResult<IReadOnlyList<ComplexGatewayStateRecord>>([]),
            nameof(IWorkflowRuntimeRepository.LoadLatestVariableVersionsAsync) =>
                Task.FromResult<IReadOnlyList<InstanceVariableVersionRecord>>([]),
            nameof(IWorkflowRuntimeRepository.ListObservedSequenceFlowsAsync) =>
                Task.FromResult<IReadOnlyList<ObservedSequenceFlowRecord>>([]),
            nameof(IWorkflowRuntimeRepository.ListSequenceFlowSummariesAsync) =>
                Task.FromResult<IReadOnlyDictionary<int, SequenceFlowSummaryRecord>>(
                    new Dictionary<int, SequenceFlowSummaryRecord>()),
            nameof(IWorkflowRuntimeRepository.ChangeInstanceWorkflowVersionAsync) =>
                ChangeVersion(arguments, isBatch: false),
            nameof(IWorkflowRuntimeRepository.ChangeInstanceWorkflowVersionForBatchAsync) =>
                ChangeVersion(arguments, isBatch: true),
            nameof(IWorkflowRuntimeRepository.ListVariablesAsync) =>
                Task.FromResult<IReadOnlyList<InstanceVariableRecord>>([]),
            nameof(IWorkflowRuntimeRepository.ListHistoryAsync) =>
                Task.FromResult<IReadOnlyList<InstanceHistoryRecord>>([]),
            nameof(IWorkflowRuntimeRepository.ListVersionChangesAsync) =>
                Task.FromResult<IReadOnlyList<WorkflowInstanceVersionChangeRecord>>(audits.ToList()),
            nameof(IWorkflowRuntimeRepository.GetMultiInstanceProgressAsync) =>
                Task.FromResult<IReadOnlyDictionary<long, MultiInstanceProgressRecord>>(
                    new Dictionary<long, MultiInstanceProgressRecord>()),
            nameof(IWorkflowRuntimeRepository.GetUserTaskWorkSummariesAsync) =>
                Task.FromResult<IReadOnlyDictionary<long, UserTaskWorkSummaryRecord>>(
                    new Dictionary<long, UserTaskWorkSummaryRecord>()),
            _ => Unexpected(method, arguments)
        };

        private Task<WorkflowInstanceVersionChangeRecord> ChangeVersion(
            object?[] arguments,
            bool isBatch)
        {
            ChangeCalls++;
            if (isBatch)
            {
                BatchChangeCalls++;
                LastBatchId = (long)arguments[7]!;
                LastBatchItemId = (long)arguments[8]!;
                if (BatchMutationException is not null)
                {
                    return Task.FromException<WorkflowInstanceVersionChangeRecord>(
                        BatchMutationException);
                }
            }
            else
            {
                DirectChangeCalls++;
            }
            LastExpectedSourceWorkflowId = (long)arguments[1]!;
            LastExpectedUpdatedAt = (DateTimeOffset)arguments[2]!;
            var targetId = (long)arguments[3]!;
            var actor = (NodeExecutionActorRecord)arguments[5]!;
            var reason = (string)arguments[6]!;
            LastTargetWorkflowId = targetId;
            LastActor = actor;
            LastReason = reason;
            var changedAt = Instance.UpdatedAt.AddMinutes(1);
            var audit = new WorkflowInstanceVersionChangeRecord(
                900 + ChangeCalls,
                Instance.Id,
                Instance.WorkflowDefinitionId,
                targetId,
                actor.User,
                actor.Roles,
                reason,
                changedAt,
                isBatch ? LastBatchId : null,
                isBatch ? LastBatchItemId : null);
            audits.Add(audit);
            Instance = Instance with
            {
                WorkflowDefinitionId = targetId,
                UpdatedAt = changedAt
            };
            return Task.FromResult(audit);
        }

        private static object? JobCall(MethodInfo method, object?[] arguments) => method.Name switch
        {
            nameof(IWorkflowJobRepository.ListOpenByInstanceAsync) =>
                Task.FromResult<IReadOnlyList<WorkflowJobRecord>>([]),
            nameof(IWorkflowJobRepository.ListOpenIncidentsByInstanceAsync) =>
                Task.FromResult<IReadOnlyList<WorkflowIncidentRecord>>([]),
            _ => Unexpected(method, arguments)
        };

        private static object? TimerCall(MethodInfo method, object?[] arguments) => method.Name switch
        {
            nameof(ITimerSubscriptionRepository.ListActiveOrPausedByInstanceAsync) =>
                Task.FromResult<IReadOnlyList<TimerSubscriptionRecord>>([]),
            _ => Unexpected(method, arguments)
        };

        private object? UnitOfWorkCall(MethodInfo method, object?[] arguments) => method.Name switch
        {
            nameof(IUnitOfWork.BeginTransactionAsync) =>
                Task.FromResult<IWorkflowTransaction>(Transaction),
            nameof(IUnitOfWork.SaveChangesAsync) => SaveChanges(),
            _ => Unexpected(method, arguments)
        };

        private Task SaveChanges()
        {
            SaveCalls++;
            return SaveException is null
                ? Task.CompletedTask
                : Task.FromException(SaveException);
        }

        private static object? SettingsCall(MethodInfo method, object?[] arguments) => method.Name switch
        {
            nameof(IWorkflowSettingsRepository.LoadAllAsync) =>
                Task.FromResult<IReadOnlyDictionary<string, JsonElement>>(
                    new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)),
            _ => Unexpected(method, arguments)
        };

        private static WorkflowDefinitionRecord Definition(
            long id,
            int version,
            bool published,
            string nodeType,
            DateTimeOffset createdAt)
        {
            var model = new WorkflowModel
            {
                Id = "orders",
                Name = "Order processing",
                InitialEventId = 7,
                FlowNodes =
                [
                    new FlowNodeModel
                    {
                        Id = 7,
                        Name = "Review order",
                        ExternalId = "REVIEW_ORDER",
                        Type = nodeType
                    }
                ]
            };
            return new WorkflowDefinitionRecord(
                id,
                model.Name,
                model.Id,
                version,
                model,
                published,
                false,
                createdAt);
        }
    }

    public class StubProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = Unexpected;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Handler(
                targetMethod ?? throw new InvalidOperationException("A proxy method was not supplied."),
                args ?? []);
    }

    private static T Proxy<T>(Func<MethodInfo, object?[], object?> handler)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, StubProxy>();
        ((StubProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    private static object? Unexpected(MethodInfo method, object?[] arguments) =>
        throw new InvalidOperationException(
            $"Unexpected {method.DeclaringType?.Name}.{method.Name} call.");

    private sealed class TestTransaction : IWorkflowTransaction
    {
        public bool Committed { get; private set; }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            Committed = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
