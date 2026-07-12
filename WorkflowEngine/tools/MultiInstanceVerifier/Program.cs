using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WorkflowEngine.Service.Abstractions;
using WorkflowEngine.Service.Services;
using WorkflowEngine.Shared.Models;

var sample = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "workflow-votes.json"));
var model = JsonSerializer.Deserialize<WorkflowModel>(await File.ReadAllTextAsync(sample))
    ?? throw new InvalidOperationException("The quorum sample did not deserialize.");
WorkflowModelMigrator.Normalize(model);

var validator = new WorkflowDefinitionService(
    null!, new ParseOnlyScriptEvaluator(), NullLogger<WorkflowDefinitionService>.Instance);
var validate = typeof(WorkflowDefinitionService).GetMethod(
    "ValidateDefinition", BindingFlags.Instance | BindingFlags.NonPublic)
    ?? throw new InvalidOperationException("ValidateDefinition was not found.");
Validate(model);

Assert(JsonSerializer.Deserialize<SequenceFlowModel>("{}")!.IsSelectable,
    "Missing isSelectable must default to true.");
var voteNode = model.FlowNodes.Single(n => n.Id == 2);
var voteFlows = model.SequenceFlows.Where(f => f.SourceRef == voteNode.Id).ToList();
Assert(voteFlows.Single(f => f.Id == 201).IsSelectable, "Approve must remain selectable.");
Assert(!voteFlows.Single(f => f.Id == 202).IsSelectable, "Reject must be an engine-only default.");
Assert(voteFlows.Where(f => f.IsSelectable && !f.CancelRemainingInstances).Select(f => f.Id).SequenceEqual([201]),
    "Only selectable outcomes may receive selection counters.");

var variables = new Dictionary<string, JsonElement>
{
    ["mi.total"] = JsonSerializer.SerializeToElement(5),
    ["mi.completed"] = JsonSerializer.SerializeToElement(3),
    ["mi.remaining"] = JsonSerializer.SerializeToElement(2),
    ["requiredApprovals"] = JsonSerializer.SerializeToElement(3)
};
var counts = new Dictionary<int, int> { [201] = 3, [202] = 0 };
Assert(SequenceFlowConditionEvaluator.EvaluateCompletion(
    "CountFlow(201) >= requiredApprovals and PercentFlow(201) >= 60", variables, counts, 5));
Assert(!SequenceFlowConditionEvaluator.EvaluateCompletion(
    "CountFlow(202) >= 2", variables, counts, 5));

VerifyMode(MultiInstanceModes.Parallel);
VerifyMode(MultiInstanceModes.Sequential);

var hiddenWinner = Clone(model);
hiddenWinner.SequenceFlows.Single(f => f.Id == 202).CompletionCondition = "CountFlow(201) == 1";
Validate(hiddenWinner);
var earlyCounts = new Dictionary<int, int> { [201] = 1 };
var earlyVariables = new Dictionary<string, JsonElement>(variables)
{
    ["mi.completed"] = JsonSerializer.SerializeToElement(1),
    ["mi.remaining"] = JsonSerializer.SerializeToElement(4)
};
var earlyRoute = hiddenWinner.SequenceFlows.Where(f => f.SourceRef == 2 && !f.CancelRemainingInstances)
    .OrderBy(f => f.CompletionPriority)
    .FirstOrDefault(f => !string.IsNullOrWhiteSpace(f.CompletionCondition)
                         && SequenceFlowConditionEvaluator.EvaluateCompletion(
                             f.CompletionCondition, earlyVariables, earlyCounts, 5));
Assert(earlyRoute is { Id: 202, IsSelectable: false },
    "An engine-only route must be able to win through aggregate completion routing.");

var hiddenInterrupt = Clone(model);
hiddenInterrupt.SequenceFlows.Single(f => f.Id == 203).IsSelectable = false;
ExpectValidationFailure(hiddenInterrupt, "Hidden interrupt must be rejected.");

var hiddenWithRoles = Clone(model);
hiddenWithRoles.SequenceFlows.Single(f => f.Id == 202).Roles = ["Manager"];
ExpectValidationFailure(hiddenWithRoles, "Engine-only actor settings must be rejected.");

var noSelectableOutcome = Clone(model);
noSelectableOutcome.SequenceFlows.Single(f => f.Id == 201).IsSelectable = false;
ExpectValidationFailure(noSelectableOutcome, "At least one selectable outcome is required.");

var referencesHidden = Clone(model);
referencesHidden.SequenceFlows.Single(f => f.Id == 202).CompletionCondition = "CountFlow(202) >= 1";
ExpectValidationFailure(referencesHidden, "Completion helpers must not reference engine-only outcomes.");

var normalTaskHidden = Clone(model);
normalTaskHidden.FlowNodes.Single(n => n.Id == 2).MultiInstance = null;
ExpectValidationFailure(normalTaskHidden, "Engine-only routes must be limited to multi-instance user tasks.");

Console.WriteLine("Multi-instance selectable/engine-only routing verification passed.");

void VerifyMode(string mode)
{
    var candidate = Clone(model);
    candidate.FlowNodes.Single(n => n.Id == 2).MultiInstance!.Mode = mode;
    Validate(candidate);

    var modeCounts = new Dictionary<int, int> { [201] = 0 };
    SequenceFlowModel? winner = null;
    for (var completed = 1; completed <= 5 && winner is null; completed++)
    {
        modeCounts[201]++;
        var context = new Dictionary<string, JsonElement>(variables)
        {
            ["mi.completed"] = JsonSerializer.SerializeToElement(completed),
            ["mi.remaining"] = JsonSerializer.SerializeToElement(5 - completed),
            ["requiredApprovals"] = JsonSerializer.SerializeToElement(6)
        };
        winner = candidate.SequenceFlows.Where(f => f.SourceRef == 2 && !f.CancelRemainingInstances)
            .OrderBy(f => f.CompletionPriority)
            .FirstOrDefault(f => !string.IsNullOrWhiteSpace(f.CompletionCondition)
                                 && SequenceFlowConditionEvaluator.EvaluateCompletion(
                                     f.CompletionCondition, context, modeCounts, 5));
        if (winner is null && completed == 5)
            winner = candidate.SequenceFlows.Single(f => f.SourceRef == 2 && !f.CancelRemainingInstances && f.IsDefault);
    }

    Assert(winner is { Id: 202, IsSelectable: false },
        $"{mode} all-items completion must choose the engine-only default exactly once.");
}

void Validate(WorkflowModel candidate)
{
    try
    {
        validate.Invoke(validator, [candidate]);
    }
    catch (TargetInvocationException ex) when (ex.InnerException is not null)
    {
        throw ex.InnerException;
    }
}

void ExpectValidationFailure(WorkflowModel candidate, string message)
{
    try
    {
        Validate(candidate);
    }
    catch (WorkflowDomainException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

static WorkflowModel Clone(WorkflowModel source) =>
    JsonSerializer.Deserialize<WorkflowModel>(JsonSerializer.Serialize(source))
    ?? throw new InvalidOperationException("Workflow clone failed.");

static void Assert(bool condition, string? message = null)
{
    if (!condition) throw new InvalidOperationException(message ?? "A multi-instance verification assertion failed.");
}

sealed class ParseOnlyScriptEvaluator : IScriptEvaluator
{
    public ScriptResult Evaluate(string script, IScriptContext context, CancellationToken cancellationToken) =>
        new(true, null);

    public bool IsValid(string script, out string? error)
    {
        error = null;
        return true;
    }
}
