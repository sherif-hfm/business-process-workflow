using System.Text.Json;
using WorkflowEngine.Service.Abstractions;
using WorkflowEngine.Service.Models;
using WorkflowEngine.Shared.Dtos;
using WorkflowEngine.Shared.Models;

namespace WorkflowEngine.Service.Services;

public sealed class WorkflowEngineService(
    IWorkflowDefinitionRepository definitions,
    IWorkflowRuntimeRepository runtime,
    IUnitOfWork unitOfWork)
    : IWorkflowEngineService
{
    public async Task<InstanceDetailDto> StartInstanceAsync(
        long workflowId,
        string? startedBy,
        Dictionary<string, JsonElement>? variableValues,
        CancellationToken cancellationToken)
    {
        var workflow = await GetPublishedWorkflowAsync(workflowId, cancellationToken);
        var initialStep = GetStep(workflow.Definition, workflow.Definition.InitialStepId!.Value);
        ValidateVariableValues(initialStep.Variables, variableValues);

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var instance = await runtime.AddInstanceAsync(workflow.Id, initialStep.Id, startedBy, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        foreach (var variable in initialStep.Variables)
        {
            if (TryGetValue(variableValues, variable.Name, out var value))
            {
                await runtime.AddVariableAsync(instance.Id, variable.Name, null, value, cancellationToken);
            }
        }

        instance = await ResolveAutoAdvanceAsync(instance, workflow.Definition, startedBy, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return (await BuildDetailAsync(instance.Id, cancellationToken))!;
    }

    public async Task<IReadOnlyList<InstanceSummaryDto>> ListInstancesAsync(
        string? status,
        CancellationToken cancellationToken)
    {
        var instances = await runtime.ListInstancesAsync(status, cancellationToken);
        var result = new List<InstanceSummaryDto>(instances.Count);

        foreach (var instance in instances)
        {
            var workflow = await definitions.GetAsync(instance.WorkflowDefinitionId, cancellationToken)
                ?? throw new WorkflowDomainException($"Workflow definition #{instance.WorkflowDefinitionId} was not found.");
            result.Add(ToSummary(instance, workflow));
        }

        return result;
    }

    public Task<InstanceDetailDto?> GetInstanceAsync(long id, CancellationToken cancellationToken) =>
        BuildDetailAsync(id, cancellationToken);

    public async Task<IReadOnlyList<ActionModel>> GetAvailableActionsAsync(
        long id,
        CancellationToken cancellationToken)
    {
        var instance = await runtime.GetInstanceAsync(id, cancellationToken);
        if (instance is null || instance.Status != WorkflowInstanceStatuses.Running)
        {
            return [];
        }

        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var step = GetStep(workflow.Definition, instance.CurrentStepId);
        if (step.Type == WorkflowStepTypes.End || step.AutoAdvance)
        {
            return [];
        }

        if (step.RequiresClaim && string.IsNullOrWhiteSpace(instance.ClaimedBy))
        {
            return [];
        }

        return step.Actions;
    }

    public async Task<InstanceDetailDto?> ClaimAsync(
        long id,
        string? user,
        CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var instance = await runtime.GetInstanceForUpdateAsync(id, cancellationToken);
        if (instance is null)
        {
            return null;
        }

        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var step = GetStep(workflow.Definition, instance.CurrentStepId);
        if (instance.Status != WorkflowInstanceStatuses.Running || !step.RequiresClaim)
        {
            throw new WorkflowDomainException("The current step cannot be claimed.");
        }

        var normalizedUser = NormalizeUser(user);
        if (!string.IsNullOrWhiteSpace(instance.ClaimedBy) && instance.ClaimedBy != normalizedUser)
        {
            throw new WorkflowDomainException($"The current step is already claimed by '{instance.ClaimedBy}'.");
        }

        await runtime.UpdateInstanceAsync(
            instance.Id,
            instance.CurrentStepId,
            instance.Status,
            normalizedUser,
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await BuildDetailAsync(id, cancellationToken);
    }

    public async Task<InstanceDetailDto?> UnclaimAsync(long id, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var instance = await runtime.GetInstanceForUpdateAsync(id, cancellationToken);
        if (instance is null)
        {
            return null;
        }

        await runtime.UpdateInstanceAsync(instance.Id, instance.CurrentStepId, instance.Status, null, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await BuildDetailAsync(id, cancellationToken);
    }

    public async Task<InstanceDetailDto?> TakeActionAsync(
        long id,
        int actionId,
        string? performedBy,
        Dictionary<string, JsonElement>? variableValues,
        CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var instance = await runtime.GetInstanceForUpdateAsync(id, cancellationToken);
        if (instance is null)
        {
            return null;
        }

        if (instance.Status != WorkflowInstanceStatuses.Running)
        {
            throw new WorkflowDomainException("Only running instances can take actions.");
        }

        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var step = GetStep(workflow.Definition, instance.CurrentStepId);
        var action = step.Actions.SingleOrDefault(a => a.Id == actionId);
        if (action is null || step.AutoAdvance || step.Type == WorkflowStepTypes.End)
        {
            throw new WorkflowDomainException("The requested action is not available from the current step.");
        }

        EnsureActionAllowedByClaim(step, instance, performedBy);

        ValidateVariableValues(action.Variables, variableValues);
        foreach (var variable in action.Variables)
        {
            if (TryGetValue(variableValues, variable.Name, out var value))
            {
                await runtime.AddVariableAsync(instance.Id, variable.Name, action.Id, value, cancellationToken);
            }
        }

        var payload = CloneDictionary(variableValues);
        await runtime.AddHistoryAsync(
            instance.Id,
            action.Id,
            step.Id,
            action.ToStepId,
            performedBy,
            payload,
            null,
            cancellationToken);

        var nextStep = GetStep(workflow.Definition, action.ToStepId);
        var nextStatus = nextStep.Type == WorkflowStepTypes.End
            ? WorkflowInstanceStatuses.Completed
            : WorkflowInstanceStatuses.Running;

        instance = instance with
        {
            CurrentStepId = nextStep.Id,
            Status = nextStatus,
            ClaimedBy = null,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await runtime.UpdateInstanceAsync(instance.Id, instance.CurrentStepId, instance.Status, null, cancellationToken);
        instance = await ResolveAutoAdvanceAsync(instance, workflow.Definition, performedBy, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await BuildDetailAsync(id, cancellationToken);
    }

    public async Task<bool> CancelAsync(long id, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var instance = await runtime.GetInstanceForUpdateAsync(id, cancellationToken);
        if (instance is null)
        {
            return false;
        }

        await runtime.UpdateInstanceAsync(
            instance.Id,
            instance.CurrentStepId,
            WorkflowInstanceStatuses.Cancelled,
            instance.ClaimedBy,
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return true;
    }

    private async Task<WorkflowInstanceRecord> ResolveAutoAdvanceAsync(
        WorkflowInstanceRecord instance,
        WorkflowModel definition,
        string? performedBy,
        CancellationToken cancellationToken)
    {
        var maxHops = definition.Steps.Count + 1;
        for (var hop = 0; hop < maxHops; hop++)
        {
            if (instance.Status != WorkflowInstanceStatuses.Running)
            {
                return instance;
            }

            var currentStep = GetStep(definition, instance.CurrentStepId);
            if (!currentStep.AutoAdvance)
            {
                return instance;
            }

            if (currentStep.NextStepId is null)
            {
                throw new WorkflowDomainException($"Auto-advance step #{currentStep.Id} is missing nextStepId.");
            }

            var nextStep = GetStep(definition, currentStep.NextStepId.Value);
            var nextStatus = nextStep.Type == WorkflowStepTypes.End
                ? WorkflowInstanceStatuses.Completed
                : WorkflowInstanceStatuses.Running;

            await runtime.AddHistoryAsync(
                instance.Id,
                null,
                currentStep.Id,
                nextStep.Id,
                performedBy,
                null,
                "auto-advance",
                cancellationToken);

            instance = instance with
            {
                CurrentStepId = nextStep.Id,
                Status = nextStatus,
                ClaimedBy = null,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            await runtime.UpdateInstanceAsync(instance.Id, instance.CurrentStepId, instance.Status, null, cancellationToken);
        }

        throw new WorkflowDomainException("Auto-advance cycle detected.");
    }

    private async Task<InstanceDetailDto?> BuildDetailAsync(long id, CancellationToken cancellationToken)
    {
        var instance = await runtime.GetInstanceAsync(id, cancellationToken);
        if (instance is null)
        {
            return null;
        }

        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var variables = await runtime.ListVariablesAsync(id, cancellationToken);
        var history = await runtime.ListHistoryAsync(id, cancellationToken);
        var step = GetStep(workflow.Definition, instance.CurrentStepId);

        return new InstanceDetailDto(
            instance.Id,
            WorkflowDefinitionService.ToDetail(workflow),
            instance.CurrentStepId,
            step.Name,
            instance.Status,
            instance.ClaimedBy,
            instance.StartedBy,
            instance.CreatedAt,
            instance.UpdatedAt,
            variables.Select(v => new InstanceVariableDto(
                v.Id,
                v.VariableName,
                v.SourceActionId,
                v.Value,
                v.SetAt)).ToList(),
            history.Select(h => new InstanceHistoryDto(
                h.Id,
                h.ActionId,
                h.FromStepId,
                h.ToStepId,
                h.PerformedBy,
                h.Payload,
                h.Note,
                h.PerformedAt)).ToList());
    }

    private async Task<WorkflowDefinitionRecord> GetPublishedWorkflowAsync(
        long id,
        CancellationToken cancellationToken)
    {
        var workflow = await GetWorkflowAsync(id, cancellationToken);
        if (!workflow.IsPublished)
        {
            throw new WorkflowDomainException("Workflow must be published before instances can be started.");
        }

        return workflow;
    }

    private async Task<WorkflowDefinitionRecord> GetWorkflowAsync(long id, CancellationToken cancellationToken) =>
        await definitions.GetAsync(id, cancellationToken)
        ?? throw new WorkflowDomainException($"Workflow definition #{id} was not found.");

    private static InstanceSummaryDto ToSummary(
        WorkflowInstanceRecord instance,
        WorkflowDefinitionRecord workflow)
    {
        var step = GetStep(workflow.Definition, instance.CurrentStepId);
        return new InstanceSummaryDto(
            instance.Id,
            workflow.Id,
            workflow.Name,
            workflow.Version,
            instance.CurrentStepId,
            step.Name,
            instance.Status,
            instance.ClaimedBy,
            instance.StartedBy,
            instance.CreatedAt,
            instance.UpdatedAt);
    }

    private static StepModel GetStep(WorkflowModel definition, int stepId) =>
        definition.Steps.SingleOrDefault(s => s.Id == stepId)
        ?? throw new WorkflowDomainException($"Step #{stepId} was not found in workflow '{definition.Name}'.");

    private static void EnsureActionAllowedByClaim(
        StepModel step,
        WorkflowInstanceRecord instance,
        string? performedBy)
    {
        if (!step.RequiresClaim)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(instance.ClaimedBy))
        {
            throw new WorkflowDomainException("The current step must be claimed before taking an action.");
        }

        if (instance.ClaimedBy != NormalizeUser(performedBy))
        {
            throw new WorkflowDomainException($"Only '{instance.ClaimedBy}' can take actions on this step.");
        }
    }

    private static string NormalizeUser(string? user) =>
        string.IsNullOrWhiteSpace(user) ? "anonymous" : user.Trim();

    private static void ValidateVariableValues(
        IReadOnlyList<VariableModel> variables,
        Dictionary<string, JsonElement>? values)
    {
        foreach (var variable in variables.Where(v => v.Required))
        {
            if (!TryGetValue(values, variable.Name, out var value) || IsEmpty(value))
            {
                throw new WorkflowDomainException($"Required variable '{variable.Name}' is missing.");
            }
        }
    }

    private static bool TryGetValue(
        Dictionary<string, JsonElement>? values,
        string name,
        out JsonElement value)
    {
        value = default;
        if (values is null)
        {
            return false;
        }

        foreach (var pair in values)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value.Clone();
                return true;
            }
        }

        return false;
    }

    private static bool IsEmpty(JsonElement value) =>
        value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
        || (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()));

    private static Dictionary<string, JsonElement>? CloneDictionary(Dictionary<string, JsonElement>? values)
    {
        if (values is null)
        {
            return null;
        }

        return values.ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
    }
}
