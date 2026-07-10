using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkflowEngine.Shared.Models;

public sealed class WorkflowModel
{
    [JsonPropertyName("id")]
    [JsonConverter(typeof(WorkflowIdConverter))]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("initialEventId")]
    public int? InitialEventId { get; set; }

    // Process-level (instance-scoped) variable declarations. Unlike start-event
    // and sequence-flow variables these are never supplied by a user; they are
    // initialized from `defaultValue` at instance start and mutated by scriptTask
    // nodes during pass-through routing. Visible to gateways, service tasks, and
    // validation rules through the same WithContext overlay as other variables.
    [JsonPropertyName("variables")]
    public List<VariableModel> Variables { get; set; } = [];

    [JsonPropertyName("lanes")]
    public List<LaneModel> Lanes { get; set; } = [];

    [JsonPropertyName("flowNodes")]
    public List<FlowNodeModel> FlowNodes { get; set; } = [];

    [JsonPropertyName("sequenceFlows")]
    public List<SequenceFlowModel> SequenceFlows { get; set; } = [];

    [JsonPropertyName("cancelRoles")]
    public List<string> CancelRoles { get; set; } = [];

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

public sealed class LaneModel
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("externalId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExternalId { get; set; }

    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }

    [JsonPropertyName("w")]
    public int W { get; set; }

    [JsonPropertyName("h")]
    public int H { get; set; }
}

