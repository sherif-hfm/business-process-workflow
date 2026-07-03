using System.Text.Json.Serialization;

namespace WorkflowEngine.Shared.Models;

public sealed class WorkflowModel
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("initialStepId")]
    public int? InitialStepId { get; set; }

    [JsonPropertyName("phases")]
    public List<PhaseModel> Phases { get; set; } = [];

    [JsonPropertyName("steps")]
    public List<StepModel> Steps { get; set; } = [];
}

public sealed class PhaseModel
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

public sealed class StepModel
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = WorkflowStepTypes.UserTask;

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
    public List<ActionModel> Actions { get; set; } = [];
}

public sealed class ActionModel
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

public static class WorkflowStepTypes
{
    public const string StartEvent = "startEvent";
    public const string EndEvent = "endEvent";
    public const string UserTask = "userTask";
    public const string Task = "task";

    public static bool IsStart(string type) =>
        type is StartEvent or LegacyStart;

    public static bool IsEnd(string type) =>
        type is EndEvent or LegacyEnd;

    public static bool IsUserTask(string type) =>
        type is UserTask;

    public static bool IsAutomatic(string type) =>
        string.Equals(type, Task, StringComparison.Ordinal);

    public static bool IsSupported(string type) =>
        type is StartEvent or EndEvent or UserTask or Task;

    private const string LegacyStart = "start";
    private const string LegacyTask = "task";
    private const string LegacyEnd = "end";
}

public static class WorkflowVariableTypes
{
    public const string String = "string";
    public const string Number = "number";
    public const string Boolean = "boolean";
    public const string Date = "date";
    public const string DateTime = "datetime";
}
