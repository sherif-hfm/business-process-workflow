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

        foreach (var node in model.FlowNodes)
        {
            ApplyNodeInvariants(node);
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
        if (BpmnFlowNodeTypes.IsEnd(node.Type))
        {
            node.RequiresClaim = false;
            node.Roles = [];
            node.Variables = [];
        }
        else if (BpmnFlowNodeTypes.IsAutomatic(node.Type) || BpmnFlowNodeTypes.IsGateway(node.Type))
        {
            node.RequiresClaim = false;
            node.Roles = [];
            node.Variables = [];
        }
        else if (BpmnFlowNodeTypes.IsStart(node.Type))
        {
            node.RequiresClaim = false;
            node.Roles = [];
        }
    }
}
