using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WorkflowEngine.Service.Abstractions;
using WorkflowEngine.Service.Services;
using WorkflowEngine.Shared.Models;

var sample = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "votes.json"));
var loadedModel = JsonSerializer.Deserialize<WorkflowModel>(await File.ReadAllTextAsync(sample))
    ?? throw new InvalidOperationException("The quorum sample did not deserialize.");
var model = Clone(loadedModel);
WorkflowModelMigrator.Normalize(model);

var validator = new WorkflowDefinitionService(
    null!, new ParseOnlyScriptEvaluator(), NullLogger<WorkflowDefinitionService>.Instance);
var validate = typeof(WorkflowDefinitionService).GetMethod(
    "ValidateDefinition", BindingFlags.Instance | BindingFlags.NonPublic)
    ?? throw new InvalidOperationException("ValidateDefinition was not found.");
Validate(model);

foreach (var fileName in new[]
         {
             "votes.json", "votes-cardinality.json", "votes-users-list.json",
             "votes-cardinality-approve-reject.json"
         })
{
    var path = Path.Combine(Path.GetDirectoryName(sample)!, fileName);
    if (!File.Exists(path)) continue;
    var canonical = JsonSerializer.Deserialize<WorkflowModel>(await File.ReadAllTextAsync(path))
        ?? throw new InvalidOperationException($"The canonical sample '{fileName}' did not deserialize.");
    WorkflowModelMigrator.Normalize(canonical);
    Validate(canonical);
}

Assert(JsonSerializer.Deserialize<SequenceFlowModel>("{}")!.IsSelectable,
    "Missing isSelectable must default to true.");
Assert(!JsonSerializer.Deserialize<MultiInstanceModel>("{}")!.OnePerActor,
    "Missing onePerActor must default to false.");
Assert(JsonSerializer.Deserialize<MultiInstanceModel>("{\"onePerActor\":true}")!.OnePerActor,
    "onePerActor=true must round-trip through the shared workflow model.");
Assert(JsonSerializer.Deserialize<MultiInstanceModel>("{}")!.CompletionEvaluation
       == MultiInstanceCompletionEvaluations.AfterEach,
    "Missing completionEvaluation must default to afterEach.");
Assert(JsonSerializer.Deserialize<MultiInstanceModel>("{\"completionEvaluation\":\"afterAll\"}")!
       .CompletionEvaluation == MultiInstanceCompletionEvaluations.AfterAll,
    "completionEvaluation=afterAll must round-trip through the shared workflow model.");
var voteNode = model.FlowNodes.Single(n => n.Id == 2);
if (string.Equals(model.Name, "workflow-votes-cardinality", StringComparison.OrdinalIgnoreCase))
{
    Assert(voteNode.MultiInstance is { Source: MultiInstanceSources.Cardinality, OnePerActor: true },
        "The cardinality vote sample must enable onePerActor.");
}
var voteFlows = model.SequenceFlows.Where(f => f.SourceRef == voteNode.Id).ToList();
Assert(voteFlows.Single(f => f.Id == 201).IsSelectable, "Approve must remain selectable.");
Assert(voteFlows.Where(f => f.IsSelectable && !f.IsDefault && !f.CancelRemainingInstances)
        .Select(f => f.Id).SequenceEqual([201]),
    "Only selectable outcomes may receive selection counters.");
var defaultOutcome = voteFlows.Single(f => f.IsDefault);
Assert(!defaultOutcome.IsSelectable && defaultOutcome.CompletionCondition is null
       && defaultOutcome.CompletionPriority is null,
    "The multi-instance default must be a pure engine-only fallback.");
var interrupt = voteFlows.Single(f => f.Id == 203);
Assert(interrupt.IsSelectable && interrupt.CancelRemainingInstances,
    "The manager cancel flow must remain a selectable interrupt action.");
Assert(interrupt.Roles.Contains("Manager", StringComparer.OrdinalIgnoreCase),
    "The manager cancel flow must remain role-restricted.");

