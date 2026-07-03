namespace WorkflowEngine.Shared.Models;

public static class WorkflowModelMigrator
{
    public static void Normalize(WorkflowModel model)
    {
        var isLegacy = model.Steps.Any(step =>
            step.Type is "start" or "end" || step.AutoAdvance);

        foreach (var step in model.Steps)
        {
            if (isLegacy)
            {
                MigrateLegacyStep(step);
            }
            else
            {
                MigrateStartEventActions(step);
                ApplyTypeInvariants(step);
            }
        }
    }

    private static void MigrateLegacyStep(StepModel step)
    {
        switch (step.Type)
        {
            case "start":
                step.Type = WorkflowStepTypes.StartEvent;
                if (step.Actions.Count > 0)
                {
                    var firstAction = step.Actions[0];
                    if (step.NextStepId is null)
                    {
                        step.NextStepId = firstAction.ToStepId;
                    }

                    MergeActionVariables(step, firstAction);
                    step.Actions = [];
                }
                else if (step.AutoAdvance && step.NextStepId.HasValue)
                {
                    step.Actions = [];
                }

                break;
            case "end":
                step.Type = WorkflowStepTypes.EndEvent;
                break;
            case "task" when step.AutoAdvance:
                step.Type = WorkflowStepTypes.Task;
                break;
            case "task":
                step.Type = WorkflowStepTypes.UserTask;
                break;
        }

        step.AutoAdvance = false;
        MigrateStartEventActions(step);
        ApplyTypeInvariants(step);
    }

    private static void MigrateStartEventActions(StepModel step)
    {
        if (!WorkflowStepTypes.IsStart(step.Type) || step.Actions.Count == 0)
        {
            return;
        }

        var firstAction = step.Actions[0];
        if (step.NextStepId is null)
        {
            step.NextStepId = firstAction.ToStepId;
        }

        MergeActionVariables(step, firstAction);
        step.Actions = [];
    }

    private static void MergeActionVariables(StepModel step, ActionModel action)
    {
        foreach (var variable in action.Variables)
        {
            if (step.Variables.All(v =>
                    !string.Equals(v.Name, variable.Name, StringComparison.OrdinalIgnoreCase)))
            {
                step.Variables.Add(variable);
            }
        }
    }

    private static void ApplyTypeInvariants(StepModel step)
    {
        if (WorkflowStepTypes.IsEnd(step.Type))
        {
            step.RequiresClaim = false;
            step.NextStepId = null;
            step.AutoAdvance = false;
            step.Actions = [];
        }
        else if (WorkflowStepTypes.IsAutomatic(step.Type))
        {
            step.RequiresClaim = false;
            step.Roles = [];
            step.Actions = [];
            step.Variables = [];
            step.AutoAdvance = false;
        }
        else if (WorkflowStepTypes.IsUserTask(step.Type))
        {
            step.NextStepId = null;
            step.AutoAdvance = false;
        }
        else if (WorkflowStepTypes.IsStart(step.Type))
        {
            step.RequiresClaim = false;
            step.Roles = [];
            step.Actions = [];
            step.AutoAdvance = false;
        }
    }
}
