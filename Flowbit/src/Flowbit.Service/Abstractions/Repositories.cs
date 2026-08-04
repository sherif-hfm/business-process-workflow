using System.Text.Json;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Abstractions;

public interface IWorkflowDefinitionRepository
{
    Task<IReadOnlyList<WorkflowDefinitionRecord>> ListLatestAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkflowDefinitionRecord>> ListVersionsByKeyAsync(string workflowKey, CancellationToken cancellationToken);

    Task<WorkflowDefinitionRecord?> GetAsync(long id, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<long, WorkflowDefinitionRecord>> GetManyAsync(
        IReadOnlyCollection<long> ids,
        CancellationToken cancellationToken);

    Task<WorkflowDefinitionRecord?> GetPublishedAsync(long id, CancellationToken cancellationToken);

    Task<WorkflowDefinitionRecord?> GetDefaultByWorkflowKeyAsync(string workflowKey, CancellationToken cancellationToken);

    Task LockFamilyForStartAsync(string workflowKey, CancellationToken cancellationToken);

    Task<bool> IsBusinessKeyScopeActiveAsync(string workflowKey, CancellationToken cancellationToken);

    Task<WorkflowDefinitionRecord> AddAsync(
        string name,
        WorkflowModel definition,
        bool isPublished,
        CancellationToken cancellationToken);

    Task<bool> SetPublishedAsync(long id, bool isPublished, CancellationToken cancellationToken);

    Task<bool> SetDefaultAsync(long id, bool isDefault, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(long id, CancellationToken cancellationToken);
}

public interface IWorkflowRuntimeRepository
{
    Task<WorkflowInstanceRecord> AddInstanceAsync(
        long workflowDefinitionId,
        string workflowKey,
        string? idempotencyKey,
        string? businessKey,
        string? businessKeyUniqueness,
        CurrentNodeSnapshot node,
        string? startedBy,
        IReadOnlyList<string> startedByRoles,
        CancellationToken cancellationToken);

    Task<PagedResult<InstanceListItem>> ListInstancesAsync(
        string? status,
        long? instanceId,
        long? workflowId,
        string? workflowKey,
        string? businessKey,
        int? nodeId,
        string? nodeExternalId,
        VariableFilterExpression? variableFilter,
        IReadOnlyList<InstanceSortCriterion> sort,
        InstanceListAuthorization authorization,
        string? cursor,
        bool includeVariables,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<PagedResult<InboxListItem>> ListInboxAsync(
        string user,
        IReadOnlyCollection<string> roles,
        DateTimeOffset asOf,
        long? instanceId,
        long? workflowId,
        string? workflowKey,
        string? businessKey,
        int? nodeId,
        string? nodeExternalId,
        VariableFilterExpression? variableFilter,
        IReadOnlyList<InboxSortCriterion> sort,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<PagedResult<ManagedUserTaskRecord>> ListManageableUserTasksAsync(
        IReadOnlyCollection<string> managerRoles,
        long? taskId,
        long? instanceId,
        long? workflowId,
        string? workflowKey,
        string? businessKey,
        int? nodeId,
        string? nodeExternalId,
        string? owner,
        string? ownership,
        VariableFilterExpression? variableFilter,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<PagedResult<ManagedUserTaskRecord>> ListDistributableUserTasksAsync(
        string workflowKey,
        long? taskId,
        long? instanceId,
        long? workflowId,
        string? businessKey,
        int? nodeId,
        string? nodeExternalId,
        string? owner,
        string? ownership,
        VariableFilterExpression? variableFilter,
        bool includeVariables,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<WorkflowInstanceRecord?> GetInstanceAsync(long id, CancellationToken cancellationToken);

    Task<string?> GetInstanceStatusAsync(long id, CancellationToken cancellationToken);

    Task<WorkflowInstanceRecord?> GetInstanceForUpdateAsync(
        long id,
        bool lockActiveUserTask,
        CancellationToken cancellationToken);

    Task<ExecutionTokenRecord?> GetExecutionTokenAsync(
        long tokenId,
        bool forUpdate,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ExecutionTokenRecord>> GetExecutionTokensAsync(
        IReadOnlyCollection<long> tokenIds,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ExecutionTokenRecord>> ListExecutionTokensAsync(
        long instanceId,
        string? status,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ExecutionTokenRecord>> ListCurrentExecutionTokensAsync(
        long instanceId,
        long representativeTokenId,
        CancellationToken cancellationToken);

    Task<ExecutionTokenRecord> AddExecutionTokenAsync(
        long instanceId,
        CurrentNodeSnapshot node,
        long? gatewayBranchId,
        int? arrivedViaFlowId,
        NodeExecutionActorRecord triggeredBy,
        CancellationToken cancellationToken,
        int automaticActivationCount = 0,
        IReadOnlyCollection<long>? automaticActivationStateIds = null);

    Task<IReadOnlyList<ExecutionTokenRecord>> AddGatewayBranchTokensAsync(
        long instanceId,
        CurrentNodeSnapshot gateway,
        long? parentBranchId,
        IReadOnlyList<long> gatewayBranchIds,
        IReadOnlyCollection<long> complexDrainStateIds,
        NodeExecutionActorRecord triggeredBy,
        CancellationToken cancellationToken,
        int automaticActivationCount = 0,
        IReadOnlyCollection<long>? automaticActivationStateIds = null);

    Task UpdateExecutionTokenAsync(
        long tokenId,
        CurrentNodeSnapshot node,
        string tokenStatus,
        long? gatewayBranchId,
        int? arrivedViaFlowId,
        string? terminationReason,
        string? claimedBy,
        NodeExecutionActorRecord triggeredBy,
        NodeExecutionCompletionRecord? currentCompletion,
        CancellationToken cancellationToken,
        bool deferSave = false,
        int? automaticActivationCount = null,
        IReadOnlyCollection<long>? automaticActivationStateIds = null);

    /// <summary>
    /// Fences an active token at a durable async/timer wait. The update succeeds
    /// only when the token still owns <paramref name="activationId"/> and is not
    /// already waiting on another job.
    /// </summary>
    Task<bool> SetExecutionTokenWaitAsync(
        long tokenId,
        Guid activationId,
        string waitState,
        long? waitingJobId,
        long? waitingTimerSubscriptionId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Clears an exact durable wait after the owning job has been fenced under
    /// the instance lock. A stale job receives false and must not write state.
    /// </summary>
    Task<bool> ClearExecutionTokenWaitAsync(
        long tokenId,
        Guid activationId,
        string waitState,
        long? waitingJobId,
        long? waitingTimerSubscriptionId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Updates the consecutive automatic-activation count only when the token
    /// still owns the supplied activation fence. A stale worker receives false.
    /// </summary>
    Task<bool> SetExecutionTokenAutomaticActivationCountAsync(
        long tokenId,
        Guid activationId,
        int automaticActivationCount,
        CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the Complex Gateway activation lineage only when the token
    /// still owns the supplied activation fence. A stale worker receives false.
    /// </summary>
    Task<bool> SetExecutionTokenAutomaticActivationStateIdsAsync(
        long tokenId,
        Guid activationId,
        IReadOnlyCollection<long> automaticActivationStateIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Activates the pending node execution created for asyncBefore and creates
    /// its normal user-task work item when applicable.
    /// </summary>
    Task<ExecutionTokenRecord?> ActivatePendingNodeAsync(
        long tokenId,
        Guid activationId,
        string? claimedBy,
        CancellationToken cancellationToken);

    /// <summary>
    /// Completes the current node execution without moving the token. Used by
    /// asyncAfter before the outgoing traversal is committed by a later job.
    /// </summary>
    Task<bool> CompleteCurrentNodeForWaitAsync(
        long tokenId,
        Guid activationId,
        NodeExecutionCompletionRecord completion,
        CancellationToken cancellationToken);

    Task SetExecutionTokenStatusAsync(
        long tokenId,
        string tokenStatus,
        string? terminationReason,
        NodeExecutionCompletionRecord completion,
        CancellationToken cancellationToken);

    Task SetExecutionTokensStatusAsync(
        IReadOnlyCollection<long> tokenIds,
        string tokenStatus,
        string? terminationReason,
        string completionReason,
        NodeExecutionActorRecord actor,
        CancellationToken cancellationToken);

    Task MergeExecutionTokensAsync(
        IReadOnlyCollection<long> tokenIds,
        long? exitGatewayBranchId,
        string completionReason,
        NodeExecutionActorRecord actor,
        CancellationToken cancellationToken);

    Task SetInstanceStatusAsync(
        long instanceId,
        string status,
        CancellationToken cancellationToken);

    Task<GatewayExecutionRecord> AddGatewayExecutionAsync(
        long instanceId,
        int gatewayNodeId,
        string gatewayType,
        string direction,
        string? phase,
        int? cycle,
        long? parentBranchId,
        IReadOnlyList<int> selectedFlowIds,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<GatewayExecutionRecord>> ListGatewayExecutionsAsync(
        long instanceId,
        string? status,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<GatewayExecutionRecord>> ListCurrentGatewayExecutionsAsync(
        long instanceId,
        CancellationToken cancellationToken);

    Task<GatewayExecutionRecord?> GetGatewayExecutionAsync(
        long executionId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<GatewayBranchRecord>> ListGatewayBranchesAsync(
        long executionId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<GatewayBranchRecord>> ListGatewayBranchesForInstanceAsync(
        long instanceId,
        bool activeOnly,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<GatewayBranchRecord>> ListGatewayBranchesForExecutionsAsync(
        IReadOnlyCollection<long> executionIds,
        CancellationToken cancellationToken);

    Task SetGatewayExecutionStatusAsync(
        long executionId,
        string status,
        string completionReason,
        int? interruptingNodeId,
        long? interruptingTokenId,
        CancellationToken cancellationToken);

    Task SetGatewayExecutionsStatusAsync(
        IReadOnlyCollection<long> executionIds,
        string status,
        string completionReason,
        int? interruptingNodeId,
        long? interruptingTokenId,
        CancellationToken cancellationToken);

    Task SetGatewayBranchStatusAsync(
        long branchId,
        string status,
        CancellationToken cancellationToken);

    Task SetGatewayBranchesStatusAsync(
        IReadOnlyCollection<long> branchIds,
        string status,
        CancellationToken cancellationToken);

    Task<ComplexGatewayStateRecord?> GetComplexGatewayStateAsync(
        long instanceId,
        int gatewayNodeId,
        bool forUpdate,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ComplexGatewayStateRecord>> ListComplexGatewayStatesAsync(
        long instanceId,
        CancellationToken cancellationToken);

    Task<ComplexGatewayStateRecord> SaveComplexGatewayStateAsync(
        long instanceId,
        int gatewayNodeId,
        string phase,
        int cycle,
        IReadOnlyCollection<int> contributingFlowIds,
        IReadOnlyCollection<int> remainingFlowIds,
        IReadOnlyCollection<long> activationDrainStateIds,
        IReadOnlyCollection<long> drainingTokenIds,
        long? activeExecutionId,
        CancellationToken cancellationToken,
        int? automaticActivationCount = null);

    /// <summary>
    /// Atomically reads the maximum automatic-activation count and inherited
    /// Complex Gateway lineage for all instance tokens carrying a state marker,
    /// then removes that marker from every matching token regardless of status.
    /// </summary>
    Task<AutomaticActivationStateConsumptionRecord>
        ConsumeExecutionTokenAutomaticActivationStateAsync(
        long instanceId,
        long complexGatewayStateId,
        int fallbackAutomaticActivationCount,
        CancellationToken cancellationToken);

    Task RegisterTokenAtComplexGatewayAsync(
        long tokenId,
        long? complexGatewayStateId,
        int? complexGatewayCycle,
        CancellationToken cancellationToken);

    Task AddComplexDrainMarkerAsync(
        IReadOnlyCollection<long> tokenIds,
        long complexGatewayStateId,
        CancellationToken cancellationToken);

    Task SetComplexDrainMarkersAsync(
        long tokenId,
        IReadOnlyCollection<long> complexGatewayStateIds,
        CancellationToken cancellationToken);

    Task ClearComplexDrainMarkerAsync(
        long instanceId,
        long complexGatewayStateId,
        CancellationToken cancellationToken);

    Task CancelOpenUserTasksForTokensAsync(
        IReadOnlyCollection<long> tokenIds,
        string completionReason,
        NodeExecutionActorRecord actor,
        CancellationToken cancellationToken);

    Task CancelActiveMultiInstancesForTokensAsync(
        IReadOnlyCollection<long> tokenIds,
        string completionReason,
        NodeExecutionActorRecord actor,
        CancellationToken cancellationToken);

    Task<MultiInstanceExecutionRecord> AddMultiInstanceAsync(
        long instanceId,
        long tokenId,
        CurrentNodeSnapshot node,
        MultiInstanceModel configuration,
        IReadOnlyList<JsonElement?> items,
        IReadOnlyList<int> outcomeFlowIds,
        NodeExecutionActorRecord triggeredBy,
        CancellationToken cancellationToken);

    Task<MultiInstanceExecutionRecord?> GetActiveMultiInstanceAsync(
        long tokenId,
        bool forUpdate,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MultiInstanceExecutionRecord>> ListMultiInstancesAsync(
        long instanceId,
        string? status,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<MultiInstanceExecutionRecord>> ListCurrentMultiInstancesAsync(
        long instanceId,
        CancellationToken cancellationToken);

    Task<MultiInstanceExecutionRecord?> GetMultiInstanceAsync(
        long executionId,
        bool forUpdate,
        CancellationToken cancellationToken);

    Task<UserTaskRecord?> GetUserTaskAsync(long taskId, bool forUpdate, CancellationToken cancellationToken);

    Task<UserTaskRecord?> GetActiveUserTaskAsync(
        long instanceId,
        bool forUpdate,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<UserTaskRecord>> ListUserTasksAsync(
        long instanceId,
        string? status,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<UserTaskRecord>> ListCurrentUserTasksAsync(
        long instanceId,
        CancellationToken cancellationToken);

    Task<PagedResult<UserTaskRecord>> ListUserTasksPageAsync(
        long instanceId,
        string? status,
        string user,
        IReadOnlyCollection<string> roles,
        DateTimeOffset asOf,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<UserTaskRecord>> ListExecutionTasksAsync(long executionId, CancellationToken cancellationToken);

    Task<AssignmentInheritanceSourceRecord?> GetAssignmentInheritanceSourceAsync(
        long instanceId,
        int? nodeId,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<long, UserTaskWorkSummaryRecord>> GetUserTaskWorkSummariesAsync(
        IReadOnlyCollection<long> instanceIds,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<long, MultiInstanceProgressRecord>> GetMultiInstanceProgressAsync(
        IReadOnlyCollection<long> executionIds,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<long, MultiInstanceActorStateRecord>> GetMultiInstanceActorStatesAsync(
        IReadOnlyCollection<long> executionIds,
        string actor,
        CancellationToken cancellationToken);

    Task<bool> HasCompletedMultiInstanceItemAsync(
        long executionId,
        string completedBy,
        CancellationToken cancellationToken);

    Task<long?> GetClaimedMultiInstanceItemIdAsync(
        long executionId,
        string claimedBy,
        CancellationToken cancellationToken);

    Task<long?> GetOwnedMultiInstanceItemIdAsync(
        long executionId,
        string owner,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<int, int>> ListMultiInstanceFlowCountsAsync(
        long executionId,
        CancellationToken cancellationToken);

    Task CompleteMultiInstanceItemAsync(
        long taskId,
        int selectedFlowId,
        string completedBy,
        IReadOnlyList<string> completedByRoles,
        Dictionary<string, JsonElement> result,
        CancellationToken cancellationToken,
        string? actingFor = null,
        long? delegationId = null);

    Task CompleteUserTaskAsync(
        long taskId,
        int selectedFlowId,
        string completedBy,
        IReadOnlyList<string> completedByRoles,
        Dictionary<string, JsonElement> result,
        CancellationToken cancellationToken,
        string? actingFor = null,
        long? delegationId = null,
        string? completionKind = null,
        string? completionReason = null,
        long? administrativeActionBatchId = null);

    Task CompleteAdministrativeActionBatchItemAsync(
        long batchId,
        long sourceUserTaskId,
        long? newUserTaskId,
        long? versionChangeAuditId,
        JsonElement? result,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken);

    Task ActivateNextMultiInstanceItemAsync(
        long executionId,
        NodeExecutionActorRecord actor,
        CancellationToken cancellationToken);

    Task CloseMultiInstanceAsync(
        long executionId,
        int winningFlowId,
        string completionReason,
        NodeExecutionActorRecord actor,
        CancellationToken cancellationToken);

    Task<DateTimeOffset> UpdateUserTaskClaimAsync(long taskId, string? claimedBy, CancellationToken cancellationToken);

    Task<DateTimeOffset> UpdateUserTaskAssignmentAsync(
        long taskId,
        string? assignee,
        bool requiresClaim,
        CancellationToken cancellationToken);

    Task<DateTimeOffset> TouchInstanceAsync(long id, CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkflowInstanceVersionChangeRecord>> ListVersionChangesAsync(
        long instanceId,
        CancellationToken cancellationToken);

    Task<WorkflowInstanceVersionChangeRecord> ChangeInstanceWorkflowVersionAsync(
        long instanceId,
        long expectedSourceWorkflowDefinitionId,
        DateTimeOffset expectedUpdatedAt,
        long targetWorkflowDefinitionId,
        WorkflowModel targetDefinition,
        NodeExecutionActorRecord actor,
        string reason,
        CancellationToken cancellationToken,
        long? administrativeActionBatchId = null);

    Task AddVariableAsync(
        long instanceId,
        string variableName,
        int? sourceActionId,
        string? setBy,
        JsonElement value,
        CancellationToken cancellationToken,
        long? nodeExecutionId = null,
        string? actingFor = null,
        long? delegationId = null);

    Task<IReadOnlyList<InstanceVariableRecord>> ListVariablesAsync(
        long instanceId,
        CancellationToken cancellationToken);

    Task AddHistoryAsync(
        long instanceId,
        int? actionId,
        int fromStepId,
        int toStepId,
        string? performedBy,
        Dictionary<string, JsonElement>? payload,
        string? note,
        CancellationToken cancellationToken,
        string? actingFor = null,
        long? delegationId = null);

    Task AddTokenHistoryAsync(
        long instanceId,
        long tokenId,
        int? actionId,
        int fromStepId,
        int toStepId,
        string? performedBy,
        Dictionary<string, JsonElement>? payload,
        string? note,
        CancellationToken cancellationToken,
        string? actingFor = null,
        long? delegationId = null);

    Task AddMultiInstanceHistoryAsync(
        long instanceId,
        long tokenId,
        long? userTaskId,
        long executionId,
        int? itemIndex,
        int actionId,
        int fromStepId,
        int toStepId,
        string? performedBy,
        Dictionary<string, JsonElement>? payload,
        string note,
        CancellationToken cancellationToken,
        string? actingFor = null,
        long? delegationId = null);

    Task AddUserTaskActionHistoryAsync(
        long instanceId,
        long tokenId,
        long userTaskId,
        int actionId,
        int fromStepId,
        int toStepId,
        string performedBy,
        Dictionary<string, JsonElement> payload,
        CancellationToken cancellationToken,
        string? actingFor = null,
        long? delegationId = null,
        string? note = null,
        string? reason = null,
        long? administrativeActionBatchId = null);

    Task AddUserTaskHistoryAsync(
        long instanceId,
        long tokenId,
        long userTaskId,
        long? multiInstanceExecutionId,
        int? itemIndex,
        int nodeId,
        string performedBy,
        Dictionary<string, JsonElement> payload,
        string note,
        CancellationToken cancellationToken,
        string? actingFor = null,
        long? delegationId = null);

    Task<IReadOnlyList<InstanceHistoryRecord>> ListHistoryAsync(
        long instanceId,
        CancellationToken cancellationToken);

    Task<long?> GetLatestNodeEntryHistoryIdAsync(
        long instanceId,
        int nodeId,
        CancellationToken cancellationToken);

    Task<long?> GetLatestTokenNodeEntryHistoryIdAsync(
        long instanceId,
        long tokenId,
        int nodeId,
        CancellationToken cancellationToken);

    Task<MessageDeliveryReceiptRecord?> GetMessageDeliveryReceiptAsync(
        long instanceId,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task AddMessageDeliveryReceiptAsync(
        MessageDeliveryReceiptRecord receipt,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<InstanceVariableVersionRecord>> LoadLatestVariableVersionsAsync(
        long instanceId,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<int, SequenceFlowSummaryRecord>> ListSequenceFlowSummariesAsync(
        long instanceId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ObservedSequenceFlowRecord>> ListObservedSequenceFlowsAsync(
        long instanceId,
        CancellationToken cancellationToken);

    Task<SequenceFlowSummaryRecord> AppendSequenceFlowOccurrenceAsync(
        SequenceFlowOccurrenceWriteRecord occurrence,
        CancellationToken cancellationToken);

    Task<IdempotencyReservationRecord> ReserveIdempotencyKeyAsync(
        string workflowKey,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task BindIdempotencyKeyAsync(
        string workflowKey,
        string idempotencyKey,
        long instanceId,
        CancellationToken cancellationToken);

    Task<BusinessKeyReservationRecord> ReserveBusinessKeyAsync(
        string workflowKey,
        string businessKey,
        string uniqueness,
        CancellationToken cancellationToken);

    Task BindBusinessKeyAsync(
        string workflowKey,
        string businessKey,
        long instanceId,
        CancellationToken cancellationToken);
}

public interface IWorkflowSettingsRepository
{
    Task<IReadOnlyDictionary<string, JsonElement>> LoadAllAsync(CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, JsonElement>> LoadAllFreshAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkflowSettingRecord>> ListAsync(CancellationToken cancellationToken);

    Task<WorkflowSettingRecord> CreateAsync(
        string? settingNamespace,
        string name,
        JsonElement value,
        string? description,
        CancellationToken cancellationToken);

    Task<WorkflowSettingRecord?> UpdateAsync(
        long id,
        JsonElement value,
        string? description,
        DateTimeOffset expectedUpdatedAt,
        CancellationToken cancellationToken);

    Task<bool> DeleteByIdAsync(
        long id,
        DateTimeOffset expectedUpdatedAt,
        CancellationToken cancellationToken);
}

public interface IEngineSettingsRepository
{
    Task<EngineSettingRecord?> GetByKeyAsync(string key, CancellationToken cancellationToken);
    Task<IReadOnlyList<EngineSettingRecord>> SearchAsync(string pattern, CancellationToken cancellationToken);
    Task<EngineSettingRecord> SetAsync(string key, string value, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken);

    Task<IReadOnlyList<EngineSettingRecord>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<EngineSettingRecord>>(
            new NotSupportedException("This repository does not support settings management."));

    Task<EngineSettingRecord> CreateAsync(
        string? settingNamespace,
        string key,
        string value,
        string? description,
        CancellationToken cancellationToken) =>
        Task.FromException<EngineSettingRecord>(
            new NotSupportedException("This repository does not support settings management."));

    Task<EngineSettingRecord?> UpdateAsync(
        long id,
        string value,
        string? description,
        DateTimeOffset expectedUpdatedAt,
        CancellationToken cancellationToken) =>
        Task.FromException<EngineSettingRecord?>(
            new NotSupportedException("This repository does not support settings management."));

    Task<bool> DeleteByIdAsync(
        long id,
        DateTimeOffset expectedUpdatedAt,
        CancellationToken cancellationToken) =>
        Task.FromException<bool>(
            new NotSupportedException("This repository does not support settings management."));
}

public interface IWorkflowTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}

public interface IUnitOfWork
{
    Task<IWorkflowTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Detaches state left in the unit of work after a rolled-back transaction.
    /// Durable job retry/incident writes must never flush workflow mutations
    /// that belonged to the failed transaction.
    /// </summary>
    void DiscardChanges();
}