var normalizedFlowCount = model.SequenceFlows.Count;
var normalizedDefaultId = defaultOutcome.Id;
WorkflowModelMigrator.Normalize(model);
Assert(model.SequenceFlows.Count == normalizedFlowCount
       && model.SequenceFlows.Single(f => f.SourceRef == voteNode.Id && f.IsDefault).Id == normalizedDefaultId,
    "Multi-instance default normalization must be idempotent and keep a stable flow id.");

VerifyLegacyDefaultSplit(model, preserveCompletionCondition: true);
VerifyLegacyDefaultSplit(model, preserveCompletionCondition: false);

var normalTask = Clone(model);
normalTask.FlowNodes.Single(n => n.Id == 2).MultiInstance = null;
normalTask.SequenceFlows.RemoveAll(f => f.SourceRef == 2 && f.Id != 201);
var normalFlow = normalTask.SequenceFlows.Single(f => f.Id == 201);
normalFlow.IsDefault = true;
normalFlow.IsSelectable = false;
normalFlow.Condition = "ignored == true";
normalFlow.CompletionCondition = null;
normalFlow.CompletionPriority = null;
normalFlow.CancelRemainingInstances = false;
WorkflowModelMigrator.Normalize(normalTask);
Assert(!normalFlow.IsDefault && normalFlow.IsSelectable && normalFlow.Condition is null,
    "A normal user-task default must normalize to an always-available ordinary action.");
Validate(normalTask);

var invalidNormalDefault = Clone(normalTask);
invalidNormalDefault.SequenceFlows.Single(f => f.Id == 201).IsDefault = true;
ExpectValidationFailure(invalidNormalDefault, "Normal user-task defaults must be rejected after normalization.");

var selectableDefault = Clone(model);
selectableDefault.SequenceFlows.Single(f => f.SourceRef == 2 && f.IsDefault).IsSelectable = true;
ExpectValidationFailure(selectableDefault, "Selectable multi-instance defaults must be rejected.");

var conditionedDefault = Clone(model);
conditionedDefault.SequenceFlows.Single(f => f.SourceRef == 2 && f.IsDefault).CompletionCondition = "1 == 0";
ExpectValidationFailure(conditionedDefault, "A multi-instance default completion condition must be rejected.");

var prioritizedDefault = Clone(model);
prioritizedDefault.SequenceFlows.Single(f => f.SourceRef == 2 && f.IsDefault).CompletionPriority = 99;
ExpectValidationFailure(prioritizedDefault, "A multi-instance default completion priority must be rejected.");

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

var hiddenWinner = WithEngineOnlyConditionalOutcome(model);
hiddenWinner.SequenceFlows.Single(f => f.Id == 202).CompletionCondition = "CountFlow(201) == 1";
Validate(hiddenWinner);
var earlyCounts = new Dictionary<int, int> { [201] = 1 };
var earlyVariables = new Dictionary<string, JsonElement>(variables)
{
    ["mi.completed"] = JsonSerializer.SerializeToElement(1),
    ["mi.remaining"] = JsonSerializer.SerializeToElement(4)
};
var earlyRoute = hiddenWinner.SequenceFlows.Where(f => f.SourceRef == 2 && !f.IsDefault
                                                                     && !f.CancelRemainingInstances)
    .OrderBy(f => f.CompletionPriority)
    .FirstOrDefault(f => !string.IsNullOrWhiteSpace(f.CompletionCondition)
                         && SequenceFlowConditionEvaluator.EvaluateCompletion(
                             f.CompletionCondition, earlyVariables, earlyCounts, 5));
Assert(earlyRoute is { Id: 202, IsSelectable: false },
    "An engine-only route must be able to win through aggregate completion routing.");

var hiddenInterrupt = Clone(model);
hiddenInterrupt.SequenceFlows.Single(f => f.Id == 203).IsSelectable = false;
ExpectValidationFailure(hiddenInterrupt, "Hidden interrupt must be rejected.");

var hiddenWithRoles = WithEngineOnlyConditionalOutcome(model);
hiddenWithRoles.SequenceFlows.Single(f => f.Id == 202).Roles = ["Manager"];
ExpectValidationFailure(hiddenWithRoles, "Engine-only actor settings must be rejected.");

