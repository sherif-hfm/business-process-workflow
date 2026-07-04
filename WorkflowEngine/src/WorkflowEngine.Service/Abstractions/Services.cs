using System.Text.Json;
using WorkflowEngine.Shared.Dtos;
using WorkflowEngine.Shared.Models;

namespace WorkflowEngine.Service.Abstractions;

/// <summary>
/// Identity of the caller performing an operation, derived from the validated JWT
/// (name + role claims). <see cref="Roles"/> is empty when the token carries none.
/// <see cref="Claims"/> carries the token's raw claims (type -> value, first wins);
/// the engine exposes only allowlisted entries as <c>sys.claim.*</c> context values.
/// </summary>
public sealed record ActorContext(
    string? User,
    IReadOnlyCollection<string> Roles,
    IReadOnlyDictionary<string, string> Claims)
{
    public static readonly ActorContext Anonymous =
        new(null, [], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Server-side configuration for read-only workflow context sources. <see cref="Config"/>
/// values are exposed as <c>config.*</c> placeholders/parameters (keeps secrets out of the
/// versioned definition JSON); <see cref="AllowedClaims"/> whitelists which JWT claims are
/// exposed as <c>sys.claim.*</c>. Neither is ever persisted to instance variables.
/// </summary>
public sealed class WorkflowContextOptions
{
    public const string SectionName = "WorkflowContext";

    public Dictionary<string, string> Config { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> AllowedClaims { get; set; } = [];
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

    Task<PagedResult<InstanceSummaryDto>> ListInstancesAsync(
        string? status,
        IReadOnlyList<string>? variables,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<PagedResult<InboxItemDto>> GetInboxAsync(
        ActorContext actor,
        IReadOnlyList<string>? variables,
        int page,
        int pageSize,
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

/// <summary>
/// A fully-resolved outgoing REST request for a service task (all ${var}
/// placeholders already substituted).
/// </summary>
public sealed record ServiceTaskRequest(
    string Method,
    string Url,
    IReadOnlyList<ServiceTaskHeader> Headers,
    string? Body,
    int TimeoutSeconds);

public sealed record ServiceTaskHeader(string Name, string Value);

/// <summary>
/// Outcome of a service-task REST call. <see cref="Completed"/> is false for
/// transport failures (timeout / network), in which case <see cref="StatusCode"/>
/// is 0. A completed call still reports its real <see cref="StatusCode"/> even
/// when it is not a 2xx.
/// </summary>
public sealed record ServiceTaskResult(
    bool Completed,
    int StatusCode,
    string? Body,
    string? Error)
{
    public bool IsSuccess => Completed && StatusCode is >= 200 and < 300;
}

/// <summary>
/// Executes a resolved <see cref="ServiceTaskRequest"/> against an external REST
/// endpoint. Implemented in the infrastructure layer over <c>HttpClient</c>.
/// </summary>
public interface IServiceTaskInvoker
{
    Task<ServiceTaskResult> InvokeAsync(ServiceTaskRequest request, CancellationToken cancellationToken);
}
