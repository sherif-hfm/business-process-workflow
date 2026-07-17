namespace Flowbit.Shared.Models;

public static class WorkflowModelMigrator
{
    public static void Normalize(WorkflowModel model)
    {
        if (model.FlowNodes.Count == 0 && model.LegacySteps is { Count: > 0 })
        {
            ConvertLegacy(model);
        }

        model.LegacyInitialStepId = null;
        model.LegacyPhases = null;
        model.LegacySteps = null;

        model.Variables ??= [];
        model.CancelRoles ??= [];
        model.UnclaimRoles ??= [];
        model.TaskAssignmentRoles = NormalizeRoles(model.TaskAssignmentRoles);

        foreach (var node in model.FlowNodes)
        {
            ApplyNodeInvariants(node, model.Variables);
        }

        NormalizeUserTaskDefaultFlows(model);

    }

    private static void ConvertLegacy(WorkflowModel model)
    {
        model.InitialEventId ??= model.LegacyInitialStepId;

        if (model.LegacyPhases is { Count: > 0 })
        {
            model.Lanes = model.LegacyPhases
                .Select(p => new LaneModel { Id = p.Id, Name = p.Name, X = p.X, Y = p.Y, W = p.W, H = p.H })
                .ToList();
        }

        var flows = new List<SequenceFlowModel>();
        var nextFlowId = 1;

        foreach (var step in model.LegacySteps!)
        {
            var type = MapLegacyType(step);
            var node = new FlowNodeModel
            {
                Id = step.Id,
                Name = step.Name,
                Type = type,
                LaneId = step.PhaseId,
                X = step.X,
                Y = step.Y,
                Roles = step.Roles ?? [],
                RequiresClaim = step.RequiresClaim,
                Variables = step.Variables ?? []
            };

            if (BpmnFlowNodeTypes.IsStart(type))
            {
                // Legacy start steps advanced either through a single action or nextStepId.
                if (step.Actions.Count > 0)
                {
                    var first = step.Actions[0];
                    MergeVariables(node.Variables, first.Variables);
                    flows.Add(new SequenceFlowModel
                    {
                        Id = NextFlowId(ref nextFlowId, flows),
                        Name = string.Empty,
                        SourceRef = step.Id,
                        TargetRef = first.ToStepId
                    });
                }
                else if (step.NextStepId is int startTarget)
                {
                    flows.Add(new SequenceFlowModel
                    {
                        Id = NextFlowId(ref nextFlowId, flows),
                        Name = string.Empty,
                        SourceRef = step.Id,
                        TargetRef = startTarget
                    });
                }
            }
            else if (BpmnFlowNodeTypes.IsAutomatic(type))
            {
                if (step.NextStepId is int autoTarget)
                {
                    flows.Add(new SequenceFlowModel
                    {
                        Id = NextFlowId(ref nextFlowId, flows),
                        Name = string.Empty,
                        SourceRef = step.Id,
                        TargetRef = autoTarget
                    });
                }
            }
            else
            {
                foreach (var action in step.Actions)
                {
                    flows.Add(new SequenceFlowModel
                    {
                        Id = action.Id != 0 ? action.Id : NextFlowId(ref nextFlowId, flows),
                        Name = action.Name,
                        SourceRef = step.Id,
                        TargetRef = action.ToStepId,
                        Roles = action.Roles ?? [],
                        Variables = action.Variables ?? []
                    });
                }
            }

            model.FlowNodes.Add(node);
        }

        model.SequenceFlows = flows;
    }

    private static int NextFlowId(ref int seed, List<SequenceFlowModel> flows)
    {
        var max = flows.Count == 0 ? 0 : flows.Max(f => f.Id);
        var candidate = Math.Max(seed, max + 1);
        seed = candidate + 1;
        return candidate;
    }