var noSelectableOutcome = Clone(model);
noSelectableOutcome.SequenceFlows.Single(f => f.Id == 201).IsSelectable = false;
ExpectValidationFailure(noSelectableOutcome, "At least one selectable outcome is required.");

var referencesHidden = WithEngineOnlyConditionalOutcome(model);
referencesHidden.SequenceFlows.Single(f => f.Id == 202).CompletionCondition = "CountFlow(202) >= 1";
ExpectValidationFailure(referencesHidden, "Completion helpers must not reference engine-only outcomes.");

var afterAll = Clone(model);
afterAll.FlowNodes.Single(n => n.Id == 2).MultiInstance!.CompletionEvaluation =
    MultiInstanceCompletionEvaluations.AfterAll;
Validate(afterAll);

var invalidCompletionEvaluation = Clone(model);
invalidCompletionEvaluation.FlowNodes.Single(n => n.Id == 2).MultiInstance!.CompletionEvaluation = "sometimes";
ExpectValidationFailure(invalidCompletionEvaluation,
    "Unsupported multi-instance completion evaluation timing must be rejected.");

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
        winner = candidate.SequenceFlows.Where(f => f.SourceRef == 2 && !f.IsDefault
                                                               && !f.CancelRemainingInstances)
            .OrderBy(f => f.CompletionPriority)
            .FirstOrDefault(f => !string.IsNullOrWhiteSpace(f.CompletionCondition)
                                 && SequenceFlowConditionEvaluator.EvaluateCompletion(
                                     f.CompletionCondition, context, modeCounts, 5));
        if (winner is null && completed == 5)
            winner = candidate.SequenceFlows.Single(f => f.SourceRef == 2 && !f.CancelRemainingInstances && f.IsDefault);
    }

    Assert(winner is { IsDefault: true, IsSelectable: false },
        $"{mode} all-items completion must choose the engine-only default exactly once.");
}

void VerifyLegacyDefaultSplit(WorkflowModel source, bool preserveCompletionCondition)
{
    var candidate = Clone(source);
    candidate.SequenceFlows.RemoveAll(f => f.SourceRef == 2 && f.IsDefault);
    var action = candidate.SequenceFlows.Single(f => f.Id == 201);
    action.IsDefault = true;
    action.IsSelectable = true;
    action.Condition = "ignored == true";
    if (!preserveCompletionCondition) action.CompletionCondition = null;
    var expectedCondition = action.CompletionCondition;

    WorkflowModelMigrator.Normalize(candidate);

    var migratedAction = candidate.SequenceFlows.Single(f => f.Id == 201);
    var fallback = candidate.SequenceFlows.Single(f => f.SourceRef == 2 && f.IsDefault);
    Assert(!migratedAction.IsDefault && migratedAction.IsSelectable && migratedAction.Condition is null,
        "Legacy default migration must retain the original flow as a selectable outcome.");
    Assert(migratedAction.CompletionCondition == (preserveCompletionCondition ? expectedCondition : "1 == 0"),
        "Legacy default migration must preserve or synthesize the outcome completion condition.");
    Assert(migratedAction.CompletionPriority is > 0,
        "Legacy default migration must retain or allocate a positive outcome priority.");
    Assert(!fallback.IsSelectable && fallback.TargetRef == migratedAction.TargetRef
           && fallback.CompletionCondition is null && fallback.CompletionPriority is null,
        "Legacy default migration must create a pure hidden fallback to the same target.");
    Validate(candidate);
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

static WorkflowModel WithEngineOnlyConditionalOutcome(WorkflowModel source)
{
    var candidate = Clone(source);
    candidate.SequenceFlows.Add(new SequenceFlowModel
    {
        Id = 202,
        Name = "Engine-only fallback",
        SourceRef = 2,
        TargetRef = 3,
        IsSelectable = false,
        IsDefault = false,
        CompletionCondition = "1 == 0",
        CompletionPriority = 20
    });
    return candidate;
}

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
