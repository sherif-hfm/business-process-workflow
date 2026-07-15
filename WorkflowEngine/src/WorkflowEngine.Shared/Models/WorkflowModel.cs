using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkflowEngine.Shared.Models;

/// <summary>
/// Represents the complete structure and schema of a workflow definition.
/// </summary>
public sealed class WorkflowModel
{
    /// <summary>
    /// The unique identifier of the workflow definition (string or number).
    /// </summary>
    [JsonPropertyName("id")]
    [JsonConverter(typeof(WorkflowIdConverter))]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// The human-readable name of the workflow.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The ID of the initial start event node.
    /// </summary>
    [JsonPropertyName("initialEventId")]
    public int? InitialEventId { get; set; }

    // Process-level (instance-scoped) variable declarations. Unlike start-event
    // and sequence-flow variables these are never supplied by a user; they are
    // initialized from `defaultValue` at instance start and mutated by scriptTask
    // nodes during pass-through routing. Visible to gateways, service tasks, and
    // validation rules through the same WithContext overlay as other variables.
    /// <summary>
    /// The list of process-level variables declared in the workflow.
    /// </summary>
    [JsonPropertyName("variables")]
    public List<VariableModel> Variables { get; set; } = [];

    /// <summary>
    /// The list of swimlanes used to group nodes or define roles/permissions.
    /// </summary>
    [JsonPropertyName("lanes")]
    public List<LaneModel> Lanes { get; set; } = [];

    /// <summary>
    /// The list of flow nodes (steps, tasks, gateways, events) in the workflow.
    /// </summary>
    [JsonPropertyName("flowNodes")]
    public List<FlowNodeModel> FlowNodes { get; set; } = [];

    /// <summary>
    /// The list of sequence flows (directed edges) connecting the flow nodes.
    /// </summary>
    [JsonPropertyName("sequenceFlows")]
    public List<SequenceFlowModel> SequenceFlows { get; set; } = [];

    /// <summary>
    /// The list of roles allowed to cancel instances of this workflow.
    /// </summary>
    [JsonPropertyName("cancelRoles")]
    public List<string> CancelRoles { get; set; } = [];

    /// <summary>
    /// The list of roles allowed to unclaim tasks in this workflow.
    /// </summary>
    [JsonPropertyName("unclaimRoles")]
    public List<string> UnclaimRoles { get; set; } = [];

    // ---- Legacy read shims (older JSONB snapshots) ----
    // These are populated only when deserializing pre-BPMN documents and are
    // folded into the new structure by WorkflowModelMigrator. They are never
    // written back out (ignored when null).

