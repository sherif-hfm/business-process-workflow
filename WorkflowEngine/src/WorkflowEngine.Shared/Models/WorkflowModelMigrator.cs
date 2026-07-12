namespace WorkflowEngine.Shared.Models;

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

        foreach (var node in model.FlowNodes)
        {
            ApplyNodeInvariants(node);
        }

        // After a type change (e.g. startEvent -> messageStartEvent) the
        // initialEventId may still reference a node that is no longer a valid
        // user start event. Fall back to the first remaining startEvent, or
        // null when the workflow is message-started only.
        if (model.InitialEventId is not null)
        {
            var initialNode = model.FlowNodes.SingleOrDefault(n => n.Id == model.InitialEventId);
            if (initialNode is null || !BpmnFlowNodeTypes.IsStart(initialNode.Type))
            {
                model.InitialEventId = model.FlowNodes
                    .FirstOrDefault(n => BpmnFlowNodeTypes.IsStart(n.Type))
                    ?.Id;
            }
        }
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

    private static string MapLegacyType(LegacyStepModel step) => step.Type switch
    {
        "start" => BpmnFlowNodeTypes.StartEvent,
        "startEvent" => BpmnFlowNodeTypes.StartEvent,
        "end" => BpmnFlowNodeTypes.EndEvent,
        "endEvent" => BpmnFlowNodeTypes.EndEvent,
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

    private static void ApplyNodeInvariants(FlowNodeModel node)
    {
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
            node.Message = null;
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
            node.Condition = null;
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
            node.Condition = null;
            node.Message ??= new MessageCatchModel();
        }
        else if (BpmnFlowNodeTypes.IsMessageStart(node.Type))
        {
            // A message start event is an entry point started by an external
            // system. It carries start variables (like a startEvent) and the
            // delivery configuration on node.Message (like a message catch), and
            // nothing else. Pass-through: the engine auto-advances off it after
            // creating the instance.
            node.RequiresClaim = false;
            node.ClaimMode = ClaimModes.Fresh;
            node.InheritClaimFromNodeId = null;
            node.Roles = [];
            node.Variables ??= [];
            node.Service = null;
            node.Assignments = [];
            node.Script = null;
            node.Condition = null;
            node.Message ??= new MessageCatchModel();
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
                node.MultiInstance.Mode = node.MultiInstance.Mode == MultiInstanceModes.Sequential
                    ? MultiInstanceModes.Sequential
                    : MultiInstanceModes.Parallel;
                node.MultiInstance.Source = node.MultiInstance.Source == MultiInstanceSources.Cardinality
                    ? MultiInstanceSources.Cardinality
                    : MultiInstanceSources.Collection;
                if (node.MultiInstance.Source == MultiInstanceSources.Collection)
                {
                    node.MultiInstance.CardinalityExpression = null;
                }
                else
                {
                    node.MultiInstance.CollectionVariable = null;
                }
            }
        }
    }
}
