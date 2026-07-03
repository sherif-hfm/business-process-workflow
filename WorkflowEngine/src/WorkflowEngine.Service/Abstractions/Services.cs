using System.Text.Json;
using WorkflowEngine.Shared.Dtos;
using WorkflowEngine.Shared.Models;

namespace WorkflowEngine.Service.Abstractions;

/// <summary>
/// Identity of the caller performing an operation, derived from the validated JWT
/// (name + role claims). <see cref="Roles"/> is empty when the token carries none.
/// </summary>
public sealed record ActorContext(string? User, IReadOnlyCollection<string> Roles)
{
    public static readonly ActorContext Anonymous = new(null, []);
}

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
        ActorContext actor,
        int? startEventId,
        Dictionary<string, JsonElement>? variableValues,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<InstanceSummaryDto>> ListInstancesAsync(
        string? status,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<InboxItemDto>> GetInboxAsync(
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<InstanceDetailDto?> GetInstanceAsync(long id, CancellationToken cancellationToken);

    Task<IReadOnlyList<SequenceFlowModel>> GetAvailableFlowsAsync(
        long id,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<InstanceDetailDto?> ClaimAsync(long id, ActorContext actor, CancellationToken cancellationToken);

    Task<InstanceDetailDto?> UnclaimAsync(long id, CancellationToken cancellationToken);

    Task<InstanceDetailDto?> TakeFlowAsync(
        long id,
        int flowId,
        ActorContext actor,
        Dictionary<string, JsonElement>? variableValues,
        CancellationToken cancellationToken);

    Task<bool> CancelAsync(long id, CancellationToken cancellationToken);
}