public sealed class FlowNodeModel
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("externalId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExternalId { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = BpmnFlowNodeTypes.UserTask;

    [JsonPropertyName("laneId")]
    public int? LaneId { get; set; }

    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }

    [JsonPropertyName("roles")]
    public List<string> Roles { get; set; } = [];

    [JsonPropertyName("requiresClaim")]
    public bool RequiresClaim { get; set; }

    [JsonPropertyName("claimMode")]
    public string ClaimMode { get; set; } = ClaimModes.Fresh;

    [JsonPropertyName("inheritClaimFromNodeId")]
    public int? InheritClaimFromNodeId { get; set; }

    [JsonPropertyName("variables")]
    public List<VariableModel> Variables { get; set; } = [];

    [JsonPropertyName("service")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ServiceTaskModel? Service { get; set; }

    // intermediateMessageCatchEvent only: delivery configuration. The node rests
    // (like a userTask) until a matching message is delivered via
    // POST /api/instances/{id}/message, which authenticates against the expected
    // clientId/clientSecret + a required custom header, then maps the message
    // payload into instance variables via outputMappings and advances down the
    // single outgoing flow.
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MessageCatchModel? Message { get; set; }

    // scriptTask only: which authoring mode is active. "ncalc" (default) uses
    // Assignments (NCalc expressions); "javascript" uses Script (a Jint-evaluated
    // JS body with a bound `execution` host object). Exactly one mode's data is
    // populated at a time; ApplyNodeInvariants clears the other.
    [JsonPropertyName("scriptFormat")]
    public string ScriptFormat { get; set; } = ScriptFormats.NCalc;

    // scriptTask only: ordered list of variable assignments executed during the
    // pass-through hop when scriptFormat is "ncalc". Each `expression` is an NCalc
    // expression evaluated against the current variables + sys.*/config.* context;
    // the typed result is coerced to the target variable's declared dataType and
    // persisted. A later assignment can reference an earlier one in the same list.
    [JsonPropertyName("assignments")]
    public List<AssignmentModel> Assignments { get; set; } = [];

    // scriptTask only, when scriptFormat is "javascript": a JavaScript body run by
    // Jint in a sandboxed Engine (no CLR access) with a bound `execution` host
    // object exposing getVariable/setVariable/getVariables/hasVariable.
    // setVariable targets must be declared process variables (model.Variables);
    // the value is coerced to the target's dataType and its validation re-checked,
    // exactly like an NCalc assignment.
    [JsonPropertyName("script")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Script { get; set; }

    // userTask only: optional NCalc visibility gate. When present and false, the
    // task is hidden from the inbox and no outgoing flows can be shown or taken.
    // It does not affect routing; the instance still rests on the node.
    [JsonPropertyName("condition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Condition { get; set; }

    // errorBoundaryEvent only: the host activity (serviceTask/scriptTask) id this
    // boundary is attached to. The boundary renders on the host's border and its
    // single outgoing flow is the error path taken when the host fails at runtime.
    [JsonPropertyName("attachedToRef")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? AttachedToRef { get; set; }

    // errorBoundaryEvent only, optional: when set, the failure reason is written
    // to this instance variable when the boundary catches a host failure, so the
    // error path can branch on or display it. No declaration required (mirrors
    // serviceTask statusVariable).
    [JsonPropertyName("errorVariable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorVariable { get; set; }
}

public static class ScriptFormats
{
    public const string NCalc = "ncalc";
    public const string JavaScript = "javascript";
}

public sealed class AssignmentModel
{
    [JsonPropertyName("variable")]
    public string Variable { get; set; } = string.Empty;

    [JsonPropertyName("expression")]
    public string Expression { get; set; } = string.Empty;
}

public sealed class ServiceTaskModel
{
    [JsonPropertyName("method")]
    public string Method { get; set; } = "GET";

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("headers")]
    public List<ServiceHeaderModel> Headers { get; set; } = [];

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; set; } = 30;

    [JsonPropertyName("statusVariable")]
    public string? StatusVariable { get; set; }

    [JsonPropertyName("outputMappings")]
    public List<ServiceOutputMappingModel> OutputMappings { get; set; } = [];
}

public sealed class ServiceHeaderModel
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;
}

public sealed class ServiceOutputMappingModel
{
    [JsonPropertyName("variable")]
    public string Variable { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    // When true, the engine treats a missing/unresolvable path as a failure:
    // for a serviceTask the call is failed (routing out an attached
    // errorBoundaryEvent or, with no boundary, a 400); for a message catch the
    // delivery is rejected with a 400 before any variables are written.
    [JsonPropertyName("required")]
    public bool Required { get; set; }
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
// outputMappings extract dotted-path values from the inbound JSON message body
// and write them to instance variables raw/uncoerced (mirrors serviceTask).
public sealed class MessageCatchModel
{
    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = string.Empty;

    [JsonPropertyName("clientSecret")]
    public string ClientSecret { get; set; } = string.Empty;

    [JsonPropertyName("headerName")]
    public string HeaderName { get; set; } = string.Empty;

    [JsonPropertyName("headerValue")]
    public string HeaderValue { get; set; } = string.Empty;

    [JsonPropertyName("headerValidation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HeaderValidation { get; set; }

    [JsonPropertyName("outputMappings")]
    public List<ServiceOutputMappingModel> OutputMappings { get; set; } = [];

    // messageStartEvent only (ignored by an intermediateMessageCatchEvent): names a
    // declared start variable on the node whose value is used as the idempotency
    // key. The variable must be mapped by outputMappings; before creating an
    // instance the engine searches for an existing instance of this workflowKey
    // already carrying that key value and, if found, returns that instance's ack
    // instead of creating a duplicate (so a retried webhook is a no-op). The
    // search is guarded by a transaction-scoped advisory lock so concurrent retries
    // serialize. null disables idempotency (a retry creates a duplicate).
    [JsonPropertyName("idempotencyVariable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IdempotencyVariable { get; set; }
}

public static class ClaimModes
{
    public const string Fresh = "fresh";
    public const string Previous = "previous";
    public const string FromNode = "fromNode";
}

public sealed class SequenceFlowModel
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("externalId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExternalId { get; set; }

    [JsonPropertyName("sourceRef")]
    public int SourceRef { get; set; }

    [JsonPropertyName("targetRef")]
    public int TargetRef { get; set; }

    [JsonPropertyName("roles")]
    public List<string> Roles { get; set; } = [];

    [JsonPropertyName("variables")]
    public List<VariableModel> Variables { get; set; } = [];

    [JsonPropertyName("condition")]
    public string? Condition { get; set; }

    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; set; }

    [JsonPropertyName("canActWithoutClaim")]
    public bool CanActWithoutClaim { get; set; }
}

public sealed class VariableModel
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("dataType")]
    public string DataType { get; set; } = WorkflowVariableTypes.String;

    [JsonPropertyName("isArray")]
    public bool IsArray { get; set; }

    [JsonPropertyName("required")]
    public bool Required { get; set; }

    // Optional variables only: applied (and persisted) when no value is supplied
    // at start / flow-take. Held as raw JSON so any data type round-trips. A string
    // default may contain ${...} placeholders (other variables + sys.*/config.*
    // context) that are resolved before the value is coerced and persisted.
    [JsonPropertyName("defaultValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? DefaultValue { get; set; }

    // Optional NCalc expression validated at start / flow-take against the final
    // collected values (supplied + resolved defaults) overlaid with sys.*/config.*
    // context. A falsy or unresolvable expression rejects the operation. Empty means
    // no rule. Parse-checked at author time in ValidateDefinition.
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
    // POST /api/workflows/{workflowKey}/message-start. Carries start variables
    // (like a startEvent) and a message config (credentials + required header +
    // outputMappings + optional idempotencyVariable). IsStart is intentionally
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
