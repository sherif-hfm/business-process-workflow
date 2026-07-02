using System.Text.Json;
using WorkflowEngine.Service.Models;
using WorkflowEngine.Shared.Models;

namespace WorkflowEngine.Service.Abstractions;

public interface IWorkflowDefinitionRepository
{
    Task<IReadOnlyList<WorkflowDefinitionRecord>> ListLatestAsync(CancellationToken cancellationToken);

    Task<WorkflowDefinitionRecord?> GetAsync(long id, CancellationToken cancellationToken);

    Task<int> GetLatestVersionAsync(string name, CancellationToken cancellationToken);

    Task<WorkflowDefinitionRecord> AddAsync(
        string name,
        int version,
        WorkflowModel definition,
        bool isPublished,
        CancellationToken cancellationToken);

    Task<bool> SetPublishedAsync(long id, bool isPublished, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(long id, CancellationToken cancellationToken);
}

public interface IWorkflowRuntimeRepository
{
    Task<WorkflowInstanceRecord> AddInstanceAsync(
        long workflowDefinitionId,
        int currentStepId,
        string? startedBy,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkflowInstanceRecord>> ListInstancesAsync(
        string? status,
        CancellationToken cancellationToken);

    Task<WorkflowInstanceRecord?> GetInstanceAsync(long id, CancellationToken cancellationToken);

    Task<WorkflowInstanceRecord?> GetInstanceForUpdateAsync(long id, CancellationToken cancellationToken);

    Task UpdateInstanceAsync(
        long id,
        int currentStepId,
        string status,
        string? claimedBy,
        CancellationToken cancellationToken);

    Task AddVariableAsync(
        long instanceId,
        string variableName,
        int? sourceActionId,
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

    Task<IReadOnlyList<InstanceHistoryRecord>> ListHistoryAsync(
        long instanceId,
        CancellationToken cancellationToken);
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
