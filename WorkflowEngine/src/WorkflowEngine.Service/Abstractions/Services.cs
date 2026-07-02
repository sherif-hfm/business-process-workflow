using System.Text.Json;
using WorkflowEngine.Shared.Dtos;
using WorkflowEngine.Shared.Models;

namespace WorkflowEngine.Service.Abstractions;

public interface IWorkflowDefinitionService
{
    Task<IReadOnlyList<WorkflowSummaryDto>> ListLatestAsync(CancellationToken cancellationToken);

    Task<WorkflowDetailDto?> GetAsync(long id, CancellationToken cancellationToken);

    Task<WorkflowDetailDto> CreateAsync(
        WorkflowModel definition,
        bool publish,
        CancellationToken cancellationToken);

    Task<WorkflowDetailDto?> CreateNewVersionAsync(
        long sourceWorkflowId,
        WorkflowModel definition,
        bool publish,
        CancellationToken cancellationToken);

    Task<bool> PublishAsync(long id, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(long id, CancellationToken cancellationToken);
}

public interface IWorkflowEngineService
{
    Task<InstanceDetailDto> StartInstanceAsync(
        long workflowId,
        string? startedBy,
        Dictionary<string, JsonElement>? variableValues,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<InstanceSummaryDto>> ListInstancesAsync(
        string? status,
        CancellationToken cancellationToken);

    Task<InstanceDetailDto?> GetInstanceAsync(long id, CancellationToken cancellationToken);

    Task<IReadOnlyList<ActionModel>> GetAvailableActionsAsync(long id, CancellationToken cancellationToken);

    Task<InstanceDetailDto?> ClaimAsync(long id, string? user, CancellationToken cancellationToken);

    Task<InstanceDetailDto?> UnclaimAsync(long id, CancellationToken cancellationToken);

    Task<InstanceDetailDto?> TakeActionAsync(
        long id,
        int actionId,
        string? performedBy,
        Dictionary<string, JsonElement>? variableValues,
        CancellationToken cancellationToken);

    Task<bool> CancelAsync(long id, CancellationToken cancellationToken);
}