    private static List<string> NormalizeRoles(IEnumerable<string>? roles) =>
        (roles ?? [])
        .Where(role => !string.IsNullOrWhiteSpace(role))
        .Select(role => role.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>
    /// Canonicalizes user-task defaults while preserving the behavior of older
    /// multi-instance definitions whose selectable vote/action was also marked
    /// as the aggregate default.
    /// </summary>
    private static void NormalizeUserTaskDefaultFlows(WorkflowModel model)
    {
        var flowIdSeed = model.SequenceFlows.Count == 0
            ? 1
            : model.SequenceFlows.Max(flow => flow.Id) + 1;

        foreach (var node in model.FlowNodes.Where(node => BpmnFlowNodeTypes.IsUserTask(node.Type)))
        {
            var outgoing = model.SequenceFlows.Where(flow => flow.SourceRef == node.Id).ToList();
            if (node.MultiInstance is null)
            {
                foreach (var flow in outgoing.Where(flow => flow.IsDefault))
                {
                    // A normal user task never routes automatically. Its legacy
                    // default was effectively an always-available user action.
                    flow.IsDefault = false;
                    flow.IsSelectable = true;
                    flow.Condition = null;
                }
                continue;
            }

            foreach (var legacyDefault in outgoing.Where(flow => flow.IsDefault && flow.IsSelectable).ToList())
            {
                // Preserve the original flow id and actor-facing metadata so
                // in-flight outcome counters and integrations remain stable.
                legacyDefault.IsDefault = false;
                legacyDefault.Condition = null;
                legacyDefault.CancelRemainingInstances = false;
                if (string.IsNullOrWhiteSpace(legacyDefault.CompletionCondition))
                {
                    legacyDefault.CompletionCondition = "1 == 0";
                }

                var conflictingPriority = legacyDefault.CompletionPriority is null or <= 0
                    || outgoing.Any(flow => flow.Id != legacyDefault.Id
                                            && flow.CompletionPriority == legacyDefault.CompletionPriority);
                if (conflictingPriority)
                {
                    legacyDefault.CompletionPriority = NextCompletionPriority(outgoing);
                }

                var fallback = new SequenceFlowModel
                {
                    Id = NextFlowId(ref flowIdSeed, model.SequenceFlows),
                    Name = string.Empty,
                    ExternalId = null,
                    SourceRef = legacyDefault.SourceRef,
                    TargetRef = legacyDefault.TargetRef,
                    IsDefault = true,
                    IsSelectable = false
                };
                model.SequenceFlows.Add(fallback);
                outgoing.Add(fallback);
            }

            foreach (var fallback in outgoing.Where(flow => flow.IsDefault))
            {
                fallback.IsSelectable = false;
                fallback.Roles = [];
                fallback.Variables = [];
                fallback.Condition = null;
                fallback.CanActWithoutClaim = false;
                fallback.CompletionCondition = null;
                fallback.CompletionPriority = null;
                fallback.CancelRemainingInstances = false;
            }
        }
    }

    private static int NextCompletionPriority(IEnumerable<SequenceFlowModel> flows) =>
        flows.Where(flow => flow.CompletionPriority is > 0)
            .Select(flow => flow.CompletionPriority!.Value)
            .DefaultIfEmpty(0)
            .Max() + 1;

    private static string MapLegacyType(LegacyStepModel step) => step.Type switch
    {
        "start" => BpmnFlowNodeTypes.StartEvent,
        "startEvent" => BpmnFlowNodeTypes.StartEvent,
        "end" => BpmnFlowNodeTypes.EndEvent,
        "endEvent" => BpmnFlowNodeTypes.EndEvent,
        "errorEndEvent" => BpmnFlowNodeTypes.ErrorEndEvent,
        "task" when step.AutoAdvance => BpmnFlowNodeTypes.Task,
        "task" => BpmnFlowNodeTypes.UserTask,
        "userTask" => BpmnFlowNodeTypes.UserTask,
        "exclusiveGateway" => BpmnFlowNodeTypes.ExclusiveGateway,
        _ => BpmnFlowNodeTypes.UserTask
    };

    private static void MergeVariables(List<VariableModel> target, List<VariableModel>? source)
    {
        if (source is null)
        {
            return;
        }

        foreach (var variable in source)
        {
            if (target.All(v => !string.Equals(v.Name, variable.Name, StringComparison.OrdinalIgnoreCase)))
            {
                target.Add(variable);
            }
        }
    }

    private static void ApplyNodeInvariants(
        FlowNodeModel node,
        IReadOnlyList<VariableModel> processVariables)
    {
        if (!BpmnFlowNodeTypes.IsEntry(node.Type))
        {
            node.BusinessKey = null;
            node.Idempotency = null;
        }
        else if (node.BusinessKey is not null)
        {
            node.BusinessKey.Uniqueness = CanonicalizeKnown(
                node.BusinessKey.Uniqueness,
                BusinessKeyUniqueness.Active,
                BusinessKeyUniqueness.All);
        }

        if (BpmnFlowNodeTypes.IsEntry(node.Type) && node.Idempotency is not null)
        {
            node.Idempotency.HeaderName = node.Idempotency.HeaderName?.Trim()!;
            node.Idempotency.Variable = node.Idempotency.Variable?.Trim()!;
        }

        if (!BpmnFlowNodeTypes.IsUserTask(node.Type))
        {
            node.MultiInstance = null;
        }

        if (BpmnFlowNodeTypes.IsEnd(node.Type))
        {
            node.RequiresClaim = false;
            node.ClaimMode = ClaimModes.Fresh;
            node.InheritClaimFromNodeId = null;
            node.Roles = [];
            node.Variables = [];
            node.Service = null;
            node.Message = null;
            node.ScriptFormat = ScriptFormats.NCalc;
            node.Assignments = [];
            node.Script = null;
            node.AssigneeExpression = null;
            node.AttachedToRef = null;
            node.ErrorVariable = null;
        }
        else if (BpmnFlowNodeTypes.IsAutomatic(node.Type) || BpmnFlowNodeTypes.IsGateway(node.Type))
        {
            node.RequiresClaim = false;
            node.ClaimMode = ClaimModes.Fresh;
            node.InheritClaimFromNodeId = null;
            node.Roles = [];
            node.Variables = [];
            node.Message = null;
        }
        else if (BpmnFlowNodeTypes.IsServiceTask(node.Type))
        {
            // Service tasks are automatic; the REST configuration lives on node.Service.
            node.RequiresClaim = false;
            node.ClaimMode = ClaimModes.Fresh;
            node.InheritClaimFromNodeId = null;
            node.Roles = [];
            node.Variables = [];
            node.Service ??= new ServiceTaskModel();
            NormalizeServiceOutputMappings(node.Service.OutputMappings, processVariables);
            node.Assignments = [];
            node.Message = null;
        }
        else if (BpmnFlowNodeTypes.IsScriptTask(node.Type))
        {
            // Script tasks are automatic; the authored logic lives on either
            // node.Assignments (ncalc) or node.Script (javascript), never both.
            // The first automatic node type to carry authored data, so this is a
            // deliberate case rather than a fall-through.
            node.RequiresClaim = false;
            node.ClaimMode = ClaimModes.Fresh;
            node.InheritClaimFromNodeId = null;
            node.Roles = [];
            node.Variables = [];
            node.Service = null;
            node.Message = null;
            if (node.ScriptFormat != ScriptFormats.JavaScript)
            {
                node.ScriptFormat = ScriptFormats.NCalc;
            }

            if (node.ScriptFormat == ScriptFormats.JavaScript)
            {
                node.Assignments = [];
            }
            else
            {
                node.Assignments ??= [];
                node.Script = null;
            }
        }
        else if (BpmnFlowNodeTypes.IsStart(node.Type))
        {
            node.RequiresClaim = false;
            node.ClaimMode = ClaimModes.Fresh;
            node.InheritClaimFromNodeId = null;
            node.Roles ??= [];
            node.Service = null;
            node.Assignments = [];
            node.Script = null;
            node.AssigneeExpression = null;
            node.AttachedToRef = null;
            node.ErrorVariable = null;
            node.Message = null;
        }
        else if (BpmnFlowNodeTypes.IsErrorBoundary(node.Type))
        {
            // A boundary event is attached to a service/script task; it carries
            // only attachedToRef and the optional errorVariable, nothing else.
            node.RequiresClaim = false;
            node.ClaimMode = ClaimModes.Fresh;
            node.InheritClaimFromNodeId = null;
            node.Roles = [];
            node.Variables = [];
            node.Service = null;
            node.Assignments = [];
            node.Script = null;
            node.Message = null;
        }
        else if (BpmnFlowNodeTypes.IsMessageCatch(node.Type))
        {
            // A message catch event rests until a message is delivered; the
            // delivery configuration lives on node.Message. Like a userTask it
            // is not pass-through, but it carries no claim/role/variable data.
            node.RequiresClaim = false;
            node.ClaimMode = ClaimModes.Fresh;
            node.InheritClaimFromNodeId = null;
            node.Roles = [];
            node.Variables = [];
            node.Service = null;
            node.Assignments = [];
            node.Script = null;
            node.AssigneeExpression = null;
            node.AttachedToRef = null;
            node.ErrorVariable = null;
            node.Message ??= new MessageCatchModel();
            NormalizeMessageCatchMappings(node.Message.OutputMappings, processVariables);
        }
        else if (BpmnFlowNodeTypes.IsMessageStart(node.Type))
        {
            // A message start event is an entry point started by an external
            // system. Its typed output mappings are the start-variable declarations;
            // node.Variables is retained only as an import source for older JSON.
            node.RequiresClaim = false;
            node.ClaimMode = ClaimModes.Fresh;
            node.InheritClaimFromNodeId = null;
            node.Roles = [];
            node.Variables ??= [];
            node.Service = null;
            node.Assignments = [];
            node.Script = null;
            node.AssigneeExpression = null;
            node.AttachedToRef = null;
            node.ErrorVariable = null;
            node.Message ??= new MessageCatchModel();
            NormalizeMessageStartMappings(node);
        }
        else if (BpmnFlowNodeTypes.IsUserTask(node.Type))
        {
            // Tolerant load: older documents have no claimMode.
            if (string.IsNullOrWhiteSpace(node.ClaimMode))
            {
                node.ClaimMode = ClaimModes.Fresh;
            }

            if (node.ClaimMode != ClaimModes.FromNode)
            {
                node.InheritClaimFromNodeId = null;
            }

            node.Message = null;

            if (node.MultiInstance is not null)
            {
                node.MultiInstance.Mode = CanonicalizeKnown(
                    node.MultiInstance.Mode,
                    MultiInstanceModes.Parallel,
                    MultiInstanceModes.Sequential);
                node.MultiInstance.Source = CanonicalizeKnown(
                    node.MultiInstance.Source,
                    MultiInstanceSources.Collection,
                    MultiInstanceSources.Cardinality);
                node.MultiInstance.CompletionEvaluation = CanonicalizeKnown(
                    node.MultiInstance.CompletionEvaluation,
                    MultiInstanceCompletionEvaluations.AfterEach,
                    MultiInstanceCompletionEvaluations.AfterAll);
                if (node.MultiInstance.Source == MultiInstanceSources.Collection)
                {
                    node.MultiInstance.CardinalityExpression = null;
                    node.MultiInstance.OnePerActor = false;
                }
                else
                {
                    node.MultiInstance.CollectionVariable = null;
                }
            }
        }
    }

    /// <summary>
    /// Converts the historical message-start shape (node variables plus raw output
    /// mappings) into typed mappings. Immutable stored definitions are normalized in
    /// memory; a later version/save naturally emits the canonical shape.
    /// </summary>
    private static void NormalizeMessageStartMappings(FlowNodeModel node)
    {
        var message = node.Message!;
        message.OutputMappings ??= [];
        var legacyVariables = node.Variables ?? [];

        var legacyIdempotencyVariable = message.IdempotencyVariable;
        if (!string.IsNullOrWhiteSpace(legacyIdempotencyVariable))
        {
            var declaredIdempotency = legacyVariables.FirstOrDefault(variable =>
                string.Equals(variable.Name, legacyIdempotencyVariable, StringComparison.OrdinalIgnoreCase));
            if (declaredIdempotency is not null)
            {
                legacyIdempotencyVariable = declaredIdempotency.Name;
            }

            if (node.Idempotency is null)
            {
                node.Idempotency = new IdempotencyModel
                {
                    HeaderName = IdempotencyHeaders.Standard,
                    Variable = legacyIdempotencyVariable
                };
                message.IdempotencyVariable = null;

                // Historical definitions could also map the header-owned variable
                // from the body. Remove only during unambiguous legacy conversion.
                message.OutputMappings.RemoveAll(mapping =>
                    string.Equals(mapping.Variable, legacyIdempotencyVariable, StringComparison.OrdinalIgnoreCase));
            }
        }

        var represented = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in message.OutputMappings)
        {
            var declared = legacyVariables.FirstOrDefault(variable =>
                string.Equals(variable.Name, mapping.Variable, StringComparison.OrdinalIgnoreCase));
            if (declared is not null)
            {
                mapping.Variable = declared.Name;
                mapping.DataType ??= declared.DataType;
                mapping.IsArray ??= declared.IsArray;
                mapping.Required = mapping.Required || declared.Required;
                mapping.DefaultValue ??= declared.DefaultValue;
                mapping.Validation ??= declared.Validation;
                represented.Add(declared.Name);
            }
            else
            {
                // A legacy undeclared mapping was previously extracted but ignored
                // by message start. Under the new create-variable behavior, preserve
                // its raw shape by declaring it as JSON.
                mapping.DataType ??= WorkflowVariableTypes.Json;
                mapping.IsArray ??= false;
            }

            if (mapping.DataType is not null)
            {
                mapping.DataType = CanonicalizeKnown(
                    mapping.DataType,
                    WorkflowVariableTypes.String,
                    WorkflowVariableTypes.Number,
                    WorkflowVariableTypes.Boolean,
                    WorkflowVariableTypes.Date,
                    WorkflowVariableTypes.DateTime,
                    WorkflowVariableTypes.Json);
            }
            mapping.IsArray ??= false;
        }

        foreach (var variable in legacyVariables)
        {
            if (represented.Contains(variable.Name)
                || string.Equals(variable.Name, node.Idempotency?.Variable, StringComparison.OrdinalIgnoreCase)
                || string.Equals(variable.Name, message.IdempotencyVariable, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var hasDefault = variable.DefaultValue is { } defaultValue
                && defaultValue.ValueKind is not (System.Text.Json.JsonValueKind.Null
                    or System.Text.Json.JsonValueKind.Undefined);
            if (!hasDefault && !variable.Required)
            {
                // It could never produce a stored value in the old runtime.
                continue;
            }

            // Default-only variables become valid pathless mappings. A required
            // variable with neither mapping nor default remains pathless and will be
            // rejected by definition validation with a precise missing-source error.
            message.OutputMappings.Add(new MessageOutputMappingModel
            {
                Variable = variable.Name,
                Path = string.Empty,
                Required = variable.Required,
                DataType = variable.DataType,
                IsArray = variable.IsArray,
                DefaultValue = variable.DefaultValue,
                Validation = variable.Validation
            });
        }

        node.Variables = [];
    }

    private static void NormalizeServiceOutputMappings(
        IEnumerable<ServiceOutputMappingModel> mappings,
        IReadOnlyList<VariableModel> processVariables)
    {
        foreach (var mapping in mappings)
        {
            NormalizeTypedOutputMapping(
                mapping.Variable,
                value => mapping.Variable = value,
                mapping.DataType,
                value => mapping.DataType = value,
                mapping.IsArray,
                value => mapping.IsArray = value,
                processVariables);
        }
    }

    private static void NormalizeMessageCatchMappings(
        IEnumerable<MessageOutputMappingModel> mappings,
        IReadOnlyList<VariableModel> processVariables)
    {
        foreach (var mapping in mappings)
        {
            NormalizeTypedOutputMapping(
                mapping.Variable,
                value => mapping.Variable = value,
                mapping.DataType,
                value => mapping.DataType = value,
                mapping.IsArray,
                value => mapping.IsArray = value,
                processVariables);
        }
    }

    /// <summary>
    /// Canonicalizes a service/catch mapping. Historical raw mappings inherit a
    /// matching process-variable contract; undeclared targets remain compatible
    /// by becoming scalar JSON mappings.
    /// </summary>
    private static void NormalizeTypedOutputMapping(
        string variableName,
        Action<string> setVariableName,
        string? dataType,
        Action<string> setDataType,
        bool? isArray,
        Action<bool> setIsArray,
        IReadOnlyList<VariableModel> processVariables)
    {
        var processVariable = processVariables.FirstOrDefault(variable =>
            string.Equals(variable.Name, variableName, StringComparison.OrdinalIgnoreCase));
        if (processVariable is not null)
        {
            setVariableName(processVariable.Name);
            dataType ??= processVariable.DataType;
            isArray ??= processVariable.IsArray;
        }
        else
        {
            dataType ??= WorkflowVariableTypes.Json;
            isArray ??= false;
        }

        setDataType(CanonicalizeKnown(
            dataType,
            WorkflowVariableTypes.String,
            WorkflowVariableTypes.Number,
            WorkflowVariableTypes.Boolean,
            WorkflowVariableTypes.Date,
            WorkflowVariableTypes.DateTime,
            WorkflowVariableTypes.Json));
        setIsArray(isArray.Value);
    }

    private static string CanonicalizeKnown(string value, params string[] supported)
    {
        var canonical = supported.FirstOrDefault(candidate =>
            string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase));
        return canonical ?? value;
    }
}
