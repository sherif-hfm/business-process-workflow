using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using WorkflowEngine.Service.Abstractions;
using WorkflowEngine.Service.Models;
using WorkflowEngine.Shared.Dtos;
using WorkflowEngine.Shared.Models;

namespace WorkflowEngine.Service.Services;

public sealed class WorkflowEngineService(
    IWorkflowDefinitionRepository definitions,
    IWorkflowRuntimeRepository runtime,
    IUnitOfWork unitOfWork,
    IServiceTaskInvoker serviceTaskInvoker,
    IScriptEvaluator scriptEvaluator,
    WorkflowContextOptions contextOptions,
    TimeProvider timeProvider)
    : IWorkflowEngineService
{
    public async Task<InstanceDetailDto> StartInstanceAsync(
        long workflowId,
        ActorContext actor,
        int? startEventId,
        Dictionary<string, JsonElement>? variableValues,
        CancellationToken cancellationToken)
    {
        var startedBy = actor.User;
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
        var instance = await runtime.AddInstanceAsync(workflow.Id, ToSnapshot(startEvent), startedBy, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Resolve templated defaults and run NCalc validation against the final values
        // overlaid with sys.*/config.* context, then persist each resolved value.
        var startContext = WithContext(
            new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase),
            actor, instance, workflow.Definition, startEvent);
        var startValues = ResolveAndValidateVariables(startEvent.Variables, variableValues, startContext);
        foreach (var pair in startValues)
        {
            await runtime.AddVariableAsync(instance.Id, pair.Key, null, pair.Value, cancellationToken);
        }

        // Initialize process-level variables from their authored defaults so every
        // declared name is readable from hop 0. Defaults are templated/coerced like
        // start-variable defaults and validated against the start values + context.
        var processContext = new Dictionary<string, JsonElement>(startContext, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in startValues)
        {
            processContext[pair.Key] = pair.Value;
        }
        var processValues = ResolveAndValidateVariables(workflow.Definition.Variables, null, processContext);
        foreach (var pair in processValues)
        {
            await runtime.AddVariableAsync(instance.Id, pair.Key, null, pair.Value, cancellationToken);
        }

        // Flush variables so pass-through gateways can read them within this transaction.
        await unitOfWork.SaveChangesAsync(cancellationToken);
        instance = await ResolvePassThroughAsync(instance, workflow.Definition, actor, cancellationToken);
        instance = await ApplyClaimInheritanceAsync(instance, workflow.Definition, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return (await BuildDetailAsync(instance.Id, cancellationToken))!;
    }

    public async Task<PagedResult<InstanceSummaryDto>> ListInstancesAsync(
        string? status,
        long? instanceId,
        long? workflowId,
        int? workflowKey,
        int? nodeId,
        string? nodeExternalId,
        IReadOnlyList<string>? variables,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var variableFilters = ParseVariableFilters(variables);
        var paged = await runtime.ListInstancesAsync(status, instanceId, workflowId, workflowKey, nodeId, nodeExternalId, variableFilters, page, pageSize, cancellationToken);
        var items = paged.Items.Select(ToSummary).ToList();
        return new PagedResult<InstanceSummaryDto>(items, paged.Page, paged.PageSize, paged.TotalCount);
    }

    public async Task<PagedResult<InboxItemDto>> GetInboxAsync(
        ActorContext actor,
        long? instanceId,
        long? workflowId,
        int? workflowKey,
        int? nodeId,
        string? nodeExternalId,
        IReadOnlyList<string>? variables,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var normalizedUser = NormalizeUser(actor.User);
        var normalizedRoles = NormalizeRoles(actor.Roles);
        var variableFilters = ParseVariableFilters(variables);

        // Pull the full actor-filtered candidate set (running user tasks) from SQL.
        // Visibility conditions are evaluated below so the count and paging are exact.
        var candidates = await runtime.ListInboxCandidatesAsync(
            normalizedUser, normalizedRoles, instanceId, workflowId, workflowKey, nodeId, nodeExternalId, variableFilters, cancellationToken);

        if (candidates.Count == 0)
        {
            return new PagedResult<InboxItemDto>([], page, pageSize, 0);
        }

        // Skip optimization: load definitions only once, then only load variables and
        // evaluate conditions if at least one relevant userTask carries a node condition.
        var definitionIds = candidates.Select(c => c.WorkflowDefinitionId).Distinct().ToList();
        var definitions = new Dictionary<long, WorkflowDefinitionRecord>(definitionIds.Count);
        foreach (var id in definitionIds)
        {
            definitions[id] = await GetWorkflowAsync(id, cancellationToken);
        }

        var hasNodeCondition = definitions.Values.Any(w =>
            w.Definition.FlowNodes.Any(n =>
                BpmnFlowNodeTypes.IsUserTask(n.Type) && !string.IsNullOrWhiteSpace(n.Condition)));

        List<InstanceListItem> visible;
        if (hasNodeCondition)
        {
            var instanceIds = candidates.Select(c => c.Id).ToList();
            var allVars = await runtime.ListVariablesForInstancesAsync(instanceIds, cancellationToken);
            // instance_variables is append-only (one row per write), so the same
            // variable name may appear multiple times per instance. Take the last
            // write (records are ordered by InstanceId then Id ascending, so later
            // entries overwrite earlier ones).
            var varsByInstance = new Dictionary<long, Dictionary<string, JsonElement>>();
            foreach (var v in allVars)
            {
                if (!varsByInstance.TryGetValue(v.InstanceId, out var dict))
                {
                    dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                    varsByInstance[v.InstanceId] = dict;
                }

                dict[v.VariableName] = v.Value;
            }

            visible = new List<InstanceListItem>(candidates.Count);
            foreach (var row in candidates)
            {
                var definition = definitions[row.WorkflowDefinitionId];
                var node = definition.Definition.FlowNodes.SingleOrDefault(n => n.Id == row.CurrentNodeId);

                // Defensive: a candidate whose resting node cannot be resolved in its own
                // definition version is not actionable; skip it rather than fail the whole inbox.
                if (node is null)
                {
                    continue;
                }

                // Fast path: an unconditioned userTask (or one in a definition without a
                // condition) is always visible and needs no variable/context evaluation.
                if (string.IsNullOrWhiteSpace(node.Condition))
                {
                    visible.Add(row);
                    continue;
                }

                var instance = new WorkflowInstanceRecord(
                    row.Id,
                    row.WorkflowDefinitionId,
                    row.CurrentNodeId,
                    row.Status,
                    row.ClaimedBy,
                    row.StartedBy,
                    row.CreatedAt,
                    row.UpdatedAt);

                varsByInstance.TryGetValue(row.Id, out var stored);
                var ctx = WithContext(
                    stored ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase),
                    actor,
                    instance,
                    definition.Definition,
                    node);
                if (NodeVisible(node, ctx))
                {
                    visible.Add(row);
                }
            }
        }
        else
        {
            visible = [.. candidates];
        }

        var totalCount = visible.Count;
        var pageItems = visible
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        var items = pageItems.Select(row => ToInboxItem(row, normalizedUser, normalizedRoles)).ToList();
        return new PagedResult<InboxItemDto>(items, page, pageSize, totalCount);
    }

    private static InboxItemDto ToInboxItem(InstanceListItem row, string normalizedUser, IReadOnlySet<string> normalizedRoles)
    {
        var claimedByMe = row.ClaimedBy == normalizedUser;
        var claimedByOther = !string.IsNullOrWhiteSpace(row.ClaimedBy) && !claimedByMe;
        var roleMatch = row.CurrentNodeRoles.Count == 0
            || row.CurrentNodeRoles.Any(normalizedRoles.Contains);

        var canClaim = row.CurrentRequiresClaim && !claimedByMe && !claimedByOther && roleMatch;
        var canAct = claimedByMe || (!row.CurrentRequiresClaim && roleMatch);

        return new InboxItemDto(
            row.Id,
            row.WorkflowId,
            row.WorkflowName,
            row.CurrentNodeId,
            row.CurrentNodeName,
            row.CurrentNodeExternalId,
            row.CurrentNodeRoles,
            row.CurrentRequiresClaim,
            row.ClaimedBy,
            claimedByMe,
            canClaim,
            canAct,
            row.CreatedAt,
            row.UpdatedAt);
    }

    public Task<InstanceDetailDto?> GetInstanceAsync(long id, CancellationToken cancellationToken) =>
        BuildDetailAsync(id, cancellationToken);

    public async Task<IReadOnlyList<SequenceFlowModel>> GetAvailableFlowsAsync(
        long id,
        ActorContext actor,
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

        if (!RoleAllowed(node, NormalizeRoles(actor.Roles)))
        {
            return [];
        }

        if (node.RequiresClaim && string.IsNullOrWhiteSpace(instance.ClaimedBy))
        {
            return [];
        }

        var stored = await LoadVariablesAsync(instance.Id, cancellationToken);
        var evalCtx = WithContext(stored, actor, instance, workflow.Definition, node);
        if (!NodeVisible(node, evalCtx))
        {
            return [];
        }

        return OutgoingFlows(workflow.Definition, node.Id)
            .Where(f => f.IsDefault || string.IsNullOrWhiteSpace(f.Condition)
                        || SequenceFlowConditionEvaluator.Evaluate(f.Condition, evalCtx))
            .ToList();
    }

    public async Task<InstanceDetailDto?> ClaimAsync(
        long id,
        ActorContext actor,
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

        EnsureRoleAllowed(node, actor);

        var stored = await LoadVariablesAsync(instance.Id, cancellationToken);
        var evalCtx = WithContext(stored, actor, instance, workflow.Definition, node);
        if (!NodeVisible(node, evalCtx))
        {
            throw new WorkflowDomainException("The current flow node is not currently visible.");
        }

        var normalizedUser = NormalizeUser(actor.User);
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
        ActorContext actor,
        Dictionary<string, JsonElement>? variableValues,
        CancellationToken cancellationToken)
    {
        var performedBy = actor.User;
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

        EnsureRoleAllowed(node, actor);
        EnsureActionAllowedByClaim(node, instance, performedBy);

        ValidateVariableValues(flow.Variables, variableValues);

        // Resolve templated defaults and run NCalc validation against the existing
        // stored variables plus the final flow values, overlaid with context.
        var storedForValidation = await LoadVariablesAsync(instance.Id, cancellationToken);
        var flowContext = WithContext(storedForValidation, actor, instance, workflow.Definition, node);
        if (!NodeVisible(node, flowContext))
        {
            throw new WorkflowDomainException("The current flow node is not currently visible.");
        }

        var flowValues = ResolveAndValidateVariables(flow.Variables, variableValues, flowContext);
        foreach (var pair in flowValues)
        {
            flowContext[pair.Key] = pair.Value;
        }

        if (!flow.IsDefault && !string.IsNullOrWhiteSpace(flow.Condition)
            && !SequenceFlowConditionEvaluator.Evaluate(flow.Condition, flowContext))
        {
            throw new WorkflowDomainException(
                $"Sequence flow '{flow.Name}' condition is not satisfied: '{flow.Condition}'.");
        }

        foreach (var pair in flowValues)
        {
            await runtime.AddVariableAsync(instance.Id, pair.Key, flow.Id, pair.Value, cancellationToken);
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

        await runtime.UpdateInstanceNodeAsync(instance.Id, ToSnapshot(nextNode), instance.Status, null, cancellationToken);
        // Flush captured variables so pass-through gateways can read them within this transaction.
        await unitOfWork.SaveChangesAsync(cancellationToken);
        instance = await ResolvePassThroughAsync(instance, workflow.Definition, actor, cancellationToken);
        instance = await ApplyClaimInheritanceAsync(instance, workflow.Definition, cancellationToken);

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
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        var performedBy = actor.User;
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

            // Stored instance variables overlaid with read-only sys.*/config.* context.
            // The merged map is used only for evaluation and is never persisted.
            var stored = await LoadVariablesAsync(instance.Id, cancellationToken);
            var variables = WithContext(stored, actor, instance, definition, currentNode);

            if (BpmnFlowNodeTypes.IsServiceTask(currentNode.Type))
            {
                await ExecuteServiceTaskAsync(instance, currentNode, variables, cancellationToken);
                // Reload so downstream gateways/service tasks see any written outputs.
                stored = await LoadVariablesAsync(instance.Id, cancellationToken);
                variables = WithContext(stored, actor, instance, definition, currentNode);
            }

            if (BpmnFlowNodeTypes.IsScriptTask(currentNode.Type))
            {
                await ExecuteScriptTaskAsync(instance, currentNode, definition, actor, variables, cancellationToken);
                // Reload so the outgoing-flow selector and the next hop see the writes.
                stored = await LoadVariablesAsync(instance.Id, cancellationToken);
                variables = WithContext(stored, actor, instance, definition, currentNode);
            }

            var flow = SelectPassThroughFlow(definition, currentNode, variables);
            var nextNode = GetFlowNode(definition, flow.TargetRef);
            var nextStatus = BpmnFlowNodeTypes.IsEnd(nextNode.Type)
                ? WorkflowInstanceStatuses.Completed
                : WorkflowInstanceStatuses.Running;

            var note = currentNode.Type switch
            {
                var t when BpmnFlowNodeTypes.IsStart(t) => "start",
                var t when BpmnFlowNodeTypes.IsGateway(t) => "gateway",
                var t when BpmnFlowNodeTypes.IsServiceTask(t) => "service",
                var t when BpmnFlowNodeTypes.IsScriptTask(t) => "script",
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

            await runtime.UpdateInstanceNodeAsync(instance.Id, ToSnapshot(nextNode), instance.Status, null, cancellationToken);
        }

        throw new WorkflowDomainException("Pass-through routing cycle detected.");
    }

    // Auto-claims a resting user task to a prior actor when the node opts in via
    // claimMode. Resolved from instance history (each taken flow logs PerformedBy):
    // "previous" inherits the last user action's actor; "fromNode" inherits the last
    // user action taken from the referenced node. Falls back to unclaimed when no
    // matching history exists yet (e.g. the first time the task is reached).
    private async Task<WorkflowInstanceRecord> ApplyClaimInheritanceAsync(
        WorkflowInstanceRecord instance,
        WorkflowModel definition,
        CancellationToken cancellationToken)
    {
        if (instance.Status != WorkflowInstanceStatuses.Running)
        {
            return instance;
        }

        var node = GetFlowNode(definition, instance.CurrentStepId);
        if (!BpmnFlowNodeTypes.IsUserTask(node.Type)
            || !node.RequiresClaim
            || node.ClaimMode == ClaimModes.Fresh)
        {
            return instance;
        }

        var history = await runtime.ListHistoryAsync(instance.Id, cancellationToken);
        // A user action is a history row with ActionId != null and PerformedBy set;
        // service/gateway/automatic pass-through hops log ActionId == null.
        var userActions = history
            .Where(h => h.ActionId != null && !string.IsNullOrWhiteSpace(h.PerformedBy));

        if (node.ClaimMode == ClaimModes.FromNode)
        {
            userActions = userActions.Where(h => h.FromStepId == node.InheritClaimFromNodeId);
        }

        var claimant = userActions
            .OrderByDescending(h => h.PerformedAt)
            .Select(h => h.PerformedBy)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(claimant))
        {
            return instance;
        }

        await runtime.UpdateInstanceAsync(instance.Id, instance.CurrentStepId, instance.Status, claimant, cancellationToken);
        return instance with { ClaimedBy = claimant, UpdatedAt = DateTimeOffset.UtcNow };
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
            var kind = BpmnFlowNodeTypes.IsStart(node.Type)
                ? "Start event"
                : BpmnFlowNodeTypes.IsServiceTask(node.Type)
                    ? "Service task"
                    : BpmnFlowNodeTypes.IsScriptTask(node.Type) ? "Script task" : "Automatic task";
            throw new WorkflowDomainException($"{kind} #{node.Id} must have exactly one outgoing sequence flow.");
        }

        return outgoing[0];
    }

    private async Task ExecuteServiceTaskAsync(
        WorkflowInstanceRecord instance,
        FlowNodeModel node,
        IReadOnlyDictionary<string, JsonElement> variables,
        CancellationToken cancellationToken)
    {
        var service = node.Service
            ?? throw new WorkflowDomainException($"Service task #{node.Id} has no service configuration.");

        var url = ServiceTaskTemplating.SubstituteScalar(service.Url, variables);
        var headers = service.Headers
            .Select(h => new ServiceTaskHeader(h.Name, ServiceTaskTemplating.SubstituteScalar(h.Value, variables)))
            .ToList();
        var body = string.IsNullOrEmpty(service.Body)
            ? null
            : ServiceTaskTemplating.SubstituteJson(service.Body, variables);

        var request = new ServiceTaskRequest(service.Method, url, headers, body, service.TimeoutSeconds);
        var result = await serviceTaskInvoker.InvokeAsync(request, cancellationToken);

        if (result.IsSuccess)
        {
            await ApplyServiceOutputsAsync(instance.Id, service, result, cancellationToken);
            await WriteStatusVariableAsync(instance.Id, service, result.StatusCode, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return;
        }

        if (string.Equals(service.OnError, ServiceTaskErrorModes.Continue, StringComparison.Ordinal))
        {
            await WriteStatusVariableAsync(instance.Id, service, result.StatusCode, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return;
        }

        var reason = result.Error ?? $"HTTP status {result.StatusCode}";
        throw new WorkflowDomainException($"Service task #{node.Id} call to '{url}' failed ({reason}).");
    }

    private async Task ApplyServiceOutputsAsync(
        long instanceId,
        ServiceTaskModel service,
        ServiceTaskResult result,
        CancellationToken cancellationToken)
    {
        if (service.OutputMappings.Count == 0 || string.IsNullOrWhiteSpace(result.Body))
        {
            return;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(result.Body);
        }
        catch (JsonException)
        {
            // Non-JSON response body: nothing to map.
            return;
        }

        using (document)
        {
            foreach (var mapping in service.OutputMappings)
            {
                if (ServiceTaskTemplating.TryExtract(document.RootElement, mapping.Path, out var value))
                {
                    await runtime.AddVariableAsync(instanceId, mapping.Variable, null, value.Clone(), cancellationToken);
                }
            }
        }
    }

    private async Task WriteStatusVariableAsync(
        long instanceId,
        ServiceTaskModel service,
        int statusCode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(service.StatusVariable))
        {
            return;
        }

        var value = JsonSerializer.SerializeToElement(statusCode);
        await runtime.AddVariableAsync(instanceId, service.StatusVariable, null, value, cancellationToken);
    }

    // Executes a scriptTask inside the pass-through loop, in either authoring mode:
    //   - ncalc: each assignment's NCalc expression is evaluated in order against a
    //     running overlay (so a later assignment sees an earlier one's write within
    //     the same node), coerced to the target's dataType, and staged.
    //   - javascript: the script runs under Jint via IScriptEvaluator; execution.
    //     setVariable stages a write the same way (through EngineScriptContext),
    //     validated against declared process variables and coerced identically.
    // All staged writes are then persisted (append-only; last write wins) and each
    // distinct target variable's NCalc validation rule is re-checked against the
    // reloaded map, rejecting the transition just like a bad flow input.
    private async Task ExecuteScriptTaskAsync(
        WorkflowInstanceRecord instance,
        FlowNodeModel node,
        WorkflowModel definition,
        ActorContext actor,
        IReadOnlyDictionary<string, JsonElement> variables,
        CancellationToken cancellationToken)
    {
        var byName = definition.Variables
            .Where(v => !string.IsNullOrWhiteSpace(v.Name))
            .ToDictionary(v => v.Name!, StringComparer.OrdinalIgnoreCase);

        var overlay = new Dictionary<string, JsonElement>(variables, StringComparer.OrdinalIgnoreCase);
        var writes = new List<(VariableModel Target, JsonElement Value)>();

        if (string.Equals(node.ScriptFormat, ScriptFormats.JavaScript, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(node.Script))
            {
                return;
            }

            var context = new EngineScriptContext(overlay, byName, writes);
            var result = scriptEvaluator.Evaluate(node.Script, context, cancellationToken);
            if (!result.Success)
            {
                throw new WorkflowDomainException($"Script task #{node.Id} failed: {result.Error}");
            }
        }
        else
        {
            if (node.Assignments.Count == 0)
            {
                return;
            }

            foreach (var assignment in node.Assignments)
            {
                if (string.IsNullOrWhiteSpace(assignment.Variable))
                {
                    throw new WorkflowDomainException($"Script task #{node.Id} has an assignment with no variable name.");
                }

                if (!byName.TryGetValue(assignment.Variable, out var target))
                {
                    throw new WorkflowDomainException(
                        $"Script task #{node.Id} assigns '{assignment.Variable}' which is not a declared process variable.");
                }

                var result = SequenceFlowConditionEvaluator.EvaluateValue(assignment.Expression, overlay);
                var coerced = CoerceScriptValue(result, target);
                overlay[target.Name!] = coerced;
                writes.Add((target, coerced));
            }
        }

        if (writes.Count == 0)
        {
            return;
        }

        foreach (var (target, value) in writes)
        {
            await runtime.AddVariableAsync(instance.Id, target.Name!, null, value, cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Re-read the persisted values (including the just-written assignments) and
        // re-run each distinct target variable's validation rule against the full context.
        var reloaded = await LoadVariablesAsync(instance.Id, cancellationToken);
        var validated = WithContext(reloaded, actor, instance, definition, node);
        foreach (var target in writes.Select(w => w.Target).Distinct())
        {
            if (string.IsNullOrWhiteSpace(target.Validation))
            {
                continue;
            }

            if (!SequenceFlowConditionEvaluator.Evaluate(target.Validation, validated))
            {
                throw new WorkflowDomainException(
                    $"Variable '{target.Name}' failed validation: '{target.Validation}'.");
            }
        }
    }

    // IScriptContext backing a scriptTask's JavaScript body: reads see the running
    // overlay (stored vars + sys.*/config.* context, updated as the script writes,
    // so a script reads its own writes within the same execution); SetVariable
    // enforces the "declared process variables only" rule, coerces via
    // CoerceScriptValue (shared with the ncalc path), and stages the write for
    // persistence after the script returns successfully.
    private sealed class EngineScriptContext(
        Dictionary<string, JsonElement> overlay,
        IReadOnlyDictionary<string, VariableModel> declared,
        List<(VariableModel Target, JsonElement Value)> writes) : IScriptContext
    {
        public bool TryGetVariable(string name, out JsonElement value) => overlay.TryGetValue(name, out value);

        public bool HasVariable(string name) => overlay.ContainsKey(name);

        public IReadOnlyDictionary<string, JsonElement> GetVariables() => overlay;

        public void SetVariable(string name, JsonElement rawValue)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new WorkflowDomainException("execution.setVariable requires a variable name.");
            }

            if (!declared.TryGetValue(name, out var target))
            {
                throw new WorkflowDomainException(
                    $"execution.setVariable('{name}', ...) is not a declared process variable.");
            }

            var coerced = CoerceScriptValue(JsonElementToObject(rawValue), target);
            overlay[target.Name!] = coerced;
            writes.Add((target, coerced));
        }
    }

    // Converts an arbitrary raw JsonElement (e.g. a value marshalled from a JS
    // script) into the loosely-typed CLR shape CoerceScriptScalar expects
    // (string/long/double/bool/null); arrays and objects pass through as a cloned
    // JsonElement, which CoerceScriptValue/CoerceScriptScalar's fallback arm
    // re-serializes correctly (System.Text.Json has first-class JsonElement support).
    private static object? JsonElementToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => element.Clone()
    };

    // Coerces a script result (NCalc: long/double/bool/string/null; JavaScript: the
    // same, or an already array/object-shaped JsonElement) into the JSON shape
    // declared by the target variable. number/boolean/string are strict; date and
    // datetime keep the authored text. A scalar assigned to a declared array
    // variable is wrapped in a single-element array; an already array-shaped result
    // (a genuine JS array) has each element coerced to the declared element type.
    private static JsonElement CoerceScriptValue(object? result, VariableModel variable)
    {
        if (variable.IsArray)
        {
            if (result is JsonElement { ValueKind: JsonValueKind.Array } arrayElement)
            {
                var items = new List<object?>();
                foreach (var item in arrayElement.EnumerateArray())
                {
                    items.Add(CoerceScriptScalar(JsonElementToObject(item), variable.DataType));
                }

                return JsonSerializer.SerializeToElement(items);
            }

            // A scalar result (NCalc never produces arrays) is wrapped so a
            // declared array variable still receives a single-element value.
            return JsonSerializer.SerializeToElement(new[] { CoerceScriptScalar(result, variable.DataType) });
        }

        return JsonSerializer.SerializeToElement(CoerceScriptScalar(result, variable.DataType));
    }

    private static object? CoerceScriptScalar(object? result, string dataType)
    {
        if (result is null)
        {
            return null;
        }

        return dataType switch
        {
            WorkflowVariableTypes.Number => ToNumber(result),
            WorkflowVariableTypes.Boolean => ToBoolean(result),
            WorkflowVariableTypes.String => Convert.ToString(result, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => result
        };
    }

    private static object? ToNumber(object result) => result switch
    {
        long l => l,
        double d => d % 1 == 0 && Math.Abs(d) < 9.2e18 ? (long)d : d,
        int or short or byte or sbyte or ushort or uint or ulong or float or decimal
            => Convert.ToDouble(result, CultureInfo.InvariantCulture),
        bool b => b ? 1L : 0L,
        string s when double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var n)
            => n % 1 == 0 && Math.Abs(n) < 9.2e18 ? (long)n : n,
        _ => Convert.ToString(result, CultureInfo.InvariantCulture) ?? string.Empty
    };

    private static object? ToBoolean(object result) => result switch
    {
        bool b => b,
        long l => l != 0,
        double d => Math.Abs(d) > 0,
        int or short or byte or sbyte or ushort or uint or ulong or float or decimal
            => Convert.ToDouble(result, CultureInfo.InvariantCulture) != 0,
        string s when bool.TryParse(s, out var b) => b,
        string s when double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var n) => Math.Abs(n) > 0,
        _ => Convert.ToString(result, CultureInfo.InvariantCulture) ?? string.Empty
    };

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

    // Returns a copy of the stored variables overlaid with read-only context
    // (sys.*/config.*). Context wins on collision so it can never be spoofed by a
    // stored variable. The result is for evaluation only and is never persisted.
    private Dictionary<string, JsonElement> WithContext(
        Dictionary<string, JsonElement> stored,
        ActorContext actor,
        WorkflowInstanceRecord instance,
        WorkflowModel definition,
        FlowNodeModel currentNode)
    {
        var merged = new Dictionary<string, JsonElement>(stored, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in BuildContextMap(actor, instance, definition, currentNode))
        {
            merged[pair.Key] = pair.Value;
        }

        return merged;
    }

    private Dictionary<string, JsonElement> BuildContextMap(
        ActorContext actor,
        WorkflowInstanceRecord instance,
        WorkflowModel definition,
        FlowNodeModel currentNode)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        void Put(string key, object? value) => map[key] = JsonSerializer.SerializeToElement(value);

        Put("sys.now", now.ToString("o", CultureInfo.InvariantCulture));
        Put("sys.today", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Put("sys.user", actor.User ?? string.Empty);
        Put("sys.roles", actor.Roles.ToArray());
        Put("sys.instanceId", instance.Id);
        Put("sys.workflowId", instance.WorkflowDefinitionId);
        Put("sys.workflowName", definition.Name);
        Put("sys.nodeId", currentNode.Id);
        Put("sys.nodeName", currentNode.Name);

        foreach (var allowed in contextOptions.AllowedClaims ?? [])
        {
            if (!string.IsNullOrWhiteSpace(allowed)
                && TryResolveClaim(actor.Claims, allowed, out var claimValue))
            {
                Put($"sys.claim.{allowed}", claimValue);
            }
        }

        if (contextOptions.Config is { } config)
        {
            foreach (var pair in config)
            {
                Put($"config.{pair.Key}", pair.Value);
            }
        }

        return map;
    }

    // Resolves an allowlisted claim by exact type or by the last segment of a
    // URI-style claim type (e.g. "email" matches ".../claims/emailaddress" only when
    // the suffix equals the requested name).
    private static bool TryResolveClaim(
        IReadOnlyDictionary<string, string> claims,
        string name,
        out string value)
    {
        if (claims.TryGetValue(name, out var direct))
        {
            value = direct;
            return true;
        }

        foreach (var pair in claims)
        {
            var slash = pair.Key.LastIndexOf('/');
            if (slash >= 0 && slash < pair.Key.Length - 1
                && string.Equals(pair.Key[(slash + 1)..], name, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    // Resolves the final value for each scope variable and validates it: a supplied
    // value wins; otherwise a templated/coerced default is applied when present.
    // String defaults may carry ${...} placeholders resolved against the running map
    // (base stored vars + sys.*/config.* context + already-resolved values), so a
    // later default can reference an earlier one. Each variable's NCalc validation
    // rule is then evaluated against the final map; a falsy result rejects the call.
    private static Dictionary<string, JsonElement> ResolveAndValidateVariables(
        IReadOnlyList<VariableModel> variables,
        Dictionary<string, JsonElement>? variableValues,
        Dictionary<string, JsonElement> contextBase)
    {
        var working = new Dictionary<string, JsonElement>(contextBase, StringComparer.OrdinalIgnoreCase);
        var resolved = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        foreach (var variable in variables)
        {
            if (TryGetValue(variableValues, variable.Name, out var value))
            {
                resolved[variable.Name] = value;
                working[variable.Name] = value;
            }
            else if (TryResolveDefault(variable, working, out var defaultValue))
            {
                resolved[variable.Name] = defaultValue;
                working[variable.Name] = defaultValue;
            }
        }

        foreach (var variable in variables)
        {
            if (string.IsNullOrWhiteSpace(variable.Validation))
            {
                continue;
            }

            if (!SequenceFlowConditionEvaluator.Evaluate(variable.Validation, working))
            {
                throw new WorkflowDomainException(
                    $"Variable '{variable.Name}' failed validation: '{variable.Validation}'.");
            }
        }

        return resolved;
    }

    // A missing optional variable falls back to its authored default: any ${...}
    // placeholders in a string default (or string array elements) are substituted
    // from the running map, then the value is coerced to the declared data type and
    // persisted like any supplied value.
    private static bool TryResolveDefault(
        VariableModel variable,
        IReadOnlyDictionary<string, JsonElement> map,
        out JsonElement value)
    {
        value = default;
        if (variable.DefaultValue is not { } raw
            || raw.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        value = CoerceDefault(SubstituteDefault(raw, map), variable);
        return true;
    }

    // Substitutes ${...} placeholders in a string default (or each string element of
    // an array default). Non-string values pass through unchanged.
    private static JsonElement SubstituteDefault(
        JsonElement raw,
        IReadOnlyDictionary<string, JsonElement> map)
    {
        if (raw.ValueKind == JsonValueKind.String)
        {
            return JsonSerializer.SerializeToElement(
                ServiceTaskTemplating.SubstituteScalar(raw.GetString(), map));
        }

        if (raw.ValueKind == JsonValueKind.Array)
        {
            var array = new JsonArray();
            foreach (var item in raw.EnumerateArray())
            {
                array.Add(item.ValueKind == JsonValueKind.String
                    ? ServiceTaskTemplating.SubstituteScalar(item.GetString(), map)
                    : JsonNode.Parse(item.GetRawText()));
            }

            return JsonSerializer.SerializeToElement(array);
        }

        return raw.Clone();
    }

    private static JsonElement CoerceDefault(JsonElement raw, VariableModel variable)
    {
        // Arrays/objects and already-typed values are kept as authored; only a loosely
        // typed string default for a number/boolean is parsed to the right JSON kind.
        if (variable.IsArray || raw.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
        {
            return raw.Clone();
        }

        if (raw.ValueKind == JsonValueKind.String)
        {
            var text = raw.GetString();
            switch (variable.DataType)
            {
                case WorkflowVariableTypes.Number
                    when double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var number):
                    return number % 1 == 0 && Math.Abs(number) < 9.2e18
                        ? JsonSerializer.SerializeToElement((long)number)
                        : JsonSerializer.SerializeToElement(number);
                case WorkflowVariableTypes.Boolean when bool.TryParse(text, out var flag):
                    return JsonSerializer.SerializeToElement(flag);
            }
        }

        return raw.Clone();
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
            node.ExternalId,
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

    // Parses raw "name:value" filter strings (split on the first ':') into
    // exact-match VariableFilters. Malformed or empty-name entries are rejected.
    private static IReadOnlyList<VariableFilter> ParseVariableFilters(IReadOnlyList<string>? variables)
    {
        if (variables is null || variables.Count == 0)
        {
            return [];
        }

        var filters = new List<VariableFilter>(variables.Count);
        foreach (var raw in variables)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var separator = raw.IndexOf(':');
            if (separator <= 0)
            {
                throw new WorkflowDomainException(
                    $"Invalid variable filter '{raw}'. Expected format 'name:value'.");
            }

            var name = raw[..separator].Trim();
            var value = raw[(separator + 1)..].Trim();
            if (name.Length == 0)
            {
                throw new WorkflowDomainException(
                    $"Invalid variable filter '{raw}'. Variable name is required.");
            }

            filters.Add(new VariableFilter(name, value));
        }

        return filters;
    }

    private static InstanceSummaryDto ToSummary(InstanceListItem row) =>
        new(
            row.Id,
            row.WorkflowId,
            row.WorkflowName,
            row.WorkflowVersion,
            row.CurrentNodeId,
            row.CurrentNodeName,
            row.CurrentNodeExternalId,
            row.Status,
            row.ClaimedBy,
            row.StartedBy,
            row.CreatedAt,
            row.UpdatedAt);

    private static CurrentNodeSnapshot ToSnapshot(FlowNodeModel node) =>
        new(node.Id, node.Name, node.ExternalId, node.Type, node.Roles, node.RequiresClaim);

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

    private static HashSet<string> NormalizeRoles(IReadOnlyCollection<string> roles) =>
        roles
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // Node roles are advisory candidate roles; an empty set means the task is open to anyone.
    private static bool RoleAllowed(FlowNodeModel node, IReadOnlySet<string> roles) =>
        node.Roles.Count == 0 || node.Roles.Any(roles.Contains);

    private static void EnsureRoleAllowed(FlowNodeModel node, ActorContext actor)
    {
        if (!RoleAllowed(node, NormalizeRoles(actor.Roles)))
        {
            throw new WorkflowDomainException(
                $"'{NormalizeUser(actor.User)}' does not have a role permitted to act on this flow node.");
        }
    }

    // userTask visibility gate. When the node has a condition, the task is visible
    // only if the expression evaluates to true. Empty/null condition is always true.
    private static bool NodeVisible(FlowNodeModel node, Dictionary<string, JsonElement> context) =>
        string.IsNullOrWhiteSpace(node.Condition)
        || SequenceFlowConditionEvaluator.Evaluate(node.Condition, context);

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