    [JsonPropertyName("initialStepId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? LegacyInitialStepId { get; set; }

    [JsonPropertyName("phases")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<LaneModel>? LegacyPhases { get; set; }

    [JsonPropertyName("steps")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<LegacyStepModel>? LegacySteps { get; set; }
}

/// <summary>
/// Represents a swimlane container in a workflow layout.
/// </summary>
public sealed class LaneModel
{
    /// <summary>
    /// The unique integer ID of the lane.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>
    /// The display name of the lane.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// An optional external identifier mapping the lane to business structures.
    /// </summary>
    [JsonPropertyName("externalId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExternalId { get; set; }

    /// <summary>
    /// The X coordinate of the lane on the designer canvas.
    /// </summary>
    [JsonPropertyName("x")]
    public int X { get; set; }

    /// <summary>
    /// The Y coordinate of the lane on the designer canvas.
    /// </summary>
    [JsonPropertyName("y")]
    public int Y { get; set; }

    /// <summary>
    /// The width of the lane.
    /// </summary>
    [JsonPropertyName("w")]
    public int W { get; set; }

    /// <summary>
    /// The height of the lane.
    /// </summary>
    [JsonPropertyName("h")]
    public int H { get; set; }
}

/// <summary>
/// Represents a flow node (event, task, gateway) inside a workflow.
/// </summary>
public sealed class FlowNodeModel
{
    /// <summary>
    /// The unique integer ID of the node.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>
    /// The name of the node.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The user-defined external ID of the node.
    /// </summary>
    [JsonPropertyName("externalId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExternalId { get; set; }

    /// <summary>
    /// The type of the flow node (e.g. userTask, serviceTask, gateway, etc.).
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = BpmnFlowNodeTypes.UserTask;

    /// <summary>
    /// The ID of the lane this node resides in.
    /// </summary>
    [JsonPropertyName("laneId")]
    public int? LaneId { get; set; }

    /// <summary>
    /// The X coordinate on the designer canvas.
    /// </summary>
    [JsonPropertyName("x")]
    public int X { get; set; }

    /// <summary>
    /// The Y coordinate on the designer canvas.
    /// </summary>
    [JsonPropertyName("y")]
    public int Y { get; set; }

    /// <summary>
    /// The roles authorized to claim or act on this userTask.
    /// </summary>
    [JsonPropertyName("roles")]
    public List<string> Roles { get; set; } = [];

    /// <summary>
    /// Whether the task requires claiming before actions can be taken.
    /// </summary>
    [JsonPropertyName("requiresClaim")]
    public bool RequiresClaim { get; set; }

    /// <summary>
    /// How claims are inherited (fresh, previous, fromNode).
    /// </summary>
    [JsonPropertyName("claimMode")]
    public string ClaimMode { get; set; } = ClaimModes.Fresh;

    /// <summary>
    /// The node ID from which to inherit the claim when claimMode is fromNode.
    /// </summary>
    [JsonPropertyName("inheritClaimFromNodeId")]
    public int? InheritClaimFromNodeId { get; set; }

    /// <summary>
    /// Declared variables scope limit for this node.
    /// </summary>
    [JsonIgnore]
    public List<VariableModel> Variables { get; set; } = [];

    /// <summary>
    /// JSON bridge that omits the obsolete separate variables section for a
    /// messageStartEvent while preserving the common in-memory node API.
    /// </summary>
    [JsonPropertyName("variables")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<VariableModel>? SerializedVariables
    {
        get => BpmnFlowNodeTypes.IsMessageStart(Type) ? null : Variables;
        set => Variables = value ?? [];
    }

    /// <summary>
    /// Service task HTTP call configurations.
    /// </summary>
    [JsonPropertyName("service")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ServiceTaskModel? Service { get; set; }

    // intermediateMessageCatchEvent only: delivery configuration. The node rests
    // (like a userTask) until a matching message is delivered via
    // POST /api/instances/{id}/message, which authenticates against the expected
    // clientId/clientSecret + a required custom header, then maps the message
    // payload into instance variables via outputMappings and advances down the
    // single outgoing flow.
    /// <summary>
    /// Delivery and message correlation configuration for catch/start events.
    /// </summary>
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MessageCatchModel? Message { get; set; }

    /// <summary>
    /// Optional domain business-key configuration for a start or message-start event.
    /// </summary>
    [JsonPropertyName("businessKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BusinessKeyModel? BusinessKey { get; set; }

    /// <summary>
    /// Optional transport idempotency configuration for a start or message-start event.
    /// </summary>
    [JsonPropertyName("idempotency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IdempotencyModel? Idempotency { get; set; }

    // scriptTask only: which authoring mode is active. "ncalc" (default) uses
    // Assignments (NCalc expressions); "javascript" uses Script (a Jint-evaluated
    // JS body with a bound `execution` host object). Exactly one mode's data is
    // populated at a time; ApplyNodeInvariants clears the other.
    /// <summary>
    /// The script task format (ncalc or javascript).
    /// </summary>
    [JsonPropertyName("scriptFormat")]
    public string ScriptFormat { get; set; } = ScriptFormats.NCalc;

    // scriptTask only: ordered list of variable assignments executed during the
    // pass-through hop when scriptFormat is "ncalc". Each `expression` is an NCalc
    // expression evaluated against the current variables + sys.*/config.* context;
    // the typed result is coerced to the target variable's declared dataType and
    // persisted. A later assignment can reference an earlier one in the same list.
    /// <summary>
    /// Ordered list of assignments executed in NCalc format.
    /// </summary>
    [JsonPropertyName("assignments")]
    public List<AssignmentModel> Assignments { get; set; } = [];

    // scriptTask only, when scriptFormat is "javascript": a JavaScript body run by
    // Jint in a sandboxed Engine (no CLR access) with a bound `execution` host
    // object exposing getVariable/setVariable/getVariables/hasVariable.
    // setVariable targets must be declared process variables (model.Variables);
    // the value is coerced to the target's dataType and its validation re-checked,
    // exactly like an NCalc assignment.
    /// <summary>
    /// The JavaScript code body to execute (for script tasks).
    /// </summary>
    [JsonPropertyName("script")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Script { get; set; }

    // Normal userTask only: optional NCalc expression evaluated once when the
    // work item is created. A successful string result is snapshotted as the
    // runtime assignee; an unresolved/invalid result leaves the task unassigned.
    /// <summary>
    /// Optional NCalc expression that resolves the direct task assignee.
    /// </summary>
    [JsonPropertyName("assignee")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AssigneeExpression { get; set; }

    /// <summary>
    /// Optional multi-instance loop configuration for a user task.
    /// </summary>
    [JsonPropertyName("multiInstance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MultiInstanceModel? MultiInstance { get; set; }

    // errorBoundaryEvent only: the host activity (serviceTask/scriptTask) id this
    // boundary is attached to. The boundary renders on the host's border and its
    // single outgoing flow is the error path taken when the host fails at runtime.
    /// <summary>
    /// The ID of the host serviceTask or scriptTask this boundary catches errors for.
    /// </summary>
    [JsonPropertyName("attachedToRef")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? AttachedToRef { get; set; }

    // errorBoundaryEvent only, optional: when set, the failure reason is written
    // to this instance variable when the boundary catches a host failure, so the
    // error path can branch on or display it. No declaration required (mirrors
    // serviceTask statusVariable).
    /// <summary>
    /// The variable name where the failure reason is written when caught.
    /// </summary>
    [JsonPropertyName("errorVariable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorVariable { get; set; }
}

public static class ScriptFormats
{
    public const string NCalc = "ncalc";
    public const string JavaScript = "javascript";
}

/// <summary>
/// Represents a variable assignment inside a script task.
/// </summary>
public sealed class AssignmentModel
{
    /// <summary>
    /// The name of the process variable to assign the value to.
    /// </summary>
    [JsonPropertyName("variable")]
    public string Variable { get; set; } = string.Empty;

    /// <summary>
    /// The expression (NCalc) to evaluate and assign.
    /// </summary>
    [JsonPropertyName("expression")]
    public string Expression { get; set; } = string.Empty;
}

/// <summary>
/// Configures the HTTP REST call made by a service task.
/// </summary>
public sealed class ServiceTaskModel
{
    /// <summary>
    /// The HTTP method to use (e.g. GET, POST, PUT).
    /// </summary>
    [JsonPropertyName("method")]
    public string Method { get; set; } = "GET";

    /// <summary>
    /// The target REST API URL (supports variable interpolation).
    /// </summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Optional HTTP header values to send with the request.
    /// </summary>
    [JsonPropertyName("headers")]
    public List<ServiceHeaderModel> Headers { get; set; } = [];

    /// <summary>
    /// Optional body payload to send (for POST/PUT).
    /// </summary>
    [JsonPropertyName("body")]
    public string? Body { get; set; }

    /// <summary>
    /// Timeout limit in seconds before the HTTP request is considered failed.
    /// </summary>
    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Optional variable name to store the returned HTTP status code.
    /// </summary>
    [JsonPropertyName("statusVariable")]
    public string? StatusVariable { get; set; }

    /// <summary>
    /// Dotted-path maps to extract fields from the JSON response and write to variables.
    /// </summary>
    [JsonPropertyName("outputMappings")]
    public List<ServiceOutputMappingModel> OutputMappings { get; set; } = [];
}

/// <summary>
/// Represents a key-value HTTP header entry.
/// </summary>
public sealed class ServiceHeaderModel
{
    /// <summary>
    /// The HTTP header name (e.g., Authorization).
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The HTTP header value (supports variable interpolation).
    /// </summary>
    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// Maps a typed value from an HTTP service response into an instance variable.
/// </summary>
public sealed class ServiceOutputMappingModel
{
    /// <summary>
    /// The name of the process variable to write the extracted value to.
    /// </summary>
    [JsonPropertyName("variable")]
    public string Variable { get; set; } = string.Empty;

    /// <summary>
    /// The dotted path within the JSON response body to extract the value from.
    /// </summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    // When true, the engine treats a missing/unresolvable path as a failure:
    // for a serviceTask the call is failed (routing out an attached
    // errorBoundaryEvent or, with no boundary, a 400); for a message catch the
    // delivery is rejected with a 400 before any variables are written.
    /// <summary>
    /// If true, a missing or unresolvable path will fail the step.
    /// </summary>
    [JsonPropertyName("required")]
    public bool Required { get; set; }

    /// <summary>
    /// The scalar element type expected from the response.
    /// Null identifies a legacy raw mapping until normalization.
    /// </summary>
    [JsonPropertyName("dataType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DataType { get; set; }

    /// <summary>
    /// Whether the response value must be an array of <see cref="DataType"/>.
    /// </summary>
    [JsonPropertyName("isArray")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsArray { get; set; }

    /// <summary>
    /// Operation-specific fallback used only when the response path is absent.
    /// </summary>
    [JsonPropertyName("defaultValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? DefaultValue { get; set; }

    /// <summary>
    /// Optional NCalc rule evaluated against the final output overlay.
    /// </summary>
    [JsonPropertyName("validation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Validation { get; set; }
}

/// <summary>
/// Maps a typed value from an inbound message. On a message start the mapping is
/// also the complete declaration of the start variable. On an intermediate
/// message catch it is a typed, operation-specific write contract.
/// </summary>
public sealed class MessageOutputMappingModel
{
    [JsonPropertyName("variable")]
    public string Variable { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("required")]
    public bool Required { get; set; }

    /// <summary>
    /// Expected scalar element type. Null identifies a legacy raw mapping until
    /// the definition migrator canonicalizes it.
    /// </summary>
    [JsonPropertyName("dataType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DataType { get; set; }

    /// <summary>
    /// Whether the mapped value must be an array of <see cref="DataType"/>.
    /// </summary>
    [JsonPropertyName("isArray")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsArray { get; set; }

    [JsonPropertyName("defaultValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? DefaultValue { get; set; }

    [JsonPropertyName("validation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Validation { get; set; }
}

// Delivery configuration for an intermediateMessageCatchEvent. All scalar
// fields are ${var}-templatable (ServiceTaskTemplating.SubstituteScalar) against
// the instance variables + sys.*/config.*/setting.* context, so an expected
// secret can be sourced from ${config.apiSecret} to stay out of the definition
// JSON. The delivery caller (POST /api/instances/{id}/message) is authenticated
// against the resolved clientId/clientSecret (sent as X-Client-Id / X-Client-Secret
// headers) and must supply a header matching headerName/headerValue. When
// headerValidation is set, it is an NCalc rule evaluated with the incoming header
// value bound as `header` (plus instance vars/context); it must be truthy.
// Both message entry/catch outputMappings are typed contracts with defaults and
// NCalc validation. Message-start mappings additionally act as start-variable
// declarations.
/// <summary>
/// Delivery configuration for catching external messages or webhooks.
/// </summary>
public sealed class MessageCatchModel
{
    /// <summary>
    /// The expected OAuth/Client credential ID.
    /// </summary>
    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// The expected OAuth/Client credential secret.
    /// </summary>
    [JsonPropertyName("clientSecret")]
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>
    /// The required custom header name for correlation.
    /// </summary>
    [JsonPropertyName("headerName")]
    public string HeaderName { get; set; } = string.Empty;

    /// <summary>
    /// The required header value for validation.
    /// </summary>
    [JsonPropertyName("headerValue")]
    public string HeaderValue { get; set; } = string.Empty;

    /// <summary>
    /// An optional NCalc validation rule for additional header value checks.
    /// </summary>
    [JsonPropertyName("headerValidation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HeaderValidation { get; set; }

    /// <summary>
    /// Dotted-path maps to extract fields from the incoming JSON message body.
    /// </summary>
    [JsonPropertyName("outputMappings")]
    public List<MessageOutputMappingModel> OutputMappings { get; set; } = [];

    // Legacy messageStartEvent-only shape. WorkflowModelMigrator moves this value
    // into FlowNodeModel.Idempotency and clears it before canonical serialization.
    /// <summary>
    /// Optional start variable name used as an idempotency key (messageStartEvent only).
    /// </summary>
    [JsonPropertyName("idempotencyVariable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IdempotencyVariable { get; set; }
}

/// <summary>
/// Defines the claim modes for user tasks.
/// </summary>
public static class ClaimModes
{
    /// <summary>
    /// Fresh claim mode, unclaimed on entry.
    /// </summary>
    public const string Fresh = "fresh";

    /// <summary>
    /// Inherits the claim from the previous actor in the history.
    /// </summary>
    public const string Previous = "previous";

    /// <summary>
    /// Inherits the claim from a specific prior node.
    /// </summary>
    public const string FromNode = "fromNode";
}

/// <summary>
/// Represents a directed sequence flow (transition connection) in a workflow.
/// </summary>
public sealed class SequenceFlowModel
{
    /// <summary>
    /// The unique integer ID of the sequence flow.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>
    /// The name of the sequence flow.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The user-defined external ID of the sequence flow.
    /// </summary>
    [JsonPropertyName("externalId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExternalId { get; set; }

    /// <summary>
    /// The ID of the source flow node.
    /// </summary>
    [JsonPropertyName("sourceRef")]
    public int SourceRef { get; set; }

    /// <summary>
    /// The ID of the target flow node.
    /// </summary>
    [JsonPropertyName("targetRef")]
    public int TargetRef { get; set; }

    /// <summary>
    /// Roles authorized to transition along this flow (userTask flows only).
    /// </summary>
    [JsonPropertyName("roles")]
    public List<string> Roles { get; set; } = [];

    /// <summary>
    /// Required input variable declarations for this transition flow.
    /// </summary>
    [JsonPropertyName("variables")]
    public List<VariableModel> Variables { get; set; } = [];

    /// <summary>
    /// The NCalc condition expression evaluated to determine if this flow is taken.
    /// </summary>
    [JsonPropertyName("condition")]
    public string? Condition { get; set; }

    /// <summary>
    /// If true, this flow acts as the default fallback when other conditions fail.
    /// </summary>
    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; set; }

    /// <summary>
    /// Whether this multi-instance user-task flow is exposed as an action that an
    /// actor may select. Engine-only completion/default routes set this to false.
    /// </summary>
    [JsonPropertyName("isSelectable")]
    public bool IsSelectable { get; set; } = true;

    /// <summary>
    /// If true, an actor can trigger this flow without claiming the userTask first.
    /// </summary>
    [JsonPropertyName("canActWithoutClaim")]
    public bool CanActWithoutClaim { get; set; }

    /// <summary>
    /// Aggregate NCalc condition evaluated after a multi-instance item completes.
    /// </summary>
    [JsonPropertyName("completionCondition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CompletionCondition { get; set; }

    /// <summary>
    /// Lower values win when several multi-instance completion conditions match.
    /// </summary>
    [JsonPropertyName("completionPriority")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CompletionPriority { get; set; }

    /// <summary>
    /// Whether selecting this flow immediately cancels the other multi-instance items.
    /// </summary>
    [JsonPropertyName("cancelRemainingInstances")]
    public bool CancelRemainingInstances { get; set; }
}

/// <summary>
/// Configures the start variable used as a workflow-family business key.
/// </summary>
public sealed class BusinessKeyModel
{
    [JsonPropertyName("variable")]
    public string Variable { get; set; } = string.Empty;

    [JsonPropertyName("uniqueness")]
    public string Uniqueness { get; set; } = null!;
}

/// <summary>
/// Configures the header-sourced transport retry key for an entry event.
/// </summary>
public sealed class IdempotencyModel
{
    [JsonPropertyName("headerName")]
    public string HeaderName { get; set; } = IdempotencyHeaders.Standard;

    [JsonPropertyName("variable")]
    public string Variable { get; set; } = string.Empty;
}

public static class IdempotencyHeaders
{
    public const string Standard = "Idempotency-Key";
    public const string LegacyAlias = "X-Idempotency-Key";
}

public static class BusinessKeyUniqueness
{
    public const string Active = "active";
    public const string All = "all";
}

/// <summary>
/// Configures parallel or sequential repetitions of a user task.
/// </summary>
public sealed class MultiInstanceModel
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = MultiInstanceModes.Parallel;

    [JsonPropertyName("source")]
    public string Source { get; set; } = MultiInstanceSources.Collection;

    [JsonPropertyName("collectionVariable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CollectionVariable { get; set; }

    [JsonPropertyName("cardinalityExpression")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CardinalityExpression { get; set; }

    /// <summary>
    /// When true for a cardinality source, each authenticated actor may complete
    /// at most one item in this multi-instance execution.
    /// </summary>
    [JsonPropertyName("onePerActor")]
    public bool OnePerActor { get; set; }

    /// <summary>
    /// Controls whether aggregate completion conditions are evaluated after each
    /// completed item or only after every item has completed.
    /// </summary>
    [JsonPropertyName("completionEvaluation")]
    public string CompletionEvaluation { get; set; } = MultiInstanceCompletionEvaluations.AfterEach;

    [JsonPropertyName("resultVariable")]
    public string ResultVariable { get; set; } = string.Empty;
}

public static class MultiInstanceModes
{
    public const string Parallel = "parallel";
    public const string Sequential = "sequential";
}

public static class MultiInstanceSources
{
    public const string Collection = "collection";
    public const string Cardinality = "cardinality";
}

public static class MultiInstanceCompletionEvaluations
{
    public const string AfterEach = "afterEach";
    public const string AfterAll = "afterAll";
}

public static class UserTaskConstraints
{
    public const int MaxActorNameLength = 300;
}

/// <summary>
/// Represents a variable declaration model within a scope.
/// </summary>
public sealed class VariableModel
{
    /// <summary>
    /// The unique integer ID of the variable declaration.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>
    /// The declared variable name.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The data type of the variable (string, number, boolean, date, datetime).
    /// </summary>
    [JsonPropertyName("dataType")]
    public string DataType { get; set; } = WorkflowVariableTypes.String;

    /// <summary>
    /// If true, the variable holds an array of values instead of a single scalar.
    /// </summary>
    [JsonPropertyName("isArray")]
    public bool IsArray { get; set; }

    /// <summary>
    /// If true, the variable must be supplied before advancing the transition.
    /// </summary>
    [JsonPropertyName("required")]
    public bool Required { get; set; }

    // Optional variables only: applied (and persisted) when no value is supplied
    // at start / flow-take. Held as raw JSON so any data type round-trips. A string
    // default may contain ${...} placeholders (other variables + sys.*/config.*
    // context) that are resolved before the value is coerced and persisted.
    /// <summary>
    /// The default value used when no input is supplied.
    /// </summary>
    [JsonPropertyName("defaultValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? DefaultValue { get; set; }

    // Optional NCalc expression validated at start / flow-take against the final
    // collected values (supplied + resolved defaults) overlaid with sys.*/config.*
    // context. A falsy or unresolvable expression rejects the operation. Empty means
    // no rule. Parse-checked at author time in ValidateDefinition.
    /// <summary>
    /// An optional NCalc validation expression.
    /// </summary>
    [JsonPropertyName("validation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Validation { get; set; }
}

public sealed class LegacyStepModel
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "userTask";

    [JsonPropertyName("phaseId")]
    public int? PhaseId { get; set; }

    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }

    [JsonPropertyName("roles")]
    public List<string> Roles { get; set; } = [];

    [JsonPropertyName("requiresClaim")]
    public bool RequiresClaim { get; set; }

    [JsonPropertyName("autoAdvance")]
    public bool AutoAdvance { get; set; }

    [JsonPropertyName("nextStepId")]
    public int? NextStepId { get; set; }

    [JsonPropertyName("variables")]
    public List<VariableModel> Variables { get; set; } = [];

    [JsonPropertyName("actions")]
    public List<LegacyActionModel> Actions { get; set; } = [];
}

public sealed class LegacyActionModel
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("toStepId")]
    public int ToStepId { get; set; }

    [JsonPropertyName("roles")]
    public List<string> Roles { get; set; } = [];

    [JsonPropertyName("variables")]
    public List<VariableModel> Variables { get; set; } = [];
}

public static class BpmnFlowNodeTypes
{
    public const string StartEvent = "startEvent";
    public const string EndEvent = "endEvent";
    public const string UserTask = "userTask";
    public const string Task = "task";
    public const string ServiceTask = "serviceTask";
    public const string ScriptTask = "scriptTask";
    public const string ExclusiveGateway = "exclusiveGateway";
    // Terminal event that ends the instance with the Faulted status (vs the
    // Completed status set by a plain endEvent). Typically reached via an
    // errorBoundaryEvent's error path, directly or through a handler task.
    public const string ErrorEndEvent = "errorEndEvent";
    // Boundary event attached to a serviceTask/scriptTask (attachedToRef). When
    // the host fails at runtime, the token routes out the boundary's single
    // outgoing "error" flow instead of failing the transition.
    public const string ErrorBoundaryEvent = "errorBoundaryEvent";
    // Intermediate catch event that rests (like a userTask) until a matching
    // message is delivered via POST /api/instances/{id}/message, then advances
    // down its single outgoing flow. Async integration / webhook / callback step.
    public const string IntermediateMessageCatchEvent = "intermediateMessageCatchEvent";
    // Entry event started by an external system via
    // POST /api/workflows/{workflowKey}/message-start. Typed outputMappings on its
    // message config are the start-variable declarations. Optional transport
    // idempotency is the generic node-level Idempotency contract. IsStart is intentionally
    // false: the user POST /api/instances path rejects it, so a message-start
    // event is system-only. Pass-through: the engine auto-advances off it after
    // creating the instance (history note "messageStart").
    public const string MessageStartEvent = "messageStartEvent";

    public static bool IsStart(string type) =>
        string.Equals(type, StartEvent, StringComparison.Ordinal);

    // A plain endEvent completes the instance; an errorEndEvent faults it. Both
    // are terminal (no outgoing flows) and share the end-like invariants.
    public static bool IsEnd(string type) =>
        string.Equals(type, EndEvent, StringComparison.Ordinal)
        || string.Equals(type, ErrorEndEvent, StringComparison.Ordinal);

    public static bool IsErrorEnd(string type) =>
        string.Equals(type, ErrorEndEvent, StringComparison.Ordinal);

    public static bool IsErrorBoundary(string type) =>
        string.Equals(type, ErrorBoundaryEvent, StringComparison.Ordinal);

    public static bool IsUserTask(string type) =>
        string.Equals(type, UserTask, StringComparison.Ordinal);

    public static bool IsAutomatic(string type) =>
        string.Equals(type, Task, StringComparison.Ordinal);

    public static bool IsServiceTask(string type) =>
        string.Equals(type, ServiceTask, StringComparison.Ordinal);

    public static bool IsScriptTask(string type) =>
        string.Equals(type, ScriptTask, StringComparison.Ordinal);

    public static bool IsGateway(string type) =>
        string.Equals(type, ExclusiveGateway, StringComparison.Ordinal);

    public static bool IsMessageCatch(string type) =>
        string.Equals(type, IntermediateMessageCatchEvent, StringComparison.Ordinal);

    public static bool IsMessageStart(string type) =>
        string.Equals(type, MessageStartEvent, StringComparison.Ordinal);

    // Either kind of entry event: a user-facing startEvent or a system-only
    // messageStartEvent. A workflow must have at least one entry event.
    public static bool IsEntry(string type) =>
        IsStart(type) || IsMessageStart(type);

    // A boundary event auto-advances down its single outgoing error flow once
    // the token is routed onto it, so it behaves as a pass-through node.
    // A message catch event is intentionally NOT pass-through: it rests until a
    // message is delivered (ResolvePassThroughAsync stops on it).
    // A message start event IS pass-through: the engine creates the instance on
    // it then auto-advances, exactly like a plain startEvent.
    public static bool IsPassThrough(string type) =>
        IsStart(type) || IsMessageStart(type) || IsAutomatic(type) || IsServiceTask(type) || IsScriptTask(type) || IsGateway(type) || IsErrorBoundary(type);

    public static bool IsSupported(string type) =>
        type is StartEvent or EndEvent or UserTask or Task or ServiceTask or ScriptTask or ExclusiveGateway or ErrorEndEvent or ErrorBoundaryEvent or IntermediateMessageCatchEvent or MessageStartEvent;
}

public static class WorkflowVariableTypes
{
    public const string String = "string";
    public const string Number = "number";
    public const string Boolean = "boolean";
    public const string Date = "date";
    public const string DateTime = "datetime";
    public const string Json = "json";
}

public sealed class WorkflowIdConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            if (reader.TryGetInt64(out long longVal)) return longVal.ToString();
            if (reader.TryGetDouble(out double doubleVal)) return doubleVal.ToString();
        }
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString() ?? string.Empty;
        }
        throw new JsonException("Workflow ID must be a number or string.");
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}
