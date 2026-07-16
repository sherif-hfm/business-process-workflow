using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

public sealed class WorkflowEngineService(
    IWorkflowDefinitionRepository definitions,
    IWorkflowRuntimeRepository runtime,
    IUnitOfWork unitOfWork,
    IServiceTaskInvoker serviceTaskInvoker,
    IScriptEvaluator scriptEvaluator,
    WorkflowContextOptions contextOptions,
    TimeProvider timeProvider,
    IWorkflowSettingsRepository settings,
    IEngineSettingsRepository engineSettings,
    ILogger<WorkflowEngineService> logger)
    : IWorkflowEngineService
{
    private Dictionary<string, JsonElement>? _settingsCache;

    private async Task LoadSettingsAsync(CancellationToken cancellationToken)
    {
        if (_settingsCache is not null)
        {
            return;
        }

        _settingsCache = (Dictionary<string, JsonElement>)await settings.LoadAllAsync(cancellationToken);
    }

    public async Task<InstanceDetailDto> StartInstanceAsync(
        long? workflowId,
        string? workflowKey,
        ActorContext actor,
        int? startEventId,
        Dictionary<string, JsonElement>? variableValues,
        IReadOnlyDictionary<string, IReadOnlyList<string>> requestHeaders,
        CancellationToken cancellationToken)
    {
        var (instance, definition) = await StartInstanceCoreAsync(
            workflowId, workflowKey, actor, startEventId, variableValues, requestHeaders, cancellationToken);
        return (await BuildDetailAsync(instance.Id, cancellationToken))!;
    }

    public async Task<StartInstanceResultDto> StartInstanceSlimAsync(
        long? workflowId,
        string? workflowKey,
        ActorContext actor,
        int? startEventId,
        Dictionary<string, JsonElement>? variableValues,
        IReadOnlyDictionary<string, IReadOnlyList<string>> requestHeaders,
        CancellationToken cancellationToken)
    {
        var (instance, definition) = await StartInstanceCoreAsync(
            workflowId, workflowKey, actor, startEventId, variableValues, requestHeaders, cancellationToken);
        var node = GetFlowNode(definition, instance.CurrentStepId);
        return new StartInstanceResultDto(
            instance.Id,
            node.Id,
            node.Name,
            node.ExternalId,
            instance.Status,
            instance.BusinessKey,
            instance.BusinessKeyUniqueness,
            instance.StartedBy,
            instance.CreatedAt,
            instance.UpdatedAt);
    }

    private async Task<(WorkflowInstanceRecord Instance, WorkflowModel Definition)> StartInstanceCoreAsync(
        long? workflowId,
        string? workflowKey,
        ActorContext actor,
        int? startEventId,
        Dictionary<string, JsonElement>? variableValues,
        IReadOnlyDictionary<string, IReadOnlyList<string>> requestHeaders,
        CancellationToken cancellationToken)
    {
        await LoadSettingsAsync(cancellationToken);
        var startedBy = actor.User;
        
        WorkflowDefinitionRecord workflow;
        if (workflowId.HasValue)
        {
            workflow = await GetPublishedWorkflowAsync(workflowId.Value, cancellationToken);
        }
        else if (!string.IsNullOrEmpty(workflowKey))
        {
            workflow = await definitions.GetDefaultByWorkflowKeyAsync(workflowKey, cancellationToken)
                ?? throw new WorkflowDomainException($"No default workflow found for workflowKey '{workflowKey}'.");
        }
        else
        {
            logger.LogWarning("Start instance rejected: neither WorkflowId nor WorkflowKey was specified.");
            throw new WorkflowDomainException("Either WorkflowId or WorkflowKey must be specified to start an instance.");
        }

        await EnsureBusinessKeyFamilyStartableAsync(workflow, cancellationToken);

        logger.LogDebug("Starting workflow instance for definition {WorkflowKey} (ID: {WorkflowId}) by user {User}", workflowKey ?? workflow.WorkflowKey.ToString(), workflow.Id, startedBy ?? "anonymous");

        var resolvedStartEventId = startEventId ?? workflow.Definition.InitialEventId
            ?? throw new WorkflowDomainException("Workflow has no default start event.");

        var startEvent = GetFlowNode(workflow.Definition, resolvedStartEventId);
        if (!BpmnFlowNodeTypes.IsStart(startEvent.Type))
        {
            logger.LogWarning("Start instance rejected: flow node #{NodeId} is not a start event.", resolvedStartEventId);
            throw new WorkflowDomainException($"Flow node #{resolvedStartEventId} is not a start event.");
        }

        EnsureRoleAllowed(startEvent, actor);

        var idempotency = ResolveIdempotencyInput(startEvent, requestHeaders);

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        if (idempotency is not null)
        {
            var reservation = await runtime.ReserveIdempotencyKeyAsync(
                workflow.WorkflowKey,
                idempotency.Key,
                cancellationToken);
            if (!reservation.Reserved)
            {
                throw new IdempotencyKeyConflictException(reservation.ExistingInstanceId
                    ?? throw new InvalidOperationException("A conflicting idempotency claim has no instance."));
            }
        }

        if (idempotency is not null
            && variableValues?.Keys.Any(name =>
                string.Equals(name, idempotency.Variable, StringComparison.OrdinalIgnoreCase)) == true)
        {
            throw new WorkflowDomainException(
                $"Entry event #{startEvent.Id} idempotency variable '{idempotency.Variable}' must be supplied only through header '{idempotency.HeaderName}'.");
        }

        var normalizedStart = NormalizeBusinessKeyInput(startEvent, variableValues);
        variableValues = normalizedStart.Values;
        ValidateVariableValues(startEvent.Variables, variableValues);

        if (normalizedStart.BusinessKey is not null)
        {
            var reservation = await runtime.ReserveBusinessKeyAsync(
                workflow.WorkflowKey,
                normalizedStart.BusinessKey,
                normalizedStart.Uniqueness!,
                cancellationToken);
            if (!reservation.Reserved)
            {
                throw new BusinessKeyConflictException(reservation.ExistingInstanceId
                    ?? throw new InvalidOperationException("A conflicting business-key claim has no instance."));
            }
        }

        var instance = await runtime.AddInstanceAsync(
            workflow.Id,
            workflow.WorkflowKey,
            idempotency?.Key,
            normalizedStart.BusinessKey,
            normalizedStart.Uniqueness,
            ToSnapshot(startEvent),
            startedBy,
            cancellationToken);
        if (idempotency is not null)
        {
            await runtime.BindIdempotencyKeyAsync(
                workflow.WorkflowKey, idempotency.Key, instance.Id, cancellationToken);
        }
        if (normalizedStart.BusinessKey is not null)
        {
            await runtime.BindBusinessKeyAsync(
                workflow.WorkflowKey, normalizedStart.BusinessKey, instance.Id, cancellationToken);
        }

        // Resolve templated defaults and run NCalc validation against the final values
        // overlaid with sys.*/config.* context, then persist each resolved value.
        var startContext = WithContext(
            new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase),
            actor, instance, workflow.Definition, startEvent);
        if (idempotency is not null)
        {
            startContext[idempotency.Variable] = idempotency.Value;
            await runtime.AddVariableAsync(
                instance.Id,
                idempotency.Variable,
                null,
                startedBy,
                idempotency.Value,
                cancellationToken);
        }
        var startValues = ResolveAndValidateVariables(startEvent.Variables, variableValues, startContext);
        foreach (var pair in startValues)
        {
            await runtime.AddVariableAsync(instance.Id, pair.Key, null, startedBy, pair.Value, cancellationToken);
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
            await runtime.AddVariableAsync(instance.Id, pair.Key, null, startedBy, pair.Value, cancellationToken);
        }

        // Flush variables so pass-through gateways can read them within this transaction.
        await unitOfWork.SaveChangesAsync(cancellationToken);
        instance = await ResolvePassThroughAsync(instance, workflow.Definition, actor, cancellationToken);
        await EnsureMultiInstanceInitializedAsync(instance, workflow.Definition, actor, cancellationToken);
        instance = await ApplyClaimInheritanceAsync(instance, workflow.Definition, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var restingNode = GetFlowNode(workflow.Definition, instance.CurrentStepId);
        logger.LogDebug("Successfully started workflow instance {InstanceId} resting on step {CurrentStepId} ({CurrentStepType})", instance.Id, instance.CurrentStepId, restingNode.Type);

        return (instance, workflow.Definition);
    }

    public async Task<MessageStartAckDto> StartByMessageAsync(
        string workflowKey,
        string? startEventExternalId,
        IncomingMessage message,
        CancellationToken cancellationToken)
    {
        await LoadSettingsAsync(cancellationToken);

        // Resolve the default version by the stable, cross-version key so
        // a webhook caller addresses the workflow without knowing per-version ids.
        var workflow = await definitions.GetDefaultByWorkflowKeyAsync(workflowKey, cancellationToken)
            ?? throw new WorkflowDomainException($"No default workflow found for workflowKey {workflowKey}.");
        await EnsureBusinessKeyFamilyStartableAsync(workflow, cancellationToken);

        // Select the messageStartEvent: match the requested externalId when given;
        // else the single message-start node; else reject as ambiguous/absent.
        var definition = workflow.Definition;
        var startEvents = definition.FlowNodes.Where(n => BpmnFlowNodeTypes.IsMessageStart(n.Type)).ToList();
        FlowNodeModel startEvent;
        if (!string.IsNullOrWhiteSpace(startEventExternalId))
        {
            startEvent = startEvents.SingleOrDefault(n => string.Equals(n.ExternalId, startEventExternalId, StringComparison.Ordinal))
                ?? throw new WorkflowDomainException($"No message start event with externalId '{startEventExternalId}' was found in workflow '{definition.Name}'.");
        }
        else
        {
            if (startEvents.Count == 0)
            {
                throw new WorkflowDomainException($"Workflow '{definition.Name}' has no message start event.");
            }

            if (startEvents.Count > 1)
            {
                throw new WorkflowDomainException($"Workflow '{definition.Name}' has multiple message start events; specify one via the 'startEvent' query parameter (its externalId).");
            }

            startEvent = startEvents[0];
        }

        logger.LogInformation("Starting workflow instance by message on workflowKey {WorkflowKey} using start node #{StartNodeId} ({StartNodeName})",
            workflowKey, startEvent.Id, startEvent.Name);

        var messageConfig = startEvent.Message
            ?? throw new WorkflowDomainException($"Message start event #{startEvent.Id} has no message configuration.");

        // Resolve templated expected credentials + required header against an
        // instance-less auth context (config/setting + non-caller-influenced sys.*).
        var actor = message.Actor;
        var performedBy = actor.User;
        var authContext = BuildAuthContext(
            new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase),
            instance: null,
            definition,
            startEvent);

        var expectedClientId = ServiceTaskTemplating.SubstituteScalar(messageConfig.ClientId, authContext);
        var expectedClientSecret = ServiceTaskTemplating.SubstituteScalar(messageConfig.ClientSecret, authContext);
        var expectedHeaderName = ServiceTaskTemplating.SubstituteScalar(messageConfig.HeaderName, authContext);
        var expectedHeaderValue = ServiceTaskTemplating.SubstituteScalar(messageConfig.HeaderValue, authContext);

        // Authenticate the caller against the node's expected client id/secret.
        if (!string.Equals(message.ClientId ?? string.Empty, expectedClientId, StringComparison.Ordinal)
            || !ConstantTimeEquals(message.ClientSecret ?? string.Empty, expectedClientSecret))
        {
            logger.LogWarning("Message start on workflowKey {WorkflowKey} rejected: invalid client credentials (client id '{ClientId}').", workflowKey, message.ClientId);
            throw new WorkflowUnauthorizedException("Invalid client credentials.");
        }

        // Validate the required custom header (a domain error, 400, since the
        // caller has authenticated via the client id/secret): present, equal to the
        // resolved expected value, and (when set) satisfying the NCalc headerValidation.
        if (!message.Headers.TryGetValue(expectedHeaderName, out var incomingHeaderValues)
            || incomingHeaderValues.Count == 0
            || incomingHeaderValues[0] is not { } incomingHeaderValue)
        {
            logger.LogWarning("Message start on workflowKey {WorkflowKey} rejected: required header '{HeaderName}' is missing.", workflowKey, expectedHeaderName);
            throw new WorkflowDomainException($"Required header '{expectedHeaderName}' is missing.");
        }

        if (!ConstantTimeEquals(incomingHeaderValue, expectedHeaderValue))
        {
            logger.LogWarning("Message start on workflowKey {WorkflowKey} rejected: header '{HeaderName}' value mismatch.", workflowKey, expectedHeaderName);
            throw new WorkflowDomainException($"Header '{expectedHeaderName}' does not match the expected value.");
        }

        if (!string.IsNullOrWhiteSpace(messageConfig.HeaderValidation))
        {
            // headerValidation may reference sys.* context (the caller is by now
            // authenticated); there is no instance yet, so the context is the auth
            // context plus the incoming header bound as `header`.
            var validationCtx = new Dictionary<string, JsonElement>(authContext, StringComparer.OrdinalIgnoreCase)
            {
                ["header"] = JsonSerializer.SerializeToElement(incomingHeaderValue)
            };
            if (!SequenceFlowConditionEvaluator.Evaluate(messageConfig.HeaderValidation, validationCtx))
            {
                logger.LogWarning("Message start on workflowKey {WorkflowKey} rejected: header '{HeaderName}' failed validation '{Validation}'.", workflowKey, expectedHeaderName, messageConfig.HeaderValidation);
                throw new WorkflowDomainException(
                    $"Header '{expectedHeaderName}' failed validation: '{messageConfig.HeaderValidation}'.");
            }
        }

        var idempotency = ResolveIdempotencyInput(startEvent, message.Headers);

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        if (idempotency is not null)
        {
            var reservation = await runtime.ReserveIdempotencyKeyAsync(
                workflow.WorkflowKey,
                idempotency.Key,
                cancellationToken);
            if (!reservation.Reserved)
            {
                logger.LogInformation("Message start request for workflowKey {WorkflowKey} conflicted with an existing idempotency key. Existing instance: {InstanceId}",
                    workflowKey, reservation.ExistingInstanceId);
                throw new IdempotencyKeyConflictException(reservation.ExistingInstanceId
                    ?? throw new InvalidOperationException("A conflicting idempotency claim has no instance."));
            }
        }

        // Extract supplied payload values. Message-start required/default semantics
        // are resolved from the typed mappings after the optional idempotency header
        // is added to the validation context.
        var suppliedValues = ExtractMessageStartOutputs(startEvent, messageConfig, message.Payload);

        var mappingVariables = messageConfig.OutputMappings.Select(ToMessageStartVariable).ToList();
        var resolutionContext = new Dictionary<string, JsonElement>(authContext, StringComparer.OrdinalIgnoreCase);
        if (idempotency is not null)
        {
            resolutionContext[idempotency.Variable] = idempotency.Value;
        }

        var resolvedValues = ResolveVariables(mappingVariables, suppliedValues, resolutionContext);
        ValidateVariableValues(mappingVariables, resolvedValues);
        ValidateMessageStartTypes(startEvent, mappingVariables, resolvedValues);

        // Business-key normalization trims the selected mapped string and writes the
        // same canonical value back into the values persisted on the instance.
        var normalizedStart = NormalizeBusinessKeyInput(startEvent, resolvedValues);
        var mappedValues = normalizedStart.Values
            ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (idempotency is not null)
        {
            mappedValues[idempotency.Variable] = idempotency.Value;
        }
        ValidateResolvedVariableRules(mappingVariables, mappedValues, resolutionContext);

        if (normalizedStart.BusinessKey is not null)
        {
            var reservation = await runtime.ReserveBusinessKeyAsync(
                workflow.WorkflowKey,
                normalizedStart.BusinessKey,
                normalizedStart.Uniqueness!,
                cancellationToken);
            if (!reservation.Reserved)
            {
                var existingId = reservation.ExistingInstanceId
                    ?? throw new InvalidOperationException("A conflicting business-key claim has no instance.");
                throw new BusinessKeyConflictException(existingId);
            }
        }

        // Start the instance on the message-start node (pass-through: the loop
        // auto-advances off it on the next hop). Mirror StartInstanceAsync.
        var instance = await runtime.AddInstanceAsync(
            workflow.Id,
            workflow.WorkflowKey,
            idempotency?.Key,
            normalizedStart.BusinessKey,
            normalizedStart.Uniqueness,
            ToSnapshot(startEvent),
            performedBy,
            cancellationToken);
        if (idempotency is not null)
        {
            await runtime.BindIdempotencyKeyAsync(
                workflow.WorkflowKey, idempotency.Key, instance.Id, cancellationToken);
        }
        if (normalizedStart.BusinessKey is not null)
        {
            await runtime.BindBusinessKeyAsync(
                workflow.WorkflowKey, normalizedStart.BusinessKey, instance.Id, cancellationToken);
        }
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Every typed output mapping plus the implicit idempotency variable is an
        // instance variable. Resolution and validation completed before reservation;
        // persistence remains inside the start transaction.
        var startContext = WithContext(
            new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase),
            actor, instance, definition, startEvent);
        foreach (var pair in mappedValues)
        {
            await runtime.AddVariableAsync(instance.Id, pair.Key, null, performedBy, pair.Value, cancellationToken);
        }

        // Initialize process-level variables from their authored defaults.
        var processContext = new Dictionary<string, JsonElement>(startContext, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in mappedValues)
        {
            processContext[pair.Key] = pair.Value;
        }
        var processValues = ResolveAndValidateVariables(definition.Variables, null, processContext);
        foreach (var pair in processValues)
        {
            await runtime.AddVariableAsync(instance.Id, pair.Key, null, performedBy, pair.Value, cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        instance = await ResolvePassThroughAsync(instance, definition, actor, cancellationToken);
        await EnsureMultiInstanceInitializedAsync(instance, definition, actor, cancellationToken);
        instance = await ApplyClaimInheritanceAsync(instance, definition, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Successfully started workflow instance {InstanceId} by message correlation. Status: {Status}, resting on node {CurrentStepId}",
            instance.Id, instance.Status, instance.CurrentStepId);

        return await BuildStartAckAsync(instance.Id, cancellationToken);
    }

    // Builds the slim start ack: only the resting node identity + status, no
    // definition/variables/history (the message-start endpoint is AllowAnonymous).
    private async Task<MessageStartAckDto> BuildStartAckAsync(long id, CancellationToken cancellationToken)
    {
        var instance = await runtime.GetInstanceAsync(id, cancellationToken)
            ?? throw new WorkflowDomainException($"Instance #{id} was not found after start.");
        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var node = GetFlowNode(workflow.Definition, instance.CurrentStepId);
        return new MessageStartAckDto(
            instance.Id,
            node.Id,
            node.Name,
            node.ExternalId,
            instance.Status,
            instance.CreatedAt);
    }

    public async Task<PagedResult<InstanceSummaryDto>> ListInstancesAsync(
        string? status,
        long? instanceId,
        long? workflowId,
        string? workflowKey,
        string? businessKey,
        int? nodeId,
        string? nodeExternalId,
        IReadOnlyList<string>? variables,
        bool includeVariables,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var variableFilters = ParseVariableFilters(variables);
        var paged = await runtime.ListInstancesAsync(status, instanceId, workflowId, workflowKey, businessKey, nodeId, nodeExternalId, variableFilters, includeVariables, page, pageSize, cancellationToken);
        var items = paged.Items.Select(ToSummary).ToList();
        return new PagedResult<InstanceSummaryDto>(items, paged.Page, paged.PageSize, paged.TotalCount);
    }

    public async Task<PagedResult<InboxItemDto>> GetInboxAsync(
        ActorContext actor,
        long? instanceId,
        long? workflowId,
        string? workflowKey,
        string? businessKey,
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
        var paged = await runtime.ListInboxAsync(
            normalizedUser, normalizedRoles, instanceId, workflowId, workflowKey, businessKey, nodeId,
            nodeExternalId, variableFilters, page, pageSize, cancellationToken);

        if (paged.Items.Count == 0)
        {
            return new PagedResult<InboxItemDto>([], paged.Page, paged.PageSize, paged.TotalCount);
        }

        var definitionIds = paged.Items.Select(c => c.WorkflowDefinitionId).Distinct().ToList();
        var definitionsById = new Dictionary<long, WorkflowDefinitionRecord>(definitionIds.Count);
        foreach (var id in definitionIds)
        {
            definitionsById[id] = await GetWorkflowAsync(id, cancellationToken);
        }

        // The actor and definition version are stable for this request. Cache the
        // outgoing-flow role/claim calculation per node instead of rescanning the
        // full sequence-flow list for every inbox row (especially multi-instance rows).
        var authorizationByNode = new Dictionary<(long DefinitionId, int NodeId), (bool HasRestrictedFlows, bool CanTakeAny, bool HasBypass)>();
        var canActByTask = new Dictionary<long, bool>();
        var hasBypassClaimByTask = new Dictionary<long, bool>();
        foreach (var row in paged.Items)
        {
            var key = (row.WorkflowDefinitionId, row.CurrentNodeId);
            if (!authorizationByNode.TryGetValue(key, out var authorization))
            {
                var definition = definitionsById[row.WorkflowDefinitionId].Definition;
                var node = GetFlowNode(definition, row.CurrentNodeId);
                authorization = (
                    HasRoleRestrictedFlows(node, definition),
                    CanTakeAnyFlow(node, definition, normalizedRoles),
                    CanTakeAnyBypassClaimFlow(node, definition, normalizedRoles));
                authorizationByNode[key] = authorization;
            }

            var taskKey = InboxAuthorizationKey(row);
            if (authorization.HasRestrictedFlows)
            {
                canActByTask[taskKey] = authorization.CanTakeAny;
            }
            if (authorization.HasBypass)
            {
                hasBypassClaimByTask[taskKey] = true;
            }
        }

        var executionIds = paged.Items.Where(row => row.MultiInstanceExecutionId is not null)
            .Select(row => row.MultiInstanceExecutionId!.Value)
            .Distinct()
            .ToList();
        var progressByExecution = await BuildProgressAsync(executionIds, cancellationToken);
        var items = paged.Items.Select(row => ToInboxItem(row, normalizedUser, normalizedRoles,
            canActByTask, hasBypassClaimByTask,
            row.MultiInstanceExecutionId is long executionId ? progressByExecution.GetValueOrDefault(executionId) : null)).ToList();
        return new PagedResult<InboxItemDto>(items, paged.Page, paged.PageSize, paged.TotalCount);
    }

    private static long InboxAuthorizationKey(InstanceListItem row) => row.UserTaskId ?? row.Id;

    private static InboxItemDto ToInboxItem(
        InstanceListItem row,
        string normalizedUser,
        IReadOnlySet<string> normalizedRoles,
        Dictionary<long, bool>? canActByTask = null,
        Dictionary<long, bool>? hasBypassClaimByTask = null,
        MultiInstanceProgressDto? multiInstance = null)
    {
        var claimedByMe = string.Equals(row.ClaimedBy, normalizedUser, StringComparison.OrdinalIgnoreCase);
        var claimedByOther = !string.IsNullOrWhiteSpace(row.ClaimedBy) && !claimedByMe;
        var roleMatch = row.CurrentNodeRoles.Count == 0
            || row.CurrentNodeRoles.Any(normalizedRoles.Contains);

        var canClaim = row.CurrentRequiresClaim && !claimedByMe && !claimedByOther && roleMatch;
        var canAct = claimedByMe || (!row.CurrentRequiresClaim && roleMatch);

        // If the task has a bypass-claim flow and the user has the role to take it,
        // they can act directly on it even if it requires a claim and is unclaimed
        // (or claimed by someone else).
        var authorizationKey = InboxAuthorizationKey(row);
        if (hasBypassClaimByTask is not null && hasBypassClaimByTask.TryGetValue(authorizationKey, out var hasBypass) && hasBypass)
        {
            if (roleMatch)
            {
                canAct = true;
            }
        }

        // When the returned page contains role-restricted flows, refine the action flags:
        // the actor must additionally be able to take at least one outgoing flow.
        // A task with role-restricted flows that all exclude the actor is visible
        // but not actionable (and shouldn't be claimed, since it could never
        // advance). Tasks without role-restricted flows are absent from the map
        // and keep the historical node-only results above.
        if (canActByTask is not null && canActByTask.TryGetValue(authorizationKey, out var canTakeAny))
        {
            if (!canTakeAny)
            {
                canAct = false;
                canClaim = false;
            }
        }

        return new InboxItemDto(
            row.Id,
            row.UserTaskId ?? 0,
            row.MultiInstanceExecutionId,
            row.ItemIndex,
            row.ItemValue,
            row.Assignee,
            multiInstance,
            row.WorkflowId,
            row.WorkflowName,
            row.BusinessKey,
            row.BusinessKeyUniqueness,
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
        await LoadSettingsAsync(cancellationToken);
        var instance = await runtime.GetInstanceAsync(id, cancellationToken);
        if (instance is null || instance.Status != WorkflowInstanceStatuses.Running)
        {
            return [];
        }

        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var node = GetFlowNode(workflow.Definition, instance.CurrentStepId);
        if (node.MultiInstance is not null)
        {
            var tasks = await runtime.ListUserTasksAsync(id, UserTaskRecordStatuses.Active, cancellationToken);
            if (tasks.Count != 1)
                throw new WorkflowConflictException("The instance has multiple active user tasks; use a task-addressed endpoint.");
            return await GetUserTaskAvailableFlowsAsync(tasks[0].Id, actor, cancellationToken);
        }
        if (!BpmnFlowNodeTypes.IsUserTask(node.Type))
        {
            return [];
        }

        var task = instance.ActiveUserTaskId is long taskId
            ? await runtime.GetUserTaskAsync(taskId, false, cancellationToken)
            : null;
        if (task is null || !CanUserTaskActor(task, node, actor))
        {
            return [];
        }

        var actorRoles = NormalizeRoles(actor.Roles);
        if (!RoleAllowed(node, actorRoles))
        {
            return [];
        }

        var stored = await LoadVariablesAsync(instance.Id, cancellationToken);
        var evalCtx = WithContext(stored, actor, instance, workflow.Definition, node);
        var normalizedUser = NormalizeUser(actor.User);
        var claimedByMe = string.Equals(task.ClaimedBy, normalizedUser, StringComparison.OrdinalIgnoreCase);

        return OutgoingFlows(workflow.Definition, node.Id)
            .Where(f => f.IsSelectable && !f.IsDefault
                        && RoleAllowed(f.Roles, actorRoles)
                        && (!task.RequiresClaim || claimedByMe || f.CanActWithoutClaim)
                        && (string.IsNullOrWhiteSpace(f.Condition)
                            || SequenceFlowConditionEvaluator.Evaluate(f.Condition, evalCtx)))
            .ToList();
    }

    public async Task<InstanceDetailDto?> ClaimAsync(
        long id,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        await LoadSettingsAsync(cancellationToken);
        var preview = await runtime.GetInstanceAsync(id, cancellationToken);
        if (preview is not null)
        {
            var previewWorkflow = await GetWorkflowAsync(preview.WorkflowDefinitionId, cancellationToken);
            var previewNode = GetFlowNode(previewWorkflow.Definition, preview.CurrentStepId);
            if (previewNode.MultiInstance is not null)
            {
                var tasks = await runtime.ListUserTasksAsync(id, UserTaskRecordStatuses.Active, cancellationToken);
                if (tasks.Count != 1) throw new WorkflowConflictException("The instance has multiple active user tasks; use a task-addressed endpoint.");
                await ClaimUserTaskAsync(tasks[0].Id, actor, cancellationToken);
                return await BuildDetailAsync(id, cancellationToken);
            }
        }
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var instance = await runtime.GetInstanceForUpdateAsync(id, true, cancellationToken);
        if (instance is null)
        {
            logger.LogInformation("Claim instance {InstanceId}: instance not found.", id);
            return null;
        }

        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var node = GetFlowNode(workflow.Definition, instance.CurrentStepId);
        var task = instance.ActiveUserTaskId is long taskId
            ? await runtime.GetUserTaskAsync(taskId, true, cancellationToken)
            : null;
        if (task?.Assignee is not null)
        {
            throw new WorkflowDomainException("Directly assigned tasks do not use claim/unclaim.");
        }
        if (instance.Status != WorkflowInstanceStatuses.Running || task is null || !task.RequiresClaim)
        {
            logger.LogWarning("Claim rejected on instance {InstanceId}: node #{NodeId} is not claimable (status={Status}, requiresClaim={RequiresClaim}).",
                id, node.Id, instance.Status, task?.RequiresClaim ?? false);
            throw new WorkflowDomainException("The current flow node cannot be claimed.");
        }

        EnsureRoleAllowed(node, actor);

        var normalizedUser = NormalizeUser(actor.User);
        if (!string.IsNullOrWhiteSpace(instance.ClaimedBy) && instance.ClaimedBy != normalizedUser)
        {
            logger.LogWarning("Claim rejected on instance {InstanceId}: node already claimed by '{ClaimedBy}', requested by '{User}'.",
                id, instance.ClaimedBy, normalizedUser);
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

        logger.LogInformation("Instance {InstanceId} claimed by user '{User}' on node #{NodeId} ({NodeName}).",
            id, normalizedUser, node.Id, node.Name);

        return await BuildDetailAsync(id, cancellationToken);
    }

    public async Task<InstanceDetailDto?> UnclaimAsync(long id, ActorContext actor, CancellationToken cancellationToken)
    {
        var preview = await runtime.GetInstanceAsync(id, cancellationToken);
        if (preview is not null)
        {
            var previewWorkflow = await GetWorkflowAsync(preview.WorkflowDefinitionId, cancellationToken);
            var previewNode = GetFlowNode(previewWorkflow.Definition, preview.CurrentStepId);
            if (previewNode.MultiInstance is not null)
            {
                var tasks = await runtime.ListUserTasksAsync(id, UserTaskRecordStatuses.Active, cancellationToken);
                if (tasks.Count != 1) throw new WorkflowConflictException("The instance has multiple active user tasks; use a task-addressed endpoint.");
                await UnclaimUserTaskAsync(tasks[0].Id, actor, cancellationToken);
                return await BuildDetailAsync(id, cancellationToken);
            }
        }
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var instance = await runtime.GetInstanceForUpdateAsync(id, true, cancellationToken);
        if (instance is null)
        {
            logger.LogInformation("Unclaim instance {InstanceId}: instance not found.", id);
            return null;
        }

        var activeTask = instance.ActiveUserTaskId is long taskId
            ? await runtime.GetUserTaskAsync(taskId, true, cancellationToken)
            : null;
        if (activeTask?.Assignee is not null)
        {
            throw new WorkflowDomainException("Directly assigned tasks do not use claim/unclaim.");
        }

        var normalizedUser = NormalizeUser(actor.User);
        if (!string.IsNullOrWhiteSpace(instance.ClaimedBy))
        {
            var isClaimant = string.Equals(instance.ClaimedBy, normalizedUser, StringComparison.OrdinalIgnoreCase);

            var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
            var unclaimRoles = workflow.Definition.UnclaimRoles ?? [];

            var actorRoles = NormalizeRoles(actor.Roles);
            var hasUnclaimRole = unclaimRoles.Any(r => actorRoles.Contains(r));

            if (!isClaimant && !hasUnclaimRole)
            {
                logger.LogWarning("Unclaim rejected on instance {InstanceId}: user '{User}' is not the claimant ('{ClaimedBy}') and lacks an unclaim role.",
                    id, normalizedUser, instance.ClaimedBy);
                throw new WorkflowDomainException($"Only the user who claimed the task ('{instance.ClaimedBy}') or users with unclaim permissions can unclaim this task.");
            }
        }

        await runtime.UpdateInstanceAsync(instance.Id, instance.CurrentStepId, instance.Status, null, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Instance {InstanceId} unclaimed by user '{User}' (previous claimant: '{PreviousClaimedBy}').",
            id, normalizedUser, instance.ClaimedBy);

        return await BuildDetailAsync(id, cancellationToken);
    }

    public async Task<UserTaskDto?> GetUserTaskAsync(
        long taskId,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        var task = await runtime.GetUserTaskAsync(taskId, false, cancellationToken);
        if (task is null) return null;
        var instance = await runtime.GetInstanceAsync(task.InstanceId, cancellationToken)
            ?? throw new WorkflowDomainException($"Workflow instance #{task.InstanceId} was not found.");
        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var node = GetFlowNode(workflow.Definition, task.NodeId);
        EnsureUserTaskActor(task, node, actor, requireActive: false);
        return await BuildUserTaskDtoAsync(task, cancellationToken);
    }

    public async Task<IReadOnlyList<SequenceFlowModel>> GetUserTaskAvailableFlowsAsync(
        long taskId,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        await LoadSettingsAsync(cancellationToken);
        var task = await runtime.GetUserTaskAsync(taskId, false, cancellationToken);
        if (task is null || task.Status != UserTaskRecordStatuses.Active) return [];
        var instance = await runtime.GetInstanceAsync(task.InstanceId, cancellationToken);
        if (instance is null || instance.Status != WorkflowInstanceStatuses.Running) return [];
        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var node = GetFlowNode(workflow.Definition, task.NodeId);
        if (!CanUserTaskActor(task, node, actor)) return [];

        var stored = await LoadVariablesAsync(instance.Id, cancellationToken);
        var context = WithContext(stored, actor, instance, workflow.Definition, node);
        if (task.MultiInstanceExecutionId is long executionId)
        {
            var execution = await runtime.GetMultiInstanceAsync(executionId, false, cancellationToken);
            if (execution is null || execution.Status != MultiInstanceRecordStatuses.Active) return [];
            if (execution.OnePerActor)
            {
                var user = NormalizeUser(actor.User);
                if (await runtime.HasCompletedMultiInstanceItemAsync(execution.Id, user, cancellationToken))
                    return [];
                var claimedTaskId = await runtime.GetClaimedMultiInstanceItemIdAsync(
                    execution.Id, user, cancellationToken);
                if (claimedTaskId is not null && claimedTaskId != task.Id)
                    return [];
            }
            AddMultiInstanceContext(context, task, execution);
        }

        var roles = NormalizeRoles(actor.Roles);
        return OutgoingFlows(workflow.Definition, node.Id)
            .Where(f => f.IsSelectable && !f.IsDefault
                        && RoleAllowed(f.Roles, roles)
                        && (!task.RequiresClaim
                            || string.Equals(task.ClaimedBy, NormalizeUser(actor.User), StringComparison.OrdinalIgnoreCase)
                            || f.CanActWithoutClaim)
                        && (string.IsNullOrWhiteSpace(f.Condition)
                            || SequenceFlowConditionEvaluator.Evaluate(f.Condition, context)))
            .ToList();
    }

    public async Task<UserTaskDto?> ClaimUserTaskAsync(
        long taskId,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        var initialTask = await runtime.GetUserTaskAsync(taskId, false, cancellationToken);
        if (initialTask is null) return null;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var instance = await runtime.GetInstanceForUpdateAsync(initialTask.InstanceId, false, cancellationToken)
            ?? throw new WorkflowConflictException("The workflow instance no longer exists.");
        if (instance.Status != WorkflowInstanceStatuses.Running)
            throw new WorkflowConflictException("The workflow instance is no longer running.");
        MultiInstanceExecutionRecord? execution = null;
        if (initialTask.MultiInstanceExecutionId is long executionId)
        {
            execution = await runtime.GetMultiInstanceAsync(executionId, true, cancellationToken);
            if (execution is null || execution.InstanceId != instance.Id
                                  || execution.Status != MultiInstanceRecordStatuses.Active)
                throw new WorkflowConflictException("The multi-instance execution has already closed.");
            if (instance.ActiveTokenId != execution.TokenId || instance.CurrentStepId != execution.NodeId)
                throw new WorkflowConflictException("The multi-instance execution is no longer the current workflow step.");
        }

        var task = await runtime.GetUserTaskAsync(taskId, true, cancellationToken);
        if (task is null || task.InstanceId != instance.Id
                         || task.MultiInstanceExecutionId != initialTask.MultiInstanceExecutionId
                         || task.TokenId != instance.ActiveTokenId
                         || task.NodeId != instance.CurrentStepId)
            throw new WorkflowConflictException("The user task is no longer current.");
        if (task.Assignee is not null)
            throw new WorkflowDomainException("Directly assigned tasks do not use claim/unclaim.");
        if (task.Status != UserTaskRecordStatuses.Active || !task.RequiresClaim)
            throw new WorkflowDomainException("The user task cannot be claimed.");
        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var node = GetFlowNode(workflow.Definition, task.NodeId);
        EnsureRoleAllowed(node, actor);
        var user = NormalizeUser(actor.User);
        if (execution is { OnePerActor: true })
        {
            if (await runtime.HasCompletedMultiInstanceItemAsync(execution.Id, user, cancellationToken))
                throw new WorkflowConflictException("The actor has already completed an item in this multi-instance execution.");
            var claimedTaskId = await runtime.GetClaimedMultiInstanceItemIdAsync(
                execution.Id, user, cancellationToken);
            if (claimedTaskId is not null && claimedTaskId != task.Id)
                throw new WorkflowConflictException("The actor has already claimed another item in this multi-instance execution.");
        }
        if (!string.IsNullOrWhiteSpace(task.ClaimedBy)
            && !string.Equals(task.ClaimedBy, user, StringComparison.OrdinalIgnoreCase))
            throw new WorkflowConflictException($"The user task is already claimed by '{task.ClaimedBy}'.");
        await runtime.UpdateUserTaskClaimAsync(taskId, user, cancellationToken);
        await runtime.TouchInstanceAsync(instance.Id, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetUserTaskAsync(taskId, actor, cancellationToken);
    }

    public async Task<UserTaskDto?> UnclaimUserTaskAsync(
        long taskId,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        var initialTask = await runtime.GetUserTaskAsync(taskId, false, cancellationToken);
        if (initialTask is null) return null;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var instance = await runtime.GetInstanceForUpdateAsync(initialTask.InstanceId, false, cancellationToken)
            ?? throw new WorkflowConflictException("The workflow instance no longer exists.");
        if (instance.Status != WorkflowInstanceStatuses.Running)
            throw new WorkflowConflictException("The workflow instance is no longer running.");
        MultiInstanceExecutionRecord? execution = null;
        if (initialTask.MultiInstanceExecutionId is long executionId)
        {
            execution = await runtime.GetMultiInstanceAsync(executionId, true, cancellationToken);
            if (execution is null || execution.InstanceId != instance.Id
                                  || execution.Status != MultiInstanceRecordStatuses.Active)
                throw new WorkflowConflictException("The multi-instance execution has already closed.");
            if (instance.ActiveTokenId != execution.TokenId || instance.CurrentStepId != execution.NodeId)
                throw new WorkflowConflictException("The multi-instance execution is no longer the current workflow step.");
        }
        var task = await runtime.GetUserTaskAsync(taskId, true, cancellationToken);
        if (task is null || task.InstanceId != instance.Id
                         || task.MultiInstanceExecutionId != initialTask.MultiInstanceExecutionId
                         || task.TokenId != instance.ActiveTokenId
                         || task.NodeId != instance.CurrentStepId
                         || task.Status != UserTaskRecordStatuses.Active)
            throw new WorkflowConflictException("The user task is no longer current.");
        if (task.Assignee is not null)
            throw new WorkflowDomainException("Directly assigned tasks do not use claim/unclaim.");
        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var user = NormalizeUser(actor.User);
        var roles = NormalizeRoles(actor.Roles);
        var mayOverride = workflow.Definition.UnclaimRoles.Any(roles.Contains);
        if (!string.IsNullOrWhiteSpace(task.ClaimedBy)
            && !string.Equals(task.ClaimedBy, user, StringComparison.OrdinalIgnoreCase)
            && !mayOverride)
            throw new WorkflowDomainException("Only the claimant or a configured unclaim role can unclaim this user task.");
        await runtime.UpdateUserTaskClaimAsync(taskId, null, cancellationToken);
        await runtime.TouchInstanceAsync(instance.Id, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetUserTaskAsync(taskId, actor, cancellationToken);
    }

    public async Task<PagedResult<UserTaskDto>> ListUserTasksAsync(
        long instanceId,
        string? status,
        int page,
        int pageSize,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var records = await runtime.ListUserTasksAsync(instanceId, status, cancellationToken);
        var instance = await runtime.GetInstanceAsync(instanceId, cancellationToken);
        if (instance is null) return new PagedResult<UserTaskDto>([], page, pageSize, 0);
        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var visible = new List<UserTaskRecord>();
        foreach (var task in records)
        {
            var node = GetFlowNode(workflow.Definition, task.NodeId);
            if (CanUserTaskActor(task, node, actor))
                visible.Add(task);
        }
        var pageRecords = visible.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var executionIds = pageRecords
            .Where(task => task.MultiInstanceExecutionId is not null)
            .Select(task => task.MultiInstanceExecutionId!.Value)
            .Distinct()
            .ToList();
        var progressCache = await BuildProgressAsync(executionIds, cancellationToken);
        var items = new List<UserTaskDto>(pageRecords.Count);
        foreach (var task in pageRecords)
        {
            var progress = task.MultiInstanceExecutionId is long executionId
                ? progressCache.GetValueOrDefault(executionId)
                : null;
            items.Add(ToUserTaskDto(task, progress));
        }
        return new PagedResult<UserTaskDto>(items, page, pageSize, visible.Count);
    }

    public async Task<IReadOnlyList<SequenceFlowModel>> GetMultiInstanceInterruptFlowsAsync(
        long executionId,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        await LoadSettingsAsync(cancellationToken);
        var execution = await runtime.GetMultiInstanceAsync(executionId, false, cancellationToken);
        if (execution is null || execution.Status != MultiInstanceRecordStatuses.Active)
            return [];

        var instance = await runtime.GetInstanceAsync(execution.InstanceId, cancellationToken);
        if (instance is null || instance.Status != WorkflowInstanceStatuses.Running
            || instance.ActiveTokenId != execution.TokenId
            || instance.CurrentStepId != execution.NodeId)
            return [];

        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var node = GetFlowNode(workflow.Definition, execution.NodeId);
        var roles = NormalizeRoles(actor.Roles);
        if (node.MultiInstance is null || !BpmnFlowNodeTypes.IsUserTask(node.Type) || !RoleAllowed(node, roles))
            return [];

        var stored = await LoadVariablesAsync(instance.Id, cancellationToken);
        var context = WithContext(stored, actor, instance, workflow.Definition, node);
        AddMultiInstanceExecutionContext(context, execution);
        return OutgoingFlows(workflow.Definition, node.Id)
            .Where(f => f.IsSelectable && !f.IsDefault
                        && f.CancelRemainingInstances
                        && RoleAllowed(f.Roles, roles)
                        && (string.IsNullOrWhiteSpace(f.Condition)
                            || SequenceFlowConditionEvaluator.Evaluate(f.Condition, context)))
            .ToList();
    }

    public async Task<InstanceDetailDto?> TakeMultiInstanceInterruptFlowAsync(
        long executionId,
        int flowId,
        ActorContext actor,
        Dictionary<string, JsonElement>? variableValues,
        CancellationToken cancellationToken)
    {
        await LoadSettingsAsync(cancellationToken);
        var initialExecution = await runtime.GetMultiInstanceAsync(executionId, false, cancellationToken);
        if (initialExecution is null) return null;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var instance = await runtime.GetInstanceForUpdateAsync(initialExecution.InstanceId, false, cancellationToken)
            ?? throw new WorkflowConflictException("The workflow instance no longer exists.");
        var execution = await runtime.GetMultiInstanceAsync(executionId, true, cancellationToken);
        if (execution is null || execution.InstanceId != instance.Id
                              || execution.Status != MultiInstanceRecordStatuses.Active)
            throw new WorkflowConflictException("The multi-instance execution has already closed.");

        if (instance.Status != WorkflowInstanceStatuses.Running
            || instance.ActiveTokenId != execution.TokenId
            || instance.CurrentStepId != execution.NodeId)
            throw new WorkflowConflictException("The multi-instance execution is no longer the current workflow step.");

        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var node = GetFlowNode(workflow.Definition, execution.NodeId);
        if (node.MultiInstance is null || !BpmnFlowNodeTypes.IsUserTask(node.Type))
            throw new WorkflowConflictException("The execution is not an active multi-instance user task.");

        var flow = OutgoingFlows(workflow.Definition, node.Id).SingleOrDefault(f => f.Id == flowId)
            ?? throw new WorkflowDomainException("The requested flow is not an action of this multi-instance execution.");
        if (!flow.IsSelectable || flow.IsDefault || !flow.CancelRemainingInstances)
            throw new WorkflowDomainException("Only selectable interrupting flows can be taken at the multi-instance execution level.");

        EnsureRoleAllowed(node, actor);
        var roles = NormalizeRoles(actor.Roles);
        if (!RoleAllowed(flow.Roles, roles))
            throw new WorkflowDomainException("The actor does not have a role permitted for this interrupt action.");

        ValidateVariableValues(flow.Variables, variableValues);
        var stored = await LoadVariablesAsync(instance.Id, cancellationToken);
        var context = WithContext(stored, actor, instance, workflow.Definition, node);
        AddMultiInstanceExecutionContext(context, execution);
        var values = ResolveAndValidateVariables(flow.Variables, variableValues, context);
        foreach (var pair in values) context[pair.Key] = pair.Value;
        if (!string.IsNullOrWhiteSpace(flow.Condition)
            && !SequenceFlowConditionEvaluator.Evaluate(flow.Condition, context))
            throw new WorkflowDomainException("The selected interrupt action condition is not satisfied.");

        foreach (var pair in values)
        {
            await runtime.AddVariableAsync(instance.Id, pair.Key, flow.Id, actor.User, pair.Value, cancellationToken);
        }

        await CloseAndAdvanceMultiInstanceAsync(
            execution,
            instance,
            workflow,
            node,
            flow,
            "interrupt",
            actor,
            variableValues,
            context,
            null,
            null,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await BuildDetailAsync(instance.Id, cancellationToken);
    }

    public async Task<UserTaskActionAckDto?> TakeUserTaskFlowAsync(
        long taskId,
        int flowId,
        ActorContext actor,
        Dictionary<string, JsonElement>? variableValues,
        CancellationToken cancellationToken)
    {
        await LoadSettingsAsync(cancellationToken);
        var initialTask = await runtime.GetUserTaskAsync(taskId, false, cancellationToken);
        if (initialTask is null) return null;
        if (initialTask.MultiInstanceExecutionId is null)
        {
            var detail = await TakeFlowAsync(initialTask.InstanceId, flowId, actor, variableValues, cancellationToken);
            if (detail is null) return null;
            return new UserTaskActionAckDto(taskId, detail.Id, UserTaskRecordStatuses.Completed, detail.Status,
                flowId, detail.CurrentNodeId, detail.CurrentNodeName, detail.CurrentNodeExternalId, null, detail.UpdatedAt);
        }

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var instance = await runtime.GetInstanceForUpdateAsync(initialTask.InstanceId, false, cancellationToken)
            ?? throw new WorkflowConflictException("The workflow instance no longer exists.");
        if (instance.Status != WorkflowInstanceStatuses.Running)
            throw new WorkflowConflictException("The workflow instance is no longer running.");
        var execution = await runtime.GetMultiInstanceAsync(initialTask.MultiInstanceExecutionId.Value, true, cancellationToken);
        if (execution is null || execution.InstanceId != instance.Id
                              || execution.Status != MultiInstanceRecordStatuses.Active)
            throw new WorkflowConflictException("The multi-instance execution has already closed.");
        if (instance.ActiveTokenId != execution.TokenId || instance.CurrentStepId != execution.NodeId)
            throw new WorkflowConflictException("The multi-instance execution is no longer the current workflow step.");
        var task = await runtime.GetUserTaskAsync(taskId, true, cancellationToken);
        if (task is null || task.InstanceId != instance.Id
                         || task.MultiInstanceExecutionId != execution.Id
                         || task.TokenId != instance.ActiveTokenId
                         || task.NodeId != instance.CurrentStepId
                         || task.Status != UserTaskRecordStatuses.Active)
            throw new WorkflowConflictException("The user task is no longer active.");

        var user = NormalizeUser(actor.User);
        if (execution.OnePerActor)
        {
            if (await runtime.HasCompletedMultiInstanceItemAsync(execution.Id, user, cancellationToken))
                throw new WorkflowConflictException("The actor has already completed an item in this multi-instance execution.");
            var claimedTaskId = await runtime.GetClaimedMultiInstanceItemIdAsync(
                execution.Id, user, cancellationToken);
            if (claimedTaskId is not null && claimedTaskId != task.Id)
                throw new WorkflowConflictException("The actor has already claimed another item in this multi-instance execution.");
        }

        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var node = GetFlowNode(workflow.Definition, task.NodeId);
        var flow = OutgoingFlows(workflow.Definition, node.Id).SingleOrDefault(f => f.Id == flowId)
            ?? throw new WorkflowDomainException("The requested flow is not an action of this user task.");
        if (!flow.IsSelectable || flow.IsDefault)
            throw new WorkflowDomainException("The requested flow is an engine-only/default route and cannot be selected by a user.");
        EnsureUserTaskActor(task, node, actor, requireActive: true);
        var actorRoles = NormalizeRoles(actor.Roles);
        if (!RoleAllowed(flow.Roles, actorRoles))
            throw new WorkflowDomainException("The actor does not have a role permitted for this action.");
        if (task.RequiresClaim && !flow.CanActWithoutClaim
            && !string.Equals(task.ClaimedBy, NormalizeUser(actor.User), StringComparison.OrdinalIgnoreCase))
            throw new WorkflowDomainException("The user task must be claimed by the actor before taking this action.");

        ValidateVariableValues(flow.Variables, variableValues);
        var stored = await LoadVariablesAsync(instance.Id, cancellationToken);
        var context = WithContext(stored, actor, instance, workflow.Definition, node);
        AddMultiInstanceContext(context, task, execution);
        var values = ResolveAndValidateVariables(flow.Variables, variableValues, context);
        foreach (var pair in values) context[pair.Key] = pair.Value;
        if (!string.IsNullOrWhiteSpace(flow.Condition)
            && !SequenceFlowConditionEvaluator.Evaluate(flow.Condition, context))
            throw new WorkflowDomainException("The selected action condition is not satisfied.");

        await runtime.CompleteMultiInstanceItemAsync(task.Id, flow.Id, user, values, cancellationToken);
        var updatedCompleted = execution.CompletedCount + 1;
        var counts = (await runtime.ListMultiInstanceFlowCountsAsync(execution.Id, cancellationToken))
            .ToDictionary(p => p.Key, p => p.Value);
        context["mi.completed"] = JsonSerializer.SerializeToElement(updatedCompleted);
        context["mi.remaining"] = JsonSerializer.SerializeToElement(execution.TotalCount - updatedCompleted);

        SequenceFlowModel? winning = null;
        string? reason = null;
        if (flow.CancelRemainingInstances)
        {
            winning = flow;
            reason = "interrupt";
        }
        else
        {
            var allItemsCompleted = updatedCompleted == execution.TotalCount;
            var evaluateCompletionConditions =
                node.MultiInstance!.CompletionEvaluation == MultiInstanceCompletionEvaluations.AfterEach
                || allItemsCompleted;
            if (evaluateCompletionConditions)
            {
                winning = OutgoingFlows(workflow.Definition, node.Id)
                    .Where(f => !f.IsDefault && !f.CancelRemainingInstances
                                && !string.IsNullOrWhiteSpace(f.CompletionCondition))
                    .OrderBy(f => f.CompletionPriority)
                    .FirstOrDefault(f => SequenceFlowConditionEvaluator.EvaluateCompletion(
                        f.CompletionCondition, context, counts, execution.TotalCount));
            }
            if (winning is not null) reason = "condition";
            else if (allItemsCompleted)
            {
                winning = OutgoingFlows(workflow.Definition, node.Id)
                    .Single(f => !f.CancelRemainingInstances && f.IsDefault);
                reason = "all";
            }
        }

        if (winning is null)
        {
            if (execution.Mode == MultiInstanceModes.Sequential)
                await runtime.ActivateNextMultiInstanceItemAsync(execution.Id, cancellationToken);
            await runtime.AddMultiInstanceHistoryAsync(instance.Id, task.TokenId, task.Id, execution.Id,
                task.ItemIndex ?? 0, flow.Id, node.Id, node.Id, user,
                CloneDictionary(variableValues), "multiInstanceItem", cancellationToken);
            var activityAt = await runtime.TouchInstanceAsync(instance.Id, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            var progress = await BuildProgressAsync(execution.Id, cancellationToken);
            return new UserTaskActionAckDto(task.Id, instance.Id, UserTaskRecordStatuses.Completed,
                instance.Status, flow.Id, node.Id, node.Name, node.ExternalId, progress, activityAt);
        }

        var lockedInstance = await CloseAndAdvanceMultiInstanceAsync(
            execution,
            instance,
            workflow,
            node,
            winning,
            reason!,
            actor,
            variableValues,
            context,
            task.Id,
            task.ItemIndex,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var resting = GetFlowNode(workflow.Definition, lockedInstance.CurrentStepId);
        var closedProgress = await BuildProgressAsync(execution.Id, cancellationToken);
        return new UserTaskActionAckDto(task.Id, instance.Id, UserTaskRecordStatuses.Completed,
            lockedInstance.Status, flow.Id, resting.Id, resting.Name, resting.ExternalId,
            closedProgress, lockedInstance.UpdatedAt);
    }

    private async Task<WorkflowInstanceRecord> CloseAndAdvanceMultiInstanceAsync(
        MultiInstanceExecutionRecord execution,
        WorkflowInstanceRecord instance,
        WorkflowDefinitionRecord workflow,
        FlowNodeModel node,
        SequenceFlowModel winning,
        string reason,
        ActorContext actor,
        Dictionary<string, JsonElement>? variableValues,
        Dictionary<string, JsonElement> context,
        long? userTaskId,
        int? itemIndex,
        CancellationToken cancellationToken)
    {
        var user = NormalizeUser(actor.User);
        await runtime.CloseMultiInstanceAsync(execution.Id, winning.Id, reason, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        var result = await BuildMultiInstanceResultAsync(execution.Id, cancellationToken);
        await runtime.AddVariableAsync(instance.Id, execution.ResultVariable, node.Id, user, result, cancellationToken);
        context[execution.ResultVariable] = result;
        await runtime.AddMultiInstanceHistoryAsync(
            instance.Id,
            execution.TokenId,
            userTaskId,
            execution.Id,
            itemIndex,
            winning.Id,
            node.Id,
            winning.TargetRef,
            user,
            CloneDictionary(variableValues),
            reason == "interrupt" ? "multiInstanceInterrupt" : "multiInstanceComplete",
            cancellationToken);

        var nextNode = GetFlowNode(workflow.Definition, winning.TargetRef);
        var nextStatus = BpmnFlowNodeTypes.IsErrorEnd(nextNode.Type) ? WorkflowInstanceStatuses.Faulted
            : BpmnFlowNodeTypes.IsEnd(nextNode.Type) ? WorkflowInstanceStatuses.Completed
            : WorkflowInstanceStatuses.Running;
        var lockedInstance = instance with
        {
            CurrentStepId = nextNode.Id,
            Status = nextStatus,
            ClaimedBy = null,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var nextContext = WithContext(context, actor, lockedInstance, workflow.Definition, nextNode);
        await runtime.UpdateInstanceNodeAsync(
            lockedInstance.Id,
            ToSnapshot(nextNode, nextContext, lockedInstance.Id),
            nextStatus,
            null,
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        lockedInstance = await ResolvePassThroughAsync(lockedInstance, workflow.Definition, actor, cancellationToken);
        await EnsureMultiInstanceInitializedAsync(lockedInstance, workflow.Definition, actor, cancellationToken);
        lockedInstance = await ApplyClaimInheritanceAsync(lockedInstance, workflow.Definition, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return lockedInstance;
    }

    public async Task<InstanceDetailDto?> TakeFlowAsync(
        long id,
        int flowId,
        ActorContext actor,
        Dictionary<string, JsonElement>? variableValues,
        CancellationToken cancellationToken)
    {
        await LoadSettingsAsync(cancellationToken);
        var preview = await runtime.GetInstanceAsync(id, cancellationToken);
        if (preview is not null)
        {
            var previewWorkflow = await GetWorkflowAsync(preview.WorkflowDefinitionId, cancellationToken);
            var previewNode = GetFlowNode(previewWorkflow.Definition, preview.CurrentStepId);
            if (previewNode.MultiInstance is not null)
            {
                var tasks = await runtime.ListUserTasksAsync(id, UserTaskRecordStatuses.Active, cancellationToken);
                if (tasks.Count != 1) throw new WorkflowConflictException("The instance has multiple active user tasks; use a task-addressed endpoint.");
                await TakeUserTaskFlowAsync(tasks[0].Id, flowId, actor, variableValues, cancellationToken);
                return await BuildDetailAsync(id, cancellationToken);
            }
        }
        var performedBy = actor.User;
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var instance = await runtime.GetInstanceForUpdateAsync(id, true, cancellationToken);
        if (instance is null)
        {
            logger.LogInformation("Take flow {FlowId} on instance {InstanceId}: instance not found.", flowId, id);
            return null;
        }

        if (instance.Status != WorkflowInstanceStatuses.Running)
        {
            logger.LogWarning("Take flow {FlowId} rejected on instance {InstanceId}: instance status is {Status} (not Running).", flowId, id, instance.Status);
            throw new WorkflowDomainException("Only running instances can take a sequence flow.");
        }

        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var node = GetFlowNode(workflow.Definition, instance.CurrentStepId);
        var flow = OutgoingFlows(workflow.Definition, node.Id).SingleOrDefault(f => f.Id == flowId);
        if (flow is null || !BpmnFlowNodeTypes.IsUserTask(node.Type))
        {
            logger.LogWarning("Take flow {FlowId} rejected on instance {InstanceId}: flow not available from current node #{NodeId} ({NodeType}).",
                flowId, id, node.Id, node.Type);
            throw new WorkflowDomainException("The requested sequence flow is not available from the current node.");
        }
        if (!flow.IsSelectable || flow.IsDefault)
        {
            throw new WorkflowDomainException("The requested sequence flow is an engine-only/default route and cannot be selected by a user.");
        }

        var task = instance.ActiveUserTaskId is long taskId
            ? await runtime.GetUserTaskAsync(taskId, true, cancellationToken)
            : null;
        if (task is null)
        {
            throw new WorkflowConflictException("The active user task could not be resolved.");
        }
        if (task.InstanceId != instance.Id
            || task.TokenId != instance.ActiveTokenId
            || task.NodeId != node.Id
            || task.MultiInstanceExecutionId is not null
            || task.Status != UserTaskRecordStatuses.Active)
            throw new WorkflowConflictException("The active user task is no longer current.");
        EnsureUserTaskActor(task, node, actor, requireActive: true);

        logger.LogInformation("Taking sequence flow {FlowId} ({FlowName}) on instance {InstanceId} from node {SourceNodeId} ({SourceNodeType}) to {TargetNodeId} by user '{User}'",
            flowId, flow.Name, id, node.Id, node.Type, flow.TargetRef, performedBy ?? "anonymous");

        var actorRoles = NormalizeRoles(actor.Roles);
        EnsureRoleAllowed(node, actorRoles, actor.User);
        if (!RoleAllowed(flow.Roles, actorRoles))
        {
            logger.LogWarning("Take flow {FlowId} rejected on instance {InstanceId}: user '{User}' lacks a flow role ({FlowRoles}).",
                flowId, id, performedBy, string.Join(",", flow.Roles ?? []));
            throw new WorkflowDomainException(
                $"'{NormalizeUser(actor.User)}' does not have a role permitted to take this sequence flow.");
        }
        EnsureActionAllowedByClaim(task, flow, performedBy);

        ValidateVariableValues(flow.Variables, variableValues);

        // Resolve templated defaults and run NCalc validation against the existing
        // stored variables plus the final flow values, overlaid with context.
        var storedForValidation = await LoadVariablesAsync(instance.Id, cancellationToken);
        var flowContext = WithContext(storedForValidation, actor, instance, workflow.Definition, node);
        var flowValues = ResolveAndValidateVariables(flow.Variables, variableValues, flowContext);
        foreach (var pair in flowValues)
        {
            flowContext[pair.Key] = pair.Value;
        }

        if (!string.IsNullOrWhiteSpace(flow.Condition)
            && !SequenceFlowConditionEvaluator.Evaluate(flow.Condition, flowContext))
        {
            logger.LogWarning("Take flow {FlowId} ({FlowName}) rejected on instance {InstanceId}: flow condition '{Condition}' evaluated to false.",
                flowId, flow.Name, id, flow.Condition);
            throw new WorkflowDomainException(
                $"Sequence flow '{flow.Name}' condition is not satisfied: '{flow.Condition}'.");
        }

        foreach (var pair in flowValues)
        {
            await runtime.AddVariableAsync(instance.Id, pair.Key, flow.Id, performedBy, pair.Value, cancellationToken);
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
        var nextStatus = BpmnFlowNodeTypes.IsErrorEnd(nextNode.Type)
            ? WorkflowInstanceStatuses.Faulted
            : BpmnFlowNodeTypes.IsEnd(nextNode.Type)
                ? WorkflowInstanceStatuses.Completed
                : WorkflowInstanceStatuses.Running;

        instance = instance with
        {
            CurrentStepId = nextNode.Id,
            Status = nextStatus,
            ClaimedBy = null,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var nextContext = WithContext(flowContext, actor, instance, workflow.Definition, nextNode);
        await runtime.UpdateInstanceNodeAsync(
            instance.Id,
            ToSnapshot(nextNode, nextContext, instance.Id),
            instance.Status,
            null,
            cancellationToken);
        // Flush captured variables so pass-through gateways can read them within this transaction.
        await unitOfWork.SaveChangesAsync(cancellationToken);
        instance = await ResolvePassThroughAsync(instance, workflow.Definition, actor, cancellationToken);
        await EnsureMultiInstanceInitializedAsync(instance, workflow.Definition, actor, cancellationToken);
        instance = await ApplyClaimInheritanceAsync(instance, workflow.Definition, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var restingNode = GetFlowNode(workflow.Definition, instance.CurrentStepId);
        logger.LogInformation("Successfully completed transition for instance {InstanceId} through flow {FlowId}. Current status: {Status}, resting on node {CurrentStepId} ({CurrentStepType})",
            instance.Id, flowId, instance.Status, instance.CurrentStepId, restingNode.Type);

        return await BuildDetailAsync(id, cancellationToken);
    }

    public async Task<MessageDeliveryAckDto?> DeliverMessageAsync(
        long id,
        IncomingMessage message,
        CancellationToken cancellationToken)
    {
        await LoadSettingsAsync(cancellationToken);
        var actor = message.Actor;
        var performedBy = actor.User;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var instance = await runtime.GetInstanceForUpdateAsync(id, false, cancellationToken);
        if (instance is null)
        {
            logger.LogInformation("Deliver message to instance {InstanceId}: instance not found.", id);
            return null;
        }

        if (instance.Status != WorkflowInstanceStatuses.Running)
        {
            logger.LogWarning("Deliver message to instance {InstanceId} rejected: instance status is {Status} (not Running).", id, instance.Status);
            throw new WorkflowDomainException("Only running instances can receive a message.");
        }

        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var node = GetFlowNode(workflow.Definition, instance.CurrentStepId);
        if (!BpmnFlowNodeTypes.IsMessageCatch(node.Type))
        {
            logger.LogWarning("Deliver message to instance {InstanceId} rejected: current node #{NodeId} ({NodeType}) is not a message catch event.", id, node.Id, node.Type);
            throw new WorkflowDomainException("The instance is not currently waiting for a message.");
        }

        logger.LogInformation("Delivering message to catch node #{NodeId} ({NodeName}) on instance {InstanceId} for client '{ClientId}'",
            node.Id, node.Name, id, message.ClientId);

        var messageConfig = node.Message
            ?? throw new WorkflowDomainException($"Message catch event #{node.Id} has no message configuration.");

        // Resolve the templated expected credentials + required header. Credentials
        // and the header name/value are resolved against stored instance variables
        // overlaid with read-only config.*/setting.* context ONLY - the caller's
        // sys.user/sys.roles are intentionally excluded so an unverified caller
        // cannot satisfy a credential by templating it from ${sys.user} (the value
        // would resolve to the empty string they don't send). sys.* context the
        // caller cannot influence (sys.now/sys.today/sys.instanceId/sys.workflowId/
        // sys.nodeId/sys.nodeName) is still available since it is not attacker-
        // controlled and may be useful in a headerValue/headerValidation template.
        var stored = await LoadVariablesAsync(instance.Id, cancellationToken);
        var authContext = BuildAuthContext(stored, instance, workflow.Definition, node);

        var expectedClientId = ServiceTaskTemplating.SubstituteScalar(messageConfig.ClientId, authContext);
        var expectedClientSecret = ServiceTaskTemplating.SubstituteScalar(messageConfig.ClientSecret, authContext);
        var expectedHeaderName = ServiceTaskTemplating.SubstituteScalar(messageConfig.HeaderName, authContext);
        var expectedHeaderValue = ServiceTaskTemplating.SubstituteScalar(messageConfig.HeaderValue, authContext);

        // Authenticate the caller against the node's expected client id/secret.
        if (!string.Equals(message.ClientId ?? string.Empty, expectedClientId, StringComparison.Ordinal)
            || !ConstantTimeEquals(message.ClientSecret ?? string.Empty, expectedClientSecret))
        {
            logger.LogWarning("Deliver message to instance {InstanceId} rejected: invalid client credentials (client id '{ClientId}').", id, message.ClientId);
            throw new WorkflowUnauthorizedException("Invalid client credentials.");
        }

        // Validate the required custom header: present, equal to the resolved
        // expected value, and (when set) satisfying the NCalc headerValidation rule
        // with the incoming value bound as `header` alongside instance vars/context.
        // Header failures are domain errors (400), not auth failures (401): the
        // caller has already authenticated via the client id/secret, so a header
        // problem is a bad request rather than an identity failure.
        if (!message.Headers.TryGetValue(expectedHeaderName, out var incomingHeaderValues)
            || incomingHeaderValues.Count == 0
            || incomingHeaderValues[0] is not { } incomingHeaderValue)
        {
            logger.LogWarning("Deliver message to instance {InstanceId} rejected: required header '{HeaderName}' is missing.", id, expectedHeaderName);
            throw new WorkflowDomainException(
                $"Required header '{expectedHeaderName}' is missing.");
        }

        if (!ConstantTimeEquals(incomingHeaderValue, expectedHeaderValue))
        {
            logger.LogWarning("Deliver message to instance {InstanceId} rejected: header '{HeaderName}' value mismatch.", id, expectedHeaderName);
            throw new WorkflowDomainException(
                $"Header '{expectedHeaderName}' does not match the expected value.");
        }

        if (!string.IsNullOrWhiteSpace(messageConfig.HeaderValidation))
        {
            // The headerValidation rule may legitimately reference sys.user (the
            // now-authenticated client id) and other sys.* context, so it is evaluated
            // against the full context (caller actor included) plus the incoming header.
            var fullContext = WithContext(stored, actor, instance, workflow.Definition, node);
            var validationCtx = new Dictionary<string, JsonElement>(fullContext, StringComparer.OrdinalIgnoreCase)
            {
                ["header"] = JsonSerializer.SerializeToElement(incomingHeaderValue)
            };
            if (!SequenceFlowConditionEvaluator.Evaluate(messageConfig.HeaderValidation, validationCtx))
            {
                logger.LogWarning("Deliver message to instance {InstanceId} rejected: header '{HeaderName}' failed validation '{Validation}'.", id, expectedHeaderName, messageConfig.HeaderValidation);
                throw new WorkflowDomainException(
                    $"Header '{expectedHeaderName}' failed validation: '{messageConfig.HeaderValidation}'.");
            }
        }

        // Resolve the complete typed mapping batch before writing anything. The
        // authenticated client becomes sys.user for defaults and NCalc rules.
        var outputContext = WithContext(stored, actor, instance, workflow.Definition, node);
        var mappedValues = await ApplyMessageOutputsAsync(
            instance.Id,
            node.Id,
            performedBy,
            messageConfig,
            workflow.Definition.Variables,
            message.Payload,
            outputContext,
            cancellationToken);
        foreach (var pair in mappedValues)
        {
            stored[pair.Key] = pair.Value;
        }

        // Advance down the single unconditional outgoing flow (ValidateDefinition
        // enforced exactly one for a message catch event). SingleOrDefault + a
        // domain exception keeps a malformed legacy definition from surfacing as a
        // bare 500 (matching SelectPassThroughFlow's style).
        var flow = OutgoingFlows(workflow.Definition, node.Id).SingleOrDefault()
            ?? throw new WorkflowDomainException($"Message catch event #{node.Id} has no outgoing sequence flow.");
        var nextNode = GetFlowNode(workflow.Definition, flow.TargetRef);
        var nextStatus = BpmnFlowNodeTypes.IsErrorEnd(nextNode.Type)
            ? WorkflowInstanceStatuses.Faulted
            : BpmnFlowNodeTypes.IsEnd(nextNode.Type)
                ? WorkflowInstanceStatuses.Completed
                : WorkflowInstanceStatuses.Running;

        await runtime.AddHistoryAsync(
            instance.Id,
            null,
            node.Id,
            nextNode.Id,
            performedBy,
            null,
            "message",
            cancellationToken);

        instance = instance with
        {
            CurrentStepId = nextNode.Id,
            Status = nextStatus,
            ClaimedBy = null,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var nextContext = WithContext(stored, actor, instance, workflow.Definition, nextNode);
        await runtime.UpdateInstanceNodeAsync(
            instance.Id,
            ToSnapshot(nextNode, nextContext, instance.Id),
            instance.Status,
            null,
            cancellationToken);
        // Flush mapped variables so pass-through gateways can read them within this transaction.
        await unitOfWork.SaveChangesAsync(cancellationToken);
        instance = await ResolvePassThroughAsync(instance, workflow.Definition, actor, cancellationToken);
        await EnsureMultiInstanceInitializedAsync(instance, workflow.Definition, actor, cancellationToken);
        instance = await ApplyClaimInheritanceAsync(instance, workflow.Definition, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var restingNode = GetFlowNode(workflow.Definition, instance.CurrentStepId);
        logger.LogInformation("Successfully delivered message to instance {InstanceId} on node {NodeId}. Advancing to {NextNodeId} ({NextNodeType})",
            instance.Id, node.Id, instance.CurrentStepId, restingNode.Type);

        // Return a slim ack (no definition/variables/history) so an AllowAnonymous
        // webhook caller cannot read the full workflow model or instance data.
        return await BuildMessageAckAsync(instance.Id, cancellationToken);
    }

    // Builds the credential/header resolution context: stored instance variables
    // overlaid with config.*/setting.* and the sys.* entries an unverified caller
    // cannot influence (now/today/instance/workflow/node). It deliberately omits
    // sys.user and sys.roles (which come from the unverified X-Client-Id header)
    // and sys.claim.* (JWT claims, absent for an anonymous delivery) so a templated
    // credential cannot be satisfied by the very caller it is supposed to authenticate.
    // Builds the credential/header resolution context: stored instance variables
    // (empty for a message start, which has no instance yet) overlaid with
    // config.*/setting.* and the sys.* entries an unverified caller cannot
    // influence. For a delivery to an existing instance, sys.instanceId is the
    // instance id; for a message start (no instance yet) it is absent. sys.user
    // and sys.roles are deliberately excluded so a templated credential cannot
    // be satisfied by the very caller it is supposed to authenticate.
    private Dictionary<string, JsonElement> BuildAuthContext(
        Dictionary<string, JsonElement> stored,
        WorkflowInstanceRecord? instance,
        WorkflowModel definition,
        FlowNodeModel currentNode)
    {
        var merged = new Dictionary<string, JsonElement>(stored, StringComparer.OrdinalIgnoreCase);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        void Put(string key, object? value) => merged[key] = JsonSerializer.SerializeToElement(value);

        Put("sys.now", now.ToString("o", CultureInfo.InvariantCulture));
        Put("sys.today", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (instance is not null)
        {
            Put("sys.instanceId", instance.Id);
            Put("sys.workflowId", instance.WorkflowDefinitionId);
        }
        else
        {
            // A message start resolves the workflow by key; sys.workflowId is not
            // meaningful (it would be a version row id the caller didn't address).
            Put("sys.workflowId", 0);
        }

        Put("sys.workflowName", definition.Name);
        Put("sys.nodeId", currentNode.Id);
        Put("sys.nodeName", currentNode.Name);

        if (contextOptions.Config is { } config)
        {
            foreach (var pair in config)
            {
                merged[$"config.{pair.Key}"] = JsonSerializer.SerializeToElement(pair.Value);
            }
        }

        if (_settingsCache is { } cache)
        {
            foreach (var pair in cache)
            {
                merged[pair.Key] = pair.Value;
            }
        }

        return merged;
    }

    // Builds the slim delivery ack: only the resting node identity + status, no
    // definition/variables/history (the message endpoint is AllowAnonymous).
    private async Task<MessageDeliveryAckDto?> BuildMessageAckAsync(long id, CancellationToken cancellationToken)
    {
        var instance = await runtime.GetInstanceAsync(id, cancellationToken);
        if (instance is null)
        {
            return null;
        }

        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var node = GetFlowNode(workflow.Definition, instance.CurrentStepId);
        return new MessageDeliveryAckDto(
            instance.Id,
            node.Id,
            node.Name,
            node.ExternalId,
            instance.Status,
            instance.UpdatedAt);
    }

    // Message-start mappings are typed start-variable declarations. A missing path
    // is not an extraction failure here: defaults and final required checks run in
    // declaration order after extraction. A supplied value, however, must already
    // have the declared JSON type and never falls back to the default.
    private static Dictionary<string, JsonElement> ExtractMessageStartOutputs(
        FlowNodeModel node,
        MessageCatchModel message,
        JsonElement? payload)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (payload is not { } body || body.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return result;
        }

        foreach (var mapping in message.OutputMappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.Path)
                || !ServiceTaskTemplating.TryExtract(body, mapping.Path, out var value))
            {
                continue;
            }

            var variable = ToMessageStartVariable(mapping);
            if (!TypedOutputValueValidator.IsValid(value, variable.DataType, variable.IsArray))
            {
                throw new WorkflowDomainException(
                    $"Message start event #{node.Id} output mapping '{mapping.Variable}' must be {TypedOutputValueValidator.DescribeExpected(variable.DataType, variable.IsArray)}.");
            }
            result[mapping.Variable] = value.Clone();
        }

        return result;
    }

    private static VariableModel ToMessageStartVariable(MessageOutputMappingModel mapping) => new()
    {
        Name = mapping.Variable,
        DataType = mapping.DataType ?? string.Empty,
        IsArray = mapping.IsArray ?? false,
        Required = mapping.Required,
        DefaultValue = mapping.DefaultValue,
        Validation = mapping.Validation
    };

    private static void ValidateMessageStartTypes(
        FlowNodeModel node,
        IReadOnlyList<VariableModel> variables,
        Dictionary<string, JsonElement> values)
    {
        foreach (var variable in variables)
        {
            if (!TryGetValue(values, variable.Name, out var value))
            {
                continue;
            }

            if (!TypedOutputValueValidator.IsValid(value, variable.DataType, variable.IsArray))
            {
                throw new WorkflowDomainException(
                    $"Message start event #{node.Id} output mapping '{variable.Name}' must be {TypedOutputValueValidator.DescribeExpected(variable.DataType, variable.IsArray)}.");
            }
        }
    }

    // Resolves typed service/catch outputs entirely in memory. External values
    // are strict; defaults resolve in mapping order; all mapping validations and
    // matching process-variable validations see the final overlay.
    private static Dictionary<string, JsonElement> ResolveTypedOutputs(
        int nodeId,
        string kind,
        IReadOnlyList<TypedOutputRuntime> mappings,
        IReadOnlyList<VariableModel> processVariables,
        JsonElement? payload,
        Dictionary<string, JsonElement> contextBase)
    {
        var supplied = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (payload is { } body
            && body.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            foreach (var mapping in mappings)
            {
                if (string.IsNullOrWhiteSpace(mapping.Path)
                    || !ServiceTaskTemplating.TryExtract(body, mapping.Path, out var value))
                {
                    continue;
                }

                if (!TypedOutputValueValidator.IsValid(value, mapping.DataType, mapping.IsArray))
                {
                    throw new WorkflowDomainException(
                        $"{kind} #{nodeId} output mapping '{mapping.Variable}' must be {TypedOutputValueValidator.DescribeExpected(mapping.DataType, mapping.IsArray)}.");
                }
                supplied[mapping.Variable] = value.Clone();
            }
        }

        var declarations = mappings.Select(mapping => new VariableModel
        {
            Name = mapping.Variable,
            DataType = mapping.DataType,
            IsArray = mapping.IsArray,
            Required = mapping.Required,
            DefaultValue = mapping.DefaultValue,
            Validation = mapping.Validation
        }).ToList();

        var resolved = ResolveVariables(declarations, supplied, contextBase);
        ValidateVariableValues(declarations, resolved);
        foreach (var mapping in mappings)
        {
            if (TryGetValue(resolved, mapping.Variable, out var value)
                && !TypedOutputValueValidator.IsValid(value, mapping.DataType, mapping.IsArray))
            {
                throw new WorkflowDomainException(
                    $"{kind} #{nodeId} output mapping '{mapping.Variable}' must be {TypedOutputValueValidator.DescribeExpected(mapping.DataType, mapping.IsArray)}.");
            }
        }

        ValidateResolvedVariableRules(declarations, resolved, contextBase);

        var writtenProcessVariables = processVariables
            .Where(processVariable =>
                mappings.Any(mapping => string.Equals(
                    mapping.Variable,
                    processVariable.Name,
                    StringComparison.OrdinalIgnoreCase))
                && TryGetValue(resolved, processVariable.Name, out _))
            .ToList();
        ValidateResolvedVariableRules(writtenProcessVariables, resolved, contextBase);
        return resolved;
    }

    private static TypedOutputRuntime ToTypedOutputRuntime(
        MessageOutputMappingModel mapping,
        IReadOnlyList<VariableModel> processVariables)
    {
        var processVariable = processVariables.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, mapping.Variable, StringComparison.OrdinalIgnoreCase));
        return new TypedOutputRuntime(
            processVariable?.Name ?? mapping.Variable,
            mapping.Path,
            mapping.Required,
            mapping.DataType ?? processVariable?.DataType ?? WorkflowVariableTypes.Json,
            mapping.IsArray ?? processVariable?.IsArray ?? false,
            mapping.DefaultValue,
            mapping.Validation);
    }

    private static TypedOutputRuntime ToTypedOutputRuntime(
        ServiceOutputMappingModel mapping,
        IReadOnlyList<VariableModel> processVariables)
    {
        var processVariable = processVariables.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, mapping.Variable, StringComparison.OrdinalIgnoreCase));
        return new TypedOutputRuntime(
            processVariable?.Name ?? mapping.Variable,
            mapping.Path,
            mapping.Required,
            mapping.DataType ?? processVariable?.DataType ?? WorkflowVariableTypes.Json,
            mapping.IsArray ?? processVariable?.IsArray ?? false,
            mapping.DefaultValue,
            mapping.Validation);
    }

    // Message catch failures throw before any AddVariableAsync call, so the
    // endpoint returns 400 and the locked instance remains on the catch node.
    private async Task<Dictionary<string, JsonElement>> ApplyMessageOutputsAsync(
        long instanceId,
        int nodeId,
        string? setBy,
        MessageCatchModel message,
        IReadOnlyList<VariableModel> processVariables,
        JsonElement? payload,
        Dictionary<string, JsonElement> contextBase,
        CancellationToken cancellationToken)
    {
        var mappings = message.OutputMappings
            .Select(mapping => ToTypedOutputRuntime(mapping, processVariables))
            .ToList();
        var values = ResolveTypedOutputs(
            nodeId,
            "Message catch event",
            mappings,
            processVariables,
            payload,
            contextBase);
        foreach (var pair in values)
        {
            await runtime.AddVariableAsync(instanceId, pair.Key, nodeId, setBy, pair.Value, cancellationToken);
        }
        return values;
    }

    // Constant-time string comparison to avoid leaking secret length/prefix via timing.
    private static bool ConstantTimeEquals(string a, string b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        var diff = 0;
        for (var i = 0; i < a.Length; i++)
        {
            diff |= a[i] ^ b[i];
        }

        return diff == 0;
    }

    public async Task<bool> CancelAsync(long id, ActorContext actor, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var preview = await runtime.GetInstanceAsync(id, cancellationToken);
        if (preview is null)
        {
            logger.LogInformation("Cancel instance {InstanceId}: instance not found.", id);
            return false;
        }

        var workflow = await GetWorkflowAsync(preview.WorkflowDefinitionId, cancellationToken);
        var cancelRoles = workflow.Definition.CancelRoles ?? [];
        if (cancelRoles.Count > 0)
        {
            var actorRoles = NormalizeRoles(actor.Roles);
            if (!cancelRoles.Any(r => actorRoles.Contains(r)))
            {
                logger.LogWarning("Cancel rejected on instance {InstanceId}: user '{User}' lacks a cancel role ({CancelRoles}).",
                    id, actor.User, string.Join(",", cancelRoles));
                throw new WorkflowDomainException("You do not have permission to cancel this workflow instance.");
            }
        }

        var instance = await runtime.GetInstanceForUpdateAsync(id, false, cancellationToken)
            ?? throw new WorkflowConflictException("The workflow instance disappeared while it was being cancelled.");
        if (instance.Status != WorkflowInstanceStatuses.Running)
            throw new WorkflowConflictException("Only a running workflow instance can be cancelled.");
        await runtime.CancelActiveMultiInstanceAsync(id, cancellationToken);
        await runtime.CancelOpenUserTasksAsync(id, cancellationToken);
        await runtime.UpdateInstanceAsync(
            instance.Id,
            instance.CurrentStepId,
            WorkflowInstanceStatuses.Cancelled,
            instance.ClaimedBy,
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Instance {InstanceId} cancelled by user '{User}'.", id, actor.User ?? "anonymous");

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

        // Load variables once before the loop. The overlay is maintained in memory
        // across hops so we avoid a SELECT instance_variables on every hop. Writes
        // (service/script task outputs, error-variable captures) update the overlay
        // alongside the staged EF entities, so the next hop sees them without a reload.
        var storedOverlay = await LoadVariablesAsync(instance.Id, cancellationToken);

        for (var hop = 0; hop < maxHops; hop++)
        {
            if (instance.Status != WorkflowInstanceStatuses.Running)
            {
                logger.LogDebug("Instance {InstanceId} pass-through ended with status {Status} at node #{NodeId}.", instance.Id, instance.Status, instance.CurrentStepId);
                return instance;
            }

            var currentNode = GetFlowNode(definition, instance.CurrentStepId);
            if (!BpmnFlowNodeTypes.IsPassThrough(currentNode.Type))
            {
                logger.LogDebug("Instance {InstanceId} pass-through resting on node #{NodeId} ({NodeType}).", instance.Id, currentNode.Id, currentNode.Type);
                return instance;
            }

            logger.LogDebug("Instance {InstanceId} processing pass-through hop {Hop}: current node #{NodeId} ({NodeType})",
                instance.Id, hop, currentNode.Id, currentNode.Type);

            // Build the evaluation context from the in-memory overlay overlaid with
            // read-only sys.*/config.* context. The merged map is for evaluation only
            // and is never persisted.
            var variables = WithContext(storedOverlay, actor, instance, definition, currentNode);

            TaskExecutionOutcome? outcome = null;
            if (BpmnFlowNodeTypes.IsServiceTask(currentNode.Type))
            {
                outcome = await ExecuteServiceTaskAsync(instance, currentNode, definition, actor, variables, storedOverlay, cancellationToken);
                // The overlay was updated in-place by ExecuteServiceTaskAsync; rebuild
                // the context so downstream gateways/service tasks see the written outputs.
                variables = WithContext(storedOverlay, actor, instance, definition, currentNode);
            }
            else if (BpmnFlowNodeTypes.IsScriptTask(currentNode.Type))
            {
                outcome = await ExecuteScriptTaskAsync(instance, currentNode, definition, actor, variables, storedOverlay, cancellationToken);
                // The overlay was updated in-place by ExecuteScriptTaskAsync; rebuild
                // the context so the outgoing-flow selector and the next hop see writes.
                variables = WithContext(storedOverlay, actor, instance, definition, currentNode);
            }

            // A service/script failure routes out an attached errorBoundaryEvent's
            // error flow when present (the boundary is itself pass-through and
            // auto-advances on the next hop); otherwise the transition fails
            // (rollback + 400), matching the historical no-boundary default.
            if (outcome is { Success: false })
            {
                var boundary = FindErrorBoundary(definition, currentNode.Id);
                if (boundary is null)
                {
                    logger.LogError("Task #{NodeId} failed on instance {InstanceId} and no error boundary was found. Reason: {Reason}", currentNode.Id, instance.Id, outcome.Reason);
                    throw new WorkflowDomainException(outcome.Reason ?? $"Task #{currentNode.Id} failed.");
                }

                logger.LogWarning("Task #{NodeId} failed on instance {InstanceId}. Routing to error boundary #{BoundaryId}. Reason: {Reason}",
                    currentNode.Id, instance.Id, boundary.Id, outcome.Reason);

                if (!string.IsNullOrWhiteSpace(boundary.ErrorVariable))
                {
                    var errorValue = JsonSerializer.SerializeToElement(outcome.Reason ?? string.Empty);
                    await runtime.AddVariableAsync(
                        instance.Id,
                        boundary.ErrorVariable!,
                        boundary.Id,
                        performedBy,
                        errorValue,
                        cancellationToken);
                    storedOverlay[boundary.ErrorVariable!] = errorValue;
                }

                await runtime.AddHistoryAsync(
                    instance.Id,
                    null,
                    currentNode.Id,
                    boundary.Id,
                    performedBy,
                    null,
                    "error",
                    cancellationToken);

                instance = instance with
                {
                    CurrentStepId = boundary.Id,
                    Status = WorkflowInstanceStatuses.Running,
                    ClaimedBy = null,
                    UpdatedAt = DateTimeOffset.UtcNow
                };

                await runtime.UpdateInstanceNodeAsync(instance.Id, ToSnapshot(boundary), instance.Status, null, cancellationToken);
                continue;
            }

            var flow = SelectPassThroughFlow(definition, currentNode, variables);
            var nextNode = GetFlowNode(definition, flow.TargetRef);
            var nextStatus = BpmnFlowNodeTypes.IsErrorEnd(nextNode.Type)
                ? WorkflowInstanceStatuses.Faulted
                : BpmnFlowNodeTypes.IsEnd(nextNode.Type)
                    ? WorkflowInstanceStatuses.Completed
                    : WorkflowInstanceStatuses.Running;

            var note = currentNode.Type switch
            {
                var t when BpmnFlowNodeTypes.IsStart(t) => "start",
                var t when BpmnFlowNodeTypes.IsMessageStart(t) => "messageStart",
                var t when BpmnFlowNodeTypes.IsGateway(t) => "gateway",
                var t when BpmnFlowNodeTypes.IsServiceTask(t) => "service",
                var t when BpmnFlowNodeTypes.IsScriptTask(t) => "script",
                var t when BpmnFlowNodeTypes.IsErrorBoundary(t) => "boundary",
                _ => "automatic"
            };

            logger.LogDebug("Instance {InstanceId} pass-through node #{NodeId} ({NodeType}) advancing to #{NextNodeId} ({NextNodeType}) via flow #{FlowId}",
                instance.Id, currentNode.Id, currentNode.Type, nextNode.Id, nextNode.Type, flow.Id);

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

            var nextContext = WithContext(storedOverlay, actor, instance, definition, nextNode);
            await runtime.UpdateInstanceNodeAsync(
                instance.Id,
                ToSnapshot(nextNode, nextContext, instance.Id),
                instance.Status,
                null,
                cancellationToken);
        }

        logger.LogError("Pass-through routing cycle detected on instance {InstanceId} after {MaxHops} hops.", instance.Id, maxHops);
        throw new WorkflowDomainException("Pass-through routing cycle detected.");
    }

    private async Task EnsureMultiInstanceInitializedAsync(
        WorkflowInstanceRecord instance,
        WorkflowModel definition,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        if (instance.Status != WorkflowInstanceStatuses.Running)
        {
            return;
        }

        var node = GetFlowNode(definition, instance.CurrentStepId);
        var multi = node.MultiInstance;
        if (!BpmnFlowNodeTypes.IsUserTask(node.Type) || multi is null)
        {
            return;
        }
        if (await runtime.GetActiveMultiInstanceAsync(instance.Id, node.Id, false, cancellationToken) is not null)
        {
            return;
        }

        var configured = await engineSettings.GetByKeyAsync("Workflow.MultiInstance.MaxInstances", cancellationToken);
        var maxInstances = configured is not null && int.TryParse(configured.Value, out var parsed) && parsed > 0
            ? parsed
            : 1000;
        var stored = await LoadVariablesAsync(instance.Id, cancellationToken);
        var context = WithContext(stored, actor, instance, definition, node);
        var items = new List<JsonElement?>();

        if (multi.Source == MultiInstanceSources.Collection)
        {
            if (!stored.TryGetValue(multi.CollectionVariable!, out var collection)
                || collection.ValueKind != JsonValueKind.Array)
            {
                throw new WorkflowDomainException(
                    $"Multi-instance collection '{multi.CollectionVariable}' must be a string array.");
            }
            foreach (var item in collection.EnumerateArray())
            {
                if (items.Count == maxInstances)
                {
                    throw new WorkflowDomainException(
                        $"Multi-instance item count must be between 1 and {maxInstances}; the collection exceeds the configured maximum.");
                }
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                {
                    throw new WorkflowDomainException(
                        $"Multi-instance collection '{multi.CollectionVariable}' contains an empty or non-string username.");
                }
                var username = item.GetString()!.Trim();
                if (username.Length > UserTaskConstraints.MaxActorNameLength)
                {
                    throw new WorkflowDomainException(
                        $"Multi-instance collection '{multi.CollectionVariable}' contains a username longer than {UserTaskConstraints.MaxActorNameLength} characters.");
                }
                items.Add(JsonSerializer.SerializeToElement(username));
            }
        }
        else
        {
            var raw = SequenceFlowConditionEvaluator.EvaluateValue(multi.CardinalityExpression, context);
            if (!decimal.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Number,
                    CultureInfo.InvariantCulture, out var number)
                || number != decimal.Truncate(number))
            {
                throw new WorkflowDomainException("Multi-instance cardinality must evaluate to an integer.");
            }

            if (number < 1 || number > maxInstances)
            {
                throw new WorkflowDomainException(
                    $"Multi-instance item count must be between 1 and {maxInstances}; actual count was {number}.");
            }

            var count = decimal.ToInt32(number);
            items = Enumerable.Repeat<JsonElement?>(null, count).ToList();
        }

        if (items.Count == 0 || items.Count > maxInstances)
        {
            throw new WorkflowDomainException(
                $"Multi-instance item count must be between 1 and {maxInstances}; actual count was {items.Count}.");
        }

        var emptyResult = JsonSerializer.SerializeToElement(Array.Empty<object>());
        await runtime.AddVariableAsync(instance.Id, multi.ResultVariable, node.Id, actor.User, emptyResult, cancellationToken);
        var outcomeIds = OutgoingFlows(definition, node.Id)
            .Where(f => f.IsSelectable && !f.IsDefault && !f.CancelRemainingInstances)
            .Select(f => f.Id).ToList();
        await runtime.AddMultiInstanceAsync(instance.Id, ToSnapshot(node), multi, items, outcomeIds, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
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
            || node.ClaimMode == ClaimModes.Fresh
            || node.MultiInstance is not null)
        {
            return instance;
        }

        var task = await runtime.GetActiveUserTaskAsync(instance.Id, false, cancellationToken);
        if (task is null || task.Assignee is not null || !task.RequiresClaim)
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
            logger.LogDebug("Instance {InstanceId}: claim mode '{ClaimMode}' found no prior actor to inherit; leaving unclaimed.", instance.Id, node.ClaimMode);
            return instance;
        }

        await runtime.UpdateInstanceAsync(instance.Id, instance.CurrentStepId, instance.Status, claimant, cancellationToken);
        logger.LogDebug("Instance {InstanceId}: claim mode '{ClaimMode}' inherited claim to user '{Claimant}' for node #{NodeId}.", instance.Id, node.ClaimMode, claimant, node.Id);
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
            logger.LogDebug("Evaluating exclusive gateway #{NodeId} ({NodeName}) outgoing flows...", node.Id, node.Name);
            var match = outgoing.FirstOrDefault(f =>
                !f.IsDefault
                && !string.IsNullOrWhiteSpace(f.Condition)
                && SequenceFlowConditionEvaluator.Evaluate(f.Condition, variables));
            if (match is not null)
            {
                logger.LogDebug("Exclusive gateway #{NodeId} evaluated flow {FlowId} ({FlowName}) condition '{Condition}' as True", node.Id, match.Id, match.Name, match.Condition);
                return match;
            }

            var defaultFlow = outgoing.FirstOrDefault(f => f.IsDefault);
            if (defaultFlow is not null)
            {
                logger.LogDebug("Exclusive gateway #{NodeId} condition did not match any flow; taking default flow {FlowId} ({FlowName})", node.Id, defaultFlow.Id, defaultFlow.Name);
                return defaultFlow;
            }

            logger.LogWarning("Exclusive gateway #{NodeId} evaluated all conditions as False and has no default flow", node.Id);
            throw new WorkflowDomainException(
                $"Exclusive gateway #{node.Id} has no matching condition and no default flow.");
        }

        if (outgoing.Count != 1)
        {
            var kind = BpmnFlowNodeTypes.IsStart(node.Type)
                ? "Start event"
                : BpmnFlowNodeTypes.IsMessageStart(node.Type)
                    ? "Message start event"
                    : BpmnFlowNodeTypes.IsServiceTask(node.Type)
                        ? "Service task"
                        : BpmnFlowNodeTypes.IsScriptTask(node.Type)
                            ? "Script task"
                            : BpmnFlowNodeTypes.IsErrorBoundary(node.Type)
                                ? "Error boundary event"
                                : "Automatic task";
            throw new WorkflowDomainException($"{kind} #{node.Id} must have exactly one outgoing sequence flow.");
        }

        return outgoing[0];
    }

    private async Task<TaskExecutionOutcome> ExecuteServiceTaskAsync(
        WorkflowInstanceRecord instance,
        FlowNodeModel node,
        WorkflowModel definition,
        ActorContext actor,
        IReadOnlyDictionary<string, JsonElement> variables,
        Dictionary<string, JsonElement> storedOverlay,
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
        logger.LogInformation("Service task #{NodeId} on instance {InstanceId}: invoking {Method} {Url}", node.Id, instance.Id, service.Method, url);
        var result = await serviceTaskInvoker.InvokeAsync(request, cancellationToken);

        var performedBy = actor.User;

        if (result.IsSuccess)
        {
            var mappingFailure = await ApplyServiceOutputsAsync(
                instance.Id,
                node.Id,
                performedBy,
                service,
                result,
                definition.Variables,
                new Dictionary<string, JsonElement>(variables, StringComparer.OrdinalIgnoreCase),
                storedOverlay,
                cancellationToken);
            if (mappingFailure is not null)
            {
                // A required output mapping could not be resolved from the 2xx
                // response body. Treat it like a service failure: write the status
                // variable (so an error path can branch on it), then fail the task
                // so the pass-through loop routes out an attached errorBoundaryEvent
                // (or, with no boundary, rolls back and returns 400).
                logger.LogWarning("Service task #{NodeId} on instance {InstanceId}: output mapping failed: {Reason}", node.Id, instance.Id, mappingFailure);
                await WriteStatusVariableAsync(instance.Id, node.Id, performedBy, service, result.StatusCode, storedOverlay, cancellationToken);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return TaskExecutionOutcome.Fail(mappingFailure);
            }

            logger.LogInformation("Service task #{NodeId} on instance {InstanceId} succeeded with HTTP {StatusCode}.", node.Id, instance.Id, result.StatusCode);
            await WriteStatusVariableAsync(instance.Id, node.Id, performedBy, service, result.StatusCode, storedOverlay, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return TaskExecutionOutcome.Ok();
        }

        // On failure the HTTP status (0 on transport error) is still written to
        // the optional statusVariable so the error path can branch on it. If no
        // errorBoundaryEvent is attached the loop throws (rollback + 400) and this
        // write rolls back with the transaction; if a boundary catches, it persists.
        logger.LogWarning("Service task #{NodeId} on instance {InstanceId} failed with HTTP {StatusCode}: {Reason}", node.Id, instance.Id, result.StatusCode, result.Error);
        await WriteStatusVariableAsync(instance.Id, node.Id, performedBy, service, result.StatusCode, storedOverlay, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var reason = result.Error ?? $"HTTP status {result.StatusCode}";
        return TaskExecutionOutcome.Fail($"Service task #{node.Id} call to '{url}' failed ({reason}).");
    }

    // Stages and validates every service response mapping before writing any of
    // them. A mapping error is returned as a task failure so an attached boundary
    // can catch it; a successful batch is then appended atomically in the current
    // instance transaction.
    private async Task<string?> ApplyServiceOutputsAsync(
        long instanceId,
        int nodeId,
        string? setBy,
        ServiceTaskModel service,
        ServiceTaskResult result,
        IReadOnlyList<VariableModel> processVariables,
        Dictionary<string, JsonElement> contextBase,
        Dictionary<string, JsonElement> storedOverlay,
        CancellationToken cancellationToken)
    {
        if (service.OutputMappings.Count == 0)
        {
            return null;
        }

        JsonDocument? document = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(result.Body))
            {
                document = JsonDocument.Parse(result.Body);
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse HTTP service task response body as JSON. Body: {Body}", result.Body);
        }

        try
        {
            using (document)
            {
                var mappings = service.OutputMappings
                    .Select(mapping => ToTypedOutputRuntime(mapping, processVariables))
                    .ToList();
                var payload = document is null ? (JsonElement?)null : document.RootElement;
                var values = ResolveTypedOutputs(
                    nodeId,
                    "Service task",
                    mappings,
                    processVariables,
                    payload,
                    contextBase);

                foreach (var pair in values)
                {
                    await runtime.AddVariableAsync(instanceId, pair.Key, nodeId, setBy, pair.Value, cancellationToken);
                    storedOverlay[pair.Key] = pair.Value;
                }
            }
        }
        catch (WorkflowDomainException ex)
        {
            return ex.Message;
        }

        return null;
    }

    private async Task WriteStatusVariableAsync(
        long instanceId,
        int nodeId,
        string? setBy,
        ServiceTaskModel service,
        int statusCode,
        Dictionary<string, JsonElement> storedOverlay,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(service.StatusVariable))
        {
            return;
        }

        var value = JsonSerializer.SerializeToElement(statusCode);
        await runtime.AddVariableAsync(instanceId, service.StatusVariable, nodeId, setBy, value, cancellationToken);
        storedOverlay[service.StatusVariable] = value;
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
    // overlay (the in-memory writes layered over the stored variables + context),
    // rejecting the transition just like a bad flow input. Validation runs before
    // persistence so an errorBoundaryEvent catch leaves nothing half-written.
    private async Task<TaskExecutionOutcome> ExecuteScriptTaskAsync(
        WorkflowInstanceRecord instance,
        FlowNodeModel node,
        WorkflowModel definition,
        ActorContext actor,
        IReadOnlyDictionary<string, JsonElement> variables,
        Dictionary<string, JsonElement> storedOverlay,
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
                return TaskExecutionOutcome.Ok();
            }

            var context = new EngineScriptContext(overlay, byName, writes);
            var result = scriptEvaluator.Evaluate(node.Script, context, cancellationToken);
            if (!result.Success)
            {
                logger.LogWarning("Script task #{NodeId} (javascript) on instance {InstanceId} failed: {Error}", node.Id, instance.Id, result.Error);
                return TaskExecutionOutcome.Fail($"Script task #{node.Id} failed: {result.Error}");
            }
        }
        else
        {
            if (node.Assignments.Count == 0)
            {
                return TaskExecutionOutcome.Ok();
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
            return TaskExecutionOutcome.Ok();
        }

        // Re-run each distinct target variable's validation rule against the
        // overlay (the in-memory writes layered over the stored variables + context)
        // before persisting, so an errorBoundaryEvent catch leaves nothing written.
        // Functionally equivalent to the prior reload-then-validate path since the
        // overlay already carries the coerced writes on top of the stored values.
        foreach (var target in writes.Select(w => w.Target).Distinct())
        {
            if (string.IsNullOrWhiteSpace(target.Validation))
            {
                continue;
            }

            if (!SequenceFlowConditionEvaluator.Evaluate(target.Validation, overlay))
            {
                logger.LogWarning("Script task #{NodeId} on instance {InstanceId}: variable '{Variable}' failed validation '{Validation}'.",
                    node.Id, instance.Id, target.Name, target.Validation);
                return TaskExecutionOutcome.Fail(
                    $"Variable '{target.Name}' failed validation: '{target.Validation}'.");
            }
        }

        var performedBy = actor.User;
        foreach (var (target, value) in writes)
        {
            await runtime.AddVariableAsync(instance.Id, target.Name!, node.Id, performedBy, value, cancellationToken);
            storedOverlay[target.Name!] = value;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return TaskExecutionOutcome.Ok();
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
    // (sys.*/config.*/setting.*). Context wins on collision so it can never be
    // spoofed by a stored variable. The result is for evaluation only and is
    // never persisted.
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

        if (_settingsCache is { } cache)
        {
            foreach (var pair in cache)
            {
                Put(pair.Key, pair.Value);
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
        var resolved = ResolveVariables(variables, variableValues, contextBase);
        ValidateResolvedVariableRules(variables, resolved, contextBase);
        return resolved;
    }

    private static Dictionary<string, JsonElement> ResolveVariables(
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

        return resolved;
    }

    private static void ValidateResolvedVariableRules(
        IReadOnlyList<VariableModel> variables,
        Dictionary<string, JsonElement> resolved,
        Dictionary<string, JsonElement> contextBase)
    {
        var working = new Dictionary<string, JsonElement>(contextBase, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in resolved)
        {
            working[pair.Key] = pair.Value;
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
        var multiExecution = node.MultiInstance is null
            ? null
            : await runtime.GetActiveMultiInstanceAsync(instance.Id, node.Id, false, cancellationToken);
        var multiProgress = multiExecution is null
            ? null
            : await BuildProgressAsync(multiExecution.Id, cancellationToken);
        var workSummaries = await runtime.GetUserTaskWorkSummariesAsync([id], cancellationToken);
        var userTasks = workSummaries.TryGetValue(id, out var workSummary)
            ? ToUserTaskWorkSummary(workSummary)
            : null;

        return new InstanceDetailDto(
            instance.Id,
            WorkflowDefinitionService.ToDetail(workflow),
            instance.CurrentStepId,
            node.Name,
            node.ExternalId,
            instance.Status,
            instance.BusinessKey,
            instance.BusinessKeyUniqueness,
            instance.StartedBy,
            instance.CreatedAt,
            instance.UpdatedAt,
            variables.Select(v => new InstanceVariableDto(
                v.Id,
                v.VariableName,
                v.SourceActionId,
                v.SetBy,
                v.Value,
                v.SetAt)).ToList(),
            history.Select(h => new InstanceHistoryDto(
                h.Id,
                h.TokenId,
                h.UserTaskId,
                h.MultiInstanceExecutionId,
                h.ItemIndex,
                h.ActionId,
                h.FromStepId,
                h.ToStepId,
                h.PerformedBy,
                h.Payload,
                h.Note,
                h.PerformedAt)).ToList(),
            multiProgress,
            userTasks);
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

    private async Task EnsureBusinessKeyFamilyStartableAsync(
        WorkflowDefinitionRecord workflow,
        CancellationToken cancellationToken)
    {
        var scopeActive = await definitions.IsBusinessKeyScopeActiveAsync(
            workflow.WorkflowKey, cancellationToken);
        var hasBusinessKeys = workflow.Definition.FlowNodes.Any(node =>
            BpmnFlowNodeTypes.IsEntry(node.Type) && node.BusinessKey is not null);

        if (scopeActive && !hasBusinessKeys)
        {
            throw new WorkflowDomainException(
                "This older unkeyed workflow version cannot start after business keys were enabled for its workflow key.");
        }

        if (!scopeActive && hasBusinessKeys)
        {
            throw new WorkflowDomainException(
                "This keyed workflow version cannot start until it becomes the default published version.");
        }
    }

    private static BusinessKeyStartInput NormalizeBusinessKeyInput(
        FlowNodeModel startEvent,
        Dictionary<string, JsonElement>? values)
    {
        if (startEvent.BusinessKey is null)
        {
            return new BusinessKeyStartInput(values, null, null);
        }

        var variableName = startEvent.BusinessKey.Variable;
        if (!TryGetValue(values, variableName, out var supplied)
            || supplied.ValueKind != JsonValueKind.String)
        {
            throw new WorkflowDomainException(
                $"Business key variable '{variableName}' must be supplied as an explicit JSON string.");
        }

        var businessKey = supplied.GetString()?.Trim() ?? string.Empty;
        if (businessKey.Length == 0)
        {
            throw new WorkflowDomainException("Business key must not be blank.");
        }

        if (businessKey.EnumerateRunes().Count() > 300)
        {
            throw new WorkflowDomainException("Business key must not exceed 300 characters.");
        }

        var normalized = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (values is not null)
        {
            foreach (var pair in values)
            {
                if (!string.Equals(pair.Key, variableName, StringComparison.OrdinalIgnoreCase))
                {
                    normalized[pair.Key] = pair.Value.Clone();
                }
            }
        }
        normalized[variableName] = JsonSerializer.SerializeToElement(businessKey);

        return new BusinessKeyStartInput(
            normalized,
            businessKey,
            startEvent.BusinessKey.Uniqueness);
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
            row.BusinessKey,
            row.BusinessKeyUniqueness,
            row.StartedBy,
            row.CreatedAt,
            row.UpdatedAt,
            row.UserTasks is null ? null : ToUserTaskWorkSummary(row.UserTasks),
            row.Variables);

    private async Task<UserTaskDto?> BuildUserTaskDtoAsync(UserTaskRecord task, CancellationToken cancellationToken)
    {
        var progress = task.MultiInstanceExecutionId is long executionId
            ? await BuildProgressAsync(executionId, cancellationToken)
            : null;
        return ToUserTaskDto(task, progress);
    }

    private static UserTaskDto ToUserTaskDto(UserTaskRecord task, MultiInstanceProgressDto? progress) =>
        new(task.Id, task.InstanceId, task.TokenId, task.NodeId, task.NodeName,
            task.NodeExternalId, task.Roles, task.RequiresClaim, task.Status, task.ClaimedBy,
            task.Assignee, task.ItemIndex, task.ItemValue, task.SelectedFlowId, progress,
            task.CreatedAt, task.UpdatedAt, task.CompletedAt);

    private async Task<MultiInstanceProgressDto?> BuildProgressAsync(long executionId, CancellationToken cancellationToken)
    {
        var progress = await BuildProgressAsync([executionId], cancellationToken);
        return progress.GetValueOrDefault(executionId);
    }

    private async Task<IReadOnlyDictionary<long, MultiInstanceProgressDto>> BuildProgressAsync(
        IReadOnlyCollection<long> executionIds,
        CancellationToken cancellationToken)
    {
        var records = await runtime.GetMultiInstanceProgressAsync(executionIds, cancellationToken);
        return records.ToDictionary(pair => pair.Key, pair => ToProgress(pair.Value));
    }

    private static MultiInstanceProgressDto ToProgress(MultiInstanceProgressRecord record)
    {
        var execution = record.Execution;
        return new MultiInstanceProgressDto(
            execution.Id,
            execution.Mode,
            execution.Status,
            execution.TotalCount,
            execution.CompletedCount,
            record.ActiveCount,
            record.PendingCount,
            record.CancelledCount,
            execution.WinningFlowId,
            execution.CompletionReason,
            record.FlowCounts.OrderBy(pair => pair.Key)
                .Select(pair => new MultiInstanceFlowCountDto(
                    pair.Key,
                    pair.Value,
                    execution.TotalCount == 0 ? 0d : pair.Value * 100d / execution.TotalCount))
                .ToList());
    }

    private static UserTaskWorkSummaryDto ToUserTaskWorkSummary(UserTaskWorkSummaryRecord summary) =>
        new(
            summary.IsMultiInstance,
            summary.ActiveCount,
            summary.PendingCount,
            summary.ClaimedCount,
            summary.AssignedCount,
            summary.SoleClaimedBy,
            summary.SoleAssignee);

    private async Task<JsonElement> BuildMultiInstanceResultAsync(long executionId, CancellationToken cancellationToken)
    {
        var tasks = await runtime.ListExecutionTasksAsync(executionId, cancellationToken);
        var results = tasks.OrderBy(t => t.ItemIndex).Select(t => new
        {
            index = t.ItemIndex,
            item = t.ItemValue,
            userTaskId = t.Id,
            status = t.Status,
            selectedFlowId = t.SelectedFlowId,
            completedBy = t.CompletedBy,
            completedAt = t.CompletedAt,
            variables = t.Result
        });
        return JsonSerializer.SerializeToElement(results);
    }

    private static void AddMultiInstanceContext(
        Dictionary<string, JsonElement> context,
        UserTaskRecord task,
        MultiInstanceExecutionRecord execution)
    {
        AddMultiInstanceExecutionContext(context, execution);
        context["mi.index"] = JsonSerializer.SerializeToElement(task.ItemIndex ?? 0);
        context["mi.item"] = task.ItemValue?.Clone() ?? JsonSerializer.SerializeToElement<object?>(null);
    }

    private static void AddMultiInstanceExecutionContext(
        Dictionary<string, JsonElement> context,
        MultiInstanceExecutionRecord execution)
    {
        context["mi.total"] = JsonSerializer.SerializeToElement(execution.TotalCount);
        context["mi.completed"] = JsonSerializer.SerializeToElement(execution.CompletedCount);
        context["mi.remaining"] = JsonSerializer.SerializeToElement(
            execution.TotalCount - execution.CompletedCount - execution.CancelledCount);
    }

    private static bool CanUserTaskActor(UserTaskRecord task, FlowNodeModel node, ActorContext actor)
    {
        var user = NormalizeUser(actor.User);
        return BpmnFlowNodeTypes.IsUserTask(node.Type)
               && (task.Assignee is null
                   || string.Equals(task.Assignee, user, StringComparison.OrdinalIgnoreCase))
               && RoleAllowed(node, NormalizeRoles(actor.Roles));
    }

    private static void EnsureUserTaskActor(
        UserTaskRecord task,
        FlowNodeModel node,
        ActorContext actor,
        bool requireActive)
    {
        if (requireActive && task.Status != UserTaskRecordStatuses.Active)
            throw new WorkflowConflictException("The user task is no longer active.");
        if (!CanUserTaskActor(task, node, actor))
            throw new WorkflowDomainException("The actor is not assigned or authorized for this user task.");
    }

    private CurrentNodeSnapshot ToSnapshot(
        FlowNodeModel node,
        IReadOnlyDictionary<string, JsonElement>? assigneeContext = null,
        long? instanceId = null)
    {
        string? assignee = null;
        if (BpmnFlowNodeTypes.IsUserTask(node.Type)
            && node.MultiInstance is null
            && !string.IsNullOrWhiteSpace(node.AssigneeExpression))
        {
            try
            {
                var raw = assigneeContext is null
                    ? null
                    : SequenceFlowConditionEvaluator.EvaluateValue(
                        node.AssigneeExpression, assigneeContext, preserveComplexTypes: true);
                if (raw is string value)
                {
                    var trimmed = value.Trim();
                    if (trimmed.Length is > 0 and <= UserTaskConstraints.MaxActorNameLength)
                    {
                        assignee = trimmed;
                    }
                }

                if (assignee is null)
                {
                    logger.LogWarning(
                        "User task #{NodeId} assignee expression '{Expression}' did not resolve to a non-empty string of at most {MaxLength} characters for instance {InstanceId}; creating a shared-pool task.",
                        node.Id, node.AssigneeExpression, UserTaskConstraints.MaxActorNameLength, instanceId);
                }
            }
            catch (WorkflowDomainException ex)
            {
                logger.LogWarning(ex,
                    "User task #{NodeId} assignee expression '{Expression}' failed for instance {InstanceId}; creating a shared-pool task.",
                    node.Id, node.AssigneeExpression, instanceId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "User task #{NodeId} assignee expression '{Expression}' failed unexpectedly for instance {InstanceId}; creating a shared-pool task.",
                    node.Id, node.AssigneeExpression, instanceId);
            }
        }

        return new CurrentNodeSnapshot(
            node.Id,
            node.Name,
            node.ExternalId,
            node.Type,
            node.Roles,
            node.RequiresClaim,
            assignee,
            node.MultiInstance is not null);
    }

    private static FlowNodeModel GetFlowNode(WorkflowModel definition, int nodeId) =>
        definition.FlowNodes.SingleOrDefault(n => n.Id == nodeId)
        ?? throw new WorkflowDomainException($"Flow node #{nodeId} was not found in workflow '{definition.Name}'.");

    private static IReadOnlyList<SequenceFlowModel> OutgoingFlows(WorkflowModel definition, int nodeId) =>
        definition.SequenceFlows.Where(f => f.SourceRef == nodeId).ToList();

    // True when a userTask has any role-restricted selectable outgoing flow.
    // The inbox uses this to refine CanAct/CanClaim for the current SQL page.
    private static bool HasRoleRestrictedFlows(FlowNodeModel node, WorkflowModel definition) =>
        BpmnFlowNodeTypes.IsUserTask(node.Type)
        && OutgoingFlows(definition, node.Id).Any(f => f.IsSelectable && !f.IsDefault
                                                       && f.Roles is { Count: > 0 });

    // True when the actor can take at least one outgoing flow of a userTask: a
    // flow whose roles the actor holds (empty/null roles = open to anyone). The
    // node's own roles and the claim are checked elsewhere; this only reflects
    // flow-level role gating.
    private static bool CanTakeAnyFlow(FlowNodeModel node, WorkflowModel definition, IReadOnlySet<string> actorRoles) =>
        OutgoingFlows(definition, node.Id).Any(f => f.IsSelectable && !f.IsDefault
                                                    && RoleAllowed(f.Roles, actorRoles));

    private static IReadOnlyList<SequenceFlowModel> IncomingFlows(WorkflowModel definition, int nodeId) =>
        definition.SequenceFlows.Where(f => f.TargetRef == nodeId).ToList();

    // Resolves the errorBoundaryEvent attached to a host activity, or null when
    // none is attached. ValidateDefinition enforces at most one boundary per host;
    // FirstOrDefault keeps this defensive against a hand-seeded definition that
    // somehow violates that invariant (avoids an uncaught InvalidOperationException).
    private static FlowNodeModel? FindErrorBoundary(WorkflowModel definition, int hostNodeId) =>
        definition.FlowNodes.FirstOrDefault(n =>
            BpmnFlowNodeTypes.IsErrorBoundary(n.Type) && n.AttachedToRef == hostNodeId);

    private static void EnsureActionAllowedByClaim(
        UserTaskRecord task,
        SequenceFlowModel flow,
        string? performedBy)
    {
        if (!task.RequiresClaim || flow.CanActWithoutClaim)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(task.ClaimedBy))
        {
            throw new WorkflowDomainException("The current flow node must be claimed before taking a sequence flow.");
        }

        if (!string.Equals(task.ClaimedBy, NormalizeUser(performedBy), StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkflowDomainException($"Only '{task.ClaimedBy}' can act on this flow node.");
        }
    }

    private static bool CanTakeAnyBypassClaimFlow(FlowNodeModel node, WorkflowModel definition, IReadOnlySet<string> actorRoles) =>
        BpmnFlowNodeTypes.IsUserTask(node.Type)
        && OutgoingFlows(definition, node.Id).Any(f => f.IsSelectable && !f.IsDefault
                                                       && f.CanActWithoutClaim
                                                       && RoleAllowed(f.Roles, actorRoles));

    private static string NormalizeUser(string? user) =>
        string.IsNullOrWhiteSpace(user) ? "anonymous" : user.Trim();

    private static HashSet<string> NormalizeRoles(IReadOnlyCollection<string> roles) =>
        roles
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // Candidate roles; an empty (or null) set means open to anyone. Enforced for
    // user-task nodes and for user-task sequence flows (the actor must hold at
    // least one listed role, case-insensitive). Null-tolerant so a hand-authored
    // definition with explicit "roles": null degrades to "open" instead of NRE.
    private static bool RoleAllowed(IReadOnlyCollection<string>? allowedRoles, IReadOnlySet<string> actorRoles) =>
        allowedRoles is null || allowedRoles.Count == 0 || allowedRoles.Any(actorRoles.Contains);

    private static bool RoleAllowed(FlowNodeModel node, IReadOnlySet<string> roles) =>
        RoleAllowed(node.Roles, roles);

    private static void EnsureRoleAllowed(FlowNodeModel node, ActorContext actor) =>
        EnsureRoleAllowed(node, NormalizeRoles(actor.Roles), actor.User);

    private static void EnsureRoleAllowed(FlowNodeModel node, IReadOnlySet<string> actorRoles, string? actorUser)
    {
        if (!RoleAllowed(node, actorRoles))
        {
            throw new WorkflowDomainException(
                $"'{NormalizeUser(actorUser)}' does not have a role permitted to act on this flow node.");
        }
    }

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

    private static IdempotencyStartInput? ResolveIdempotencyInput(
        FlowNodeModel entry,
        IReadOnlyDictionary<string, IReadOnlyList<string>> headers)
    {
        var configuration = entry.Idempotency;
        if (configuration is null)
        {
            return null;
        }

        var headerName = configuration.HeaderName.Trim();
        var variable = configuration.Variable.Trim();
        string value;

        if (string.Equals(headerName, IdempotencyHeaders.Standard, StringComparison.OrdinalIgnoreCase))
        {
            var hasStandard = headers.TryGetValue(IdempotencyHeaders.Standard, out var standardValues);
            var hasAlias = headers.TryGetValue(IdempotencyHeaders.LegacyAlias, out var aliasValues);
            if (!hasStandard && !hasAlias)
            {
                throw new WorkflowDomainException(
                    $"Entry event #{entry.Id} requires header '{IdempotencyHeaders.Standard}'.");
            }

            var standardValue = hasStandard
                ? ReadSingleIdempotencyHeader(entry.Id, IdempotencyHeaders.Standard, standardValues!)
                : null;
            var aliasValue = hasAlias
                ? ReadSingleIdempotencyHeader(entry.Id, IdempotencyHeaders.LegacyAlias, aliasValues!)
                : null;
            if (standardValue is not null && aliasValue is not null
                && !string.Equals(standardValue, aliasValue, StringComparison.Ordinal))
            {
                throw new WorkflowDomainException(
                    $"Entry event #{entry.Id} received conflicting idempotency header values.");
            }

            value = standardValue ?? aliasValue!;
        }
        else
        {
            if (!headers.TryGetValue(headerName, out var configuredValues))
            {
                throw new WorkflowDomainException(
                    $"Entry event #{entry.Id} requires header '{headerName}'.");
            }

            value = ReadSingleIdempotencyHeader(entry.Id, headerName, configuredValues);
        }

        if (value.EnumerateRunes().Count() > 300)
        {
            throw new WorkflowDomainException("Idempotency key must not exceed 300 characters.");
        }

        return new IdempotencyStartInput(
            headerName,
            variable,
            value,
            JsonSerializer.SerializeToElement(value));
    }

    private static string ReadSingleIdempotencyHeader(
        int entryId,
        string headerName,
        IReadOnlyList<string> values)
    {
        if (values.Count != 1)
        {
            throw new WorkflowDomainException(
                $"Entry event #{entryId} header '{headerName}' must contain exactly one value.");
        }

        var value = values[0].Trim();
        if (value.Length == 0)
        {
            throw new WorkflowDomainException(
                $"Entry event #{entryId} header '{headerName}' must not be blank.");
        }

        return value;
    }

    private sealed record IdempotencyStartInput(
        string HeaderName,
        string Variable,
        string Key,
        JsonElement Value);

    private sealed record BusinessKeyStartInput(
        Dictionary<string, JsonElement>? Values,
        string? BusinessKey,
        string? Uniqueness);

    private sealed record TypedOutputRuntime(
        string Variable,
        string Path,
        bool Required,
        string DataType,
        bool IsArray,
        JsonElement? DefaultValue,
        string? Validation);

    // Outcome of executing a serviceTask/scriptTask in the pass-through loop. On
    // Success the loop advances down the node's single outgoing flow. On Failure
    // the loop looks up an attached errorBoundaryEvent: if found the token routes
    // out the boundary's error flow; otherwise the loop throws (rollback + 400),
    // matching the historical no-boundary default (onError:fail).
    private sealed record TaskExecutionOutcome(bool Success, string? Reason)
    {
        public static TaskExecutionOutcome Ok() => new(true, null);
        public static TaskExecutionOutcome Fail(string reason) => new(false, reason);
    }
}
