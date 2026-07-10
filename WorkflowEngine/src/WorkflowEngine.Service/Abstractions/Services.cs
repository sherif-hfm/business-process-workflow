using System.Text.Json;
using WorkflowEngine.Service.Models;
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
        long? workflowId,
        string? workflowKey,
        ActorContext actor,
        int? startEventId,
        Dictionary<string, JsonElement>? variableValues,
        CancellationToken cancellationToken);

    /// <summary>
    /// Starts a new instance and returns a slim result (no definition/variables/
    /// history). This is the high-throughput path: it avoids the 4 extra SELECTs
    /// that <see cref="StartInstanceAsync"/> runs in BuildDetailAsync. Use this
    /// overload when the caller only needs the instance id and resting node.
    /// </summary>
    Task<StartInstanceResultDto> StartInstanceSlimAsync(
        long? workflowId,
        string? workflowKey,
        ActorContext actor,
        int? startEventId,
        Dictionary<string, JsonElement>? variableValues,
        CancellationToken cancellationToken);

    /// <summary>
    /// Starts a new instance by delivering a message to a <c>messageStartEvent</c>,
    /// authenticating the caller against the node's expected client id/secret +
    /// required custom header, mapping the message payload into the node's start
    /// variables via <c>outputMappings</c>, and (when <c>idempotencyVariable</c> is
    /// set) deduping a retried webhook by searching for an existing instance of
    /// the workflowKey already carrying that key value. Returns a slim ack (no
    /// definition/variables/history). Throws <c>WorkflowUnauthorizedException</c>
    /// (401) on a client id/secret mismatch and <c>WorkflowDomainException</c>
    /// (400) for a header problem, a required-mapping failure, no published
    /// version, or an ambiguous/absent start event.
    /// </summary>
    Task<MessageStartAckDto> StartByMessageAsync(
        string workflowKey,
        string? startEventExternalId,
        IncomingMessage message,
        CancellationToken cancellationToken);

    Task<PagedResult<InstanceSummaryDto>> ListInstancesAsync(
        string? status,
        long? instanceId,
        long? workflowId,
        string? workflowKey,
        int? nodeId,
        string? nodeExternalId,
        IReadOnlyList<string>? variables,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<PagedResult<InboxItemDto>> GetInboxAsync(
        ActorContext actor,
        long? instanceId,
        long? workflowId,
        string? workflowKey,
        int? nodeId,
        string? nodeExternalId,
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

    Task<InstanceDetailDto?> UnclaimAsync(long id, ActorContext actor, CancellationToken cancellationToken);

    Task<InstanceDetailDto?> TakeFlowAsync(
        long id,
        int flowId,
        ActorContext actor,
        Dictionary<string, JsonElement>? variableValues,
        CancellationToken cancellationToken);

    /// <summary>
    /// Delivers a message to an instance currently resting on an
    /// <c>intermediateMessageCatchEvent</c>, authenticating the caller against the
    /// node's expected client id/secret + required custom header, mapping the
    /// message payload into instance variables, and advancing down the single
    /// outgoing flow. Returns a slim ack (no definition/variables/history) - null
    /// when the instance does not exist (404); throws
    /// <c>WorkflowUnauthorizedException</c> on a client id/secret mismatch (401)
    /// and <c>WorkflowDomainException</c> for a header problem (missing/mismatch/
    /// validation failure) or when the instance is not running or not resting on a
    /// message catch node (400).
    /// </summary>
    Task<MessageDeliveryAckDto?> DeliverMessageAsync(
        long id,
        IncomingMessage message,
        CancellationToken cancellationToken);

    Task<bool> CancelAsync(long id, ActorContext actor, CancellationToken cancellationToken);
}

/// <summary>
/// An inbound message delivered to an instance resting on an
/// <c>intermediateMessageCatchEvent</c>. <see cref="ClientId"/>/<see cref="ClientSecret"/>
/// are taken from the <c>X-Client-Id</c>/<c>X-Client-Secret</c> request headers;
/// <see cref="Headers"/> is the full request header collection (the catch node names
/// its required header in configuration); <see cref="Payload"/> is the raw JSON body
/// from which <c>outputMappings</c> extract values. <see cref="Actor"/> carries the
/// resolved client id as the user for attribution/context (no JWT roles).
/// </summary>
public sealed record IncomingMessage(
    string? ClientId,
    string? ClientSecret,
    IReadOnlyDictionary<string, string?> Headers,
    JsonElement? Payload,
    ActorContext Actor);

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

/// <summary>
/// The host surface a scriptTask's JavaScript body sees as the bound
/// <c>execution</c> object. Reads see stored instance variables overlaid with
/// read-only <c>sys.*</c>/<c>config.*</c> context (same map as NCalc assignments);
/// <see cref="SetVariable"/> stages a write that the engine validates against the
/// declared process variable (name + dataType coercion) before persisting -
/// mirroring an NCalc assignment. Implemented by <c>WorkflowEngineService</c> so
/// the coercion/declaration rules stay in one place; the evaluator (Jint) only
/// talks to this interface and never sees <c>VariableModel</c>.
/// </summary>
public interface IScriptContext
{
    bool TryGetVariable(string name, out JsonElement value);

    bool HasVariable(string name);

    IReadOnlyDictionary<string, JsonElement> GetVariables();

    /// <summary>
    /// Stages a write for a declared process variable. <paramref name="rawValue"/>
    /// is the JS value marshalled to JSON as-is (untyped); the context coerces it
    /// to the target variable's declared dataType. Throws
    /// <c>WorkflowDomainException</c> when the name is not a declared process
    /// variable, mirroring the NCalc-assignment rule.
    /// </summary>
    void SetVariable(string name, JsonElement rawValue);
}

/// <summary>
/// Outcome of running a scriptTask JavaScript body. <see cref="Success"/> is false
/// for a script error, a parse failure, or an execution-constraint violation
/// (timeout / memory / statement limit); <see cref="Error"/> then carries a
/// human-readable reason that the engine wraps in a <c>WorkflowDomainException</c>
/// (rolling back the transition).
/// </summary>
public sealed record ScriptResult(bool Success, string? Error)
{
    public static readonly ScriptResult Ok = new(true, null);

    public static ScriptResult Fail(string error) => new(false, error);
}

/// <summary>
/// Execution limits for scriptTask JavaScript bodies, bound from the
/// <c>WorkflowScript</c> configuration section. Applied per-call by the
/// <see cref="IScriptEvaluator"/> implementation (Jint execution constraints);
/// defaults are conservative since script authors are trusted definition authors
/// but scripts still run inside a locked database transaction.
/// </summary>
public sealed class ScriptOptions
{
    public const string SectionName = "WorkflowScript";

    /// <summary>Wall-clock timeout for a single script execution.</summary>
    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>Maximum number of JS statements a single execution may run.</summary>
    public int MaxStatements { get; set; } = 100_000;

    /// <summary>Maximum bytes a single execution may allocate.</summary>
    public long MemoryBytes { get; set; } = 8_000_000;
}

/// <summary>
/// Evaluates a scriptTask JavaScript body against an <see cref="IScriptContext"/>.
/// Implemented in the infrastructure layer over Jint, in a sandboxed
/// <c>Engine</c> with no CLR access (no filesystem/network/reflection) and bounded
/// by <see cref="ScriptOptions"/>.
/// </summary>
public interface IScriptEvaluator
{
    ScriptResult Evaluate(string script, IScriptContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Parse-only syntax check used for author-time validation
    /// (<c>ValidateDefinition</c>); does not execute the script.
    /// </summary>
    bool IsValid(string script, out string? error);
}

public interface IEngineSettingsService
{
    Task<EngineSettingRecord?> GetByKeyAsync(string key, CancellationToken cancellationToken);
    Task<IReadOnlyList<EngineSettingRecord>> SearchAsync(string pattern, CancellationToken cancellationToken);
    Task<EngineSettingRecord> SetAsync(string key, string value, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken);
}
