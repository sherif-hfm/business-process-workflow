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
        int? startEventId,
        Dictionary<string, JsonElement>? variableValues,
        CancellationToken cancellationToken)
    {
        var workflow = await GetPublishedWorkflowAsync(workflowId, cancellationToken);
        var resolvedStartEventId = startEventId ?? workflow.Definition.InitialEventId
            ?? throw new WorkflowDomainException("Workflow has no default start event.");

        var startEvent = GetFlowNode(workflow.Definition, resolvedStartEventId);
        if (!BpmnFlowNodeTypes.IsStart(startEvent.Type))
        {
            throw new WorkflowDomainException($"Flow node #{resolvedStartEventId} is not a start event.");
        }

        ValidateVariableValues(startEvent.Variables, variableValues);

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var instance = await runtime.AddInstanceAsync(workflow.Id, startEvent.Id, startedBy, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        foreach (var variable in startEvent.Variables)
        {
            if (TryGetValue(variableValues, variable.Name, out var value))
            {
                await runtime.AddVariableAsync(instance.Id, variable.Name, null, value, cancellationToken);
            }
        }

        // Flush variables so pass-through gateways can read them within this transaction.
        await unitOfWork.SaveChangesAsync(cancellationToken);
        instance = await ResolvePassThroughAsync(instance, workflow.Definition, startedBy, cancellationToken);
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

    public async Task<IReadOnlyList<InboxItemDto>> GetInboxAsync(
        string? user,
        IReadOnlyCollection<string> roles,
        CancellationToken cancellationToken)
    {
        var normalizedUser = NormalizeUser(user);
        var normalizedRoles = roles
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var instances = await runtime.ListInstancesAsync(WorkflowInstanceStatuses.Running, cancellationToken);
        var definitionIds = instances.Select(i => i.WorkflowDefinitionId).Distinct().ToList();
        var workflows = new Dictionary<long, WorkflowDefinitionRecord>(definitionIds.Count);

        foreach (var definitionId in definitionIds)
        {
            var workflow = await definitions.GetAsync(definitionId, cancellationToken)
                ?? throw new WorkflowDomainException($"Workflow definition #{definitionId} was not found.");
            workflows[definitionId] = workflow;
        }

        var result = new List<InboxItemDto>();

        foreach (var instance in instances)
        {
            var workflow = workflows[instance.WorkflowDefinitionId];
            var node = GetFlowNode(workflow.Definition, instance.CurrentStepId);

            if (!BpmnFlowNodeTypes.IsUserTask(node.Type))
            {
                continue;
            }

            var claimedByMe = instance.ClaimedBy == normalizedUser;
            var claimedByOther = !string.IsNullOrWhiteSpace(instance.ClaimedBy) && !claimedByMe;
            var roleMatch = node.Roles.Count == 0
                || node.Roles.Any(r => normalizedRoles.Contains(r));

            if (!roleMatch && !claimedByMe)
            {
                continue;
            }

            if (node.RequiresClaim && claimedByOther)
            {
                continue;
            }

            var canClaim = node.RequiresClaim && !claimedByMe && !claimedByOther && roleMatch;
            var canAct = claimedByMe || (!node.RequiresClaim && roleMatch);

            result.Add(new InboxItemDto(
                instance.Id,
                workflow.Id,
                workflow.Name,
                node.Id,
                node.Name,
                node.Roles,
                node.RequiresClaim,
                instance.ClaimedBy,
                claimedByMe,
                canClaim,
                canAct,
                instance.CreatedAt,
                instance.UpdatedAt));
        }

        return result
            .OrderBy(i => i.CreatedAt)
            .ToList();
    }

    public Task<InstanceDetailDto?> GetInstanceAsync(long id, CancellationToken cancellationToken) =>
        BuildDetailAsync(id, cancellationToken);

    public async Task<IReadOnlyList<SequenceFlowModel>> GetAvailableFlowsAsync(
        long id,
        CancellationToken cancellationToken)
    {
        var instance = await runtime.GetInstanceAsync(id, cancellationToken);
        if (instance is null || instance.Status != WorkflowInstanceStatuses.Running)
        {
            return [];
        }

        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var node = GetFlowNode(workflow.Definition, instance.CurrentStepId);
        if (!BpmnFlowNodeTypes.IsUserTask(node.Type))
        {
            return [];
        }

        if (node.RequiresClaim && string.IsNullOrWhiteSpace(instance.ClaimedBy))
        {
            return [];
        }

        return OutgoingFlows(workflow.Definition, node.Id);
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
        var node = GetFlowNode(workflow.Definition, instance.CurrentStepId);
        if (instance.Status != WorkflowInstanceStatuses.Running || !node.RequiresClaim)
        {
            throw new WorkflowDomainException("The current flow node cannot be claimed.");
        }

        var normalizedUser = NormalizeUser(user);
        if (!string.IsNullOrWhiteSpace(instance.ClaimedBy) && instance.ClaimedBy != normalizedUser)
        {
            throw new WorkflowDomainException($"The current flow node is already claimed by '{instance.ClaimedBy}'.");
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

    public async Task<InstanceDetailDto?> TakeFlowAsync(
        long id,
        int flowId,
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
            throw new WorkflowDomainException("Only running instances can take a sequence flow.");
        }

        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var node = GetFlowNode(workflow.Definition, instance.CurrentStepId);
        var flow = OutgoingFlows(workflow.Definition, node.Id).SingleOrDefault(f => f.Id == flowId);
        if (flow is null || !BpmnFlowNodeTypes.IsUserTask(node.Type))
        {
            throw new WorkflowDomainException("The requested sequence flow is not available from the current node.");
        }

        EnsureActionAllowedByClaim(node, instance, performedBy);

        ValidateVariableValues(flow.Variables, variableValues);
        foreach (var variable in flow.Variables)
        {
            if (TryGetValue(variableValues, variable.Name, out var value))
            {
                await runtime.AddVariableAsync(instance.Id, variable.Name, flow.Id, value, cancellationToken);
            }
        }

        var payload = CloneDictionary(variableValues);
        await runtime.AddHistoryAsync(
            instance.Id,
            flow.Id,
            node.Id,
            flow.TargetRef,
            performedBy,
            payload,
            null,
            cancellationToken);

        var nextNode = GetFlowNode(workflow.Definition, flow.TargetRef);
        var nextStatus = BpmnFlowNodeTypes.IsEnd(nextNode.Type)
            ? WorkflowInstanceStatuses.Completed
            : WorkflowInstanceStatuses.Running;

        instance = instance with
        {
            CurrentStepId = nextNode.Id,
            Status = nextStatus,
            ClaimedBy = null,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await runtime.UpdateInstanceAsync(instance.Id, instance.CurrentStepId, instance.Status, null, cancellationToken);
        // Flush captured variables so pass-through gateways can read them within this transaction.
        await unitOfWork.SaveChangesAsync(cancellationToken);
        instance = await ResolvePassThroughAsync(instance, workflow.Definition, performedBy, cancellationToken);

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

    private async Task<WorkflowInstanceRecord> ResolvePassThroughAsync(
        WorkflowInstanceRecord instance,
        WorkflowModel definition,
        string? performedBy,
        CancellationToken cancellationToken)
    {
        var maxHops = definition.FlowNodes.Count + 1;
        for (var hop = 0; hop < maxHops; hop++)
        {
            if (instance.Status != WorkflowInstanceStatuses.Running)
            {
                return instance;
            }

            var currentNode = GetFlowNode(definition, instance.CurrentStepId);
            if (!BpmnFlowNodeTypes.IsPassThrough(currentNode.Type))
            {
                return instance;
            }

            var variables = await LoadVariablesAsync(instance.Id, cancellationToken);
            var flow = SelectPassThroughFlow(definition, currentNode, variables);
            var nextNode = GetFlowNode(definition, flow.TargetRef);
            var nextStatus = BpmnFlowNodeTypes.IsEnd(nextNode.Type)
                ? WorkflowInstanceStatuses.Completed
                : WorkflowInstanceStatuses.Running;

            var note = currentNode.Type switch
            {
                var t when BpmnFlowNodeTypes.IsStart(t) => "start",
                var t when BpmnFlowNodeTypes.IsGateway(t) => "gateway",
                _ => "automatic"
            };

            await runtime.AddHistoryAsync(
                instance.Id,
                null,
                currentNode.Id,
                nextNode.Id,
                performedBy,
                null,
                note,
                cancellationToken);

            instance = instance with
            {
                CurrentStepId = nextNode.Id,
                Status = nextStatus,
                ClaimedBy = null,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            await runtime.UpdateInstanceAsync(instance.Id, instance.CurrentStepId, instance.Status, null, cancellationToken);
        }

        throw new WorkflowDomainException("Pass-through routing cycle detected.");
    }

    private SequenceFlowModel SelectPassThroughFlow(
        WorkflowModel definition,
        FlowNodeModel node,
        IReadOnlyDictionary<string, JsonElement> variables)
    {
        var outgoing = OutgoingFlows(definition, node.Id);

        if (BpmnFlowNodeTypes.IsGateway(node.Type))
        {
            var match = outgoing.FirstOrDefault(f =>
                !f.IsDefault
                && !string.IsNullOrWhiteSpace(f.Condition)
                && SequenceFlowConditionEvaluator.Evaluate(f.Condition, variables));
            if (match is not null)
            {
                return match;
            }

            var defaultFlow = outgoing.FirstOrDefault(f => f.IsDefault);
            if (defaultFlow is not null)
            {
                return defaultFlow;
            }

            throw new WorkflowDomainException(
                $"Exclusive gateway #{node.Id} has no matching condition and no default flow.");
        }

        if (outgoing.Count != 1)
        {
            var kind = BpmnFlowNodeTypes.IsStart(node.Type) ? "Start event" : "Automatic task";
            throw new WorkflowDomainException($"{kind} #{node.Id} must have exactly one outgoing sequence flow.");
        }

        return outgoing[0];
    }

    private async Task<Dictionary<string, JsonElement>> LoadVariablesAsync(
        long instanceId,
        CancellationToken cancellationToken)
    {
        var stored = await runtime.ListVariablesAsync(instanceId, cancellationToken);
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in stored)
        {
            result[variable.VariableName] = variable.Value;
        }

        return result;
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
        var node = GetFlowNode(workflow.Definition, instance.CurrentStepId);

        return new InstanceDetailDto(
            instance.Id,
            WorkflowDefinitionService.ToDetail(workflow),
            instance.CurrentStepId,
            node.Name,
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
        var node = GetFlowNode(workflow.Definition, instance.CurrentStepId);
        return new InstanceSummaryDto(
            instance.Id,
            workflow.Id,
            workflow.Name,
            workflow.Version,
            instance.CurrentStepId,
            node.Name,
            instance.Status,
            instance.ClaimedBy,
            instance.StartedBy,
            instance.CreatedAt,
            instance.UpdatedAt);
    }

    private static FlowNodeModel GetFlowNode(WorkflowModel definition, int nodeId) =>
        definition.FlowNodes.SingleOrDefault(n => n.Id == nodeId)
        ?? throw new WorkflowDomainException($"Flow node #{nodeId} was not found in workflow '{definition.Name}'.");

    private static IReadOnlyList<SequenceFlowModel> OutgoingFlows(WorkflowModel definition, int nodeId) =>
        definition.SequenceFlows.Where(f => f.SourceRef == nodeId).ToList();

    private static void EnsureActionAllowedByClaim(
        FlowNodeModel node,
        WorkflowInstanceRecord instance,
        string? performedBy)
    {
        if (!node.RequiresClaim)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(instance.ClaimedBy))
        {
            throw new WorkflowDomainException("The current flow node must be claimed before taking a sequence flow.");
        }

        if (instance.ClaimedBy != NormalizeUser(performedBy))
        {
            throw new WorkflowDomainException($"Only '{instance.ClaimedBy}' can act on this flow node.");
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
