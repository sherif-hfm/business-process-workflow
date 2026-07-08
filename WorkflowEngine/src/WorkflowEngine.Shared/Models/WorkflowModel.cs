using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkflowEngine.Shared.Models;

public sealed class WorkflowModel
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

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

    // A boundary event auto-advances down its single outgoing error flow once
    // the token is routed onto it, so it behaves as a pass-through node.
    public static bool IsPassThrough(string type) =>
        IsStart(type) || IsAutomatic(type) || IsServiceTask(type) || IsScriptTask(type) || IsGateway(type) || IsErrorBoundary(type);

    public static bool IsSupported(string type) =>
        type is StartEvent or EndEvent or UserTask or Task or ServiceTask or ScriptTask or ExclusiveGateway or ErrorEndEvent or ErrorBoundaryEvent;
}

public static class WorkflowVariableTypes
{
    public const string String = "string";
    public const string Number = "number";
    public const string Boolean = "boolean";
    public const string Date = "date";
    public const string DateTime = "datetime";
}
