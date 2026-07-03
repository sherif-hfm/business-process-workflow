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

    [JsonPropertyName("variables")]
    public List<VariableModel> Variables { get; set; } = [];
}

public sealed class SequenceFlowModel
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

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
    public const string ExclusiveGateway = "exclusiveGateway";

    public static bool IsStart(string type) =>
        string.Equals(type, StartEvent, StringComparison.Ordinal);

    public static bool IsEnd(string type) =>
        string.Equals(type, EndEvent, StringComparison.Ordinal);

    public static bool IsUserTask(string type) =>
        string.Equals(type, UserTask, StringComparison.Ordinal);

    public static bool IsAutomatic(string type) =>
        string.Equals(type, Task, StringComparison.Ordinal);

    public static bool IsGateway(string type) =>
        string.Equals(type, ExclusiveGateway, StringComparison.Ordinal);

    public static bool IsPassThrough(string type) =>
        IsStart(type) || IsAutomatic(type) || IsGateway(type);

    public static bool IsSupported(string type) =>
        type is StartEvent or EndEvent or UserTask or Task or ExclusiveGateway;
}

public static class WorkflowVariableTypes
{
    public const string String = "string";
    public const string Number = "number";
    public const string Boolean = "boolean";
    public const string Date = "date";
    public const string DateTime = "datetime";
}
