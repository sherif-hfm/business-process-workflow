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
        CancellationToken cancellationToken);

    Task<PagedResult<InstanceListItem>> ListInstancesAsync(
        string? status,
        long? instanceId,
        long? workflowId,
        string? workflowKey,
        string? businessKey,
        int? nodeId,
        string? nodeExternalId,
        IReadOnlyList<VariableFilter> variableFilters,
        bool includeVariables,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<PagedResult<InstanceListItem>> ListInboxAsync(
        string user,
        IReadOnlyCollection<string> roles,
        long? instanceId,
        long? workflowId,
        string? workflowKey,
        string? businessKey,
        int? nodeId,
        string? nodeExternalId,
        IReadOnlyList<VariableFilter> variableFilters,
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
        IReadOnlyList<VariableFilter> variableFilters,
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
        IReadOnlyList<VariableFilter> variableFilters,
        bool includeVariables,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<WorkflowInstanceRecord?> GetInstanceAsync(long id, CancellationToken cancellationToken);

    Task<WorkflowInstanceRecord?> GetInstanceForUpdateAsync(
        long id,
        bool lockActiveUserTask,
        CancellationToken cancellationToken);

    Task<MultiInstanceExecutionRecord> AddMultiInstanceAsync(
        long instanceId,
        CurrentNodeSnapshot node,
        MultiInstanceModel configuration,
        IReadOnlyList<JsonElement?> items,
        IReadOnlyList<int> outcomeFlowIds,
        CancellationToken cancellationToken);

    Task<MultiInstanceExecutionRecord?> GetActiveMultiInstanceAsync(
        long instanceId,
        int nodeId,
        bool forUpdate,
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

    Task<PagedResult<UserTaskRecord>> ListUserTasksPageAsync(
        long instanceId,
        string? status,
        string user,
        IReadOnlyCollection<string> roles,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<UserTaskRecord>> ListExecutionTasksAsync(long executionId, CancellationToken cancellationToken);

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
        Dictionary<string, JsonElement> result,
        CancellationToken cancellationToken);

    Task CompleteUserTaskAsync(
        long taskId,
        int selectedFlowId,
        string completedBy,
        Dictionary<string, JsonElement> result,
        CancellationToken cancellationToken);

    Task ActivateNextMultiInstanceItemAsync(long executionId, CancellationToken cancellationToken);

    Task CloseMultiInstanceAsync(
        long executionId,
        int winningFlowId,
        string completionReason,
        CancellationToken cancellationToken);

    Task CancelActiveMultiInstanceAsync(long instanceId, CancellationToken cancellationToken);

    Task CancelOpenUserTasksAsync(long instanceId, CancellationToken cancellationToken);

    Task<DateTimeOffset> UpdateUserTaskClaimAsync(long taskId, string? claimedBy, CancellationToken cancellationToken);

    Task<DateTimeOffset> UpdateUserTaskAssignmentAsync(
        long taskId,
        string? assignee,
        bool requiresClaim,
        CancellationToken cancellationToken);

    Task<DateTimeOffset> TouchInstanceAsync(long id, CancellationToken cancellationToken);

    Task UpdateInstanceAsync(
        long id,
        int currentStepId,
        string status,
        string? claimedBy,
        CancellationToken cancellationToken);

    Task UpdateInstanceNodeAsync(
        long id,
        CurrentNodeSnapshot node,
        string status,
        string? claimedBy,
        CancellationToken cancellationToken);

    Task AddVariableAsync(
        long instanceId,
        string variableName,
        int? sourceActionId,
        string? setBy,
        JsonElement value,
        CancellationToken cancellationToken);

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
        CancellationToken cancellationToken);

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
        CancellationToken cancellationToken);

    Task AddUserTaskActionHistoryAsync(
        long instanceId,
        long tokenId,
        long userTaskId,
        int actionId,
        int fromStepId,
        int toStepId,
        string performedBy,
        Dictionary<string, JsonElement> payload,
        CancellationToken cancellationToken);

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
        CancellationToken cancellationToken);

    Task<IReadOnlyList<InstanceHistoryRecord>> ListHistoryAsync(
        long instanceId,
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
}

public interface IEngineSettingsRepository
{
    Task<EngineSettingRecord?> GetByKeyAsync(string key, CancellationToken cancellationToken);
    Task<IReadOnlyList<EngineSettingRecord>> SearchAsync(string pattern, CancellationToken cancellationToken);
    Task<EngineSettingRecord> SetAsync(string key, string value, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken);
}

public interface IWorkflowTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}

public interface IUnitOfWork
{
    Task<IWorkflowTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
