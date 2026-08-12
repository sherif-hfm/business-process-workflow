using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

public sealed partial class WorkflowEngineService(
    IWorkflowDefinitionRepository definitions,
    IWorkflowRuntimeRepository runtime,
    IWorkflowJobRepository jobs,
    ITimerSubscriptionRepository timerSubscriptions,
    IUserDelegationRepository delegations,
    IUnitOfWork unitOfWork,
    IServiceTaskInvoker serviceTaskInvoker,
    IScriptEvaluator scriptEvaluator,
    WorkflowContextOptions contextOptions,
    TimeProvider timeProvider,
    IWorkflowSettingsRepository settings,
    IEngineSettingsRepository engineSettings,
    ILogger<WorkflowEngineService> logger,
    IInstanceVariableUpdateRepository? variableUpdates = null)
    : IWorkflowEngineService, IWorkflowJobProcessor,
      IInstanceVersionChangeBatchExecutor
{
    private const string InstanceListRequiredRoleSettingKey =
        "WorkflowInstances.RequiredRole";
    private const string DefaultInstanceListRequiredRole = "admin";
    private static readonly JsonSerializerOptions InstanceVariableUpdateJsonOptions =
        new(JsonSerializerDefaults.Web);
    private Dictionary<string, JsonElement>? _settingsCache;

    private async Task LoadSettingsAsync(CancellationToken cancellationToken)
    {
        if (_settingsCache is not null)
        {
            return;
        }

        _settingsCache = (Dictionary<string, JsonElement>)await settings.LoadAllAsync(cancellationToken);
    }

    private async Task RefreshSettingsAsync(CancellationToken cancellationToken)
    {
        var freshSettings = await settings.LoadAllFreshAsync(cancellationToken);
        _settingsCache = freshSettings.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Clone(),
            StringComparer.OrdinalIgnoreCase);
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
        var projection = await BuildExecutionProjectionAsync(instance, cancellationToken);
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
            instance.UpdatedAt,
            ToFault(instance.Status, instance.FaultCode, instance.FaultDescription, node.Name))
        {
            ExecutionPositions = projection.ExecutionPositions,
            Completion = projection.Completion
        };
    }

    private async Task<(WorkflowInstanceRecord Instance, WorkflowModel Definition)> StartInstanceCoreAsync(
        long? workflowId,
        string? workflowKey,
        ActorContext actor,
        int? startEventId,
        Dictionary<string, JsonElement>? variableValues,
        IReadOnlyDictionary<string, IReadOnlyList<string>> requestHeaders,
        CancellationToken cancellationToken,
        bool allowTimerStart = false,
        bool joinAmbientTransaction = false,
        bool forceDurableActivities = false,
        long? requiredDefaultWorkflowId = null)
    {
        await LoadSettingsAsync(cancellationToken);
        var startedBy = actor.User;

        var hasWorkflowId = workflowId.HasValue;
        var hasWorkflowKey = !string.IsNullOrWhiteSpace(workflowKey);
        if (hasWorkflowId == hasWorkflowKey)
        {
            logger.LogWarning(
                "Start instance rejected: exactly one of WorkflowId or WorkflowKey must be specified.");
            throw new WorkflowDomainException(
                "Exactly one of WorkflowId or WorkflowKey must be specified to start an instance.");
        }
        
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

        IWorkflowTransaction? ownedTransaction = null;
        if (!joinAmbientTransaction)
        {
            ownedTransaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        }

        try
        {
            await definitions.LockFamilyForStartAsync(workflow.WorkflowKey, cancellationToken);
            workflow = workflowId.HasValue
                ? await GetPublishedWorkflowAsync(workflowId.Value, cancellationToken)
                : await definitions.GetDefaultByWorkflowKeyAsync(workflowKey!, cancellationToken)
                    ?? throw new WorkflowDomainException($"No default workflow found for workflowKey '{workflowKey}'.");
            if (requiredDefaultWorkflowId is long requiredId
                && (workflow.Id != requiredId
                    || !workflow.IsPublished
                    || !workflow.IsDefault))
            {
                throw new WorkflowConflictException(
                    "The timer-start definition is no longer the published default.");
            }

            await EnsureBusinessKeyFamilyStartableAsync(workflow, cancellationToken);
            await EnsureRequiredAssignmentFamilyStartableAsync(workflow, cancellationToken);

            logger.LogDebug("Starting workflow instance for definition {WorkflowKey} (ID: {WorkflowId}) by user {User}", workflowKey ?? workflow.WorkflowKey.ToString(), workflow.Id, startedBy ?? "anonymous");

            var resolvedStartEventId = startEventId ?? workflow.Definition.InitialEventId
                ?? throw new WorkflowDomainException("Workflow has no default start event.");

            var startEvent = GetFlowNode(workflow.Definition, resolvedStartEventId);
            if (!BpmnFlowNodeTypes.IsStart(startEvent.Type)
                && !(allowTimerStart && BpmnFlowNodeTypes.IsTimerStart(startEvent.Type)))
            {
                logger.LogWarning("Start instance rejected: flow node #{NodeId} is not a start event.", resolvedStartEventId);
                throw new WorkflowDomainException($"Flow node #{resolvedStartEventId} is not a start event.");
            }

            EnsureEntryRuntimeContract(workflow.Definition, startEvent);

            EnsureRoleAllowed(startEvent, actor);

            var idempotency = ResolveIdempotencyInput(startEvent, requestHeaders);

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
                SnapshotRoles(actor.Roles),
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
                    cancellationToken,
                    instance.CurrentNodeExecutionId);
            }
            var startValues = ResolveAndValidateVariables(startEvent.Variables, variableValues, startContext);
            foreach (var pair in startValues)
            {
                await runtime.AddVariableAsync(
                    instance.Id,
                    pair.Key,
                    null,
                    startedBy,
                    pair.Value,
                    cancellationToken,
                    instance.CurrentNodeExecutionId);
            }

            // Initialize process-level variables from their authored defaults so every
            // declared name is readable from hop 0. Defaults are templated/coerced like
            // start-variable defaults and validated against the start values + context.
            var processContext = new Dictionary<string, JsonElement>(startContext, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in startValues)
            {
                processContext[pair.Key] = pair.Value;
            }
            var processValues = ResolveAndValidateVariables(
                workflow.Definition.Variables,
                null,
                processContext,
                enforceRequired: false,
                materializeNullableNullDefaults: true);
            foreach (var pair in processValues)
            {
                await runtime.AddVariableAsync(
                    instance.Id,
                    pair.Key,
                    null,
                    startedBy,
                    pair.Value,
                    cancellationToken,
                    instance.CurrentNodeExecutionId);
            }

            // Flush variables so pass-through gateways can read them within this transaction.
            await unitOfWork.SaveChangesAsync(cancellationToken);
            var flowInfo = await LoadSequenceFlowInfoAsync(
                instance.Id, workflow.Definition, cancellationToken);
            instance = await ResolvePassThroughAsync(
                instance,
                workflow.Definition,
                actor,
                flowInfo,
                instance.ActiveTokenId,
                cancellationToken,
                forceDurableActivities: forceDurableActivities);
            await EnsureMultiInstanceInitializedAsync(instance, workflow.Definition, actor, cancellationToken);
            instance = await ApplyUserTaskOwnershipInheritanceAsync(instance, workflow.Definition, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            if (ownedTransaction is not null)
            {
                await ownedTransaction.CommitAsync(cancellationToken);
            }

            var restingNode = GetFlowNode(workflow.Definition, instance.CurrentStepId);
            logger.LogDebug("Successfully started workflow instance {InstanceId} resting on step {CurrentStepId} ({CurrentStepType})", instance.Id, instance.CurrentStepId, restingNode.Type);

            return (instance, workflow.Definition);
        }
        finally
        {
            if (ownedTransaction is not null)
            {
                await ownedTransaction.DisposeAsync();
            }
        }
    }

    public async Task<MessageStartAckDto> StartByMessageAsync(
        string workflowKey,
        string? startEventExternalId,
        IncomingMessage message,
        CancellationToken cancellationToken)
    {
        // Resolve and authenticate a preview before taking the family lock so an
        // unauthenticated caller cannot serialize legitimate starts. The locked,
        // authoritative definition is authenticated again below to cover a
        // concurrent default-version or credential change.
        var workflow = await definitions.GetDefaultByWorkflowKeyAsync(workflowKey, cancellationToken)
            ?? throw new WorkflowDomainException($"No default workflow found for workflowKey {workflowKey}.");
        var definition = workflow.Definition;
        var startEvent = SelectMessageStartEvent(definition, startEventExternalId);
        EnsureEntryRuntimeContract(definition, startEvent);
        _ = await AuthenticateMessageStartAsync(
            workflowKey,
            definition,
            startEvent,
            message,
            cancellationToken);

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await definitions.LockFamilyForStartAsync(workflow.WorkflowKey, cancellationToken);
        workflow = await definitions.GetDefaultByWorkflowKeyAsync(workflowKey, cancellationToken)
            ?? throw new WorkflowDomainException($"No default workflow found for workflowKey {workflowKey}.");
        definition = workflow.Definition;
        startEvent = SelectMessageStartEvent(definition, startEventExternalId);
        EnsureEntryRuntimeContract(definition, startEvent);

        var authentication = await AuthenticateMessageStartAsync(
            workflowKey,
            definition,
            startEvent,
            message,
            cancellationToken);
        await EnsureBusinessKeyFamilyStartableAsync(workflow, cancellationToken);
        await EnsureRequiredAssignmentFamilyStartableAsync(workflow, cancellationToken);

        logger.LogInformation("Starting workflow instance by message on workflowKey {WorkflowKey} using start node #{StartNodeId} ({StartNodeName})",
            workflowKey, startEvent.Id, startEvent.Name);

        var messageConfig = startEvent.Message
            ?? throw new WorkflowDomainException($"Message start event #{startEvent.Id} has no message configuration.");

        var actor = authentication.Actor;
        var performedBy = actor.User;
        var authContext = authentication.AuthContext;

        var idempotency = ResolveIdempotencyInput(startEvent, message.Headers);

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
            SnapshotRoles(actor.Roles),
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
            await runtime.AddVariableAsync(
                instance.Id,
                pair.Key,
                null,
                performedBy,
                pair.Value,
                cancellationToken,
                instance.CurrentNodeExecutionId);
        }

        // Initialize process-level variables from their authored defaults.
        var processContext = new Dictionary<string, JsonElement>(startContext, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in mappedValues)
        {
            processContext[pair.Key] = pair.Value;
        }
        var processValues = ResolveAndValidateVariables(
            definition.Variables,
            null,
            processContext,
            enforceRequired: false,
            materializeNullableNullDefaults: true);
        foreach (var pair in processValues)
        {
            await runtime.AddVariableAsync(
                instance.Id,
                pair.Key,
                null,
                performedBy,
                pair.Value,
                cancellationToken,
                instance.CurrentNodeExecutionId);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        var flowInfo = await LoadSequenceFlowInfoAsync(
            instance.Id, definition, cancellationToken);
        instance = await ResolvePassThroughAsync(
            instance, definition, actor, flowInfo, cancellationToken);
        await EnsureMultiInstanceInitializedAsync(instance, definition, actor, cancellationToken);
        instance = await ApplyUserTaskOwnershipInheritanceAsync(instance, definition, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        var ack = await BuildStartAckAsync(instance, definition, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Successfully started workflow instance {InstanceId} by message correlation. Status: {Status}, resting on node {CurrentStepId}",
            instance.Id, instance.Status, instance.CurrentStepId);

        return ack;
    }

    // Builds the slim start ack: only the resting node identity + status, no
    // definition/variables/history (the message-start endpoint is AllowAnonymous).
    private async Task<MessageStartAckDto> BuildStartAckAsync(
        WorkflowInstanceRecord instance,
        WorkflowModel definition,
        CancellationToken cancellationToken)
    {
        var node = GetFlowNode(definition, instance.CurrentStepId);
        var projection = await BuildExecutionProjectionAsync(instance, cancellationToken);
        return new MessageStartAckDto(
            instance.Id,
            node.Id,
            node.Name,
            node.ExternalId,
            instance.Status,
            instance.CreatedAt,
            ToFault(instance.Status, instance.FaultCode, instance.FaultDescription, node.Name))
        {
            ExecutionPositions = projection.ExecutionPositions,
            Completion = projection.Completion
        };
    }

    private static FlowNodeModel SelectMessageStartEvent(
        WorkflowModel definition,
        string? externalId)
    {
        var startEvents = definition.FlowNodes
            .Where(node => BpmnFlowNodeTypes.IsMessageStart(node.Type))
            .ToList();
        if (string.IsNullOrWhiteSpace(externalId))
        {
            return startEvents.Count switch
            {
                0 => throw new WorkflowDomainException(
                    $"Workflow '{definition.Name}' has no message start event."),
                1 => startEvents[0],
                _ => throw new WorkflowDomainException(
                    $"Workflow '{definition.Name}' has multiple message start events; specify one via the 'startEvent' query parameter (its externalId).")
            };
        }

        var matches = startEvents
            .Where(node => string.Equals(node.ExternalId, externalId, StringComparison.Ordinal))
            .Take(2)
            .ToList();
        return matches.Count switch
        {
            1 => matches[0],
            > 1 => throw new WorkflowDomainException(
                $"Workflow '{definition.Name}' has multiple message start events with externalId '{externalId}'."),
            _ => throw new WorkflowDomainException(
                $"No message start event with externalId '{externalId}' was found in workflow '{definition.Name}'.")
        };
    }

    public Task<PagedResult<InstanceSummaryDto>> ListInstancesAsync(
        ActorContext actor,
        string? status,
        long? instanceId,
        long? workflowId,
        string? workflowKey,
        string? businessKey,
        int? nodeId,
        string? nodeExternalId,
        IReadOnlyList<string>? variables,
        IReadOnlyList<string>? sort,
        string? cursor,
        bool includeVariables,
        int page,
        int pageSize,
        CancellationToken cancellationToken) =>
        ListInstancesCoreAsync(
            actor, status, instanceId, workflowId, workflowKey, businessKey,
            nodeId, nodeExternalId,
            VariableFilterParser.FromLegacy(ParseVariableFilters(variables)),
            ParseInstanceSort(sort), cursor, includeVariables, page, pageSize,
            cancellationToken);

    public Task<PagedResult<InstanceSummaryDto>> SearchInstancesAsync(
        ActorContext actor,
        InstanceSearchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ListInstancesCoreAsync(
            actor,
            request.Status,
            request.InstanceId,
            request.WorkflowId,
            request.WorkflowKey,
            request.BusinessKey,
            request.NodeId,
            request.NodeExternalId,
            VariableFilterParser.Parse(request.VariableFilter),
            ParseInstanceSort(ToLegacySort(request.Sort)),
            request.Cursor,
            request.IncludeVariables ?? false,
            Math.Max(1, request.Page ?? 1),
            Math.Clamp(request.PageSize ?? 50, 1, 200),
            cancellationToken);
    }

    private async Task<PagedResult<InstanceSummaryDto>> ListInstancesCoreAsync(
        ActorContext actor,
        string? status,
        long? instanceId,
        long? workflowId,
        string? workflowKey,
        string? businessKey,
        int? nodeId,
        string? nodeExternalId,
        VariableFilterExpression? variableFilter,
        IReadOnlyList<InstanceSortCriterion> sortCriteria,
        string? cursor,
        bool includeVariables,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var normalizedSort = WorkflowInstanceCursor.NormalizeSort(sortCriteria);
        if (!string.IsNullOrWhiteSpace(cursor)
            && !WorkflowInstanceCursor.TryDecode(
                cursor,
                normalizedSort,
                out _))
        {
            throw new WorkflowDomainException(
                "The instance cursor is invalid, expired, or belongs to a different sort order.");
        }
        if (string.IsNullOrWhiteSpace(cursor) && page > 1)
        {
            throw new WorkflowDomainException(
                "Instance pages after the first require the opaque cursor returned by the preceding page.");
        }

        var authorization = await ResolveInstanceListAuthorizationAsync(
            actor,
            cancellationToken);
        var paged = await runtime.ListInstancesAsync(
            status,
            instanceId,
            workflowId,
            workflowKey,
            businessKey,
            nodeId,
            nodeExternalId,
            variableFilter,
            normalizedSort,
            authorization,
            cursor,
            includeVariables,
            page,
            pageSize,
            cancellationToken);
        var jobSummaries = await jobs.GetInstanceJobSummariesAsync(
            paged.Items.Select(item => item.Id).ToArray(),
            cancellationToken);
        var items = paged.Items.Select(row =>
        {
            var summary = ToSummary(row);
            return jobSummaries.TryGetValue(row.Id, out var jobsForInstance)
                ? summary with
                {
                    Jobs = new InstanceJobSummaryDto(
                        jobsForInstance.OpenCount,
                        jobsForInstance.QueuedCount,
                        jobsForInstance.RunningCount,
                        jobsForInstance.IncidentCount,
                        jobsForInstance.NearestDueAt)
                }
                : summary;
        }).ToArray();
        return new PagedResult<InstanceSummaryDto>(
            items,
            paged.Page,
            paged.PageSize,
            paged.TotalCount)
        {
            NextCursor = paged.NextCursor
        };
    }

    private async Task<InstanceListAuthorization> ResolveInstanceListAuthorizationAsync(
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        var setting = await engineSettings.GetByKeyAsync(
            InstanceListRequiredRoleSettingKey,
            cancellationToken);
        var configuredGlobalRoles = string.IsNullOrWhiteSpace(setting?.Value)
            ? [DefaultInstanceListRequiredRole]
            : setting.Value
                .Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries)
                .Where(static role => role.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        if (configuredGlobalRoles.Length == 0)
        {
            configuredGlobalRoles = [DefaultInstanceListRequiredRole];
        }

        var lowerCallerRoles = actor.Roles
            .Where(static role => !string.IsNullOrWhiteSpace(role))
            .Select(static role => role.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var isGlobalReader = configuredGlobalRoles
            .Select(static role => role.ToLowerInvariant())
            .Intersect(lowerCallerRoles, StringComparer.Ordinal)
            .Any();
        return new InstanceListAuthorization(isGlobalReader, lowerCallerRoles);
    }

    public Task<PagedResult<InboxItemDto>> GetInboxAsync(
        ActorContext actor,
        long? instanceId,
        long? workflowId,
        string? workflowKey,
        string? businessKey,
        int? nodeId,
        string? nodeExternalId,
        IReadOnlyList<string>? variables,
        IReadOnlyList<string>? sort,
        bool includeVariables,
        int page,
        int pageSize,
        CancellationToken cancellationToken) =>
        GetInboxCoreAsync(
            actor, instanceId, workflowId, workflowKey, businessKey, nodeId,
            nodeExternalId,
            VariableFilterParser.FromLegacy(ParseVariableFilters(variables)),
            ParseInboxSort(sort), includeVariables, page, pageSize,
            cancellationToken);

    public Task<PagedResult<InboxItemDto>> SearchInboxAsync(
        ActorContext actor,
        InboxSearchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return GetInboxCoreAsync(
            actor,
            request.InstanceId,
            request.WorkflowId,
            request.WorkflowKey,
            request.BusinessKey,
            request.NodeId,
            request.NodeExternalId,
            VariableFilterParser.Parse(request.VariableFilter),
            ParseInboxSort(ToLegacySort(request.Sort)),
            request.IncludeVariables ?? false,
            Math.Max(1, request.Page ?? 1),
            Math.Clamp(request.PageSize ?? 50, 1, 200),
            cancellationToken);
    }

    private async Task<PagedResult<InboxItemDto>> GetInboxCoreAsync(
        ActorContext actor,
        long? instanceId,
        long? workflowId,
        string? workflowKey,
        string? businessKey,
        int? nodeId,
        string? nodeExternalId,
        VariableFilterExpression? variableFilter,
        IReadOnlyList<InboxSortCriterion> sortCriteria,
        bool includeVariables,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        await LoadSettingsAsync(cancellationToken);
        var visibilityContext = CreateInboxVisibilityContext(actor);
        var normalizedUser = visibilityContext.User;
        var normalizedRoles = NormalizeRoles(visibilityContext.Roles);
        var paged = await runtime.ListInboxAsync(
            visibilityContext,
            instanceId, workflowId, workflowKey, businessKey, nodeId,
            nodeExternalId, variableFilter, sortCriteria, page, pageSize, cancellationToken);

        if (paged.Items.Count == 0)
        {
            return new PagedResult<InboxItemDto>([], paged.Page, paged.PageSize, paged.TotalCount);
        }

        var definitionIds = paged.Items.Select(c => c.WorkflowDefinitionId).Distinct().ToList();
        var definitionsById = await definitions.GetManyAsync(definitionIds, cancellationToken);
        foreach (var id in definitionIds)
        {
            if (!definitionsById.ContainsKey(id))
            {
                throw new WorkflowDomainException($"Workflow definition #{id} was not found.");
            }
        }

        var canActByTask = new Dictionary<long, bool>();
        var hasBypassClaimByTask = new Dictionary<long, bool>();
        var accessByTask = new Dictionary<long, ResolvedUserTaskAccess>();
        var attributesByTask = new Dictionary<long, IReadOnlyList<WorkflowAttributeModel>>();
        foreach (var row in paged.Items)
        {
            var taskKey = InboxAuthorizationKey(row);
            var workflow = definitionsById[row.WorkflowDefinitionId];
            var node = GetFlowNode(workflow.Definition, row.CurrentNodeId);
            attributesByTask[taskKey] = NodeAttributes(workflow, row.CurrentNodeId);
            var task = ToInboxUserTaskRecord(row);
            var access = ResolveProjectedUserTaskAccess(
                task, actor, row.ActingFor, row.DelegationId);
            accessByTask[taskKey] = access;
            var instance = ToInboxInstanceRecord(row, workflow);
            var execution = row.MultiInstanceProgress?.Execution;
            var eligible = GetEligibleUserTaskFlows(
                instance, workflow, node, task, execution, access.ExecutionActor,
                row.Variables ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase),
                visibilityContext.AsOf);
            canActByTask[taskKey] = eligible.Count > 0;
            hasBypassClaimByTask[taskKey] = eligible.Any(flow => CanBypassClaim(flow, normalizedRoles));
        }

        var progressByExecution = paged.Items
            .Where(row => row.MultiInstanceProgress is not null)
            .GroupBy(row => row.MultiInstanceProgress!.Execution.Id)
            .ToDictionary(group => group.Key, group => ToProgress(group.First().MultiInstanceProgress!));
        var items = paged.Items.Select(row => ToInboxItem(row, normalizedUser, normalizedRoles,
            accessByTask, attributesByTask, canActByTask, hasBypassClaimByTask,
            row.MultiInstanceExecutionId is long executionId ? progressByExecution.GetValueOrDefault(executionId) : null,
            includeVariables)).ToList();
        return new PagedResult<InboxItemDto>(items, paged.Page, paged.PageSize, paged.TotalCount);
    }

    private static long InboxAuthorizationKey(InboxListItem row) => row.UserTaskId;

    private static WorkflowInstanceRecord ToInboxInstanceRecord(
        InboxListItem row,
        WorkflowDefinitionRecord workflow) =>
        new(row.InstanceId, row.WorkflowDefinitionId, workflow.WorkflowKey, null, row.BusinessKey,
            row.BusinessKeyUniqueness, row.TokenId, row.CurrentNodeId, row.UserTaskId,
            row.Status, row.ClaimedBy, row.StartedBy, row.InstanceCreatedAt, row.InstanceUpdatedAt);

    private static UserTaskRecord ToInboxUserTaskRecord(InboxListItem row) =>
        new(row.UserTaskId, row.InstanceId, row.TokenId, row.CurrentNodeId, row.CurrentNodeName,
            row.CurrentNodeExternalId, row.CurrentNodeRoles, row.CurrentRequiresClaim,
            row.CurrentRequiresAssignment,
            UserTaskRecordStatuses.Active, row.ClaimedBy, row.MultiInstanceExecutionId,
            row.ItemIndex, row.ItemValue, row.Assignee, null, null, null, null,
            row.TaskCreatedAt, row.TaskUpdatedAt, null);

    private static InboxItemDto ToInboxItem(
        InboxListItem row,
        string normalizedUser,
        IReadOnlySet<string> normalizedRoles,
        IReadOnlyDictionary<long, ResolvedUserTaskAccess> accessByTask,
        IReadOnlyDictionary<long, IReadOnlyList<WorkflowAttributeModel>> attributesByTask,
        Dictionary<long, bool>? canActByTask = null,
        Dictionary<long, bool>? hasBypassClaimByTask = null,
        MultiInstanceProgressDto? multiInstance = null,
        bool includeVariables = false)
    {
        var claimedByMe = string.Equals(row.ClaimedBy, normalizedUser, StringComparison.OrdinalIgnoreCase);
        var authorizationKey = InboxAuthorizationKey(row);
        var access = accessByTask[authorizationKey];
        var claimedByRepresentedOwner = string.Equals(
            row.ClaimedBy, access.RepresentedOwner, StringComparison.OrdinalIgnoreCase);
        var claimedByOther = !string.IsNullOrWhiteSpace(row.ClaimedBy) && !claimedByRepresentedOwner;
        var roleMatch = row.CurrentNodeRoles.Count == 0
            || row.CurrentNodeRoles.Any(normalizedRoles.Contains);

        var canClaim = access.DelegationId is null
                       && row.CurrentRequiresClaim
                       && !claimedByMe
                       && !claimedByOther
                       && roleMatch;
        var canAct = claimedByRepresentedOwner || (!row.CurrentRequiresClaim && roleMatch);
        if (row.CurrentRequiresAssignment && row.Assignee is null)
        {
            canClaim = false;
            canAct = false;
        }

        // If the task has a bypass-claim flow and the user has the role to take it,
        // they can act directly on it even if it requires a claim and is unclaimed
        // (or claimed by someone else).
        if (hasBypassClaimByTask is not null && hasBypassClaimByTask.TryGetValue(authorizationKey, out var hasBypass) && hasBypass)
        {
            if (roleMatch)
            {
                canAct = true;
            }
        }

        // Refine the flags with flow roles and stored-state conditions. A task
        // with no currently visible action remains listed but cannot be claimed
        // or acted on until its stored state makes an action visible.
        if (canActByTask is not null && canActByTask.TryGetValue(authorizationKey, out var canTakeAny))
        {
            if (!canTakeAny)
            {
                canAct = false;
                canClaim = false;
            }
        }

        return new InboxItemDto(
            row.InstanceId,
            row.UserTaskId,
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
            row.CurrentRequiresAssignment,
            row.ClaimedBy,
            claimedByMe,
            canClaim,
            canAct,
            row.TaskCreatedAt,
            row.TaskUpdatedAt,
            row.TaskCreatedAt,
            row.TaskUpdatedAt,
            row.InstanceCreatedAt,
            row.InstanceUpdatedAt)
        {
            Variables = includeVariables ? row.Variables : null,
            DelegatedAccess = access.ToDto(),
            Attributes = attributesByTask[authorizationKey]
        };
    }

    public Task<InstanceDetailDto?> GetInstanceAsync(long id, CancellationToken cancellationToken) =>
        BuildDetailAsync(id, cancellationToken);

    public async Task<IReadOnlyList<SequenceFlowModel>?> GetAvailableFlowsAsync(
        long id,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        await LoadSettingsAsync(cancellationToken);
        var visibilityContext = CreateInboxVisibilityContext(actor);
        var instance = await runtime.GetInstanceAsync(id, cancellationToken);
        if (instance is null || instance.Status != WorkflowInstanceStatuses.Running)
        {
            return [];
        }
        var selection = await SelectLegacyUserTaskAsync(
            id, visibilityContext, cancellationToken);
        if (selection.VisibleCount == 0)
        {
            return selection.HasAnyActiveTask ? null : [];
        }
        if (selection.VisibleCount != 1)
            throw new WorkflowConflictException(
                "The instance has multiple active user tasks; use a task-addressed endpoint.");
        return await GetUserTaskAvailableFlowsAsync(selection.Task!.Id, actor, cancellationToken);
    }

    public async Task<InstanceDetailDto?> ClaimAsync(
        long id,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        await LoadSettingsAsync(cancellationToken);
        var visibilityContext = CreateInboxVisibilityContext(actor);
        var preview = await runtime.GetInstanceAsync(id, cancellationToken);
        if (preview is null) return null;
        var selection = await SelectLegacyUserTaskAsync(
            id, visibilityContext, cancellationToken);
        if (selection.VisibleCount == 0 && selection.HasAnyActiveTask)
        {
            return null;
        }
        if (selection.VisibleCount != 1)
            throw new WorkflowConflictException(
                selection.VisibleCount == 0
                    ? "The instance does not have an active user task."
                    : "The instance has multiple active user tasks; use a task-addressed endpoint.");
        if (await ClaimUserTaskAsync(selection.Task!.Id, actor, cancellationToken) is null)
        {
            return null;
        }
        return await BuildDetailAsync(id, cancellationToken);
    }

    public async Task<InstanceDetailDto?> UnclaimAsync(long id, ActorContext actor, CancellationToken cancellationToken)
    {
        await LoadSettingsAsync(cancellationToken);
        var visibilityContext = CreateInboxVisibilityContext(actor);
        var preview = await runtime.GetInstanceAsync(id, cancellationToken);
        if (preview is null) return null;
        var workflow = await GetWorkflowAsync(preview.WorkflowDefinitionId, cancellationToken);
        var mayRecover = HasUnclaimOverrideRole(workflow.Definition, actor);
        var tasks = mayRecover
            ? await runtime.ListUserTasksAsync(id, UserTaskRecordStatuses.Active, cancellationToken)
            : null;
        var selection = mayRecover
            ? (Task: tasks!.Count == 1 ? tasks[0] : null,
                VisibleCount: tasks.Count,
                HasAnyActiveTask: tasks.Count > 0)
            : await SelectLegacyUserTaskAsync(
                id, visibilityContext, cancellationToken);
        if (selection.VisibleCount == 0 && selection.HasAnyActiveTask)
        {
            return null;
        }
        if (selection.VisibleCount != 1)
            throw new WorkflowConflictException(
                selection.VisibleCount == 0
                    ? "The instance does not have an active user task."
                    : "The instance has multiple active user tasks; use a task-addressed endpoint.");
        if (await UnclaimUserTaskAsync(selection.Task!.Id, actor, cancellationToken) is null)
        {
            return null;
        }
        return await BuildDetailAsync(id, cancellationToken);
    }

    private async Task<(UserTaskRecord? Task, long VisibleCount, bool HasAnyActiveTask)>
        SelectLegacyUserTaskAsync(
            long instanceId,
            InboxVisibilityEvaluationContext visibilityContext,
            CancellationToken cancellationToken)
    {
        var visible = await runtime.ListUserTasksPageAsync(
            instanceId,
            UserTaskRecordStatuses.Active,
            visibilityContext,
            enforcePersonalAuthorization: false,
            page: 1,
            pageSize: 2,
            cancellationToken);
        if (visible.TotalCount > 0)
        {
            return (visible.Items.Count == 1 ? visible.Items[0] : null,
                visible.TotalCount,
                HasAnyActiveTask: true);
        }

        var hasAnyActiveTask = (await runtime.ListUserTasksAsync(
            instanceId, UserTaskRecordStatuses.Active, cancellationToken)).Count > 0;
        return (null, 0, hasAnyActiveTask);
    }

    public async Task<UserTaskDto?> GetUserTaskAsync(
        long taskId,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        await LoadSettingsAsync(cancellationToken);
        var visibilityContext = CreateInboxVisibilityContext(actor);
        var task = await runtime.GetUserTaskAsync(taskId, false, cancellationToken);
        if (task is null) return null;
        var instance = await runtime.GetInstanceAsync(task.InstanceId, cancellationToken)
            ?? throw new WorkflowDomainException($"Workflow instance #{task.InstanceId} was not found.");
        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var node = GetFlowNode(workflow.Definition, task.NodeId);
        var access = await ResolveUserTaskAccessAsync(
            task, instance, node, actor, false, cancellationToken,
            visibilityContext.AsOf);
        var executionActor = access?.ExecutionActor ?? actor;
        if (!await IsUserTaskInboxVisibleAsync(
                task.Id, executionActor, visibilityContext, cancellationToken))
        {
            return null;
        }
        if (access is null
            && !(task.Status == UserTaskRecordStatuses.Active
                 && task.Assignee is null
                 && task.RequiresClaim
                 && !string.IsNullOrWhiteSpace(task.ClaimedBy)
                 && HasUnclaimOverrideRole(workflow.Definition, actor)))
        {
            throw new WorkflowDomainException("The actor is not assigned or authorized for this user task.");
        }
        return await BuildUserTaskDtoAsync(
            task, executionActor, cancellationToken);
    }

    public async Task<IReadOnlyList<SequenceFlowModel>?> GetUserTaskAvailableFlowsAsync(
        long taskId,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        await LoadSettingsAsync(cancellationToken);
        var visibilityContext = CreateInboxVisibilityContext(actor);
        var task = await runtime.GetUserTaskAsync(taskId, false, cancellationToken);
        if (task is null) return null;
        if (task.Status != UserTaskRecordStatuses.Active) return [];
        var instance = await runtime.GetInstanceAsync(task.InstanceId, cancellationToken);
        if (instance is null || instance.Status != WorkflowInstanceStatuses.Running) return [];
        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var node = GetFlowNode(workflow.Definition, task.NodeId);
        var access = await ResolveUserTaskAccessAsync(
            task, instance, node, actor, false, cancellationToken,
            visibilityContext.AsOf);
        var executionActor = access?.ExecutionActor ?? actor;
        if (!await IsUserTaskInboxVisibleAsync(
                task.Id, executionActor, visibilityContext, cancellationToken))
        {
            return null;
        }
        if (access is null) return [];

        var stored = await LoadVariablesAsync(instance.Id, cancellationToken);
        var context = WithContext(
            stored, executionActor, instance, workflow.Definition, node,
            visibilityContext.AsOf);
        if (task.MultiInstanceExecutionId is long executionId)
        {
            var execution = await runtime.GetMultiInstanceAsync(executionId, false, cancellationToken);
            if (execution is null || execution.Status != MultiInstanceRecordStatuses.Active) return [];
            if (execution.OnePerActor)
            {
                var user = EffectiveUser(executionActor);
                if (await runtime.HasCompletedMultiInstanceItemAsync(execution.Id, user, cancellationToken))
                    return [];
                var ownedTaskId = await runtime.GetOwnedMultiInstanceItemIdAsync(
                    execution.Id, user, cancellationToken);
                if (ownedTaskId is not null && ownedTaskId != task.Id)
                    return [];
            }
            AddMultiInstanceContext(context, task, execution);
        }

        var roles = NormalizeRoles(executionActor.Roles);
        return OutgoingFlows(workflow.Id, workflow.Definition, node.Id)
            .Where(f => f.IsSelectable && !f.IsDefault
                        && RoleAllowed(f.Roles, roles)
                        && (!task.RequiresClaim
                            || string.Equals(task.ClaimedBy, EffectiveUser(executionActor), StringComparison.OrdinalIgnoreCase)
                            || CanBypassClaim(f, roles))
                        && (string.IsNullOrWhiteSpace(f.Condition)
                            || SequenceFlowConditionEvaluator.Evaluate(f.Condition, context)))
            .ToList();
    }

    public async Task<UserTaskDto?> ClaimUserTaskAsync(
        long taskId,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        await LoadSettingsAsync(cancellationToken);
        var visibilityContext = CreateInboxVisibilityContext(actor);
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
            if (initialTask.TokenId != execution.TokenId || initialTask.NodeId != execution.NodeId)
                throw new WorkflowConflictException("The multi-instance execution is no longer active for this token.");
        }

        var token = await runtime.GetExecutionTokenAsync(initialTask.TokenId, true, cancellationToken);
        var task = await runtime.GetUserTaskAsync(taskId, true, cancellationToken);
        if (task is null || task.InstanceId != instance.Id
                         || task.MultiInstanceExecutionId != initialTask.MultiInstanceExecutionId
                         || token is null
                         || token.InstanceId != instance.Id
                         || token.Status != ExecutionTokenRecordStatuses.Active
                         || task.TokenId != token.Id
                         || task.NodeId != token.NodeId
                         || task.Status != UserTaskRecordStatuses.Active)
            throw new WorkflowConflictException("The user task is no longer current.");
        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var node = GetFlowNode(workflow.Definition, task.NodeId);
        var access = await ResolveUserTaskAccessAsync(
            task, instance, node, actor, true, cancellationToken,
            visibilityContext.AsOf);
        var visibilityActor = access?.ExecutionActor ?? actor;
        if (!await IsUserTaskInboxVisibleAsync(
                task.Id, visibilityActor, visibilityContext, cancellationToken))
        {
            return null;
        }
        if (task.RequiresAssignment && task.Assignee is null)
            throw new WorkflowDomainException("The user task must be directly assigned before it can be claimed.");
        if (task.Assignee is not null)
            throw new WorkflowDomainException("Directly assigned tasks do not use claim/unclaim.");
        if (!task.RequiresClaim)
            throw new WorkflowDomainException("The user task cannot be claimed.");
        EnsureRoleAllowed(node, actor);
        var user = NormalizeUser(actor.User);
        if (execution is { OnePerActor: true })
        {
            if (await runtime.HasCompletedMultiInstanceItemAsync(execution.Id, user, cancellationToken))
                throw new WorkflowConflictException("The actor has already completed an item in this multi-instance execution.");
            var ownedTaskId = await runtime.GetOwnedMultiInstanceItemIdAsync(
                execution.Id, user, cancellationToken);
            if (ownedTaskId is not null && ownedTaskId != task.Id)
                throw new WorkflowConflictException("The actor already owns another item in this multi-instance execution.");
        }
        if (!string.IsNullOrWhiteSpace(task.ClaimedBy))
        {
            if (!string.Equals(task.ClaimedBy, user, StringComparison.OrdinalIgnoreCase))
                throw new WorkflowConflictException($"The user task is already claimed by '{task.ClaimedBy}'.");
            return await BuildUserTaskDtoAsync(task, actor, cancellationToken);
        }

        var eligibleFlows = await GetEligibleUserTaskFlowsAsync(
            instance, workflow, node, task, execution, actor, cancellationToken,
            visibilityContext.AsOf);
        if (eligibleFlows.Count == 0)
            throw new WorkflowDomainException(
                "The actor has no currently visible action on this user task and cannot claim it.");

        var updatedAt = await runtime.UpdateUserTaskClaimAsync(taskId, user, cancellationToken);
        await runtime.TouchInstanceAsync(instance.Id, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await BuildUserTaskDtoAsync(
            task with { ClaimedBy = user, UpdatedAt = updatedAt }, actor, cancellationToken);
    }

    public async Task<UserTaskDto?> UnclaimUserTaskAsync(
        long taskId,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        await LoadSettingsAsync(cancellationToken);
        var visibilityContext = CreateInboxVisibilityContext(actor);
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
            if (initialTask.TokenId != execution.TokenId || initialTask.NodeId != execution.NodeId)
                throw new WorkflowConflictException("The multi-instance execution is no longer active for this token.");
        }
        var token = await runtime.GetExecutionTokenAsync(initialTask.TokenId, true, cancellationToken);
        var task = await runtime.GetUserTaskAsync(taskId, true, cancellationToken);
        if (task is null || task.InstanceId != instance.Id
                         || task.MultiInstanceExecutionId != initialTask.MultiInstanceExecutionId
                         || token is null
                         || token.InstanceId != instance.Id
                         || token.Status != ExecutionTokenRecordStatuses.Active
                         || task.TokenId != token.Id
                         || task.NodeId != token.NodeId
                         || task.Status != UserTaskRecordStatuses.Active)
            throw new WorkflowConflictException("The user task is no longer current.");
        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var node = GetFlowNode(workflow.Definition, task.NodeId);
        var user = NormalizeUser(actor.User);
        var mayOverride = HasUnclaimOverrideRole(workflow.Definition, actor);
        var access = await ResolveUserTaskAccessAsync(
            task, instance, node, actor, true, cancellationToken,
            visibilityContext.AsOf);
        var executionActor = access?.ExecutionActor ?? actor;
        if (!mayOverride
            && !await IsUserTaskInboxVisibleAsync(
                task.Id, executionActor, visibilityContext, cancellationToken))
        {
            return null;
        }
        if (task.Assignee is not null)
            throw new WorkflowDomainException("Directly assigned tasks do not use claim/unclaim.");
        if (!task.RequiresClaim)
            throw new WorkflowDomainException("The user task does not use claim/unclaim.");
        if (string.IsNullOrWhiteSpace(task.ClaimedBy))
        {
            if (access is null && !mayOverride)
                throw new WorkflowDomainException(
                    "The actor is not authorized to unclaim this user task.");
            return await BuildUserTaskDtoAsync(task, executionActor, cancellationToken);
        }
        if (!string.Equals(task.ClaimedBy, EffectiveUser(executionActor), StringComparison.OrdinalIgnoreCase)
            && !mayOverride)
            throw new WorkflowDomainException("Only the claimant or a configured unclaim role can unclaim this user task.");

        var updatedAt = await runtime.UpdateUserTaskClaimAsync(taskId, null, cancellationToken);
        if (access?.DelegationId is long delegationId)
        {
            var auditPayload = new Dictionary<string, JsonElement>
            {
                ["operation"] = JsonSerializer.SerializeToElement("unclaimed"),
                ["previousClaimedBy"] = JsonSerializer.SerializeToElement(task.ClaimedBy),
                ["authority"] = JsonSerializer.SerializeToElement("userDelegation")
            };
            await runtime.AddUserTaskHistoryAsync(
                instance.Id,
                task.TokenId,
                task.Id,
                task.MultiInstanceExecutionId,
                task.ItemIndex,
                task.NodeId,
                user,
                auditPayload,
                "taskClaim",
                cancellationToken,
                access.RepresentedOwner,
                delegationId);
        }
        await runtime.TouchInstanceAsync(instance.Id, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await BuildUserTaskDtoAsync(
            task with { ClaimedBy = null, UpdatedAt = updatedAt },
            executionActor,
            cancellationToken);
    }

    public async Task<PagedResult<ManagedUserTaskDto>> ListManageableUserTasksAsync(
        ActorContext actor,
        long? taskId,
        long? instanceId,
        long? workflowId,
        string? workflowKey,
        string? businessKey,
        int? nodeId,
        string? nodeExternalId,
        string? owner,
        string? ownership,
        IReadOnlyList<string>? variables,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var normalizedOwnership = NormalizeTaskOwnershipFilter(ownership);

        var paged = await runtime.ListManageableUserTasksAsync(
            NormalizeRoles(actor.Roles),
            taskId,
            instanceId,
            workflowId,
            workflowKey,
            businessKey,
            nodeId,
            nodeExternalId,
            owner,
            normalizedOwnership,
            VariableFilterParser.FromLegacy(ParseVariableFilters(variables)),
            page,
            pageSize,
            cancellationToken);
        return await BuildManagedUserTaskPageAsync(paged, cancellationToken);
    }

    public async Task<PagedResult<ManagedUserTaskDto>> SearchManageableUserTasksAsync(
        ActorContext actor,
        ManageableUserTaskSearchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var page = Math.Max(1, request.Page ?? 1);
        var pageSize = Math.Clamp(request.PageSize ?? 50, 1, 200);
        var paged = await runtime.ListManageableUserTasksAsync(
            NormalizeRoles(actor.Roles),
            request.TaskId,
            request.InstanceId,
            request.WorkflowId,
            request.WorkflowKey,
            request.BusinessKey,
            request.NodeId,
            request.NodeExternalId,
            request.Owner,
            NormalizeTaskOwnershipFilter(request.Ownership),
            VariableFilterParser.Parse(request.VariableFilter),
            page,
            pageSize,
            cancellationToken);
        return await BuildManagedUserTaskPageAsync(paged, cancellationToken);
    }

    public async Task<PagedResult<ManagedUserTaskDto>?> ListDistributableUserTasksAsync(
        string workflowKey,
        TaskDistributionCredentials credentials,
        long? taskId,
        long? instanceId,
        long? workflowId,
        string? businessKey,
        int? nodeId,
        string? nodeExternalId,
        string? owner,
        string? ownership,
        IReadOnlyList<string>? variables,
        bool includeVariables,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var authorization = await AuthenticateTaskDistributionAsync(
            workflowKey, credentials, cancellationToken);
        if (authorization is null)
        {
            return null;
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var paged = await runtime.ListDistributableUserTasksAsync(
            authorization.Workflow.WorkflowKey,
            taskId,
            instanceId,
            workflowId,
            businessKey,
            nodeId,
            nodeExternalId,
            owner,
            NormalizeTaskOwnershipFilter(ownership),
            VariableFilterParser.FromLegacy(ParseVariableFilters(variables)),
            includeVariables,
            page,
            pageSize,
            cancellationToken);
        return await BuildManagedUserTaskPageAsync(paged, cancellationToken);
    }

    public async Task<PagedResult<ManagedUserTaskDto>?> SearchDistributableUserTasksAsync(
        string workflowKey,
        TaskDistributionCredentials credentials,
        DistributableUserTaskSearchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var authorization = await AuthenticateTaskDistributionAsync(
            workflowKey, credentials, cancellationToken);
        if (authorization is null)
        {
            return null;
        }

        var paged = await runtime.ListDistributableUserTasksAsync(
            authorization.Workflow.WorkflowKey,
            request.TaskId,
            request.InstanceId,
            request.WorkflowId,
            request.BusinessKey,
            request.NodeId,
            request.NodeExternalId,
            request.Owner,
            NormalizeTaskOwnershipFilter(request.Ownership),
            VariableFilterParser.Parse(request.VariableFilter),
            request.IncludeVariables ?? false,
            Math.Max(1, request.Page ?? 1),
            Math.Clamp(request.PageSize ?? 50, 1, 200),
            cancellationToken);
        return await BuildManagedUserTaskPageAsync(paged, cancellationToken);
    }

    public Task<UserTaskAssignmentAckDto?> AssignUserTaskAsync(
        long taskId,
        string? actorId,
        DateTimeOffset expectedUpdatedAt,
        string? reason,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        var target = NormalizeAssignmentTarget(actorId);
        return SetUserTaskAssignmentAsync(
            taskId, target, expectedUpdatedAt, reason, actor, cancellationToken);
    }

    public Task<UserTaskAssignmentAckDto?> UnassignUserTaskAsync(
        long taskId,
        DateTimeOffset expectedUpdatedAt,
        string? reason,
        ActorContext actor,
        CancellationToken cancellationToken) =>
        SetUserTaskAssignmentAsync(taskId, null, expectedUpdatedAt, reason, actor, cancellationToken);

    public async Task<UserTaskAssignmentAckDto?> AssignDistributedUserTaskAsync(
        string workflowKey,
        long taskId,
        string? actorId,
        DateTimeOffset expectedUpdatedAt,
        string? reason,
        TaskDistributionCredentials credentials,
        CancellationToken cancellationToken)
    {
        var authorization = await AuthenticateTaskDistributionAsync(
            workflowKey, credentials, cancellationToken);
        if (authorization is null)
        {
            return null;
        }

        return await SetUserTaskAssignmentAsync(
            taskId,
            NormalizeAssignmentTarget(actorId),
            expectedUpdatedAt,
            reason,
            new ActorContext(
                authorization.ClientId,
                [],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            cancellationToken,
            authorization.Workflow.WorkflowKey,
            false,
            "taskDistribution");
    }

    public async Task<UserTaskAssignmentAckDto?> UnassignDistributedUserTaskAsync(
        string workflowKey,
        long taskId,
        DateTimeOffset expectedUpdatedAt,
        string? reason,
        TaskDistributionCredentials credentials,
        CancellationToken cancellationToken)
    {
        var authorization = await AuthenticateTaskDistributionAsync(
            workflowKey, credentials, cancellationToken);
        if (authorization is null)
        {
            return null;
        }

        return await SetUserTaskAssignmentAsync(
            taskId,
            null,
            expectedUpdatedAt,
            reason,
            new ActorContext(
                authorization.ClientId,
                [],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            cancellationToken,
            authorization.Workflow.WorkflowKey,
            false,
            "taskDistribution");
    }

    private async Task<UserTaskAssignmentAckDto?> SetUserTaskAssignmentAsync(
        long taskId,
        string? targetActor,
        DateTimeOffset expectedUpdatedAt,
        string? reason,
        ActorContext actor,
        CancellationToken cancellationToken,
        string? requiredWorkflowKey = null,
        bool requireManagerRole = true,
        string? auditAuthority = null)
    {
        if (expectedUpdatedAt == default)
            throw new WorkflowDomainException("expectedUpdatedAt is required.");
        var normalizedReason = NormalizeAssignmentReason(reason);
        var initialTask = await runtime.GetUserTaskAsync(taskId, false, cancellationToken);
        if (initialTask is null) return null;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var instance = await runtime.GetInstanceForUpdateAsync(initialTask.InstanceId, false, cancellationToken)
            ?? throw new WorkflowConflictException("The workflow instance no longer exists.");
        if (requiredWorkflowKey is not null
            && !string.Equals(instance.WorkflowKey, requiredWorkflowKey, StringComparison.Ordinal))
        {
            return null;
        }
        if (instance.Status != WorkflowInstanceStatuses.Running)
            throw new WorkflowConflictException("The workflow instance is no longer running.");

        MultiInstanceExecutionRecord? execution = null;
        if (initialTask.MultiInstanceExecutionId is long executionId)
        {
            execution = await runtime.GetMultiInstanceAsync(executionId, true, cancellationToken);
            if (execution is null || execution.InstanceId != instance.Id
                                  || execution.Status != MultiInstanceRecordStatuses.Active)
                throw new WorkflowConflictException("The multi-instance execution has already closed.");
            if (initialTask.TokenId != execution.TokenId || initialTask.NodeId != execution.NodeId)
                throw new WorkflowConflictException("The multi-instance execution is no longer active for this token.");
        }

        var token = await runtime.GetExecutionTokenAsync(initialTask.TokenId, true, cancellationToken);
        var task = await runtime.GetUserTaskAsync(taskId, true, cancellationToken);
        if (task is null || task.InstanceId != instance.Id
                         || task.MultiInstanceExecutionId != initialTask.MultiInstanceExecutionId
                         || token is null
                         || token.Status != ExecutionTokenRecordStatuses.Active
                         || task.TokenId != token.Id
                         || task.NodeId != token.NodeId
                         || task.Status != UserTaskRecordStatuses.Active)
            throw new WorkflowConflictException("The user task is no longer active or current.");

        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        if (requireManagerRole)
        {
            EnsureTaskAssignmentManager(workflow.Definition, actor);
        }
        var node = GetFlowNode(workflow.Definition, task.NodeId);
        var (previousOwnership, previousOwner) = GetTaskOwnership(task);
        var authoredRequiresClaim = node.RequiresClaim;
        var desiredIsAssigned = targetActor is not null;
        var alreadyDesired = desiredIsAssigned
            ? task.Assignee is not null
              && string.Equals(task.Assignee, targetActor, StringComparison.OrdinalIgnoreCase)
              && task.ClaimedBy is null
              && !task.RequiresClaim
            : task.Assignee is null
              && task.ClaimedBy is null
              && task.RequiresClaim == authoredRequiresClaim;

        if (alreadyDesired)
        {
            return new UserTaskAssignmentAckDto(
                task.Id,
                task.InstanceId,
                UserTaskAssignmentOperations.Unchanged,
                previousOwnership,
                previousOwner,
                previousOwnership,
                previousOwner,
                task.RequiresClaim,
                task.RequiresAssignment,
                false,
                task.UpdatedAt);
        }

        if (task.UpdatedAt != expectedUpdatedAt)
            throw new WorkflowConflictException("The user task assignment changed; refresh and try again.");

        if (targetActor is not null && execution is { OnePerActor: true })
        {
            if (await runtime.HasCompletedMultiInstanceItemAsync(execution.Id, targetActor, cancellationToken))
                throw new WorkflowConflictException(
                    "The target actor has already completed an item in this multi-instance execution.");
            var ownedTaskId = await runtime.GetOwnedMultiInstanceItemIdAsync(
                execution.Id, targetActor, cancellationToken);
            if (ownedTaskId is not null && ownedTaskId != task.Id)
                throw new WorkflowConflictException(
                    "The target actor already owns another item in this multi-instance execution.");
        }

        var newRequiresClaim = targetActor is null && authoredRequiresClaim;
        var updatedAt = await runtime.UpdateUserTaskAssignmentAsync(
            task.Id, targetActor, newRequiresClaim, cancellationToken);
        var currentOwnership = targetActor is null
            ? UserTaskOwnershipKinds.Unassigned
            : UserTaskOwnershipKinds.Assigned;
        var operation = targetActor is null
            ? UserTaskAssignmentOperations.Unassigned
            : previousOwner is null
              || string.Equals(previousOwner, targetActor, StringComparison.OrdinalIgnoreCase)
                ? UserTaskAssignmentOperations.Assigned
                : UserTaskAssignmentOperations.Reassigned;
        var performedBy = auditAuthority is null
            ? NormalizeUser(actor.User)
            : actor.User ?? "anonymous";
        var auditPayload = new Dictionary<string, JsonElement>
        {
            ["operation"] = JsonSerializer.SerializeToElement(operation),
            ["previousOwnership"] = JsonSerializer.SerializeToElement(previousOwnership),
            ["previousOwner"] = JsonSerializer.SerializeToElement(previousOwner),
            ["newOwnership"] = JsonSerializer.SerializeToElement(currentOwnership),
            ["newOwner"] = JsonSerializer.SerializeToElement(targetActor),
            ["previousRequiresClaim"] = JsonSerializer.SerializeToElement(task.RequiresClaim),
            ["newRequiresClaim"] = JsonSerializer.SerializeToElement(newRequiresClaim),
            ["reason"] = JsonSerializer.SerializeToElement(normalizedReason)
        };
        if (auditAuthority is not null)
        {
            auditPayload["authority"] = JsonSerializer.SerializeToElement(auditAuthority);
        }
        await runtime.AddUserTaskHistoryAsync(
            instance.Id,
            task.TokenId,
            task.Id,
            task.MultiInstanceExecutionId,
            task.ItemIndex,
            task.NodeId,
            performedBy,
            auditPayload,
            "taskAssignment",
            cancellationToken);
        await runtime.TouchInstanceAsync(instance.Id, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new UserTaskAssignmentAckDto(
            task.Id,
            task.InstanceId,
            operation,
            previousOwnership,
            previousOwner,
            currentOwnership,
            targetActor,
            newRequiresClaim,
            task.RequiresAssignment,
            true,
            updatedAt);
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
        var instance = await runtime.GetInstanceAsync(instanceId, cancellationToken);
        if (instance is null) return new PagedResult<UserTaskDto>([], page, pageSize, 0);
        await LoadSettingsAsync(cancellationToken);
        var visibilityContext = CreateInboxVisibilityContext(actor);
        var paged = await runtime.ListUserTasksPageAsync(
            instanceId,
            status,
            visibilityContext,
            enforcePersonalAuthorization: true,
            page,
            pageSize,
            cancellationToken);
        var pageRecords = paged.Items;
        if (pageRecords.Count == 0)
            return new PagedResult<UserTaskDto>([], page, pageSize, paged.TotalCount);
        var executionIds = pageRecords
            .Where(task => task.MultiInstanceExecutionId is not null)
            .Select(task => task.MultiInstanceExecutionId!.Value)
            .Distinct()
            .ToList();
        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var stored = await LoadVariablesAsync(instance.Id, cancellationToken);
        var progressRecords = await runtime.GetMultiInstanceProgressAsync(executionIds, cancellationToken);
        var progressCache = progressRecords.ToDictionary(pair => pair.Key, pair => ToProgress(pair.Value));
        var executionsById = progressRecords.ToDictionary(pair => pair.Key, pair => pair.Value.Execution);
        var accessByTask = pageRecords.ToDictionary(
            task => task.Id,
            task => ResolveProjectedUserTaskAccess(
                task, actor, task.ActingFor, task.DelegationId));
        var onePerActorIds = progressRecords.Values
            .Where(progress => progress.Execution.OnePerActor)
            .Select(progress => progress.Execution.Id)
            .ToList();
        var actorStatesByUser =
            new Dictionary<string, IReadOnlyDictionary<long, MultiInstanceActorStateRecord>>(
                StringComparer.OrdinalIgnoreCase);
        foreach (var representedUser in accessByTask.Values
                     .Select(access => access.RepresentedOwner)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            actorStatesByUser[representedUser] =
                await runtime.GetMultiInstanceActorStatesAsync(
                    onePerActorIds, representedUser, cancellationToken);
        }
        var items = new List<UserTaskDto>(pageRecords.Count);
        foreach (var task in pageRecords)
        {
            var access = accessByTask[task.Id];
            var progress = task.MultiInstanceExecutionId is long executionId
                ? progressCache.GetValueOrDefault(executionId)
                : null;
            var capabilities = BuildUserTaskCapabilities(
                task,
                access.ExecutionActor,
                instance,
                workflow,
                stored,
                executionsById,
                actorStatesByUser.GetValueOrDefault(access.RepresentedOwner)
                    ?? new Dictionary<long, MultiInstanceActorStateRecord>(),
                visibilityContext.AsOf);
            items.Add(ToUserTaskDto(
                task,
                progress,
                capabilities,
                NodeAttributes(workflow, task.NodeId),
                access.ExecutionActor));
        }
        return new PagedResult<UserTaskDto>(items, page, pageSize, paged.TotalCount);
    }

    public async Task<IReadOnlyList<SequenceFlowModel>?> GetMultiInstanceInterruptFlowsAsync(
        long executionId,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        await LoadSettingsAsync(cancellationToken);
        var visibilityContext = CreateInboxVisibilityContext(actor);
        var execution = await runtime.GetMultiInstanceAsync(executionId, false, cancellationToken);
        if (execution is null)
            return null;
        if (execution.Status != MultiInstanceRecordStatuses.Active)
            return [];

        var instance = await runtime.GetInstanceAsync(execution.InstanceId, cancellationToken);
        var executionToken = await runtime.GetExecutionTokenAsync(execution.TokenId, false, cancellationToken);
        if (instance is null || instance.Status != WorkflowInstanceStatuses.Running
            || executionToken is null
            || executionToken.Status != ExecutionTokenRecordStatuses.Active
            || executionToken.NodeId != execution.NodeId)
            return [];

        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var node = GetFlowNode(workflow.Definition, execution.NodeId);
        if (node.MultiInstance is null || !BpmnFlowNodeTypes.IsUserTask(node.Type))
            return [];
        if (!await IsUserTaskNodeInboxVisibleAsync(
                instance.Id, node.Id, actor, visibilityContext, cancellationToken))
        {
            return null;
        }
        var roles = NormalizeRoles(actor.Roles);
        if (!RoleAllowed(node, roles))
            return [];

        var stored = await LoadVariablesAsync(instance.Id, cancellationToken);
        var context = WithContext(
            stored, actor, instance, workflow.Definition, node,
            visibilityContext.AsOf);
        AddMultiInstanceExecutionContext(context, execution);
        return OutgoingFlows(workflow.Id, workflow.Definition, node.Id)
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
        var visibilityContext = CreateInboxVisibilityContext(actor);
        var initialExecution = await runtime.GetMultiInstanceAsync(executionId, false, cancellationToken);
        if (initialExecution is null) return null;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var instance = await runtime.GetInstanceForUpdateAsync(initialExecution.InstanceId, false, cancellationToken)
            ?? throw new WorkflowConflictException("The workflow instance no longer exists.");
        var execution = await runtime.GetMultiInstanceAsync(executionId, true, cancellationToken);
        if (execution is null || execution.InstanceId != instance.Id
                              || execution.Status != MultiInstanceRecordStatuses.Active)
            throw new WorkflowConflictException("The multi-instance execution has already closed.");

        var executionToken = await runtime.GetExecutionTokenAsync(execution.TokenId, true, cancellationToken);
        if (instance.Status != WorkflowInstanceStatuses.Running
            || executionToken is null
            || executionToken.Status != ExecutionTokenRecordStatuses.Active
            || executionToken.NodeId != execution.NodeId)
            throw new WorkflowConflictException("The multi-instance execution is no longer active for its token.");

        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var node = GetFlowNode(workflow.Definition, execution.NodeId);
        if (node.MultiInstance is null || !BpmnFlowNodeTypes.IsUserTask(node.Type))
            throw new WorkflowConflictException("The execution is not an active multi-instance user task.");

        if (!await IsUserTaskNodeInboxVisibleAsync(
                instance.Id, node.Id, actor, visibilityContext, cancellationToken))
        {
            return null;
        }

        var flow = OutgoingFlows(workflow.Id, workflow.Definition, node.Id).SingleOrDefault(f => f.Id == flowId)
            ?? throw new WorkflowDomainException("The requested flow is not an action of this multi-instance execution.");
        if (!flow.IsSelectable || flow.IsDefault || !flow.CancelRemainingInstances)
            throw new WorkflowDomainException("Only selectable interrupting flows can be taken at the multi-instance execution level.");

        EnsureRoleAllowed(node, actor);
        var roles = NormalizeRoles(actor.Roles);
        if (!RoleAllowed(flow.Roles, roles))
            throw new WorkflowDomainException("The actor does not have a role permitted for this interrupt action.");

        var stored = await LoadVariablesAsync(instance.Id, cancellationToken);
        var storedContext = WithContext(
            stored, actor, instance, workflow.Definition, node,
            visibilityContext.AsOf);
        AddMultiInstanceExecutionContext(storedContext, execution);
        if (!string.IsNullOrWhiteSpace(flow.Condition)
            && !SequenceFlowConditionEvaluator.Evaluate(flow.Condition, storedContext))
            throw new WorkflowDomainException("The selected interrupt action condition is not satisfied.");

        ValidateVariableValues(flow.Variables, variableValues);
        var values = ResolveAndValidateVariables(flow.Variables, variableValues, storedContext);
        var context = new Dictionary<string, JsonElement>(storedContext, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values) context[pair.Key] = pair.Value;

        foreach (var pair in values)
        {
            await runtime.AddVariableAsync(instance.Id, pair.Key, flow.Id, actor.User, pair.Value, cancellationToken);
        }

        var flowInfo = await LoadSequenceFlowInfoAsync(
            instance.Id, workflow.Definition, cancellationToken);

        await CloseAndAdvanceMultiInstanceAsync(
            execution,
            instance,
            workflow,
            node,
            flow,
            "interrupt",
            actor,
            values,
            context,
            null,
            null,
            flowInfo,
            true,
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
        var visibilityContext = CreateInboxVisibilityContext(actor);
        var initialTask = await runtime.GetUserTaskAsync(taskId, false, cancellationToken);
        if (initialTask is null) return null;
        if (initialTask.MultiInstanceExecutionId is null)
        {
            DelegatedTaskAccessDto? delegatedAccess = null;
            var detail = await TakeFlowCoreAsync(
                initialTask.InstanceId,
                flowId,
                actor,
                variableValues,
                taskId,
                cancellationToken,
                access => delegatedAccess = access.ToDto());
            if (detail is null) return null;
            return new UserTaskActionAckDto(taskId, detail.Id, UserTaskRecordStatuses.Completed, detail.Status,
                flowId, detail.CurrentNodeId, detail.CurrentNodeName, detail.CurrentNodeExternalId, null, detail.UpdatedAt,
                detail.Fault)
            {
                ExecutionPositions = detail.ExecutionPositions,
                Completion = detail.Completion,
                DelegatedAccess = delegatedAccess
            };
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
        var executionToken = await runtime.GetExecutionTokenAsync(execution.TokenId, true, cancellationToken);
        if (executionToken is null
            || executionToken.Status != ExecutionTokenRecordStatuses.Active
            || executionToken.NodeId != execution.NodeId)
            throw new WorkflowConflictException("The multi-instance execution is no longer active for its token.");
        var task = await runtime.GetUserTaskAsync(taskId, true, cancellationToken);
        if (task is null || task.InstanceId != instance.Id
                         || task.MultiInstanceExecutionId != execution.Id
                         || task.TokenId != executionToken.Id
                         || task.NodeId != executionToken.NodeId
                         || task.Status != UserTaskRecordStatuses.Active)
            throw new WorkflowConflictException("The user task is no longer active.");

        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var node = GetFlowNode(workflow.Definition, task.NodeId);
        var access = await ResolveUserTaskAccessAsync(
            task, instance, node, actor, true, cancellationToken,
            visibilityContext.AsOf);
        var executionActor = access?.ExecutionActor ?? actor;
        if (!await IsUserTaskInboxVisibleAsync(
                task.Id, executionActor, visibilityContext, cancellationToken))
        {
            return null;
        }
        if (access is null)
            throw new WorkflowDomainException(
                "The actor is not assigned or authorized for this user task.");
        var user = NormalizeUser(actor.User);
        var representedUser = EffectiveUser(executionActor);
        if (execution.OnePerActor)
        {
            if (await runtime.HasCompletedMultiInstanceItemAsync(
                    execution.Id, representedUser, cancellationToken))
                throw new WorkflowConflictException(
                    "The represented actor has already completed an item in this multi-instance execution.");
            var ownedTaskId = await runtime.GetOwnedMultiInstanceItemIdAsync(
                execution.Id, representedUser, cancellationToken);
            if (ownedTaskId is not null && ownedTaskId != task.Id)
                throw new WorkflowConflictException(
                    "The represented actor already owns another item in this multi-instance execution.");
        }
        var flow = OutgoingFlows(workflow.Id, workflow.Definition, node.Id).SingleOrDefault(f => f.Id == flowId)
            ?? throw new WorkflowDomainException("The requested flow is not an action of this user task.");
        if (!flow.IsSelectable || flow.IsDefault)
            throw new WorkflowDomainException("The requested flow is an engine-only/default route and cannot be selected by a user.");
        EnsureUserTaskActor(task, node, executionActor, requireActive: true);
        var actorRoles = NormalizeRoles(executionActor.Roles);
        if (!RoleAllowed(flow.Roles, actorRoles))
            throw new WorkflowDomainException("The actor does not have a role permitted for this action.");
        EnsureActionAllowedByClaim(task, flow, executionActor);

        var stored = await LoadVariablesAsync(instance.Id, cancellationToken);
        var storedContext = WithContext(
            stored, executionActor, instance, workflow.Definition, node,
            visibilityContext.AsOf);
        AddMultiInstanceContext(storedContext, task, execution);
        if (!string.IsNullOrWhiteSpace(flow.Condition)
            && !SequenceFlowConditionEvaluator.Evaluate(flow.Condition, storedContext))
            throw new WorkflowDomainException("The selected action condition is not satisfied.");

        ValidateVariableValues(flow.Variables, variableValues);
        var values = ResolveAndValidateVariables(flow.Variables, variableValues, storedContext);
        var context = new Dictionary<string, JsonElement>(storedContext, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values) context[pair.Key] = pair.Value;

        var flowInfo = await LoadSequenceFlowInfoAsync(
            instance.Id, workflow.Definition, cancellationToken);
        await runtime.CompleteMultiInstanceItemAsync(
            task.Id,
            flow.Id,
            user,
            SnapshotRoles(executionActor.Roles),
            values,
            cancellationToken,
            executionActor.ActingFor,
            executionActor.DelegationId);
        await RecordSequenceFlowOccurrenceAsync(
            flowInfo,
            instance.Id,
            task.TokenId,
            task.Id,
            execution.Id,
            task.ItemIndex,
            flow,
            "multiInstanceItem",
            isAction: true,
            isTraversal: false,
            actor: executionActor,
            values: values,
            cancellationToken: cancellationToken);
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
                winning = OutgoingFlows(workflow.Id, workflow.Definition, node.Id)
                    .Where(f => !f.IsDefault && !f.CancelRemainingInstances
                                && !string.IsNullOrWhiteSpace(f.CompletionCondition))
                    .OrderBy(f => f.CompletionPriority)
                    .FirstOrDefault(f => SequenceFlowConditionEvaluator.EvaluateCompletion(
                        f.CompletionCondition, context, counts, execution.TotalCount, flowInfo));
            }
            if (winning is not null) reason = "condition";
            else if (allItemsCompleted)
            {
                winning = OutgoingFlows(workflow.Id, workflow.Definition, node.Id)
                    .Single(f => !f.CancelRemainingInstances && f.IsDefault);
                reason = "all";
            }
        }

        if (winning is null)
        {
            if (execution.Mode == MultiInstanceModes.Sequential)
                await runtime.ActivateNextMultiInstanceItemAsync(
                    execution.Id,
                    ToNodeExecutionActor(executionActor),
                    cancellationToken);
            await runtime.AddMultiInstanceHistoryAsync(instance.Id, task.TokenId, task.Id, execution.Id,
                task.ItemIndex ?? 0, flow.Id, node.Id, node.Id, user,
                CloneDictionary(values), "multiInstanceItem", cancellationToken,
                executionActor.ActingFor, executionActor.DelegationId);
            var activityAt = await runtime.TouchInstanceAsync(instance.Id, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            var progress = await BuildProgressAsync(execution.Id, cancellationToken);
            var projection = await BuildExecutionProjectionAsync(instance, cancellationToken);
            return new UserTaskActionAckDto(task.Id, instance.Id, UserTaskRecordStatuses.Completed,
                instance.Status, flow.Id, node.Id, node.Name, node.ExternalId, progress, activityAt)
            {
                ExecutionPositions = projection.ExecutionPositions,
                Completion = projection.Completion,
                DelegatedAccess = access.ToDto()
            };
        }

        var lockedInstance = await CloseAndAdvanceMultiInstanceAsync(
            execution,
            instance,
            workflow,
            node,
            winning,
            reason!,
            executionActor,
            values,
            context,
            task.Id,
            task.ItemIndex,
            flowInfo,
            false,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var resting = GetFlowNode(workflow.Definition, lockedInstance.CurrentStepId);
        var closedProgress = await BuildProgressAsync(execution.Id, cancellationToken);
        var closedProjection = await BuildExecutionProjectionAsync(lockedInstance, cancellationToken);
        return new UserTaskActionAckDto(task.Id, instance.Id, UserTaskRecordStatuses.Completed,
            lockedInstance.Status, flow.Id, resting.Id, resting.Name, resting.ExternalId,
            closedProgress, lockedInstance.UpdatedAt,
            ToFault(lockedInstance.Status, lockedInstance.FaultCode, lockedInstance.FaultDescription, resting.Name))
        {
            ExecutionPositions = closedProjection.ExecutionPositions,
            Completion = closedProjection.Completion,
            DelegatedAccess = access.ToDto()
        };
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
        SequenceFlowInfoSnapshot? flowInfo,
        bool directParentInterrupt,
        CancellationToken cancellationToken,
        AdministrativeBatchFlowContext? administrativeBatch = null)
    {
        var user = NormalizeUser(actor.User);
        await RecordSequenceFlowOccurrenceAsync(
            flowInfo,
            instance.Id,
            execution.TokenId,
            userTaskId,
            execution.Id,
            itemIndex,
            winning,
            administrativeBatch is null
                ? directParentInterrupt ? "multiInstanceInterrupt" : "multiInstanceOutcome"
                : NodeExecutionCompletionReasons.AdministrativeAction,
            isAction: directParentInterrupt || administrativeBatch is not null
                && administrativeBatch.Request.MultiInstanceMode
                == AdministrativeActionMultiInstanceModes.ForceParent,
            isTraversal: !node.AsyncAfter,
            actor: actor,
            values: directParentInterrupt ? variableValues : null,
            cancellationToken: cancellationToken,
            administrativeBatch: administrativeBatch);
        await runtime.CloseMultiInstanceAsync(
            execution.Id,
            winning.Id,
            reason,
            ToNodeExecutionActor(actor),
            cancellationToken,
            cancelledTaskCompletionKind: administrativeBatch is null
                ? null
                : NodeExecutionCompletionReasons.AdministrativeAction,
            administrativeReason: administrativeBatch?.Reason,
            administrativeActionBatchId: administrativeBatch?.BatchId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        var parentInterrupt = directParentInterrupt
            ? new MultiInstanceParentInterruptResult(
                winning.Id,
                user,
                SnapshotRoles(actor.Roles),
                timeProvider.GetUtcNow(),
                CloneDictionary(variableValues)
                ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase),
                actor.ActingFor,
                actor.DelegationId)
            : null;
        var result = await BuildMultiInstanceResultAsync(
            execution.Id,
            parentInterrupt,
            cancellationToken);
        await runtime.AddVariableAsync(
            instance.Id,
            execution.ResultVariable,
            node.Id,
            user,
            result,
            cancellationToken,
            actingFor: actor.ActingFor,
            delegationId: actor.DelegationId);
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
            administrativeBatch is null
                ? reason == "interrupt" ? "multiInstanceInterrupt" : "multiInstanceComplete"
                : NodeExecutionCompletionReasons.AdministrativeAction,
            cancellationToken,
            actor.ActingFor,
            actor.DelegationId,
            administrativeBatch?.Reason,
            administrativeBatch?.BatchId);

        var token = await runtime.GetExecutionTokenAsync(execution.TokenId, true, cancellationToken)
            ?? throw new WorkflowConflictException("The multi-instance parent token no longer exists.");
        if (token.Status != ExecutionTokenRecordStatuses.Active)
            throw new WorkflowConflictException("The multi-instance parent token is no longer active.");
        var resetAutomaticActivationCount =
            WorkflowAutomaticActivationGuard.ResetAfterExternalWaitOrTrigger();
        if (!await runtime.SetExecutionTokenAutomaticActivationCountAsync(
                token.Id,
                token.ActivationId,
                resetAutomaticActivationCount,
                cancellationToken))
        {
            throw new WorkflowConflictException(
                "The multi-instance parent token changed while resetting its automatic-activation count.");
        }
        token = token with { AutomaticActivationCount = resetAutomaticActivationCount };
        if (node.AsyncAfter)
        {
            var waitingInstance = instance with
            {
                ActiveTokenId = token.Id,
                CurrentStepId = token.NodeId,
                CurrentNodeExecutionId = token.CurrentNodeExecutionId,
                ClaimedBy = null,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await EnsureAsyncAfterWaitAsync(
                waitingInstance,
                token,
                node,
                workflow.Definition,
                actor,
                cancellationToken,
                selectedFlowId: winning.Id,
                multiInstanceExecutionId: execution.Id,
                userTaskId: userTaskId,
                administrativeBatch: administrativeBatch);
            await CancelAttachedTimerBoundaryWaitsAsync(
                instance.Id,
                [token.Id],
                cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return await runtime.GetInstanceAsync(instance.Id, cancellationToken)
                ?? waitingInstance;
        }

        await CancelAttachedTimerBoundaryWaitsAsync(
            instance.Id,
            [token.Id],
            cancellationToken);
        var nextNode = GetFlowNode(workflow.Definition, winning.TargetRef);
        var targetTokenStatus = BpmnFlowNodeTypes.IsErrorEnd(nextNode.Type)
            ? ExecutionTokenRecordStatuses.Faulted
            : BpmnFlowNodeTypes.IsEnd(nextNode.Type)
                ? ExecutionTokenRecordStatuses.Completed
                : ExecutionTokenRecordStatuses.Active;
        var terminationReason = BpmnFlowNodeTypes.IsTerminateEnd(nextNode.Type)
            ? ExecutionTokenTerminationReasons.TerminateEnd
            : BpmnFlowNodeTypes.IsErrorEnd(nextNode.Type)
                ? ExecutionTokenTerminationReasons.ErrorEnd
                : BpmnFlowNodeTypes.IsEnd(nextNode.Type)
                    ? ExecutionTokenTerminationReasons.NormalEnd
                    : null;
        var lockedInstance = instance with
        {
            ActiveTokenId = token.Id,
            CurrentStepId = nextNode.Id,
            ClaimedBy = null,
            FaultCode = BpmnFlowNodeTypes.IsErrorEnd(nextNode.Type) ? nextNode.ErrorCode : null,
            FaultDescription = BpmnFlowNodeTypes.IsErrorEnd(nextNode.Type)
                ? nextNode.ErrorDescription ?? nextNode.Name
                : null,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var nextContext = WithContext(context, actor, lockedInstance, workflow.Definition, nextNode);
        await runtime.UpdateExecutionTokenAsync(
            token.Id,
            ToSnapshot(nextNode, nextContext, lockedInstance.Id),
            targetTokenStatus,
            token.GatewayBranchId,
            winning.Id,
            terminationReason,
            null,
            ToNodeExecutionActor(actor),
            null,
            cancellationToken,
            automaticActivationCount:
                WorkflowAutomaticActivationGuard.ResetAfterExternalWaitOrTrigger());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        if (BpmnFlowNodeTypes.IsTerminateEnd(nextNode.Type))
        {
            await TerminateInstanceAsync(instance.Id, token.Id, actor, cancellationToken);
        }
        else if (BpmnFlowNodeTypes.IsErrorEnd(nextNode.Type))
        {
            await FaultInstanceAsync(instance.Id, token.Id, actor, cancellationToken);
        }
        else if (BpmnFlowNodeTypes.IsEnd(nextNode.Type))
        {
            var remaining = await runtime.ListExecutionTokensAsync(
                instance.Id, ExecutionTokenRecordStatuses.Active, cancellationToken);
            if (remaining.Count == 0)
            {
                await runtime.SetInstanceStatusAsync(
                    instance.Id, WorkflowInstanceStatuses.Completed, cancellationToken);
            }
            await CloseInactiveGatewayScopesAsync(instance.Id, "allEnded", cancellationToken);
        }
        else
        {
            lockedInstance = await ResolvePassThroughAsync(
                lockedInstance, workflow.Definition, actor, flowInfo, token.Id, cancellationToken);
            await EnsureMultiInstanceInitializedAsync(lockedInstance, workflow.Definition, actor, cancellationToken);
            lockedInstance = await ApplyUserTaskOwnershipInheritanceAsync(
                lockedInstance, workflow.Definition, cancellationToken);
        }
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await runtime.GetInstanceAsync(instance.Id, cancellationToken) ?? lockedInstance;
    }

    public Task<InstanceDetailDto?> TakeFlowAsync(
        long id,
        int flowId,
        ActorContext actor,
        Dictionary<string, JsonElement>? variableValues,
        CancellationToken cancellationToken) =>
        TakeFlowCoreAsync(id, flowId, actor, variableValues, null, cancellationToken);

    private async Task<InstanceDetailDto?> TakeFlowCoreAsync(
        long id,
        int flowId,
        ActorContext actor,
        Dictionary<string, JsonElement>? variableValues,
        long? expectedTaskId,
        CancellationToken cancellationToken,
        Action<ResolvedUserTaskAccess>? accessResolved = null,
        AdministrativeBatchFlowContext? administrativeBatch = null)
    {
        await LoadSettingsAsync(cancellationToken);
        var visibilityContext = CreateInboxVisibilityContext(actor);
        UserTaskRecord? selectedTask = null;
        if (expectedTaskId is long selectedTaskId)
        {
            selectedTask = await runtime.GetUserTaskAsync(selectedTaskId, false, cancellationToken);
            if (selectedTask is null || selectedTask.InstanceId != id)
            {
                throw new WorkflowConflictException("The selected user task is no longer available.");
            }
        }
        else
        {
            var selection = await SelectLegacyUserTaskAsync(
                id, visibilityContext, cancellationToken);
            if (selection.VisibleCount == 0 && selection.HasAnyActiveTask)
            {
                return null;
            }
            if (selection.VisibleCount != 1)
            {
                throw new WorkflowConflictException(
                    "The instance does not have exactly one active user task; use a task-addressed endpoint.");
            }
            selectedTask = selection.Task!;
            expectedTaskId = selectedTask.Id;
        }

        if (selectedTask.MultiInstanceExecutionId is not null)
        {
            if (administrativeBatch is not null)
            {
                throw new WorkflowConflictException(
                    "A multi-instance administrative action must address its parent execution.");
            }
            if (await TakeUserTaskFlowAsync(
                    selectedTask.Id,
                    flowId,
                    actor,
                    variableValues,
                    cancellationToken) is null)
            {
                return null;
            }
            return await BuildDetailAsync(id, cancellationToken);
        }

        string performedBy = NormalizeUser(actor.User);
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var instance = await runtime.GetInstanceForUpdateAsync(id, false, cancellationToken);
        if (instance is null)
        {
            logger.LogInformation("Take flow {FlowId} on instance {InstanceId}: instance not found.", flowId, id);
            return null;
        }

        if (instance.Status != WorkflowInstanceStatuses.Running)
        {
            logger.LogWarning("Take flow {FlowId} rejected on instance {InstanceId}: instance status is {Status} (not Running).", flowId, id, instance.Status);
            throw new WorkflowConflictException("Only running instances can take a sequence flow.");
        }
        if (administrativeBatch is not null)
        {
            if (instance.WorkflowDefinitionId
                != administrativeBatch.Request.ExpectedWorkflowDefinitionId)
            {
                throw new WorkflowConflictException(
                    "The workflow instance version changed after batch selection.");
            }
        }

        var task = await runtime.GetUserTaskAsync(expectedTaskId!.Value, true, cancellationToken);
        if (task is null
            || task.InstanceId != instance.Id
            || task.MultiInstanceExecutionId is not null
            || task.Status != UserTaskRecordStatuses.Active)
        {
            throw new WorkflowConflictException("The selected user task is no longer active.");
        }
        if (administrativeBatch is not null
            && task.UpdatedAt
            != administrativeBatch.Request.ExpectedPositionUpdatedAt)
        {
            throw new WorkflowConflictException(
                "The user task changed after batch selection.");
        }
        var token = await runtime.GetExecutionTokenAsync(task.TokenId, true, cancellationToken);
        if (token is null
            || token.InstanceId != instance.Id
            || token.Status != ExecutionTokenRecordStatuses.Active
            || token.NodeId != task.NodeId)
        {
            throw new WorkflowConflictException("The selected user task execution token is no longer active.");
        }
        if (administrativeBatch is not null
            && (token.Id != administrativeBatch.Request.ExpectedTokenId
                || token.ActivationId
                != administrativeBatch.Request.ExpectedTokenActivationId
                || task.Id != administrativeBatch.Request.PositionId
                || task.NodeId != administrativeBatch.Request.SourceNodeId))
        {
            throw new WorkflowConflictException(
                "The execution token changed after batch selection.");
        }
        var workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var node = GetFlowNode(workflow.Definition, task.NodeId);
        if (task.InstanceId != instance.Id
            || task.NodeId != node.Id
            || task.MultiInstanceExecutionId is not null
            || expectedTaskId is not null && task.Id != expectedTaskId.Value)
            throw new WorkflowConflictException("The active user task is no longer current.");
        ActorContext executionActor;
        if (administrativeBatch is null)
        {
            var access = await ResolveUserTaskAccessAsync(
                task, instance, node, actor, true, cancellationToken,
                visibilityContext.AsOf);
            executionActor = access?.ExecutionActor ?? actor;
            if (!await IsUserTaskInboxVisibleAsync(
                    task.Id, executionActor, visibilityContext, cancellationToken))
            {
                return null;
            }
            if (access is null)
                throw new WorkflowDomainException(
                    "The actor is not assigned or authorized for this user task.");
            accessResolved?.Invoke(access);
            EnsureUserTaskActor(task, node, executionActor, requireActive: true);
        }
        else
        {
            executionActor = actor;
        }

        var flow = OutgoingFlows(workflow.Id, workflow.Definition, node.Id).SingleOrDefault(f => f.Id == flowId);
        if (flow is null || !BpmnFlowNodeTypes.IsUserTask(node.Type))
        {
            logger.LogWarning("Take flow {FlowId} rejected on instance {InstanceId}: flow not available from current node #{NodeId} ({NodeType}).",
                flowId, id, node.Id, node.Type);
            throw new WorkflowDomainException("The requested sequence flow is not available from the current node.");
        }
        if (!flow.IsSelectable || flow.IsDefault)
        {
            throw new WorkflowDomainException(
                "The requested sequence flow is an engine-only/default route and cannot be selected by a user.");
        }
        if (administrativeBatch is not null
            && (administrativeBatch.Request.ActionKind
                    != AdministrativeActionKinds.DirectFlow
                || administrativeBatch.Request.PositionKind
                    != AdministrativeActionPositionKinds.UserTask
                || administrativeBatch.Request.UserTaskId != task.Id
                || flow.SourceRef != node.Id))
        {
            throw new WorkflowDomainException(
                "The requested flow is not a compatible administrative batch action.");
        }

        logger.LogInformation("Taking sequence flow {FlowId} ({FlowName}) on instance {InstanceId} from node {SourceNodeId} ({SourceNodeType}) to {TargetNodeId} by user '{User}'",
            flowId, flow.Name, id, node.Id, node.Type, flow.TargetRef, performedBy ?? "anonymous");

        var actorRoles = NormalizeRoles(executionActor.Roles);
        if (administrativeBatch is null)
        {
            EnsureRoleAllowed(node, actorRoles, executionActor.User);
        }
        if (administrativeBatch is null
            && !RoleAllowed(flow.Roles, actorRoles))
        {
            logger.LogWarning("Take flow {FlowId} rejected on instance {InstanceId}: user '{User}' lacks a flow role ({FlowRoles}).",
                flowId, id, performedBy, string.Join(",", flow.Roles ?? []));
            throw new WorkflowDomainException(
                $"'{NormalizeUser(actor.User)}' does not have a role permitted to take this sequence flow.");
        }
        if (administrativeBatch is null)
        {
            EnsureActionAllowedByClaim(task, flow, executionActor);
        }

        var storedForValidation = await LoadVariablesAsync(instance.Id, cancellationToken);
        var taskInstance = instance with
        {
            ActiveTokenId = token.Id,
            CurrentStepId = token.NodeId,
            ActiveUserTaskId = task.Id,
            ClaimedBy = task.ClaimedBy
        };
        var storedFlowContext = WithContext(
            storedForValidation, executionActor, taskInstance, workflow.Definition, node,
            visibilityContext.AsOf);

        if (administrativeBatch is null
            && !string.IsNullOrWhiteSpace(flow.Condition)
            && !SequenceFlowConditionEvaluator.Evaluate(flow.Condition, storedFlowContext))
        {
            logger.LogWarning("Take flow {FlowId} ({FlowName}) rejected on instance {InstanceId}: flow condition '{Condition}' evaluated to false.",
                flowId, flow.Name, id, flow.Condition);
            throw new WorkflowDomainException(
                $"Sequence flow '{flow.Name}' condition is not satisfied: '{flow.Condition}'.");
        }

        ValidateVariableValues(flow.Variables, variableValues);

        // Resolve defaults and validate submitted values against the stored-state
        // context only after the action's visibility/guard condition has passed.
        var flowValues = ResolveAndValidateVariables(flow.Variables, variableValues, storedFlowContext);
        var flowContext = new Dictionary<string, JsonElement>(storedFlowContext, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in flowValues)
        {
            flowContext[pair.Key] = pair.Value;
        }

        var flowInfo = await LoadSequenceFlowInfoAsync(
            instance.Id,
            workflow.Definition,
            cancellationToken,
            force: administrativeBatch is not null);
        await runtime.CompleteUserTaskAsync(
            task.Id,
            flow.Id,
            performedBy ?? "anonymous",
            SnapshotRoles(executionActor.Roles),
            flowValues,
            cancellationToken,
            executionActor.ActingFor,
            executionActor.DelegationId,
            completionKind: administrativeBatch is null
                ? null
                : NodeExecutionCompletionReasons.AdministrativeAction,
            completionReason: administrativeBatch?.Reason,
            administrativeActionBatchId: administrativeBatch?.BatchId);
        await RecordSequenceFlowOccurrenceAsync(
            flowInfo,
            instance.Id,
            task.TokenId,
            task.Id,
            null,
            null,
            flow,
            administrativeBatch is null
                ? "userTaskAction"
                : NodeExecutionCompletionReasons.AdministrativeAction,
            isAction: true,
            isTraversal: !node.AsyncAfter,
            actor: executionActor,
            values: flowValues,
            cancellationToken: cancellationToken,
            administrativeBatch: administrativeBatch);

        foreach (var pair in flowValues)
        {
            await runtime.AddVariableAsync(
                instance.Id,
                pair.Key,
                flow.Id,
                performedBy,
                pair.Value,
                cancellationToken,
                task.NodeExecutionId,
                executionActor.ActingFor,
                executionActor.DelegationId);
        }

        var payload = CloneDictionary(flowValues) ?? [];
        await runtime.AddUserTaskActionHistoryAsync(
            instance.Id,
            task.TokenId,
            task.Id,
            flow.Id,
            node.Id,
            flow.TargetRef,
            performedBy ?? "anonymous",
            payload,
            cancellationToken,
            executionActor.ActingFor,
            executionActor.DelegationId,
            note: administrativeBatch is null
                ? null
                : NodeExecutionCompletionReasons.AdministrativeAction,
            reason: administrativeBatch?.Reason,
            administrativeActionBatchId: administrativeBatch?.BatchId);

        if (!await runtime.SetExecutionTokenAutomaticActivationCountAsync(
                token.Id,
                token.ActivationId,
                WorkflowAutomaticActivationGuard.ResetAfterExternalWaitOrTrigger(),
                cancellationToken))
        {
            throw new WorkflowConflictException(
                "The user-task execution token changed while resetting its automatic-activation count.");
        }

        if (node.AsyncAfter)
        {
            var waitingToken = await runtime.GetExecutionTokenAsync(
                    token.Id,
                    false,
                    cancellationToken)
                ?? throw new WorkflowConflictException(
                    "The user-task execution token disappeared before async-after.");
            await EnsureAsyncAfterWaitAsync(
                taskInstance,
                waitingToken,
                node,
                workflow.Definition,
                executionActor,
                cancellationToken,
                selectedFlowId: flow.Id,
                userTaskId: task.Id,
                administrativeBatch: administrativeBatch);
            await CancelAttachedTimerBoundaryWaitsAsync(
                instance.Id,
                [token.Id],
                cancellationToken);
            if (administrativeBatch is not null)
            {
                await CompleteAdministrativeBatchItemAsync(
                    administrativeBatch.Request,
                    instance.Id,
                    affectedTaskCount: 1,
                    cancellationToken);
            }
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return await BuildDetailAsync(id, cancellationToken);
        }

        var nextNode = GetFlowNode(workflow.Definition, flow.TargetRef);
        var targetTokenStatus = BpmnFlowNodeTypes.IsErrorEnd(nextNode.Type)
            ? ExecutionTokenRecordStatuses.Faulted
            : BpmnFlowNodeTypes.IsEnd(nextNode.Type)
                ? ExecutionTokenRecordStatuses.Completed
                : ExecutionTokenRecordStatuses.Active;
        var terminationReason = BpmnFlowNodeTypes.IsTerminateEnd(nextNode.Type)
            ? ExecutionTokenTerminationReasons.TerminateEnd
            : BpmnFlowNodeTypes.IsErrorEnd(nextNode.Type)
                ? ExecutionTokenTerminationReasons.ErrorEnd
                : BpmnFlowNodeTypes.IsEnd(nextNode.Type)
                    ? ExecutionTokenTerminationReasons.NormalEnd
                    : null;

        taskInstance = taskInstance with
        {
            CurrentStepId = nextNode.Id,
            ClaimedBy = null,
            FaultCode = BpmnFlowNodeTypes.IsErrorEnd(nextNode.Type) ? nextNode.ErrorCode : null,
            FaultDescription = BpmnFlowNodeTypes.IsErrorEnd(nextNode.Type)
                ? nextNode.ErrorDescription ?? nextNode.Name
                : null,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var nextContext = WithContext(
            flowContext, executionActor, taskInstance, workflow.Definition, nextNode);
        await CancelAttachedTimerBoundaryWaitsAsync(
            instance.Id,
            [token.Id],
            cancellationToken);
        await runtime.UpdateExecutionTokenAsync(
            token.Id,
            ToSnapshot(nextNode, nextContext, instance.Id),
            targetTokenStatus,
            token.GatewayBranchId,
            flow.Id,
            terminationReason,
            null,
            ToNodeExecutionActor(executionActor),
            null,
            cancellationToken,
            automaticActivationCount:
                WorkflowAutomaticActivationGuard.ResetAfterExternalWaitOrTrigger());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        try
        {
            if (BpmnFlowNodeTypes.IsTerminateEnd(nextNode.Type))
            {
                await TerminateInstanceAsync(instance.Id, token.Id, executionActor, cancellationToken);
            }
            else if (BpmnFlowNodeTypes.IsErrorEnd(nextNode.Type))
            {
                await FaultInstanceAsync(instance.Id, token.Id, executionActor, cancellationToken);
            }
            else if (BpmnFlowNodeTypes.IsEnd(nextNode.Type))
            {
                var remaining = await runtime.ListExecutionTokensAsync(
                    instance.Id, ExecutionTokenRecordStatuses.Active, cancellationToken);
                if (remaining.Count == 0)
                {
                    await runtime.SetInstanceStatusAsync(
                        instance.Id, WorkflowInstanceStatuses.Completed, cancellationToken);
                }
                await CloseInactiveGatewayScopesAsync(instance.Id, "allEnded", cancellationToken);
            }
            else
            {
                instance = await ResolvePassThroughAsync(
                    taskInstance, workflow.Definition, executionActor, flowInfo, token.Id, cancellationToken);
                await EnsureMultiInstanceInitializedAsync(
                    instance, workflow.Definition, executionActor, cancellationToken);
                instance = await ApplyUserTaskOwnershipInheritanceAsync(
                    instance, workflow.Definition, cancellationToken);
            }
        }
        catch (WorkflowDomainException exception) when (administrativeBatch is not null)
        {
            throw new AdministrativeActionExecutionException(exception.Message, exception);
        }

        if (administrativeBatch is not null)
        {
            await CompleteAdministrativeBatchItemAsync(
                administrativeBatch.Request,
                instance.Id,
                affectedTaskCount: 1,
                cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Successfully completed transition for instance {InstanceId} through flow {FlowId} from token {TokenId}.",
            instance.Id, flowId, token.Id);

        return await BuildDetailAsync(id, cancellationToken);
    }

    public async Task<MessageDeliveryAckDto?> DeliverMessageAsync(
        long id,
        IncomingMessage message,
        CancellationToken cancellationToken,
        string? catchEventExternalId = null)
    {
        // Authenticate without a row lock first so invalid anonymous requests do
        // not serialize valid work on a known instance. The exact wait occurrence
        // is captured and checked again after the lock.
        var preview = await runtime.GetInstanceAsync(id, cancellationToken);
        if (preview is null)
        {
            logger.LogInformation("Deliver message to instance {InstanceId}: instance not found.", id);
            return null;
        }

        var workflow = await GetWorkflowAsync(preview.WorkflowDefinitionId, cancellationToken);
        var committedReceipt = await FindCommittedMessageDeliveryAsync(
            preview.Id,
            workflow.Definition,
            message.Headers,
            cancellationToken);
        if (committedReceipt is not null)
        {
            ValidateReceiptAuthentication(committedReceipt, message);
            throw MessageIdempotencyConflict(committedReceipt);
        }

        ValidateMessageDeliveryState(preview);
        var previewToken = await SelectActiveMessageCatchTokenAsync(
            preview.Id, workflow.Definition, catchEventExternalId, cancellationToken);
        var previewNode = GetFlowNode(workflow.Definition, previewToken.NodeId);
        ValidateMessageCatchNode(previewNode);
        var previewAtCatch = preview with
        {
            ActiveTokenId = previewToken.Id,
            CurrentStepId = previewToken.NodeId,
            ActiveUserTaskId = null,
            ClaimedBy = null
        };
        var previewConfig = previewNode.Message
            ?? throw new WorkflowDomainException($"Message catch event #{previewNode.Id} has no message configuration.");
        var previewIdempotencyHeaderName = GetDeliveryIdempotencyHeaderName(previewConfig);
        var idempotencyKey = previewIdempotencyHeaderName is not null
            ? ReadRequiredMessageDeliveryKey(message.Headers, previewIdempotencyHeaderName)
            : null;

        if (idempotencyKey is not null)
        {
            var existingReceipt = await runtime.GetMessageDeliveryReceiptAsync(
                id, idempotencyKey, cancellationToken);
            if (existingReceipt is not null)
            {
                ValidateReceiptAuthentication(existingReceipt, message);
                throw MessageIdempotencyConflict(existingReceipt);
            }
        }

        var previewWaitHistoryId = await runtime.GetLatestTokenNodeEntryHistoryIdAsync(
            preview.Id, previewToken.Id, previewNode.Id, cancellationToken)
            ?? throw new WorkflowDomainException(
                $"Message catch event #{previewNode.Id} has no recorded wait activation.");
        await AuthenticateMessageCatchAsync(
            previewAtCatch, workflow.Definition, previewNode, previewConfig, message, cancellationToken);

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var instance = await runtime.GetInstanceForUpdateAsync(id, false, cancellationToken);
        if (instance is null)
        {
            logger.LogInformation("Deliver message to instance {InstanceId}: instance not found.", id);
            return null;
        }

        if (idempotencyKey is not null)
        {
            var existingReceipt = await runtime.GetMessageDeliveryReceiptAsync(
                id, idempotencyKey, cancellationToken);
            if (existingReceipt is not null)
            {
                ValidateReceiptAuthentication(existingReceipt, message);
                throw MessageIdempotencyConflict(existingReceipt);
            }
        }

        ValidateMessageDeliveryState(instance);
        workflow = await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var token = await runtime.GetExecutionTokenAsync(previewToken.Id, true, cancellationToken);
        if (token is null
            || token.InstanceId != instance.Id
            || token.Status != ExecutionTokenRecordStatuses.Active
            || token.NodeId != previewNode.Id)
        {
            throw new MessageDeliveryConflictException(
                "message_wait_conflict",
                instance.Id,
                previewNode.Id,
                "Another request consumed this message wait activation.");
        }
        var node = GetFlowNode(workflow.Definition, token.NodeId);
        ValidateMessageCatchNode(node);
        var waitHistoryId = await runtime.GetLatestTokenNodeEntryHistoryIdAsync(
            instance.Id, token.Id, node.Id, cancellationToken);
        if (waitHistoryId != previewWaitHistoryId)
        {
            throw new MessageDeliveryConflictException(
                "message_wait_conflict",
                instance.Id,
                previewNode.Id,
                "Another request consumed this message wait activation.");
        }

        logger.LogInformation("Delivering message to catch node #{NodeId} ({NodeName}) on instance {InstanceId} for client '{ClientId}'",
            node.Id, node.Name, id, message.ClientId);

        var messageConfig = node.Message
            ?? throw new WorkflowDomainException($"Message catch event #{node.Id} has no message configuration.");
        var idempotencyHeaderName = GetDeliveryIdempotencyHeaderName(messageConfig);
        if ((idempotencyHeaderName is not null) != (idempotencyKey is not null)
            || !string.Equals(
                idempotencyHeaderName,
                previewIdempotencyHeaderName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new MessageDeliveryConflictException(
                "message_wait_conflict",
                instance.Id,
                previewNode.Id,
                "The message wait configuration changed while the request was in progress.");
        }

        var authentication = await AuthenticateMessageCatchAsync(
            instance with { ActiveTokenId = token.Id, CurrentStepId = token.NodeId },
            workflow.Definition,
            node,
            messageConfig,
            message,
            cancellationToken);
        var actor = authentication.Actor;
        var performedBy = actor.User;
        var stored = authentication.StoredVariables;

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
            token.CurrentNodeExecutionId,
            cancellationToken);
        foreach (var pair in mappedValues)
        {
            stored[pair.Key] = pair.Value;
        }

        // Advance down the single unconditional outgoing flow (ValidateDefinition
        // enforced exactly one for a message catch event). SingleOrDefault + a
        // domain exception keeps a malformed legacy definition from surfacing as a
        // bare 500 (matching SelectPassThroughFlow's style).
        var outgoing = OutgoingFlows(workflow.Id, workflow.Definition, node.Id).Take(2).ToList();
        if (outgoing.Count != 1)
        {
            throw new WorkflowDomainException(
                $"Message catch event #{node.Id} must have exactly one outgoing sequence flow.");
        }
        var flow = outgoing[0];
        var nextNode = GetFlowNode(workflow.Definition, flow.TargetRef);
        var targetTokenStatus = BpmnFlowNodeTypes.IsErrorEnd(nextNode.Type)
            ? ExecutionTokenRecordStatuses.Faulted
            : BpmnFlowNodeTypes.IsEnd(nextNode.Type)
                ? ExecutionTokenRecordStatuses.Completed
                : ExecutionTokenRecordStatuses.Active;
        var terminationReason = BpmnFlowNodeTypes.IsTerminateEnd(nextNode.Type)
            ? ExecutionTokenTerminationReasons.TerminateEnd
            : BpmnFlowNodeTypes.IsErrorEnd(nextNode.Type)
                ? ExecutionTokenTerminationReasons.ErrorEnd
                : BpmnFlowNodeTypes.IsEnd(nextNode.Type)
                    ? ExecutionTokenTerminationReasons.NormalEnd
                    : null;

        var flowInfo = await LoadSequenceFlowInfoAsync(
            instance.Id, workflow.Definition, cancellationToken);
        await RecordSequenceFlowOccurrenceAsync(
            flowInfo,
            instance.Id,
            token.Id,
            null,
            null,
            null,
            flow,
            "messageCatch",
            isAction: false,
            isTraversal: true,
            actor: actor,
            values: null,
            cancellationToken: cancellationToken);

        await runtime.AddTokenHistoryAsync(
            instance.Id,
            token.Id,
            null,
            node.Id,
            nextNode.Id,
            performedBy,
            null,
            "message",
            cancellationToken);

        var tokenInstance = instance with
        {
            ActiveTokenId = token.Id,
            CurrentStepId = nextNode.Id,
            ClaimedBy = null,
            FaultCode = BpmnFlowNodeTypes.IsErrorEnd(nextNode.Type) ? nextNode.ErrorCode : null,
            FaultDescription = BpmnFlowNodeTypes.IsErrorEnd(nextNode.Type)
                ? nextNode.ErrorDescription ?? nextNode.Name
                : null,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var nextContext = WithContext(stored, actor, tokenInstance, workflow.Definition, nextNode);
        await CancelAttachedTimerBoundaryWaitsAsync(
            instance.Id,
            [token.Id],
            cancellationToken);
        await runtime.UpdateExecutionTokenAsync(
            token.Id,
            ToSnapshot(nextNode, nextContext, instance.Id),
            targetTokenStatus,
            token.GatewayBranchId,
            flow.Id,
            terminationReason,
            null,
            ToNodeExecutionActor(actor),
            new NodeExecutionCompletionRecord(
                NodeExecutionRecordStatuses.Completed,
                NodeExecutionCompletionReasons.MessageDelivery,
                null,
                flow.Id,
                token.GatewayBranchId,
                ToNodeExecutionActor(actor)),
            cancellationToken,
            automaticActivationCount:
                WorkflowAutomaticActivationGuard.ResetAfterExternalWaitOrTrigger());
        await unitOfWork.SaveChangesAsync(cancellationToken);
        if (BpmnFlowNodeTypes.IsTerminateEnd(nextNode.Type))
        {
            await TerminateInstanceAsync(instance.Id, token.Id, actor, cancellationToken);
        }
        else if (BpmnFlowNodeTypes.IsErrorEnd(nextNode.Type))
        {
            await FaultInstanceAsync(instance.Id, token.Id, actor, cancellationToken);
        }
        else if (BpmnFlowNodeTypes.IsEnd(nextNode.Type))
        {
            var remaining = await runtime.ListExecutionTokensAsync(
                instance.Id, ExecutionTokenRecordStatuses.Active, cancellationToken);
            if (remaining.Count == 0)
            {
                await runtime.SetInstanceStatusAsync(
                    instance.Id, WorkflowInstanceStatuses.Completed, cancellationToken);
            }
            await CloseInactiveGatewayScopesAsync(instance.Id, "allEnded", cancellationToken);
        }
        else
        {
            instance = await ResolvePassThroughAsync(
                tokenInstance, workflow.Definition, actor, flowInfo, token.Id, cancellationToken);
            await EnsureMultiInstanceInitializedAsync(instance, workflow.Definition, actor, cancellationToken);
            instance = await ApplyUserTaskOwnershipInheritanceAsync(
                instance, workflow.Definition, cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        instance = await runtime.GetInstanceAsync(instance.Id, cancellationToken) ?? instance;

        var restingNode = GetFlowNode(workflow.Definition, instance.CurrentStepId);
        var ack = new MessageDeliveryAckDto(
            instance.Id,
            restingNode.Id,
            restingNode.Name,
            restingNode.ExternalId,
            instance.Status,
            instance.UpdatedAt,
            ToFault(
                instance.Status,
                instance.FaultCode,
                instance.FaultDescription,
                restingNode.Name));

        if (idempotencyKey is not null)
        {
            var proofs = CreateMessageDeliveryProofs(
                authentication.ClientId,
                authentication.ClientSecret,
                authentication.HeaderName,
                authentication.HeaderValue);
            await runtime.AddMessageDeliveryReceiptAsync(
                new MessageDeliveryReceiptRecord(
                    instance.Id,
                    idempotencyKey,
                    previewWaitHistoryId,
                    node.Id,
                    authentication.HeaderName,
                    MessageDeliveryProofVersion,
                    proofs.CredentialSalt,
                    proofs.CredentialHash,
                    proofs.EnvelopeSalt,
                    proofs.EnvelopeHash,
                    timeProvider.GetUtcNow()),
                cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Successfully delivered message to instance {InstanceId} on node {NodeId}. Advancing to {NextNodeId} ({NextNodeType})",
            instance.Id, node.Id, instance.CurrentStepId, restingNode.Type);

        var projection = await BuildExecutionProjectionAsync(instance, cancellationToken);
        return ack with
        {
            ExecutionPositions = projection.ExecutionPositions,
            Completion = projection.Completion
        };
    }

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

    private async Task<MessageStartAuthentication> AuthenticateMessageStartAsync(
        string workflowKey,
        WorkflowModel definition,
        FlowNodeModel node,
        IncomingMessage message,
        CancellationToken cancellationToken)
    {
        await RefreshSettingsAsync(cancellationToken);

        var messageConfig = node.Message
            ?? throw new WorkflowDomainException(
                $"Message start event #{node.Id} has no message configuration.");
        var authContext = BuildAuthContext(
            new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase),
            instance: null,
            definition,
            node);
        if (!TryResolveRequiredScalar(messageConfig.ClientId, authContext, out var expectedClientId)
            || !TryResolveRequiredScalar(messageConfig.ClientSecret, authContext, out var expectedClientSecret))
        {
            logger.LogError(
                "Message start node {NodeId} on workflowKey {WorkflowKey} has unresolved client credentials.",
                node.Id,
                workflowKey);
            throw new WorkflowUnauthorizedException("Invalid client credentials.");
        }

        var clientId = ReadSingleCredentialHeader(message.Headers, "X-Client-Id");
        var clientSecret = ReadSingleCredentialHeader(message.Headers, "X-Client-Secret");
        if (clientId.EnumerateRunes().Take(UserTaskConstraints.MaxActorNameLength + 1).Count()
                > UserTaskConstraints.MaxActorNameLength
            || !string.Equals(clientId, expectedClientId, StringComparison.Ordinal)
            || !ConstantTimeEquals(clientSecret, expectedClientSecret))
        {
            logger.LogWarning(
                "Message start on workflowKey {WorkflowKey} rejected: invalid client credentials (client id '{ClientId}').",
                workflowKey,
                clientId);
            throw new WorkflowUnauthorizedException("Invalid client credentials.");
        }

        if (!TryResolveRequiredScalar(messageConfig.HeaderName, authContext, out var expectedHeaderName)
            || !TryResolveRequiredScalar(messageConfig.HeaderValue, authContext, out var expectedHeaderValue))
        {
            logger.LogError(
                "Message start node {NodeId} on workflowKey {WorkflowKey} has unresolved correlation configuration.",
                node.Id,
                workflowKey);
            throw new WorkflowDomainException("The message correlation configuration is invalid.");
        }

        expectedHeaderName = expectedHeaderName.Trim();
        ValidateResolvedMessageHeaderName(
            expectedHeaderName,
            GetReservedMessageHeaderNames(node.Idempotency?.HeaderName));
        var incomingHeaderValue = ReadSingleCorrelationHeader(message.Headers, expectedHeaderName);
        if (!ConstantTimeEquals(incomingHeaderValue, expectedHeaderValue))
        {
            throw new WorkflowDomainException(
                $"Header '{expectedHeaderName}' does not match the expected value.");
        }

        if (!string.IsNullOrWhiteSpace(messageConfig.HeaderValidation))
        {
            var validationContext = new Dictionary<string, JsonElement>(
                authContext,
                StringComparer.OrdinalIgnoreCase)
            {
                ["header"] = JsonSerializer.SerializeToElement(incomingHeaderValue)
            };
            if (!SequenceFlowConditionEvaluator.Evaluate(
                    messageConfig.HeaderValidation,
                    validationContext))
            {
                throw new WorkflowDomainException(
                    $"Header '{expectedHeaderName}' failed validation: '{messageConfig.HeaderValidation}'.");
            }
        }

        return new MessageStartAuthentication(
            new ActorContext(clientId, [], message.Actor.Claims),
            authContext);
    }

    private async Task<MessageCatchAuthentication> AuthenticateMessageCatchAsync(
        WorkflowInstanceRecord instance,
        WorkflowModel definition,
        FlowNodeModel node,
        MessageCatchModel messageConfig,
        IncomingMessage message,
        CancellationToken cancellationToken)
    {
        await RefreshSettingsAsync(cancellationToken);

        var stored = await LoadVariablesAsync(instance.Id, cancellationToken);
        var authContext = BuildAuthContext(stored, instance, definition, node);
        if (!TryResolveRequiredScalar(messageConfig.ClientId, authContext, out var expectedClientId)
            || !TryResolveRequiredScalar(messageConfig.ClientSecret, authContext, out var expectedClientSecret))
        {
            logger.LogError(
                "Message catch node {NodeId} on instance {InstanceId} has unresolved client credentials.",
                node.Id,
                instance.Id);
            throw new WorkflowUnauthorizedException("Invalid client credentials.");
        }

        var clientId = ReadSingleCredentialHeader(message.Headers, "X-Client-Id");
        var clientSecret = ReadSingleCredentialHeader(message.Headers, "X-Client-Secret");
        if (clientId.EnumerateRunes().Take(UserTaskConstraints.MaxActorNameLength + 1).Count()
                > UserTaskConstraints.MaxActorNameLength
            || !string.Equals(clientId, expectedClientId, StringComparison.Ordinal)
            || !ConstantTimeEquals(clientSecret, expectedClientSecret))
        {
            logger.LogWarning(
                "Deliver message to instance {InstanceId} rejected: invalid client credentials (client id '{ClientId}').",
                instance.Id,
                clientId);
            throw new WorkflowUnauthorizedException("Invalid client credentials.");
        }

        if (!TryResolveRequiredScalar(messageConfig.HeaderName, authContext, out var expectedHeaderName)
            || !TryResolveRequiredScalar(messageConfig.HeaderValue, authContext, out var expectedHeaderValue))
        {
            logger.LogError(
                "Message catch node {NodeId} on instance {InstanceId} has unresolved correlation configuration.",
                node.Id,
                instance.Id);
            throw new WorkflowDomainException("The message correlation configuration is invalid.");
        }

        expectedHeaderName = expectedHeaderName.Trim();
        ValidateResolvedMessageHeaderName(
            expectedHeaderName,
            GetReservedMessageHeaderNames(GetDeliveryIdempotencyHeaderName(messageConfig)));
        var incomingHeaderValue = ReadSingleCorrelationHeader(
            message.Headers,
            expectedHeaderName);
        if (!ConstantTimeEquals(incomingHeaderValue, expectedHeaderValue))
        {
            throw new WorkflowDomainException(
                $"Header '{expectedHeaderName}' does not match the expected value.");
        }

        var actor = new ActorContext(
            clientId,
            [],
            message.Actor.Claims);
        if (!string.IsNullOrWhiteSpace(messageConfig.HeaderValidation))
        {
            var fullContext = WithContext(stored, actor, instance, definition, node);
            var validationContext = new Dictionary<string, JsonElement>(
                fullContext,
                StringComparer.OrdinalIgnoreCase)
            {
                ["header"] = JsonSerializer.SerializeToElement(incomingHeaderValue)
            };
            if (!SequenceFlowConditionEvaluator.Evaluate(
                    messageConfig.HeaderValidation,
                    validationContext))
            {
                throw new WorkflowDomainException(
                    $"Header '{expectedHeaderName}' failed validation: '{messageConfig.HeaderValidation}'.");
            }
        }

        return new MessageCatchAuthentication(
            actor,
            clientId,
            clientSecret,
            expectedHeaderName,
            incomingHeaderValue,
            stored);
    }

    private static void ValidateMessageDeliveryState(WorkflowInstanceRecord instance)
    {
        if (instance.Status != WorkflowInstanceStatuses.Running)
        {
            throw new WorkflowDomainException("Only running instances can receive a message.");
        }
    }

    private static void ValidateMessageCatchNode(FlowNodeModel node)
    {
        if (!BpmnFlowNodeTypes.IsMessageCatch(node.Type))
        {
            throw new WorkflowDomainException(
                "The instance is not currently waiting for a message.");
        }
    }

    private async Task<ExecutionTokenRecord> SelectActiveMessageCatchTokenAsync(
        long instanceId,
        WorkflowModel definition,
        string? externalId,
        CancellationToken cancellationToken)
    {
        var catches = (await runtime.ListExecutionTokensAsync(
                instanceId, ExecutionTokenRecordStatuses.Active, cancellationToken))
            .Where(token => BpmnFlowNodeTypes.IsMessageCatch(token.NodeType))
            .Where(token => string.IsNullOrWhiteSpace(externalId)
                            || string.Equals(token.NodeExternalId, externalId, StringComparison.Ordinal))
            .OrderBy(token => token.Id)
            .ToList();
        if (catches.Count == 1)
        {
            return catches[0];
        }
        if (catches.Count == 0)
        {
            throw new WorkflowDomainException(
                string.IsNullOrWhiteSpace(externalId)
                    ? "The workflow instance is not waiting for a message."
                    : $"The workflow instance is not waiting on message catch '{externalId}'.");
        }
        throw new WorkflowConflictException(
            string.IsNullOrWhiteSpace(externalId)
                ? "The instance has multiple active message catches; provide catchEvent."
                : $"More than one active token is waiting on message catch '{externalId}'.");
    }

    private static bool TryResolveRequiredScalar(
        string? template,
        IReadOnlyDictionary<string, JsonElement> context,
        out string value)
    {
        var resolved = ServiceTaskTemplating.TrySubstituteScalarStrict(
            template,
            context,
            out value,
            out _);
        return resolved && !string.IsNullOrWhiteSpace(value);
    }

    private static string ReadSingleCredentialHeader(
        IReadOnlyDictionary<string, IReadOnlyList<string>> headers,
        string name)
    {
        if (!headers.TryGetValue(name, out var values)
            || values.Count != 1
            || string.IsNullOrEmpty(values[0]))
        {
            throw new WorkflowUnauthorizedException("Invalid client credentials.");
        }

        return values[0];
    }

    private static string ReadSingleCorrelationHeader(
        IReadOnlyDictionary<string, IReadOnlyList<string>> headers,
        string name)
    {
        if (!headers.TryGetValue(name, out var values) || values.Count == 0)
        {
            throw new WorkflowDomainException($"Required header '{name}' is missing.");
        }

        if (values.Count != 1)
        {
            throw new WorkflowDomainException(
                $"Required header '{name}' must contain exactly one value.");
        }

        return values[0];
    }

    private static void ValidateResolvedMessageHeaderName(
        string name,
        IReadOnlyCollection<string> reservedHeaderNames)
    {
        if (name.Length > 300
            || !Regex.IsMatch(name, @"^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$"))
        {
            throw new WorkflowDomainException(
                "The resolved message correlation header name is invalid.");
        }

        if (name.Equals("X-Client-Id", StringComparison.OrdinalIgnoreCase)
            || name.Equals("X-Client-Secret", StringComparison.OrdinalIgnoreCase)
            || reservedHeaderNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            throw new WorkflowDomainException(
                "The resolved message correlation header name is reserved.");
        }
    }

    private static string[] GetReservedMessageHeaderNames(string? idempotencyHeaderName)
    {
        if (string.IsNullOrWhiteSpace(idempotencyHeaderName))
        {
            return [];
        }

        var normalized = idempotencyHeaderName.Trim();
        return string.Equals(normalized, IdempotencyHeaders.Standard, StringComparison.OrdinalIgnoreCase)
            ? [IdempotencyHeaders.Standard, IdempotencyHeaders.LegacyAlias]
            : [normalized];
    }

    private async Task<MessageDeliveryReceiptRecord?> FindCommittedMessageDeliveryAsync(
        long instanceId,
        WorkflowModel definition,
        IReadOnlyDictionary<string, IReadOnlyList<string>> headers,
        CancellationToken cancellationToken)
    {
        var candidateKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in definition.FlowNodes.Where(node =>
                     BpmnFlowNodeTypes.IsMessageCatch(node.Type)
                     && node.Message?.DeliveryIdempotency == true))
        {
            var headerName = GetDeliveryIdempotencyHeaderName(node.Message!);
            if (headerName is null
                || !TryReadMessageDeliveryKey(headers, headerName, out var key)
                || !candidateKeys.Add(key))
            {
                continue;
            }

            var receipt = await runtime.GetMessageDeliveryReceiptAsync(
                instanceId,
                key,
                cancellationToken);
            if (receipt is not null)
            {
                return receipt;
            }
        }

        return null;
    }

    private static string? GetDeliveryIdempotencyHeaderName(MessageCatchModel messageConfig)
    {
        if (!messageConfig.DeliveryIdempotency)
        {
            return null;
        }

        var headerName = string.IsNullOrWhiteSpace(messageConfig.DeliveryIdempotencyHeaderName)
            ? IdempotencyHeaders.Standard
            : messageConfig.DeliveryIdempotencyHeaderName.Trim();
        if (headerName.Length > 300
            || !Regex.IsMatch(headerName, @"^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$")
            || ReservedMessageDeliveryIdempotencyHeaders.Contains(headerName))
        {
            throw new WorkflowDomainException(
                "The resolved message delivery idempotency header name is invalid or reserved.");
        }

        return headerName;
    }

    private static readonly HashSet<string> ReservedMessageDeliveryIdempotencyHeaders = new(
        [
            "Authorization",
            "Proxy-Authorization",
            "Cookie",
            "Host",
            "Content-Length",
            "Content-Type",
            "Content-Encoding",
            "Transfer-Encoding",
            "Connection",
            "Keep-Alive",
            "TE",
            "Trailer",
            "Upgrade",
            "Expect",
            "X-Client-Id",
            "X-Client-Secret"
        ],
        StringComparer.OrdinalIgnoreCase);

    private static string ReadRequiredMessageDeliveryKey(
        IReadOnlyDictionary<string, IReadOnlyList<string>> headers,
        string headerName)
    {
        if (!TryReadMessageDeliveryKey(headers, headerName, out var key))
        {
            throw new WorkflowDomainException(
                $"A valid '{headerName}' idempotency header is required for this message catch event.");
        }

        return key;
    }

    private static bool TryReadMessageDeliveryKey(
        IReadOnlyDictionary<string, IReadOnlyList<string>> headers,
        string headerName,
        out string key)
    {
        key = string.Empty;
        if (!string.Equals(headerName, IdempotencyHeaders.Standard, StringComparison.OrdinalIgnoreCase))
        {
            return headers.TryGetValue(headerName, out var configuredValues)
                && TryNormalizeMessageDeliveryKey(configuredValues, out key);
        }

        var hasStandard = headers.TryGetValue(IdempotencyHeaders.Standard, out var standardValues);
        var hasAlias = headers.TryGetValue(IdempotencyHeaders.LegacyAlias, out var aliasValues);
        if (!hasStandard && !hasAlias)
        {
            return false;
        }
        if ((hasStandard && standardValues!.Count != 1)
            || (hasAlias && aliasValues!.Count != 1))
        {
            return false;
        }

        var standard = hasStandard ? standardValues![0].Trim() : null;
        var alias = hasAlias ? aliasValues![0].Trim() : null;
        if (standard is not null && alias is not null
            && !string.Equals(standard, alias, StringComparison.Ordinal))
        {
            return false;
        }

        key = standard ?? alias ?? string.Empty;
        return !string.IsNullOrWhiteSpace(key)
               && key.EnumerateRunes().Take(301).Count() <= 300;
    }

    private static bool TryNormalizeMessageDeliveryKey(
        IReadOnlyList<string> values,
        out string key)
    {
        key = string.Empty;
        if (values.Count != 1)
        {
            return false;
        }

        key = values[0].Trim();
        return key.Length > 0
               && key.EnumerateRunes().Take(301).Count() <= 300;
    }

    private static MessageDeliveryConflictException MessageIdempotencyConflict(
        MessageDeliveryReceiptRecord receipt) =>
        new(
            "idempotency_conflict",
            receipt.InstanceId,
            receipt.SourceNodeId,
            "This message delivery idempotency key has already been committed.");

    private static void ValidateReceiptAuthentication(
        MessageDeliveryReceiptRecord receipt,
        IncomingMessage message)
    {
        if (receipt.ProofVersion != MessageDeliveryProofVersion)
        {
            throw new WorkflowUnauthorizedException("Invalid client credentials.");
        }

        var clientId = ReadSingleCredentialHeader(message.Headers, "X-Client-Id");
        var clientSecret = ReadSingleCredentialHeader(message.Headers, "X-Client-Secret");
        if (!VerifyMessageDeliveryProof(
                BuildProofInput("credentials", clientId, clientSecret),
                receipt.CredentialProofSalt,
                receipt.CredentialProofHash))
        {
            throw new WorkflowUnauthorizedException("Invalid client credentials.");
        }

        var headerValue = ReadSingleCorrelationHeader(
            message.Headers,
            receipt.CorrelationHeaderName);
        if (!VerifyMessageDeliveryProof(
                BuildProofInput(
                    "envelope",
                    clientId,
                    clientSecret,
                    receipt.CorrelationHeaderName.ToLowerInvariant(),
                    headerValue),
                receipt.EnvelopeProofSalt,
                receipt.EnvelopeProofHash))
        {
            throw new WorkflowDomainException(
                $"Header '{receipt.CorrelationHeaderName}' does not match the expected value.");
        }
    }

    private static MessageDeliveryProofs CreateMessageDeliveryProofs(
        string clientId,
        string clientSecret,
        string headerName,
        string headerValue)
    {
        var credentialSalt = RandomNumberGenerator.GetBytes(MessageDeliveryProofSaltBytes);
        var envelopeSalt = RandomNumberGenerator.GetBytes(MessageDeliveryProofSaltBytes);
        return new MessageDeliveryProofs(
            credentialSalt,
            DeriveMessageDeliveryProof(
                BuildProofInput("credentials", clientId, clientSecret),
                credentialSalt),
            envelopeSalt,
            DeriveMessageDeliveryProof(
                BuildProofInput(
                    "envelope",
                    clientId,
                    clientSecret,
                    headerName.ToLowerInvariant(),
                    headerValue),
                envelopeSalt));
    }

    private static byte[] BuildProofInput(string purpose, params string[] values)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(MessageDeliveryProofVersion);
        writer.Write(purpose);
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] DeriveMessageDeliveryProof(byte[] input, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            input,
            salt,
            MessageDeliveryProofIterations,
            HashAlgorithmName.SHA256,
            MessageDeliveryProofHashBytes);

    private static bool VerifyMessageDeliveryProof(
        byte[] input,
        byte[] salt,
        byte[] expectedHash)
    {
        var actual = DeriveMessageDeliveryProof(input, salt);
        return CryptographicOperations.FixedTimeEquals(actual, expectedHash);
    }

    private const short MessageDeliveryProofVersion = 1;
    private const int MessageDeliveryProofIterations = 100_000;
    private const int MessageDeliveryProofSaltBytes = 16;
    private const int MessageDeliveryProofHashBytes = 32;

    private sealed record MessageStartAuthentication(
        ActorContext Actor,
        Dictionary<string, JsonElement> AuthContext);

    private sealed record MessageCatchAuthentication(
        ActorContext Actor,
        string ClientId,
        string ClientSecret,
        string HeaderName,
        string HeaderValue,
        Dictionary<string, JsonElement> StoredVariables);

    private sealed record MessageDeliveryProofs(
        byte[] CredentialSalt,
        byte[] CredentialHash,
        byte[] EnvelopeSalt,
        byte[] EnvelopeHash);

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

                if (!IsTypedOutputValueValid(value, mapping))
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
            Nullable = mapping.ProcessNullable == true,
            Required = mapping.Required,
            DefaultValue = mapping.DefaultValue,
            Validation = mapping.Validation
        }).ToList();

        var resolved = ResolveVariables(declarations, supplied, contextBase);
        ValidateVariableValues(declarations, resolved);
        foreach (var mapping in mappings)
        {
            if (TryGetValue(resolved, mapping.Variable, out var value)
                && !IsTypedOutputValueValid(value, mapping))
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
            processVariable?.Nullable,
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
            processVariable?.Nullable,
            mapping.DefaultValue,
            mapping.Validation);
    }

    // A mapping that targets a declared process variable inherits its nullable
    // contract. Undeclared outputs retain the historical typed-output behavior,
    // including JSON mappings accepting JSON null.
    private static bool IsTypedOutputValueValid(JsonElement value, TypedOutputRuntime mapping)
    {
        if ((value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            && mapping.ProcessNullable is { } nullable)
        {
            return nullable;
        }

        return TypedOutputValueValidator.IsValid(value, mapping.DataType, mapping.IsArray);
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
        long? nodeExecutionId,
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
            await runtime.AddVariableAsync(
                instanceId,
                pair.Key,
                nodeId,
                setBy,
                pair.Value,
                cancellationToken,
                nodeExecutionId);
        }
        return values;
    }

    // Compare fixed-size digests so differing input lengths do not return early.
    private static bool ConstantTimeEquals(string a, string b)
    {
        var left = SHA256.HashData(Encoding.UTF8.GetBytes(a));
        var right = SHA256.HashData(Encoding.UTF8.GetBytes(b));
        return CryptographicOperations.FixedTimeEquals(left, right);
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

        var gatewayExecutions = await runtime.ListGatewayExecutionsAsync(
            id,
            GatewayExecutionRecordStatuses.Active,
            cancellationToken);
        var gatewayBranches = await runtime.ListGatewayBranchesForInstanceAsync(
            id,
            true,
            cancellationToken);
        await runtime.SetGatewayBranchesStatusAsync(
            gatewayBranches.Select(branch => branch.Id).ToArray(),
            GatewayBranchRecordStatuses.Cancelled,
            cancellationToken);
        await runtime.SetGatewayExecutionsStatusAsync(
            gatewayExecutions.Select(execution => execution.Id).ToArray(),
            GatewayExecutionRecordStatuses.Cancelled,
            "instanceCancel",
            null,
            null,
            cancellationToken);

        var activeTokens = await runtime.ListExecutionTokensAsync(
            id, ExecutionTokenRecordStatuses.Active, cancellationToken);
        var activeTokenIds = activeTokens.Select(token => token.Id).ToList();
        var cancellationActor = ToNodeExecutionActor(actor);
        await CancelDurableWorkForTokensAsync(
            instance.Id,
            activeTokenIds,
            "instanceCancelled",
            cancellationToken);
        await runtime.CancelActiveMultiInstancesForTokensAsync(
            activeTokenIds,
            NodeExecutionCompletionReasons.InstanceCancelled,
            cancellationActor,
            cancellationToken);
        await runtime.CancelOpenUserTasksForTokensAsync(
            activeTokenIds,
            NodeExecutionCompletionReasons.InstanceCancelled,
            cancellationActor,
            cancellationToken);
        await runtime.SetExecutionTokensStatusAsync(
            activeTokenIds,
            ExecutionTokenRecordStatuses.Cancelled,
            ExecutionTokenTerminationReasons.InstanceCancelled,
            NodeExecutionCompletionReasons.InstanceCancelled,
            cancellationActor,
            cancellationToken);
        await runtime.SetInstanceStatusAsync(
            instance.Id, WorkflowInstanceStatuses.Cancelled, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Instance {InstanceId} cancelled by user '{User}'.", id, actor.User ?? "anonymous");

        return true;
    }

    private Task<WorkflowInstanceRecord> ResolvePassThroughAsync(
        WorkflowInstanceRecord instance,
        WorkflowModel definition,
        ActorContext actor,
        SequenceFlowInfoSnapshot? flowInfo,
        CancellationToken cancellationToken) =>
        ResolvePassThroughAsync(instance, definition, actor, flowInfo, instance.ActiveTokenId, cancellationToken);

    private async Task<WorkflowInstanceRecord> ResolvePassThroughAsync(
        WorkflowInstanceRecord instance,
        WorkflowModel definition,
        ActorContext actor,
        SequenceFlowInfoSnapshot? flowInfo,
        long startingTokenId,
        CancellationToken cancellationToken,
        PassThroughResume? resume = null,
        bool forceDurableActivities = false,
        AdministrativeInitialTraversal? administrativeInitialTraversal = null)
    {
        var queue = new Queue<long>();
        queue.Enqueue(startingTokenId);
        var tokenHops = new Dictionary<long, int>();
        var maxHops = definition.FlowNodes.Count + 1;
        // The per-token guard catches ordinary automatic cycles, but a gateway
        // fork assigns fresh token ids. Without a second, transaction-wide
        // budget, a fork whose reused branch terminates and whose spawned branch
        // loops back to the split can reset the per-token budget forever while
        // retaining the instance lock. The graph-derived global bound remains
        // generous for finite fan-out while guaranteeing that every routing
        // transaction eventually yields or fails.
        const long hardRoutingStepLimit = 10_000;
        var maxRoutingSteps = Math.Min(
            hardRoutingStepLimit,
            (long)maxHops * Math.Max(maxHops, definition.SequenceFlows.Count + 1));
        long routingSteps = 0;
        var pendingAdministrativeTraversal = administrativeInitialTraversal;
        var storedOverlay = await LoadVariablesAsync(instance.Id, cancellationToken);

        while (true)
        {
            while (queue.Count > 0)
            {
                if (!await IsInstanceRunningAsync(instance.Id, cancellationToken))
                {
                    queue.Clear();
                    break;
                }

                var tokenId = queue.Dequeue();
                var token = await runtime.GetExecutionTokenAsync(tokenId, false, cancellationToken);
                if (token is null || token.Status != ExecutionTokenRecordStatuses.Active)
                {
                    continue;
                }

                if (routingSteps >= maxRoutingSteps)
                {
                    logger.LogError(
                        "Pass-through routing work limit reached on instance {InstanceId} after {RoutingSteps} steps (limit {MaxRoutingSteps}).",
                        instance.Id,
                        routingSteps,
                        maxRoutingSteps);
                    throw new WorkflowDomainException("Pass-through routing cycle detected.");
                }
                routingSteps++;

                var hop = tokenHops.GetValueOrDefault(tokenId);
                if (hop >= maxHops)
                {
                    logger.LogError(
                        "Pass-through routing cycle detected on instance {InstanceId}, token {TokenId}, after {MaxHops} hops.",
                        instance.Id, tokenId, maxHops);
                    throw new WorkflowDomainException("Pass-through routing cycle detected.");
                }
                tokenHops[tokenId] = hop + 1;

                var currentNode = GetFlowNode(definition, token.NodeId);
                var tokenInstance = instance with
                {
                    ActiveTokenId = token.Id,
                    CurrentStepId = token.NodeId,
                    CurrentNodeExecutionId = token.CurrentNodeExecutionId,
                    FaultCode = token.FaultCode,
                    FaultDescription = token.FaultDescription
                };

                var resumesCurrentActivation = resume is not null
                    && resume.ActivationId == token.ActivationId;
                var isExternalOrCpuActivity =
                    BpmnFlowNodeTypes.IsServiceTask(currentNode.Type)
                    || BpmnFlowNodeTypes.IsScriptTask(currentNode.Type);
                var requiresDurableEntry = currentNode.AsyncBefore
                    || isExternalOrCpuActivity
                       && (currentNode.AsyncAfter || forceDurableActivities);
                var resumesCurrentDurablePhase = resumesCurrentActivation
                    && resume!.Phase is WorkflowJobKinds.AsyncBefore
                        or WorkflowJobKinds.AsyncAfter;
                if (requiresDurableEntry && !resumesCurrentDurablePhase)
                {
                    await EnsureAsyncBeforeWaitAsync(
                        tokenInstance,
                        token,
                        currentNode,
                        definition,
                        actor,
                        cancellationToken);
                    // Boundary clocks start when the durable entry job actually
                    // activates the node, not while async-before is queued.
                    // Service/script staging creates them in that activation
                    // transaction immediately before the external body runs.
                    continue;
                }

                if (BpmnFlowNodeTypes.IsTimerCatch(currentNode.Type))
                {
                    await EnsureTimerCatchWaitAsync(
                        tokenInstance,
                        token,
                        currentNode,
                        definition,
                        actor,
                        cancellationToken);
                    // A timer catch is a resting activity too. Its own catch
                    // subscription and every attached boundary subscription are
                    // staged in the same instance transaction so either the
                    // complete wait-set is visible or none of it is.
                    await EnsureAttachedTimerBoundaryWaitsAsync(
                        tokenInstance,
                        token,
                        currentNode,
                        definition,
                        actor,
                        cancellationToken);
                    continue;
                }

                if (!BpmnFlowNodeTypes.IsPassThrough(currentNode.Type))
                {
                    // This is activation time for a resting activity. The
                    // repository's activation/node uniqueness makes the call
                    // idempotent for normal re-entry while keeping async-before
                    // queue time outside the user-task boundary duration.
                    await EnsureAttachedTimerBoundaryWaitsAsync(
                        tokenInstance,
                        token,
                        currentNode,
                        definition,
                        actor,
                        cancellationToken);
                    continue;
                }

                var variables = WithContext(storedOverlay, actor, tokenInstance, definition, currentNode);
                TaskExecutionOutcome? outcome = resumesCurrentActivation
                    && resume!.ActivityAlreadyExecuted
                    ? resume.Outcome
                    : null;
                if (!(resumesCurrentActivation && resume!.ActivityAlreadyExecuted)
                    && BpmnFlowNodeTypes.IsServiceTask(currentNode.Type))
                {
                    outcome = await ExecuteServiceTaskAsync(
                        tokenInstance, currentNode, definition, actor, variables, storedOverlay, cancellationToken);
                    variables = WithContext(storedOverlay, actor, tokenInstance, definition, currentNode);
                }
                else if (!(resumesCurrentActivation && resume!.ActivityAlreadyExecuted)
                         && BpmnFlowNodeTypes.IsScriptTask(currentNode.Type))
                {
                    outcome = await ExecuteScriptTaskAsync(
                        tokenInstance,
                        currentNode,
                        definition,
                        actor,
                        variables,
                        storedOverlay,
                        flowInfo,
                        cancellationToken);
                    variables = WithContext(storedOverlay, actor, tokenInstance, definition, currentNode);
                }

            if (outcome is { Success: false })
            {
                var boundary = FindErrorBoundary(definition, currentNode.Id);
                if (boundary is null)
                {
                    throw new WorkflowDomainException(outcome.Reason ?? $"Task #{currentNode.Id} failed.");
                }

                if (!string.IsNullOrWhiteSpace(boundary.ErrorVariable))
                {
                    var errorValue = JsonSerializer.SerializeToElement(outcome.Reason ?? string.Empty);
                    await runtime.AddVariableAsync(
                        instance.Id,
                        boundary.ErrorVariable!,
                        boundary.Id,
                        actor.User,
                        errorValue,
                        cancellationToken,
                        token.CurrentNodeExecutionId,
                        actor.ActingFor,
                        actor.DelegationId);
                    storedOverlay[boundary.ErrorVariable!] = errorValue;
                }

                await runtime.AddTokenHistoryAsync(
                    instance.Id,
                    token.Id,
                    null,
                    currentNode.Id,
                    boundary.Id,
                    actor.User,
                    null,
                    "error",
                    cancellationToken,
                    actor.ActingFor,
                    actor.DelegationId);
                await CancelAttachedTimerBoundaryWaitsAsync(
                    instance.Id,
                    [token.Id],
                    cancellationToken);
                await runtime.UpdateExecutionTokenAsync(
                    token.Id,
                    ToSnapshot(boundary),
                    ExecutionTokenRecordStatuses.Active,
                    token.GatewayBranchId,
                    null,
                    null,
                    null,
                    ToNodeExecutionActor(actor),
                    new NodeExecutionCompletionRecord(
                        NodeExecutionRecordStatuses.Faulted,
                        NodeExecutionCompletionReasons.BoundaryCaught,
                        null,
                        null,
                        token.GatewayBranchId,
                        ToNodeExecutionActor(actor),
                        null,
                        outcome.Reason),
                    cancellationToken);
                queue.Enqueue(token.Id);
                continue;
            }

            if (currentNode.AsyncAfter
                && !(resumesCurrentActivation
                     && resume!.Phase == WorkflowJobKinds.AsyncAfter))
            {
                await EnsureAsyncAfterWaitAsync(
                    tokenInstance,
                    token,
                    currentNode,
                    definition,
                    actor,
                    cancellationToken);
                await CancelAttachedTimerBoundaryWaitsAsync(
                    instance.Id,
                    [token.Id],
                    cancellationToken);
                continue;
            }

            if (BpmnFlowNodeTypes.IsExclusiveGateway(currentNode.Type)
                && currentNode.JoinCancellation is not null)
            {
                await ReleaseExclusiveCancellingJoinAsync(
                    instance,
                    token,
                    currentNode,
                    definition,
                    actor,
                    storedOverlay,
                    flowInfo,
                    queue,
                    cancellationToken);
                continue;
            }

            if (BpmnFlowNodeTypes.IsParallelGateway(currentNode.Type)
                || BpmnFlowNodeTypes.IsInclusiveGateway(currentNode.Type)
                || BpmnFlowNodeTypes.IsComplexGateway(currentNode.Type))
            {
                var outgoing = OutgoingFlows(
                    instance.WorkflowDefinitionId,
                    definition,
                    currentNode.Id);
                if (BpmnFlowNodeTypes.IsParallelGateway(currentNode.Type))
                {
                    if (outgoing.Count >= 2)
                    {
                        await ForkGatewayTokenAsync(
                            instance, token, token.GatewayBranchId, currentNode, outgoing,
                            "parallelFork", null, null,
                            definition, actor, storedOverlay, flowInfo, queue, cancellationToken);
                    }
                    else
                    {
                        await TryReleaseParallelJoinAsync(
                            instance, token, currentNode, definition, actor, storedOverlay, flowInfo, queue,
                            cancellationToken);
                    }
                }
                else if (BpmnFlowNodeTypes.IsInclusiveGateway(currentNode.Type))
                {
                    if (outgoing.Count >= 2)
                    {
                        var selected = SelectInclusiveOutgoingFlows(outgoing, variables, flowInfo, currentNode.Id);
                        await ForkGatewayTokenAsync(
                            instance, token, token.GatewayBranchId, currentNode, selected,
                            "inclusiveSplit", null, null,
                            definition, actor, storedOverlay, flowInfo, queue, cancellationToken);
                    }
                    else
                    {
                        await TryReleaseInclusiveJoinAsync(
                            instance, currentNode, definition, actor, storedOverlay, flowInfo, queue,
                            cancellationToken);
                    }
                }
                else
                {
                    await TryProcessComplexGatewayAsync(
                        instance, token, currentNode, definition, actor, storedOverlay, flowInfo, queue,
                        cancellationToken);
                }
                continue;
            }

            var note = currentNode.Type switch
            {
                var t when BpmnFlowNodeTypes.IsStart(t) => "start",
                var t when BpmnFlowNodeTypes.IsMessageStart(t) => "messageStart",
                var t when BpmnFlowNodeTypes.IsExclusiveGateway(t) => "gateway",
                var t when BpmnFlowNodeTypes.IsScopedInterrupt(t) => "scopedInterruptSkipped",
                var t when BpmnFlowNodeTypes.IsServiceTask(t) => "service",
                var t when BpmnFlowNodeTypes.IsScriptTask(t) => "script",
                var t when BpmnFlowNodeTypes.IsErrorBoundary(t) => "boundary",
                _ => "automatic"
            };

            long? continuationBranchId = token.GatewayBranchId;
            if (BpmnFlowNodeTypes.IsScopedInterrupt(currentNode.Type))
            {
                var interrupted = await InterruptGatewayScopeAsync(
                    instance, token, currentNode, definition, actor, cancellationToken);
                continuationBranchId = interrupted.ParentBranchId;
                note = interrupted.Interrupted ? "scopedInterrupt" : "scopedInterruptSkipped";
            }

            var flow = SelectPassThroughFlow(
                instance.WorkflowDefinitionId,
                definition,
                currentNode,
                variables,
                flowInfo);
            var administrativeTraversal = pendingAdministrativeTraversal is not null
                && pendingAdministrativeTraversal.SourceNodeId == currentNode.Id
                && pendingAdministrativeTraversal.FlowId == flow.Id
                ? pendingAdministrativeTraversal
                : null;
            if (administrativeTraversal is not null)
            {
                note = NodeExecutionCompletionReasons.AdministrativeAction;
                pendingAdministrativeTraversal = null;
            }
            await AdvanceAutomaticTokenAsync(
                instance,
                token,
                continuationBranchId,
                currentNode,
                flow,
                note,
                definition,
                actor,
                storedOverlay,
                flowInfo,
                queue,
                cancellationToken,
                administrativeAction: administrativeTraversal is not null,
                administrativeUserTaskId: administrativeTraversal?.UserTaskId,
                administrativeMultiInstanceExecutionId:
                    administrativeTraversal?.MultiInstanceExecutionId,
                administrativeValues: administrativeTraversal?.Values,
                administrativeBatch: administrativeTraversal?.AdministrativeBatch);
            }

            if (!await IsInstanceRunningAsync(instance.Id, cancellationToken))
            {
                break;
            }

            var releasedInclusiveJoin = await TryReleaseEnabledInclusiveJoinsAsync(
                instance,
                definition,
                actor,
                storedOverlay,
                flowInfo,
                queue,
                cancellationToken);
            if (!await IsInstanceRunningAsync(instance.Id, cancellationToken))
            {
                break;
            }
            var completedComplexReset = await TryCompleteEnabledComplexResetsAsync(
                instance,
                definition,
                actor,
                storedOverlay,
                flowInfo,
                queue,
                cancellationToken);
            if (!await IsInstanceRunningAsync(instance.Id, cancellationToken))
            {
                break;
            }
            await TryFinishInterruptedComplexDrainsAsync(
                instance,
                definition,
                cancellationToken);
            if (!releasedInclusiveJoin && !completedComplexReset && queue.Count == 0)
            {
                break;
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return await runtime.GetInstanceAsync(instance.Id, cancellationToken)
            ?? throw new WorkflowConflictException("The workflow instance disappeared during routing.");
    }

    private async Task<bool> IsInstanceRunningAsync(
        long instanceId,
        CancellationToken cancellationToken) =>
        await runtime.GetInstanceStatusAsync(instanceId, cancellationToken)
        == WorkflowInstanceStatuses.Running;

    private async Task<GatewayExecutionRecord> ForkGatewayTokenAsync(
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord token,
        long? parentBranchId,
        FlowNodeModel gateway,
        IReadOnlyList<SequenceFlowModel> outgoing,
        string note,
        string? phase,
        int? cycle,
        WorkflowModel definition,
        ActorContext actor,
        Dictionary<string, JsonElement> storedOverlay,
        SequenceFlowInfoSnapshot? flowInfo,
        Queue<long> queue,
        CancellationToken cancellationToken)
    {
        var selectedOutgoing = outgoing.OrderBy(flow => flow.Id).ToList();
        var configured = await engineSettings.GetByKeyAsync(
            "Workflow.Gateway.MaxActiveTokens", cancellationToken);
        var maxActiveTokens = configured is not null
                              && int.TryParse(configured.Value, out var parsed)
                              && parsed > 0
            ? parsed
            : 1000;
        var activeCount = (await runtime.ListExecutionTokensAsync(
            instance.Id, ExecutionTokenRecordStatuses.Active, cancellationToken)).Count;
        if (activeCount + selectedOutgoing.Count - 1 > maxActiveTokens)
        {
            throw new WorkflowDomainException(
                $"Gateway #{gateway.Id} would exceed the active token limit ({maxActiveTokens}).");
        }

        var execution = await runtime.AddGatewayExecutionAsync(
            instance.Id,
            gateway.Id,
            gateway.Type,
            GatewayExecutionRecordDirections.Split,
            phase,
            cycle,
            parentBranchId,
            selectedOutgoing.Select(flow => flow.Id).ToList(),
            cancellationToken);
        var branches = await runtime.ListGatewayBranchesAsync(execution.Id, cancellationToken);
        var spawnedTokens = await runtime.AddGatewayBranchTokensAsync(
            instance.Id,
            ToSnapshot(gateway),
            parentBranchId,
            branches.Skip(1).Select(branch => branch.Id).ToList(),
            token.ComplexDrainStateIds,
            ToNodeExecutionActor(actor),
            cancellationToken,
            automaticActivationCount:
                WorkflowAutomaticActivationGuard.InheritForFork(
                    token.AutomaticActivationCount),
            automaticActivationStateIds: token.AutomaticActivationStateIds);
        var work = new List<(ExecutionTokenRecord Token, GatewayBranchRecord Branch, SequenceFlowModel Flow)>();
        for (var index = 0; index < selectedOutgoing.Count; index++)
        {
            var branch = branches.Single(item => item.Ordinal == index);
            var branchToken = index == 0
                ? token
                : spawnedTokens[index - 1];
            work.Add((branchToken, branch, selectedOutgoing[index]));
        }

        foreach (var item in work)
        {
            var current = await runtime.GetExecutionTokenAsync(item.Token.Id, false, cancellationToken);
            if (current is null || current.Status != ExecutionTokenRecordStatuses.Active)
            {
                continue;
            }
            await AdvanceAutomaticTokenAsync(
                instance,
                current,
                item.Branch.Id,
                gateway,
                item.Flow,
                note,
                definition,
                actor,
                storedOverlay,
                flowInfo,
                queue,
                cancellationToken,
                deferSave: true);
        }
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return execution;
    }

    private async Task ReleaseExclusiveCancellingJoinAsync(
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord arrivingToken,
        FlowNodeModel join,
        WorkflowModel definition,
        ActorContext actor,
        Dictionary<string, JsonElement> storedOverlay,
        SequenceFlowInfoSnapshot? flowInfo,
        Queue<long> queue,
        CancellationToken cancellationToken)
    {
        var outgoing = OutgoingFlows(
            instance.WorkflowDefinitionId,
            definition,
            join.Id).Single();
        var scopeSnapshot = await LoadActiveGatewayScopeSnapshotAsync(
            instance.Id,
            cancellationToken);
        var cancellationScope = ResolveJoinCancellationScope(
            join,
            [arrivingToken],
            scopeSnapshot);
        var continuationBranchId = await ApplyJoinCancellationAsync(
            instance,
            arrivingToken,
            join,
            cancellationScope,
            definition,
            actor,
            cancellationToken);
        await AdvanceAutomaticTokenAsync(
            instance,
            arrivingToken,
            continuationBranchId,
            join,
            outgoing,
            "gateway",
            definition,
            actor,
            storedOverlay,
            flowInfo,
            queue,
            cancellationToken);
        await CloseInactiveGatewayScopesAsync(
            instance.Id,
            "join",
            cancellationToken);
    }

    private async Task TryReleaseParallelJoinAsync(
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord arrivingToken,
        FlowNodeModel join,
        WorkflowModel definition,
        ActorContext actor,
        Dictionary<string, JsonElement> storedOverlay,
        SequenceFlowInfoSnapshot? flowInfo,
        Queue<long> queue,
        CancellationToken cancellationToken)
    {
        var incoming = IncomingFlows(instance.WorkflowDefinitionId, definition, join.Id);
        var outgoing = OutgoingFlows(instance.WorkflowDefinitionId, definition, join.Id).Single();
        var activeAtJoin = (await runtime.ListExecutionTokensAsync(
                instance.Id, ExecutionTokenRecordStatuses.Active, cancellationToken))
            .Where(token => token.NodeId == join.Id && token.ArrivedViaFlowId is not null)
            .OrderBy(token => token.Id)
            .ToList();
        var scopeSnapshot = await LoadActiveGatewayScopeSnapshotAsync(
            instance.Id,
            cancellationToken);
        var released = false;
        while (true)
        {
            await RefreshActiveTokenSnapshotAsync(activeAtJoin, cancellationToken);
            activeAtJoin.RemoveAll(token =>
                token.NodeId != join.Id || token.ArrivedViaFlowId is null);
            var selected = new List<ExecutionTokenRecord>();
            foreach (var flow in incoming)
            {
                var candidate = activeAtJoin.FirstOrDefault(token => token.ArrivedViaFlowId == flow.Id);
                if (candidate is null)
                {
                    if (released)
                    {
                        await CloseInactiveGatewayScopesAsync(
                            instance.Id,
                            "join",
                            cancellationToken);
                    }
                    return;
                }
                selected.Add(candidate);
            }

            var cancellationScope = join.JoinCancellation is null
                ? null
                : ResolveJoinCancellationScope(join, selected, scopeSnapshot);
            var (survivor, commonBranchId) = await ConsumeGatewayArrivalBatchAsync(
                instance.Id,
                selected,
                actor,
                cancellationToken,
                scopeSnapshot);
            if (cancellationScope is not null)
            {
                commonBranchId = await ApplyJoinCancellationAsync(
                    instance,
                    survivor,
                    join,
                    cancellationScope,
                    definition,
                    actor,
                    cancellationToken);
            }
            activeAtJoin.RemoveAll(token => selected.Any(item => item.Id == token.Id));

            var joinExecution = await runtime.AddGatewayExecutionAsync(
                instance.Id,
                join.Id,
                join.Type,
                GatewayExecutionRecordDirections.Merge,
                null,
                null,
                commonBranchId,
                [outgoing.Id],
                cancellationToken);
            await AdvanceAutomaticTokenAsync(
                instance,
                survivor,
                commonBranchId,
                join,
                outgoing,
                "parallelJoin",
                definition,
                actor,
                storedOverlay,
                flowInfo,
                queue,
                cancellationToken);
            await runtime.SetGatewayExecutionStatusAsync(
                joinExecution.Id,
                GatewayExecutionRecordStatuses.Joined,
                join.JoinCancellation is null ? "join" : "joinCancellation",
                null,
                null,
                cancellationToken);
            released = true;
        }
    }

    private async Task<bool> TryReleaseInclusiveJoinAsync(
        WorkflowInstanceRecord instance,
        FlowNodeModel join,
        WorkflowModel definition,
        ActorContext actor,
        Dictionary<string, JsonElement> storedOverlay,
        SequenceFlowInfoSnapshot? flowInfo,
        Queue<long> queue,
        CancellationToken cancellationToken)
    {
        var incoming = IncomingFlows(instance.WorkflowDefinitionId, definition, join.Id);
        var incomingIds = incoming.Select(flow => flow.Id).ToHashSet();
        var topology = GatewayTopologyCache.Get(instance.WorkflowDefinitionId, definition);
        var released = false;
        var activeTokens = (await runtime.ListExecutionTokensAsync(
                instance.Id, ExecutionTokenRecordStatuses.Active, cancellationToken))
            .ToList();
        var scopeSnapshot = await LoadActiveGatewayScopeSnapshotAsync(
            instance.Id,
            cancellationToken);

        while (true)
        {
            await RefreshActiveTokenSnapshotAsync(activeTokens, cancellationToken);
            var activeAtJoin = activeTokens
                .Where(token => token.NodeId == join.Id
                                && token.ArrivedViaFlowId is not null
                                && incomingIds.Contains(token.ArrivedViaFlowId.Value))
                .OrderBy(token => token.Id)
                .ToList();
            if (activeAtJoin.Count == 0)
            {
                break;
            }

            var populated = activeAtJoin
                .Select(token => token.ArrivedViaFlowId!.Value)
                .ToHashSet();
            var missing = incomingIds.Where(id => !populated.Contains(id)).ToArray();
            if (activeTokens.Any(token =>
                    token.NodeId != join.Id
                    && topology.CanReachAnyInput(join.Id, token.NodeId, missing)))
            {
                break;
            }

            var selected = populated
                .OrderBy(id => id)
                .Select(flowId => activeAtJoin.First(token => token.ArrivedViaFlowId == flowId))
                .ToList();
            var cancellationScope = join.JoinCancellation is null
                ? null
                : ResolveJoinCancellationScope(join, selected, scopeSnapshot);
            var (survivor, commonBranchId) = await ConsumeGatewayArrivalBatchAsync(
                instance.Id,
                selected,
                actor,
                cancellationToken,
                scopeSnapshot);
            if (cancellationScope is not null)
            {
                commonBranchId = await ApplyJoinCancellationAsync(
                    instance,
                    survivor,
                    join,
                    cancellationScope,
                    definition,
                    actor,
                    cancellationToken);
            }
            activeTokens.RemoveAll(token => selected.Any(item => item.Id == token.Id));

            var outgoing = OutgoingFlows(instance.WorkflowDefinitionId, definition, join.Id).Single();
            var joinExecution = await runtime.AddGatewayExecutionAsync(
                instance.Id,
                join.Id,
                join.Type,
                GatewayExecutionRecordDirections.Merge,
                null,
                null,
                commonBranchId,
                [outgoing.Id],
                cancellationToken);
            await AdvanceAutomaticTokenAsync(
                instance,
                survivor,
                commonBranchId,
                join,
                outgoing,
                "inclusiveMerge",
                definition,
                actor,
                storedOverlay,
                flowInfo,
                queue,
                cancellationToken);
            await runtime.SetGatewayExecutionStatusAsync(
                joinExecution.Id,
                GatewayExecutionRecordStatuses.Joined,
                join.JoinCancellation is null ? "join" : "joinCancellation",
                null,
                null,
                cancellationToken);
            var advancedSurvivor = await runtime.GetExecutionTokenAsync(
                survivor.Id,
                false,
                cancellationToken);
            if (advancedSurvivor?.Status == ExecutionTokenRecordStatuses.Active)
            {
                activeTokens.Add(advancedSurvivor);
            }
            released = true;
        }

        if (released)
        {
            await CloseInactiveGatewayScopesAsync(
                instance.Id,
                "join",
                cancellationToken);
        }
        return released;
    }

    private async Task RefreshActiveTokenSnapshotAsync(
        List<ExecutionTokenRecord> tokens,
        CancellationToken cancellationToken)
    {
        var currentById = (await runtime.GetExecutionTokensAsync(
                tokens.Select(token => token.Id).ToArray(),
                cancellationToken))
            .ToDictionary(token => token.Id);
        for (var index = tokens.Count - 1; index >= 0; index--)
        {
            if (!currentById.TryGetValue(tokens[index].Id, out var current)
                || current.Status != ExecutionTokenRecordStatuses.Active)
            {
                tokens.RemoveAt(index);
                continue;
            }
            tokens[index] = current;
        }
    }

    private async Task<bool> TryReleaseEnabledInclusiveJoinsAsync(
        WorkflowInstanceRecord instance,
        WorkflowModel definition,
        ActorContext actor,
        Dictionary<string, JsonElement> storedOverlay,
        SequenceFlowInfoSnapshot? flowInfo,
        Queue<long> queue,
        CancellationToken cancellationToken)
    {
        var nodeById = definition.FlowNodes.ToDictionary(node => node.Id);
        var activeTokens = await runtime.ListExecutionTokensAsync(
            instance.Id,
            ExecutionTokenRecordStatuses.Active,
            cancellationToken);
        var waitingJoinIds = activeTokens
            .Select(token => token.NodeId)
            .Distinct()
            .Where(nodeId => nodeById.TryGetValue(nodeId, out var node)
                             && BpmnFlowNodeTypes.IsInclusiveGateway(node.Type)
                             && OutgoingFlows(
                                 instance.WorkflowDefinitionId,
                                 definition,
                                 nodeId).Count == 1)
            .OrderBy(nodeId => nodeId)
            .ToList();

        var released = false;
        foreach (var joinId in waitingJoinIds)
        {
            released |= await TryReleaseInclusiveJoinAsync(
                instance,
                nodeById[joinId],
                definition,
                actor,
                storedOverlay,
                flowInfo,
                queue,
                cancellationToken);
        }
        return released;
    }

    private async Task TryProcessComplexGatewayAsync(
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord arrivingToken,
        FlowNodeModel gateway,
        WorkflowModel definition,
        ActorContext actor,
        Dictionary<string, JsonElement> storedOverlay,
        SequenceFlowInfoSnapshot? flowInfo,
        Queue<long> queue,
        CancellationToken cancellationToken)
    {
        var incoming = IncomingFlows(instance.WorkflowDefinitionId, definition, gateway.Id);
        var state = await runtime.GetComplexGatewayStateAsync(
            instance.Id, gateway.Id, true, cancellationToken)
            ?? await runtime.SaveComplexGatewayStateAsync(
                instance.Id,
                gateway.Id,
                ComplexGatewayStateRecordPhases.WaitingForStart,
                0,
                [],
                incoming.Select(flow => flow.Id).ToArray(),
                [],
                [],
                null,
                cancellationToken);
        await runtime.RegisterTokenAtComplexGatewayAsync(
            arrivingToken.Id, state.Id, state.Cycle, cancellationToken);

        if (state.Phase == ComplexGatewayStateRecordPhases.InterruptedDraining)
        {
            var belongsToInterruptedFrontier =
                arrivingToken.ComplexDrainStateIds.Contains(state.Id);
            if (!belongsToInterruptedFrontier
                && await TryFinishInterruptedComplexDrainAsync(
                    instance, gateway, definition, state, cancellationToken))
            {
                state = await runtime.GetComplexGatewayStateAsync(
                        instance.Id, gateway.Id, true, cancellationToken)
                    ?? throw new WorkflowConflictException(
                        $"Complex gateway state for node #{gateway.Id} disappeared.");
                await runtime.RegisterTokenAtComplexGatewayAsync(
                    arrivingToken.Id, state.Id, state.Cycle, cancellationToken);
            }
            else
            {
                await runtime.SetExecutionTokenStatusAsync(
                    arrivingToken.Id,
                    ExecutionTokenRecordStatuses.Cancelled,
                    ExecutionTokenTerminationReasons.GatewayScopeCancelled,
                    new NodeExecutionCompletionRecord(
                        NodeExecutionRecordStatuses.Cancelled,
                        NodeExecutionCompletionReasons.GatewayScopeCancelled,
                        null,
                        null,
                        arrivingToken.GatewayBranchId,
                        ToNodeExecutionActor(actor)),
                    cancellationToken);
                await TryFinishInterruptedComplexDrainAsync(
                    instance, gateway, definition, state, cancellationToken);
                await CloseInactiveGatewayScopesAsync(
                    instance.Id,
                    ExecutionTokenTerminationReasons.GatewayScopeCancelled,
                    cancellationToken);
                var remainingActive = await runtime.ListExecutionTokensAsync(
                    instance.Id,
                    ExecutionTokenRecordStatuses.Active,
                    cancellationToken);
                if (remainingActive.Count == 0)
                {
                    await runtime.SetInstanceStatusAsync(
                        instance.Id,
                        WorkflowInstanceStatuses.Completed,
                        cancellationToken);
                }
                return;
            }
        }

        if (state.Phase == ComplexGatewayStateRecordPhases.WaitingForReset)
        {
            await TryCompleteComplexResetAsync(
                instance,
                gateway,
                definition,
                state,
                actor,
                storedOverlay,
                flowInfo,
                queue,
                cancellationToken);
            return;
        }

        var active = await runtime.ListExecutionTokensAsync(
            instance.Id, ExecutionTokenRecordStatuses.Active, cancellationToken);
        var waiting = active
            .Where(token => token.NodeId == gateway.Id
                            && token.ComplexGatewayStateId == state.Id
                            && token.ComplexGatewayCycle == state.Cycle
                            && token.ArrivedViaFlowId is not null)
            .OrderBy(token => token.Id)
            .ToList();
        var counts = incoming.ToDictionary(
            flow => flow.Id,
            flow => waiting.Count(token => token.ArrivedViaFlowId == flow.Id));
        var conditionContext = new GatewayConditionContext(counts, WaitingForStart: true);
        var variables = WithContext(
            storedOverlay,
            actor,
            instance with
            {
                ActiveTokenId = arrivingToken.Id,
                CurrentStepId = gateway.Id,
                CurrentNodeExecutionId = arrivingToken.CurrentNodeExecutionId
            },
            definition,
            gateway);
        if (!SequenceFlowConditionEvaluator.EvaluateGateway(
                gateway.ActivationCondition,
                variables,
                conditionContext))
        {
            return;
        }

        var selectedTokens = waiting
            .Where(token => token.ArrivedViaFlowId is not null)
            .GroupBy(token => token.ArrivedViaFlowId!.Value)
            .OrderBy(group => group.Key)
            .Select(group => group.OrderBy(token => token.Id).First())
            .ToList();
        if (selectedTokens.Count == 0)
        {
            return;
        }
        var activationDrainStateIds = selectedTokens
            .SelectMany(token => token.ComplexDrainStateIds)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        var scopeSnapshot = await LoadActiveGatewayScopeSnapshotAsync(
            instance.Id,
            cancellationToken);
        var cancellationScope = gateway.JoinCancellation is null
            ? null
            : ResolveJoinCancellationScope(gateway, selectedTokens, scopeSnapshot);
        var priorCycleParents = FindPriorComplexCycleParents(
            selectedTokens,
            gateway.Id,
            state.Cycle,
            scopeSnapshot);
        var (survivor, commonBranchId) = await ConsumeGatewayArrivalBatchAsync(
            instance.Id,
            selectedTokens,
            actor,
            cancellationToken,
            scopeSnapshot,
            priorCycleParents);
        if (cancellationScope is not null)
        {
            var activationCycle = state.Cycle;
            commonBranchId = await ApplyJoinCancellationAsync(
                instance,
                survivor,
                gateway,
                cancellationScope,
                definition,
                actor,
                cancellationToken);
            var cancellingSelectedFlows = SelectComplexOutgoingFlows(
                OutgoingFlows(instance.WorkflowDefinitionId, definition, gateway.Id),
                variables,
                conditionContext,
                flowInfo,
                failWhenEmpty: true,
                gateway.Id);
            var nextState = await runtime.SaveComplexGatewayStateAsync(
                instance.Id,
                gateway.Id,
                ComplexGatewayStateRecordPhases.WaitingForStart,
                activationCycle + 1,
                [],
                incoming.Select(flow => flow.Id).ToArray(),
                [],
                [],
                null,
                cancellationToken,
                automaticActivationCount:
                    WorkflowAutomaticActivationGuard.ResetAfterExternalWaitOrTrigger());
            await EmitComplexGatewayFlowsAsync(
                instance,
                survivor,
                commonBranchId,
                gateway,
                cancellingSelectedFlows,
                "complexActivation",
                "start",
                activationCycle,
                definition,
                actor,
                storedOverlay,
                flowInfo,
                queue,
                cancellationToken,
                completionReason: "joinCancellation");

            if (!await IsInstanceRunningAsync(instance.Id, cancellationToken))
            {
                return;
            }

            var surplus = (await runtime.ListExecutionTokensAsync(
                    instance.Id,
                    ExecutionTokenRecordStatuses.Active,
                    cancellationToken))
                .Where(token => token.NodeId == gateway.Id
                                && token.Id != survivor.Id
                                && token.ArrivedViaFlowId is not null)
                .OrderBy(token => token.Id)
                .ToList();
            foreach (var token in surplus)
            {
                await runtime.RegisterTokenAtComplexGatewayAsync(
                    token.Id,
                    nextState.Id,
                    nextState.Cycle,
                    cancellationToken);
                queue.Enqueue(token.Id);
            }
            await CloseInactiveGatewayScopesAsync(
                instance.Id,
                "complex",
                cancellationToken);
            return;
        }

        var contributing = selectedTokens
            .Select(token => token.ArrivedViaFlowId!.Value)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        // Reset relevance is topology-driven, so every authored incoming flow
        // remains eligible for a late/reset arrival, including an input that
        // contributed to the start firing and can be reached again through a
        // loop. Acyclic inputs are eliminated immediately by reachability.
        var remaining = incoming
            .Select(flow => flow.Id)
            .ToArray();
        state = await runtime.SaveComplexGatewayStateAsync(
            instance.Id,
            gateway.Id,
            ComplexGatewayStateRecordPhases.WaitingForReset,
            state.Cycle,
            contributing,
            remaining,
            activationDrainStateIds,
            [],
            null,
            cancellationToken,
            automaticActivationCount: survivor.AutomaticActivationCount);

        var activationStateIds = survivor.AutomaticActivationStateIds
            .Append(state.Id)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        if (!await runtime.SetExecutionTokenAutomaticActivationStateIdsAsync(
                survivor.Id,
                survivor.ActivationId,
                activationStateIds,
                cancellationToken))
        {
            throw new WorkflowConflictException(
                "The Complex activation token changed while recording its automatic lineage.");
        }
        survivor = survivor with { AutomaticActivationStateIds = activationStateIds };

        var selectedFlows = SelectComplexOutgoingFlows(
            OutgoingFlows(instance.WorkflowDefinitionId, definition, gateway.Id),
            variables,
            conditionContext,
            flowInfo,
            failWhenEmpty: true,
            gateway.Id);
        var execution = await EmitComplexGatewayFlowsAsync(
            instance,
            survivor,
            commonBranchId,
            gateway,
            selectedFlows,
            "complexActivation",
            "start",
            state.Cycle,
            definition,
            actor,
            storedOverlay,
            flowInfo,
            queue,
            cancellationToken);

        if (!await IsInstanceRunningAsync(instance.Id, cancellationToken))
        {
            return;
        }

        var latest = await runtime.GetComplexGatewayStateAsync(
            instance.Id, gateway.Id, true, cancellationToken)
            ?? throw new WorkflowConflictException(
                $"Complex gateway #{gateway.Id} state disappeared during activation.");
        if (latest.Phase == ComplexGatewayStateRecordPhases.WaitingForReset
            && latest.Cycle == state.Cycle)
        {
            latest = await runtime.SaveComplexGatewayStateAsync(
                instance.Id,
                gateway.Id,
                latest.Phase,
                latest.Cycle,
                latest.ContributingFlowIds,
                latest.RemainingFlowIds,
                latest.ActivationDrainStateIds,
                latest.DrainingTokenIds,
                execution.Id,
                cancellationToken);
            await TryCompleteComplexResetAsync(
                instance,
                gateway,
                definition,
                latest,
                actor,
                storedOverlay,
                flowInfo,
                queue,
                cancellationToken);
        }
        await CloseInactiveGatewayScopesAsync(
            instance.Id,
            "complex",
            cancellationToken);
    }

    private async Task<bool> TryCompleteComplexResetAsync(
        WorkflowInstanceRecord instance,
        FlowNodeModel gateway,
        WorkflowModel definition,
        ComplexGatewayStateRecord state,
        ActorContext actor,
        Dictionary<string, JsonElement> storedOverlay,
        SequenceFlowInfoSnapshot? flowInfo,
        Queue<long> queue,
        CancellationToken cancellationToken)
    {
        if (state.Phase != ComplexGatewayStateRecordPhases.WaitingForReset)
        {
            return false;
        }
        if (!await IsInstanceRunningAsync(instance.Id, cancellationToken))
        {
            return false;
        }
        var remaining = state.RemainingFlowIds.ToHashSet();
        var active = await runtime.ListExecutionTokensAsync(
            instance.Id, ExecutionTokenRecordStatuses.Active, cancellationToken);
        var waiting = active
            .Where(token => token.NodeId == gateway.Id
                            && token.ComplexGatewayStateId == state.Id
                            && token.ComplexGatewayCycle == state.Cycle
                            && token.ArrivedViaFlowId is not null
                            && remaining.Contains(token.ArrivedViaFlowId.Value))
            .OrderBy(token => token.Id)
            .ToList();
        var populated = waiting
            .Select(token => token.ArrivedViaFlowId!.Value)
            .ToHashSet();
        var missing = remaining.Where(id => !populated.Contains(id)).ToArray();
        var topology = GatewayTopologyCache.Get(instance.WorkflowDefinitionId, definition);
        if (active.Any(token =>
                token.NodeId != gateway.Id
                && topology.CanReachAnyInput(gateway.Id, token.NodeId, missing)))
        {
            return false;
        }

        var selectedTokens = populated
            .OrderBy(id => id)
            .Select(flowId => waiting.First(token => token.ArrivedViaFlowId == flowId))
            .ToList();
        if (state.ActiveExecutionId is not long executionId)
        {
            throw new WorkflowConflictException(
                $"Complex gateway #{gateway.Id} cycle {state.Cycle} has no activation execution.");
        }
        var activationExecution = await runtime.GetGatewayExecutionAsync(
            executionId,
            cancellationToken);
        if (activationExecution is null
            || activationExecution.InstanceId != instance.Id
            || activationExecution.GatewayNodeId != gateway.Id
            || activationExecution.Phase != "start"
            || activationExecution.Cycle != state.Cycle)
        {
            throw new WorkflowConflictException(
                $"Complex gateway #{gateway.Id} cycle {state.Cycle} has an invalid activation execution.");
        }
        ExecutionTokenRecord? survivor = null;
        long? resetParentBranchId;
        if (selectedTokens.Count > 0)
        {
            (survivor, resetParentBranchId) = await ConsumeGatewayArrivalBatchAsync(
                instance.Id,
                selectedTokens,
                actor,
                cancellationToken,
                reconcileWithBranchIds: [activationExecution.ParentBranchId]);
        }
        else
        {
            resetParentBranchId = activationExecution.ParentBranchId;
        }
        var consumedAutomaticLineage =
            await runtime.ConsumeExecutionTokenAutomaticActivationStateAsync(
                instance.Id,
                state.Id,
                state.AutomaticActivationCount,
                cancellationToken);
        var resetAutomaticActivationCount = consumedAutomaticLineage.MaximumCount;
        // Clearing the old marker set and attaching this state to the reset
        // output is an atomic epoch handoff. Work between reset and the next
        // activation therefore remains part of the unattended chain, while no
        // terminal token from the prior cycle can pollute a later reset.
        var resetAutomaticActivationStateIds = consumedAutomaticLineage
            .InheritedStateIds
            .Append(state.Id)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        // Reset output starts the next Complex cycle outside the completed
        // activation scope. Reconciliation also preserves any outer scope
        // shared with a late arrival from a sibling branch.

        var resetCounts = state.RemainingFlowIds.ToDictionary(
            id => id,
            id => waiting.Count(token => token.ArrivedViaFlowId == id));
        var conditionContext = new GatewayConditionContext(resetCounts, WaitingForStart: false);
        var variables = WithContext(
            storedOverlay,
            actor,
            instance with
            {
                ActiveTokenId = survivor?.Id ?? instance.ActiveTokenId,
                CurrentStepId = gateway.Id,
                CurrentNodeExecutionId = survivor?.CurrentNodeExecutionId
            },
            definition,
            gateway);
        var selectedFlows = SelectComplexOutgoingFlows(
            OutgoingFlows(instance.WorkflowDefinitionId, definition, gateway.Id),
            variables,
            conditionContext,
            flowInfo,
            failWhenEmpty: false,
            gateway.Id);
        var resetDrainStateIds = state.ActivationDrainStateIds
            .Concat(selectedTokens.SelectMany(token => token.ComplexDrainStateIds))
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

        var nextState = await runtime.SaveComplexGatewayStateAsync(
            instance.Id,
            gateway.Id,
            ComplexGatewayStateRecordPhases.WaitingForStart,
            state.Cycle + 1,
            [],
            IncomingFlows(instance.WorkflowDefinitionId, definition, gateway.Id)
                .Select(flow => flow.Id)
                .ToArray(),
            [],
            [],
            null,
            cancellationToken,
            automaticActivationCount:
                WorkflowAutomaticActivationGuard.ResetAfterExternalWaitOrTrigger());

        if (selectedFlows.Count > 0)
        {
            survivor ??= await runtime.AddExecutionTokenAsync(
                instance.Id,
                ToSnapshot(gateway, variables, instance.Id),
                resetParentBranchId,
                null,
                ToNodeExecutionActor(actor),
                cancellationToken,
                automaticActivationCount: resetAutomaticActivationCount,
                automaticActivationStateIds: resetAutomaticActivationStateIds);
            if (survivor.AutomaticActivationCount != resetAutomaticActivationCount
                && !await runtime.SetExecutionTokenAutomaticActivationCountAsync(
                    survivor.Id,
                    survivor.ActivationId,
                    resetAutomaticActivationCount,
                    cancellationToken))
            {
                throw new WorkflowConflictException(
                    "The Complex reset token changed while restoring its automatic-activation count.");
            }
            if (!await runtime.SetExecutionTokenAutomaticActivationStateIdsAsync(
                    survivor.Id,
                    survivor.ActivationId,
                    resetAutomaticActivationStateIds,
                    cancellationToken))
            {
                throw new WorkflowConflictException(
                    "The Complex reset token changed while restoring its outer automatic lineage.");
            }
            await runtime.SetComplexDrainMarkersAsync(
                survivor.Id,
                resetDrainStateIds,
                cancellationToken);
            survivor = await runtime.GetExecutionTokenAsync(
                           survivor.Id,
                           false,
                           cancellationToken)
                       ?? throw new WorkflowConflictException(
                           "The Complex reset token disappeared while restoring drain provenance.");
            await EmitComplexGatewayFlowsAsync(
                instance,
                survivor,
                resetParentBranchId,
                gateway,
                selectedFlows,
                "complexReset",
                "reset",
                state.Cycle,
                definition,
                actor,
                storedOverlay,
                flowInfo,
                queue,
                cancellationToken);
        }
        else
        {
            var resetExecution = await runtime.AddGatewayExecutionAsync(
                instance.Id,
                gateway.Id,
                gateway.Type,
                OutgoingFlows(instance.WorkflowDefinitionId, definition, gateway.Id).Count >= 2
                    ? GatewayExecutionRecordDirections.Split
                    : GatewayExecutionRecordDirections.Merge,
                "reset",
                state.Cycle,
                resetParentBranchId,
                [],
                cancellationToken);
            await runtime.MergeExecutionTokensAsync(
                selectedTokens.Select(token => token.Id).ToArray(),
                resetParentBranchId,
                NodeExecutionCompletionReasons.ComplexReset,
                ToNodeExecutionActor(actor),
                cancellationToken);
            await runtime.SetGatewayExecutionStatusAsync(
                resetExecution.Id,
                GatewayExecutionRecordStatuses.Completed,
                "resetNoOutput",
                null,
                null,
                cancellationToken);
        }

        var surplus = (await runtime.ListExecutionTokensAsync(
                instance.Id, ExecutionTokenRecordStatuses.Active, cancellationToken))
            .Where(token => token.NodeId == gateway.Id
                            && token.Id != survivor?.Id
                            && token.ArrivedViaFlowId is not null)
            .OrderBy(token => token.Id)
            .ToList();
        foreach (var token in surplus)
        {
            await runtime.RegisterTokenAtComplexGatewayAsync(
                token.Id, nextState.Id, nextState.Cycle, cancellationToken);
            queue.Enqueue(token.Id);
        }

        await CloseInactiveGatewayScopesAsync(
            instance.Id,
            "complex",
            cancellationToken);

        if (selectedFlows.Count == 0 && surplus.Count == 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            var remainingActive = await runtime.ListExecutionTokensAsync(
                instance.Id, ExecutionTokenRecordStatuses.Active, cancellationToken);
            if (remainingActive.Count == 0)
            {
                await runtime.SetInstanceStatusAsync(
                    instance.Id, WorkflowInstanceStatuses.Completed, cancellationToken);
            }
        }
        return true;
    }

    private async Task<bool> TryCompleteEnabledComplexResetsAsync(
        WorkflowInstanceRecord instance,
        WorkflowModel definition,
        ActorContext actor,
        Dictionary<string, JsonElement> storedOverlay,
        SequenceFlowInfoSnapshot? flowInfo,
        Queue<long> queue,
        CancellationToken cancellationToken)
    {
        var states = await runtime.ListComplexGatewayStatesAsync(
            instance.Id,
            cancellationToken);
        var completed = false;
        foreach (var state in states
                     .Where(state =>
                         state.Phase == ComplexGatewayStateRecordPhases.WaitingForReset)
                     .OrderBy(state => state.GatewayNodeId))
        {
            completed |= await TryCompleteComplexResetAsync(
                instance,
                GetFlowNode(definition, state.GatewayNodeId),
                definition,
                state,
                actor,
                storedOverlay,
                flowInfo,
                queue,
                cancellationToken);
        }
        return completed;
    }

    private async Task<GatewayExecutionRecord> EmitComplexGatewayFlowsAsync(
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord token,
        long? parentBranchId,
        FlowNodeModel gateway,
        IReadOnlyList<SequenceFlowModel> selectedFlows,
        string note,
        string phase,
        int cycle,
        WorkflowModel definition,
        ActorContext actor,
        Dictionary<string, JsonElement> storedOverlay,
        SequenceFlowInfoSnapshot? flowInfo,
        Queue<long> queue,
        CancellationToken cancellationToken,
        string? completionReason = null)
    {
        var authoredOutgoingCount = OutgoingFlows(
            instance.WorkflowDefinitionId,
            definition,
            gateway.Id).Count;
        if (authoredOutgoingCount >= 2)
        {
            return await ForkGatewayTokenAsync(
                instance,
                token,
                parentBranchId,
                gateway,
                selectedFlows,
                note,
                phase,
                cycle,
                definition,
                actor,
                storedOverlay,
                flowInfo,
                queue,
                cancellationToken);
        }

        var execution = await runtime.AddGatewayExecutionAsync(
            instance.Id,
            gateway.Id,
            gateway.Type,
            GatewayExecutionRecordDirections.Merge,
            phase,
            cycle,
            parentBranchId,
            selectedFlows.Select(flow => flow.Id).ToArray(),
            cancellationToken);
        await AdvanceAutomaticTokenAsync(
            instance,
            token,
            parentBranchId,
            gateway,
            selectedFlows.Single(),
            note,
            definition,
            actor,
            storedOverlay,
            flowInfo,
            queue,
            cancellationToken);
        await runtime.SetGatewayExecutionStatusAsync(
            execution.Id,
            GatewayExecutionRecordStatuses.Joined,
            completionReason ?? phase,
            null,
            null,
            cancellationToken);
        return execution;
    }

    private async Task<(ExecutionTokenRecord Survivor, long? CommonBranchId)>
        ConsumeGatewayArrivalBatchAsync(
            long instanceId,
            IReadOnlyList<ExecutionTokenRecord> selected,
            ActorContext actor,
            CancellationToken cancellationToken,
            GatewayScopeSnapshot? scopeSnapshot = null,
            IReadOnlyCollection<long?>? reconcileWithBranchIds = null)
    {
        var survivor = selected.OrderBy(token => token.Id).First();
        var drainStateIds = selected
            .SelectMany(token => token.ComplexDrainStateIds)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        var branchIds = selected
            .Select(token => token.GatewayBranchId)
            .Concat(reconcileWithBranchIds ?? [])
            .ToList();
        var commonBranchId = scopeSnapshot is null
            ? await FindDeepestCommonGatewayBranchAsync(
                instanceId,
                branchIds,
                cancellationToken)
            : FindDeepestCommonGatewayBranch(
                branchIds,
                scopeSnapshot);
        var mergedAutomaticActivationCount =
            WorkflowAutomaticActivationGuard.MergeAtJoin(
                selected.Select(token => token.AutomaticActivationCount));
        if (!await runtime.SetExecutionTokenAutomaticActivationCountAsync(
                survivor.Id,
                survivor.ActivationId,
                mergedAutomaticActivationCount,
                cancellationToken))
        {
            throw new WorkflowConflictException(
                "The surviving gateway token changed while merging automatic-activation counts.");
        }
        var mergedAutomaticActivationStateIds = selected
            .SelectMany(token => token.AutomaticActivationStateIds)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        if (!await runtime.SetExecutionTokenAutomaticActivationStateIdsAsync(
                survivor.Id,
                survivor.ActivationId,
                mergedAutomaticActivationStateIds,
                cancellationToken))
        {
            throw new WorkflowConflictException(
                "The surviving gateway token changed while merging automatic-activation lineage.");
        }
        await runtime.MergeExecutionTokensAsync(
            selected
                .Where(token => token.Id != survivor.Id)
                .Select(token => token.Id)
                .ToArray(),
            commonBranchId,
            NodeExecutionCompletionReasons.GatewayJoinMerged,
            ToNodeExecutionActor(actor),
            cancellationToken);
        await runtime.SetComplexDrainMarkersAsync(
            survivor.Id,
            drainStateIds,
            cancellationToken);
        survivor = await runtime.GetExecutionTokenAsync(
                       survivor.Id,
                       false,
                       cancellationToken)
                   ?? throw new WorkflowConflictException(
                       "The surviving gateway token disappeared while merging drain provenance.");
        return (survivor, commonBranchId);
    }

    private async Task<long?> FindDeepestCommonGatewayBranchAsync(
        long instanceId,
        IReadOnlyCollection<long?> branchIds,
        CancellationToken cancellationToken)
    {
        var snapshot = await LoadActiveGatewayScopeSnapshotAsync(
            instanceId,
            cancellationToken);
        return FindDeepestCommonGatewayBranch(branchIds, snapshot);
    }

    private async Task<GatewayScopeSnapshot> LoadActiveGatewayScopeSnapshotAsync(
        long instanceId,
        CancellationToken cancellationToken)
    {
        var branches = await runtime.ListGatewayBranchesForInstanceAsync(
            instanceId, true, cancellationToken);
        var executions = await runtime.ListGatewayExecutionsAsync(
            instanceId, GatewayExecutionRecordStatuses.Active, cancellationToken);
        return new GatewayScopeSnapshot(
            branches.ToDictionary(branch => branch.Id),
            executions.ToDictionary(execution => execution.Id));
    }

    private static long? FindDeepestCommonGatewayBranch(
        IReadOnlyCollection<long?> branchIds,
        GatewayScopeSnapshot snapshot)
    {
        var ancestries = new List<IReadOnlyList<GatewayBranchRecord>>();
        foreach (var startBranchId in branchIds)
        {
            var ancestry = new List<GatewayBranchRecord>();
            var branchId = startBranchId;
            while (branchId is long id
                   && snapshot.Branches.TryGetValue(id, out var branch)
                   && snapshot.Executions.TryGetValue(branch.ExecutionId, out var execution))
            {
                ancestry.Add(branch);
                branchId = execution.ParentBranchId;
            }
            ancestries.Add(ancestry);
        }
        if (ancestries.Count == 0 || ancestries.Any(ancestry => ancestry.Count == 0))
        {
            return null;
        }

        var common = ancestries
            .Skip(1)
            .Aggregate(
                ancestries[0].Select(branch => branch.Id).ToHashSet(),
                (set, ancestry) =>
                {
                    set.IntersectWith(ancestry.Select(branch => branch.Id));
                    return set;
                });
        return ancestries[0].FirstOrDefault(branch => common.Contains(branch.Id))?.Id;
    }

    private sealed record GatewayScopeSnapshot(
        IReadOnlyDictionary<long, GatewayBranchRecord> Branches,
        IReadOnlyDictionary<long, GatewayExecutionRecord> Executions);

    private sealed record JoinCancellationScope(
        GatewayExecutionRecord Execution,
        IReadOnlyList<long> ContributingBranchIds);

    private static JoinCancellationScope ResolveJoinCancellationScope(
        FlowNodeModel join,
        IReadOnlyList<ExecutionTokenRecord> contributingTokens,
        GatewayScopeSnapshot snapshot)
    {
        var gatewayRef = join.JoinCancellation?.GatewayRef
            ?? throw new WorkflowConflictException(
                $"Cancelling join #{join.Id} has no gatewayRef.");
        if (contributingTokens.Count == 0)
        {
            throw new WorkflowConflictException(
                $"Cancelling join #{join.Id} has no contributing execution token.");
        }

        var ancestries = contributingTokens
            .Select(token => BuildGatewayAncestry(
                token.GatewayBranchId,
                snapshot.Branches,
                snapshot.Executions))
            .ToList();
        var commonExecutionIds = ancestries
            .Select(ancestry => ancestry
                .Where(branch =>
                    snapshot.Executions.TryGetValue(branch.ExecutionId, out var execution)
                    && execution.Direction == GatewayExecutionRecordDirections.Split
                    && execution.GatewayNodeId == gatewayRef)
                .Select(branch => branch.ExecutionId)
                .ToHashSet())
            .Aggregate((common, current) =>
            {
                common.IntersectWith(current);
                return common;
            });
        var selectedBranch = ancestries[0]
            .FirstOrDefault(branch => commonExecutionIds.Contains(branch.ExecutionId));
        if (selectedBranch is null
            || !snapshot.Executions.TryGetValue(selectedBranch.ExecutionId, out var execution))
        {
            throw new WorkflowConflictException(
                $"Cancelling join #{join.Id} could not resolve an active activation of gateway #{gatewayRef} shared by every contributing token.");
        }

        var contributingBranchIds = ancestries
            .Select(ancestry => ancestry.First(branch => branch.ExecutionId == execution.Id).Id)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        return new JoinCancellationScope(execution, contributingBranchIds);
    }

    private async Task<long?> ApplyJoinCancellationAsync(
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord survivor,
        FlowNodeModel join,
        JoinCancellationScope scope,
        WorkflowModel definition,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        var activeBeforeCancellation = await runtime.ListExecutionTokensAsync(
            instance.Id,
            ExecutionTokenRecordStatuses.Active,
            cancellationToken);
        var snapshotBeforeCancellation = await LoadActiveGatewayScopeSnapshotAsync(
            instance.Id,
            cancellationToken);
        var hasRemainingDescendant = activeBeforeCancellation.Any(token =>
            token.Id != survivor.Id
            && BuildGatewayAncestry(
                    token.GatewayBranchId,
                    snapshotBeforeCancellation.Branches,
                    snapshotBeforeCancellation.Executions)
                .Any(branch => branch.ExecutionId == scope.Execution.Id));
        var contributingBranchIds = scope.ContributingBranchIds.ToHashSet();
        var hasRemainingBranch = snapshotBeforeCancellation.Branches.Values.Any(branch =>
            branch.ExecutionId == scope.Execution.Id
            && !contributingBranchIds.Contains(branch.Id));
        var hasNestedExecution = snapshotBeforeCancellation.Executions.Values.Any(execution =>
            execution.Id != scope.Execution.Id
            && BuildGatewayAncestry(
                    execution.ParentBranchId,
                    snapshotBeforeCancellation.Branches,
                    snapshotBeforeCancellation.Executions)
                .Any(branch => branch.ExecutionId == scope.Execution.Id));
        var hadRemainingScopeWork = hasRemainingDescendant
                                    || hasRemainingBranch
                                    || hasNestedExecution;

        var interrupted = await InterruptGatewayScopeAsync(
            instance,
            survivor,
            join,
            definition,
            actor,
            cancellationToken,
            gatewayRefOverride: scope.Execution.GatewayNodeId,
            expectedExecutionId: scope.Execution.Id,
            completionReason: "interruptingJoin",
            // The caller's outgoing token update promotes the survivor to the
            // referenced split's parent. Closing scopes before that update can
            // hide its outer ancestry when it is the only remaining token.
            closeInactiveScopes: false);
        if (!interrupted.Interrupted)
        {
            throw new WorkflowConflictException(
                $"Cancelling join #{join.Id} could not resolve active gateway activation #{scope.Execution.Id}.");
        }

        await runtime.SetGatewayBranchesStatusAsync(
            scope.ContributingBranchIds,
            GatewayBranchRecordStatuses.Merged,
            cancellationToken);

        if (!hadRemainingScopeWork)
        {
            // InterruptGatewayScopeAsync is also responsible for closing nested
            // Complex state safely. When no sibling work actually survived until
            // this join, retain that cleanup but report the referenced split as a
            // normal join rather than an interruption.
            await runtime.SetGatewayExecutionStatusAsync(
                scope.Execution.Id,
                GatewayExecutionRecordStatuses.Joined,
                "join",
                null,
                null,
                cancellationToken);
        }

        return interrupted.ParentBranchId;
    }

    private static IReadOnlyCollection<long?> FindPriorComplexCycleParents(
        IReadOnlyCollection<ExecutionTokenRecord> tokens,
        int gatewayNodeId,
        int cycle,
        GatewayScopeSnapshot snapshot)
    {
        var parents = new HashSet<long?>();
        foreach (var token in tokens)
        {
            foreach (var branch in BuildGatewayAncestry(
                         token.GatewayBranchId,
                         snapshot.Branches,
                         snapshot.Executions))
            {
                var execution = snapshot.Executions[branch.ExecutionId];
                if (execution.GatewayNodeId == gatewayNodeId
                    && execution.Cycle is int executionCycle
                    && executionCycle < cycle)
                {
                    parents.Add(execution.ParentBranchId);
                }
            }
        }
        return parents;
    }

    private async Task<(bool Interrupted, long? ParentBranchId)> InterruptGatewayScopeAsync(
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord token,
        FlowNodeModel interrupt,
        WorkflowModel definition,
        ActorContext actor,
        CancellationToken cancellationToken,
        int? gatewayRefOverride = null,
        long? expectedExecutionId = null,
        string completionReason = "interrupt",
        bool closeInactiveScopes = true)
    {
        var gatewayRef = gatewayRefOverride ?? interrupt.GatewayRef;
        var executions = await runtime.ListGatewayExecutionsAsync(
            instance.Id, GatewayExecutionRecordStatuses.Active, cancellationToken);
        var branches = await runtime.ListGatewayBranchesForInstanceAsync(
            instance.Id, true, cancellationToken);
        var executionById = executions.ToDictionary(execution => execution.Id);
        var branchById = branches.ToDictionary(branch => branch.Id);
        var ancestry = BuildGatewayAncestry(token.GatewayBranchId, branchById, executionById);
        var selected = ancestry
            .Select(branch => executions.SingleOrDefault(execution => execution.Id == branch.ExecutionId))
            .FirstOrDefault(execution =>
                execution is not null
                && execution.Status == GatewayExecutionRecordStatuses.Active
                && execution.Direction == GatewayExecutionRecordDirections.Split
                && execution.GatewayNodeId == gatewayRef
                && (expectedExecutionId is null || execution.Id == expectedExecutionId));
        if (selected is null)
        {
            return (false, token.GatewayBranchId);
        }
        var triggeringScopeBranchId = ancestry
            .First(branch => branch.ExecutionId == selected.Id)
            .Id;

        var activeTokens = await runtime.ListExecutionTokensAsync(
            instance.Id, ExecutionTokenRecordStatuses.Active, cancellationToken);
        var cancelledTokenIds = new HashSet<long>();
        foreach (var candidate in activeTokens.Where(candidate => candidate.Id != token.Id))
        {
            var candidateAncestry = BuildGatewayAncestry(
                candidate.GatewayBranchId, branchById, executionById);
            if (candidateAncestry.Any(branch => branch.ExecutionId == selected.Id))
            {
                cancelledTokenIds.Add(candidate.Id);
            }
        }

        ComplexGatewayStateRecord? interruptedComplexState = null;
        if (BpmnFlowNodeTypes.IsComplexGateway(selected.GatewayType)
            && selected.Cycle is int selectedCycle)
        {
            interruptedComplexState = await runtime.GetComplexGatewayStateAsync(
                instance.Id, selected.GatewayNodeId, true, cancellationToken);
            if (interruptedComplexState is not null
                && interruptedComplexState.Cycle == selectedCycle
                && interruptedComplexState.Phase == ComplexGatewayStateRecordPhases.WaitingForReset
                && (interruptedComplexState.ActiveExecutionId is null
                    || interruptedComplexState.ActiveExecutionId == selected.Id))
            {
                foreach (var waiting in activeTokens.Where(candidate =>
                             candidate.Id != token.Id
                             && candidate.ComplexGatewayStateId == interruptedComplexState.Id
                             && candidate.ComplexGatewayCycle == interruptedComplexState.Cycle))
                {
                    cancelledTokenIds.Add(waiting.Id);
                }
                var complexNode = GetFlowNode(definition, interruptedComplexState.GatewayNodeId);
                var topology = GatewayTopologyCache.Get(
                    instance.WorkflowDefinitionId,
                    definition);
                var drainingTokenIds = activeTokens
                    .Where(candidate =>
                        CanReachRemainingComplexInput(
                            candidate,
                            complexNode,
                            interruptedComplexState.RemainingFlowIds,
                            topology))
                    .Select(candidate => candidate.Id)
                    .OrderBy(id => id)
                    .ToArray();
                await runtime.AddComplexDrainMarkerAsync(
                    drainingTokenIds,
                    interruptedComplexState.Id,
                    cancellationToken);
                interruptedComplexState = await runtime.SaveComplexGatewayStateAsync(
                    instance.Id,
                    selected.GatewayNodeId,
                    ComplexGatewayStateRecordPhases.InterruptedDraining,
                    interruptedComplexState.Cycle,
                    interruptedComplexState.ContributingFlowIds,
                    interruptedComplexState.RemainingFlowIds,
                    interruptedComplexState.ActivationDrainStateIds,
                    drainingTokenIds,
                    selected.Id,
                    cancellationToken);
            }
            else
            {
                interruptedComplexState = null;
            }
        }

        var nestedExecutions = executions
            .Where(execution =>
                execution.Id != selected.Id
                && execution.Status == GatewayExecutionRecordStatuses.Active
                && BuildGatewayAncestry(
                        execution.ParentBranchId,
                        branchById,
                        executionById)
                    .Any(branch => branch.ExecutionId == selected.Id))
            .OrderByDescending(execution => execution.Id)
            .ToList();
        foreach (var nested in nestedExecutions.Where(execution =>
                     BpmnFlowNodeTypes.IsComplexGateway(execution.GatewayType)
                     && execution.Cycle is not null))
        {
            var nestedState = await runtime.GetComplexGatewayStateAsync(
                instance.Id,
                nested.GatewayNodeId,
                true,
                cancellationToken);
            if (nestedState is null
                || nestedState.Cycle != nested.Cycle
                || nestedState.Phase != ComplexGatewayStateRecordPhases.WaitingForReset
                || (nestedState.ActiveExecutionId is not null
                    && nestedState.ActiveExecutionId != nested.Id))
            {
                continue;
            }

            foreach (var waiting in activeTokens.Where(candidate =>
                         candidate.Id != token.Id
                         && candidate.ComplexGatewayStateId == nestedState.Id
                         && candidate.ComplexGatewayCycle == nestedState.Cycle))
            {
                cancelledTokenIds.Add(waiting.Id);
            }
            var nestedNode = GetFlowNode(definition, nested.GatewayNodeId);
            var nestedTopology = GatewayTopologyCache.Get(
                instance.WorkflowDefinitionId,
                definition);
            var nestedDrainingTokenIds = activeTokens
                .Where(candidate =>
                    CanReachRemainingComplexInput(
                        candidate,
                        nestedNode,
                        nestedState.RemainingFlowIds,
                        nestedTopology))
                .Select(candidate => candidate.Id)
                .OrderBy(id => id)
                .ToArray();
            await runtime.AddComplexDrainMarkerAsync(
                nestedDrainingTokenIds,
                nestedState.Id,
                cancellationToken);
            await runtime.SaveComplexGatewayStateAsync(
                instance.Id,
                nested.GatewayNodeId,
                ComplexGatewayStateRecordPhases.InterruptedDraining,
                nestedState.Cycle,
                nestedState.ContributingFlowIds,
                nestedState.RemainingFlowIds,
                nestedState.ActivationDrainStateIds,
                nestedDrainingTokenIds,
                nested.Id,
                cancellationToken);
        }

        var interruptionActor = ToNodeExecutionActor(actor);
        await CancelDurableWorkForTokensAsync(
            instance.Id,
            cancelledTokenIds,
            "gatewayScopeCancelled",
            cancellationToken);
        await runtime.CancelOpenUserTasksForTokensAsync(
            cancelledTokenIds,
            NodeExecutionCompletionReasons.GatewayScopeCancelled,
            interruptionActor,
            cancellationToken);
        await runtime.CancelActiveMultiInstancesForTokensAsync(
            cancelledTokenIds,
            NodeExecutionCompletionReasons.GatewayScopeCancelled,
            interruptionActor,
            cancellationToken);
        await runtime.SetExecutionTokensStatusAsync(
            cancelledTokenIds,
            ExecutionTokenRecordStatuses.Cancelled,
            ExecutionTokenTerminationReasons.GatewayScopeCancelled,
            NodeExecutionCompletionReasons.GatewayScopeCancelled,
            interruptionActor,
            cancellationToken);

        var nestedExecutionIds = nestedExecutions
            .Select(execution => execution.Id)
            .ToHashSet();
        await runtime.SetGatewayBranchesStatusAsync(
            branches
                .Where(branch =>
                    nestedExecutionIds.Contains(branch.ExecutionId)
                    && branch.Status == GatewayBranchRecordStatuses.Active)
                .Select(branch => branch.Id)
                .ToArray(),
            GatewayBranchRecordStatuses.Cancelled,
            cancellationToken);
        await runtime.SetGatewayExecutionsStatusAsync(
            nestedExecutionIds,
            GatewayExecutionRecordStatuses.Cancelled,
            "ancestorInterrupt",
            interrupt.Id,
            token.Id,
            cancellationToken);
        await runtime.SetGatewayExecutionStatusAsync(
            selected.Id,
            GatewayExecutionRecordStatuses.Interrupted,
            completionReason,
            interrupt.Id,
            token.Id,
            cancellationToken);
        var selectedBranches = await runtime.ListGatewayBranchesAsync(
            selected.Id,
            cancellationToken);
        await runtime.SetGatewayBranchesStatusAsync(
            selectedBranches
                .Where(branch =>
                    branch.Status == GatewayBranchRecordStatuses.Active
                    && branch.Id != triggeringScopeBranchId)
                .Select(branch => branch.Id)
                .ToArray(),
            GatewayBranchRecordStatuses.Cancelled,
            cancellationToken);
        await runtime.SetGatewayBranchesStatusAsync(
            selectedBranches
                .Where(branch =>
                    branch.Status == GatewayBranchRecordStatuses.Active
                    && branch.Id == triggeringScopeBranchId)
                .Select(branch => branch.Id)
                .ToArray(),
            GatewayBranchRecordStatuses.Interrupted,
            cancellationToken);
        if (interruptedComplexState is not null)
        {
            var complexNode = GetFlowNode(definition, interruptedComplexState.GatewayNodeId);
            await TryFinishInterruptedComplexDrainAsync(
                instance,
                complexNode,
                definition,
                interruptedComplexState,
                cancellationToken);
        }
        await TryFinishInterruptedComplexDrainsAsync(
            instance,
            definition,
            cancellationToken);
        if (closeInactiveScopes)
        {
            await CloseInactiveGatewayScopesAsync(
                instance.Id,
                ExecutionTokenTerminationReasons.GatewayScopeCancelled,
                cancellationToken);
        }
        return (true, selected.ParentBranchId);
    }

    private async Task<bool> TryFinishInterruptedComplexDrainAsync(
        WorkflowInstanceRecord instance,
        FlowNodeModel gateway,
        WorkflowModel definition,
        ComplexGatewayStateRecord state,
        CancellationToken cancellationToken)
    {
        if (state.Phase != ComplexGatewayStateRecordPhases.InterruptedDraining)
        {
            return false;
        }
        var active = await runtime.ListExecutionTokensAsync(
            instance.Id, ExecutionTokenRecordStatuses.Active, cancellationToken);
        var topology = GatewayTopologyCache.Get(instance.WorkflowDefinitionId, definition);
        if (active.Any(token =>
                token.ComplexDrainStateIds.Contains(state.Id)
                && CanReachRemainingComplexInput(
                    token,
                    gateway,
                    state.RemainingFlowIds,
                    topology)))
        {
            return false;
        }
        await runtime.ClearComplexDrainMarkerAsync(
            instance.Id,
            state.Id,
            cancellationToken);
        await runtime.SaveComplexGatewayStateAsync(
            instance.Id,
            gateway.Id,
            ComplexGatewayStateRecordPhases.WaitingForStart,
            state.Cycle + 1,
            [],
            IncomingFlows(instance.WorkflowDefinitionId, definition, gateway.Id)
                .Select(flow => flow.Id)
                .ToArray(),
            [],
            [],
            null,
            cancellationToken);
        return true;
    }

    private static bool CanReachRemainingComplexInput(
        ExecutionTokenRecord token,
        FlowNodeModel gateway,
        IReadOnlyCollection<int> remainingFlowIds,
        GatewayTopologyIndex topology)
    {
        if (token.NodeId == gateway.Id)
        {
            return token.ArrivedViaFlowId is int arrivedViaFlowId
                   && remainingFlowIds.Contains(arrivedViaFlowId);
        }

        return topology.CanReachAnyInput(
            gateway.Id,
            token.NodeId,
            remainingFlowIds);
    }

    private async Task<bool> TryFinishInterruptedComplexDrainsAsync(
        WorkflowInstanceRecord instance,
        WorkflowModel definition,
        CancellationToken cancellationToken)
    {
        var states = await runtime.ListComplexGatewayStatesAsync(
            instance.Id,
            cancellationToken);
        var completed = false;
        foreach (var state in states
                     .Where(state =>
                         state.Phase == ComplexGatewayStateRecordPhases.InterruptedDraining)
                     .OrderBy(state => state.GatewayNodeId))
        {
            completed |= await TryFinishInterruptedComplexDrainAsync(
                instance,
                GetFlowNode(definition, state.GatewayNodeId),
                definition,
                state,
                cancellationToken);
        }
        return completed;
    }

    private static IReadOnlyList<GatewayBranchRecord> BuildGatewayAncestry(
        long? startBranchId,
        IReadOnlyDictionary<long, GatewayBranchRecord> branches,
        IReadOnlyDictionary<long, GatewayExecutionRecord> executions)
    {
        var result = new List<GatewayBranchRecord>();
        var visited = new HashSet<long>();
        var branchId = startBranchId;
        while (branchId is long id
               && visited.Add(id)
               && branches.TryGetValue(id, out var branch)
               && executions.TryGetValue(branch.ExecutionId, out var execution))
        {
            result.Add(branch);
            branchId = execution.ParentBranchId;
        }
        return result;
    }

    private async Task AdvanceAutomaticTokenAsync(
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord token,
        long? gatewayBranchId,
        FlowNodeModel currentNode,
        SequenceFlowModel flow,
        string note,
        WorkflowModel definition,
        ActorContext actor,
        Dictionary<string, JsonElement> storedOverlay,
        SequenceFlowInfoSnapshot? flowInfo,
        Queue<long> queue,
        CancellationToken cancellationToken,
        int immediateInterruptHops = 0,
        bool deferSave = false,
        bool administrativeAction = false,
        long? administrativeUserTaskId = null,
        long? administrativeMultiInstanceExecutionId = null,
        Dictionary<string, JsonElement>? administrativeValues = null,
        AdministrativeBatchFlowContext? administrativeBatch = null)
    {
        var nextNode = GetFlowNode(definition, flow.TargetRef);
        await RecordSequenceFlowOccurrenceAsync(
            flowInfo,
            instance.Id,
            token.Id,
            administrativeUserTaskId,
            administrativeMultiInstanceExecutionId,
            null,
            flow,
            note,
            isAction: administrativeAction,
            isTraversal: true,
            actor: actor,
            values: administrativeValues,
            cancellationToken: cancellationToken,
            administrativeBatch: administrativeBatch);
        await runtime.AddTokenHistoryAsync(
            instance.Id,
            token.Id,
            BpmnFlowNodeTypes.IsGateway(currentNode.Type)
                || administrativeBatch is null
                && BpmnFlowNodeTypes.IsTimerBoundary(currentNode.Type)
                    ? flow.Id
                    : null,
            currentNode.Id,
            nextNode.Id,
            actor.User,
            null,
            note,
            cancellationToken,
            actor.ActingFor,
            actor.DelegationId,
            administrativeBatch?.Reason,
            administrativeBatch?.BatchId);

        var targetTokenStatus = BpmnFlowNodeTypes.IsErrorEnd(nextNode.Type)
            ? ExecutionTokenRecordStatuses.Faulted
            : BpmnFlowNodeTypes.IsEnd(nextNode.Type)
                ? ExecutionTokenRecordStatuses.Completed
                : ExecutionTokenRecordStatuses.Active;
        var terminationReason = BpmnFlowNodeTypes.IsTerminateEnd(nextNode.Type)
            ? ExecutionTokenTerminationReasons.TerminateEnd
            : BpmnFlowNodeTypes.IsErrorEnd(nextNode.Type)
                ? ExecutionTokenTerminationReasons.ErrorEnd
                : BpmnFlowNodeTypes.IsEnd(nextNode.Type)
                    ? ExecutionTokenTerminationReasons.NormalEnd
                    : null;
        var targetInstance = instance with
        {
            ActiveTokenId = token.Id,
            CurrentStepId = nextNode.Id,
            FaultCode = BpmnFlowNodeTypes.IsErrorEnd(nextNode.Type) ? nextNode.ErrorCode : null,
            FaultDescription = BpmnFlowNodeTypes.IsErrorEnd(nextNode.Type)
                ? nextNode.ErrorDescription ?? nextNode.Name
                : null
        };
        var nextContext = WithContext(storedOverlay, actor, targetInstance, definition, nextNode);
        var nodeExecutionReason = note switch
        {
            "parallelFork" => NodeExecutionCompletionReasons.ParallelFork,
            "parallelJoin" => NodeExecutionCompletionReasons.ParallelJoin,
            "inclusiveSplit" => NodeExecutionCompletionReasons.InclusiveSplit,
            "inclusiveMerge" => NodeExecutionCompletionReasons.InclusiveMerge,
            "complexActivation" => NodeExecutionCompletionReasons.ComplexActivation,
            "complexReset" => NodeExecutionCompletionReasons.ComplexReset,
            "scopedInterrupt" => NodeExecutionCompletionReasons.ScopedInterrupt,
            "scopedInterruptSkipped" => NodeExecutionCompletionReasons.ScopedInterruptSkipped,
            "messageStart" => NodeExecutionCompletionReasons.MessageDelivery,
            "timer" => NodeExecutionCompletionReasons.TimerFired,
            NodeExecutionCompletionReasons.AdministrativeAction =>
                NodeExecutionCompletionReasons.AdministrativeAction,
            _ => NodeExecutionCompletionReasons.Normal
        };
        if (!BpmnFlowNodeTypes.IsTimer(currentNode.Type))
        {
            await CancelAttachedTimerBoundaryWaitsAsync(
                instance.Id,
                [token.Id],
                cancellationToken);
        }
        await runtime.UpdateExecutionTokenAsync(
            token.Id,
            ToSnapshot(nextNode, nextContext, instance.Id),
            targetTokenStatus,
            gatewayBranchId,
            flow.Id,
            terminationReason,
            null,
            ToNodeExecutionActor(actor),
            new NodeExecutionCompletionRecord(
                NodeExecutionRecordStatuses.Completed,
                nodeExecutionReason,
                BpmnFlowNodeTypes.IsGateway(currentNode.Type) ? flow.Id : null,
                flow.Id,
                gatewayBranchId,
                ToNodeExecutionActor(actor),
                HasExitGatewayBranchSnapshot: true),
            cancellationToken,
            deferSave,
            automaticActivationCount:
                BpmnFlowNodeTypes.IsTimer(currentNode.Type)
                    ? WorkflowAutomaticActivationGuard.ResetAfterExternalWaitOrTrigger()
                    : null);

        // Entering a scoped interrupt takes effect immediately. Deferring it to
        // the queue would let a later fork branch execute a service/script or
        // terminal event even though an earlier (lower-flow-id) branch had
        // already entered the interrupt. Chained interrupt continuations remain
        // bounded just like normal pass-through routing.
        if (BpmnFlowNodeTypes.IsScopedInterrupt(nextNode.Type))
        {
            if (immediateInterruptHops >= definition.FlowNodes.Count + 1)
            {
                throw new WorkflowDomainException("Pass-through routing cycle detected.");
            }
            var enteredToken = await runtime.GetExecutionTokenAsync(
                    token.Id, false, cancellationToken)
                ?? throw new WorkflowConflictException(
                    "The execution token disappeared while entering a scoped interrupt.");
            var interrupted = await InterruptGatewayScopeAsync(
                instance, enteredToken, nextNode, definition, actor, cancellationToken);
            var interruptFlow = SelectPassThroughFlow(
                instance.WorkflowDefinitionId,
                definition,
                nextNode,
                nextContext,
                flowInfo);
            await AdvanceAutomaticTokenAsync(
                instance,
                enteredToken,
                interrupted.ParentBranchId,
                nextNode,
                interruptFlow,
                interrupted.Interrupted ? "scopedInterrupt" : "scopedInterruptSkipped",
                definition,
                actor,
                storedOverlay,
                flowInfo,
                queue,
                cancellationToken,
                immediateInterruptHops + 1,
                deferSave);
            return;
        }

        if (BpmnFlowNodeTypes.IsTerminateEnd(nextNode.Type))
        {
            await TerminateInstanceAsync(instance.Id, token.Id, actor, cancellationToken);
            instance = instance with { Status = WorkflowInstanceStatuses.Completed };
            return;
        }
        if (BpmnFlowNodeTypes.IsErrorEnd(nextNode.Type))
        {
            await FaultInstanceAsync(instance.Id, token.Id, actor, cancellationToken);
            instance = instance with { Status = WorkflowInstanceStatuses.Faulted };
            return;
        }
        if (BpmnFlowNodeTypes.IsEnd(nextNode.Type))
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            var remaining = await runtime.ListExecutionTokensAsync(
                instance.Id, ExecutionTokenRecordStatuses.Active, cancellationToken);
            if (remaining.Count == 0)
            {
                await runtime.SetInstanceStatusAsync(
                    instance.Id, WorkflowInstanceStatuses.Completed, cancellationToken);
                instance = instance with { Status = WorkflowInstanceStatuses.Completed };
            }
            await CloseInactiveGatewayScopesAsync(instance.Id, "allEnded", cancellationToken);
            return;
        }

        queue.Enqueue(token.Id);
    }

    private async Task TerminateInstanceAsync(
        long instanceId,
        long triggeringTokenId,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        await unitOfWork.SaveChangesAsync(cancellationToken);
        var triggeringToken = await runtime.GetExecutionTokenAsync(
            triggeringTokenId, false, cancellationToken);
        var gatewayExecutions = await runtime.ListGatewayExecutionsAsync(
            instanceId,
            GatewayExecutionRecordStatuses.Active,
            cancellationToken);
        var gatewayBranches = await runtime.ListGatewayBranchesForInstanceAsync(
            instanceId,
            true,
            cancellationToken);
        var triggeringAncestry = BuildGatewayAncestry(
            triggeringToken?.GatewayBranchId,
            gatewayBranches.ToDictionary(branch => branch.Id),
            gatewayExecutions.ToDictionary(execution => execution.Id));
        var triggeringBranchIds = triggeringAncestry.Select(branch => branch.Id).ToHashSet();
        var active = await runtime.ListExecutionTokensAsync(
            instanceId, ExecutionTokenRecordStatuses.Active, cancellationToken);
        var cancelledIds = active.Where(token => token.Id != triggeringTokenId).Select(token => token.Id).ToList();
        var terminationActor = ToNodeExecutionActor(actor);
        await CancelDurableWorkForTokensAsync(
            instanceId,
            cancelledIds,
            "terminateEnd",
            cancellationToken);
        await runtime.CancelOpenUserTasksForTokensAsync(
            cancelledIds,
            NodeExecutionCompletionReasons.TerminateEnd,
            terminationActor,
            cancellationToken);
        await runtime.CancelActiveMultiInstancesForTokensAsync(
            cancelledIds,
            NodeExecutionCompletionReasons.TerminateEnd,
            terminationActor,
            cancellationToken);
        await runtime.SetExecutionTokensStatusAsync(
            cancelledIds,
            ExecutionTokenRecordStatuses.Cancelled,
            ExecutionTokenTerminationReasons.TerminateEnd,
            NodeExecutionCompletionReasons.TerminateEnd,
            terminationActor,
            cancellationToken);
        await runtime.SetGatewayBranchesStatusAsync(
            gatewayBranches
                .Where(branch => triggeringBranchIds.Contains(branch.Id))
                .Select(branch => branch.Id)
                .ToArray(),
            GatewayBranchRecordStatuses.Completed,
            cancellationToken);
        await runtime.SetGatewayBranchesStatusAsync(
            gatewayBranches
                .Where(branch => !triggeringBranchIds.Contains(branch.Id))
                .Select(branch => branch.Id)
                .ToArray(),
            GatewayBranchRecordStatuses.Cancelled,
            cancellationToken);
        await runtime.SetGatewayExecutionsStatusAsync(
            gatewayExecutions.Select(execution => execution.Id).ToArray(),
            GatewayExecutionRecordStatuses.Cancelled,
            "terminateEnd",
            null,
            triggeringTokenId,
            cancellationToken);
        await runtime.SetInstanceStatusAsync(instanceId, WorkflowInstanceStatuses.Completed, cancellationToken);
    }

    private async Task FaultInstanceAsync(
        long instanceId,
        long triggeringTokenId,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        await unitOfWork.SaveChangesAsync(cancellationToken);
        var triggeringToken = await runtime.GetExecutionTokenAsync(
            triggeringTokenId, false, cancellationToken);
        var gatewayExecutions = await runtime.ListGatewayExecutionsAsync(
            instanceId,
            GatewayExecutionRecordStatuses.Active,
            cancellationToken);
        var gatewayBranches = await runtime.ListGatewayBranchesForInstanceAsync(
            instanceId,
            true,
            cancellationToken);
        var triggeringAncestry = BuildGatewayAncestry(
            triggeringToken?.GatewayBranchId,
            gatewayBranches.ToDictionary(branch => branch.Id),
            gatewayExecutions.ToDictionary(execution => execution.Id));
        var triggeringBranchIds = triggeringAncestry.Select(branch => branch.Id).ToHashSet();
        var active = await runtime.ListExecutionTokensAsync(
            instanceId, ExecutionTokenRecordStatuses.Active, cancellationToken);
        var cancelledIds = active.Where(token => token.Id != triggeringTokenId).Select(token => token.Id).ToList();
        var faultActor = ToNodeExecutionActor(actor);
        await CancelDurableWorkForTokensAsync(
            instanceId,
            cancelledIds,
            "errorEnd",
            cancellationToken);
        await runtime.CancelOpenUserTasksForTokensAsync(
            cancelledIds,
            NodeExecutionCompletionReasons.ErrorEnd,
            faultActor,
            cancellationToken);
        await runtime.CancelActiveMultiInstancesForTokensAsync(
            cancelledIds,
            NodeExecutionCompletionReasons.ErrorEnd,
            faultActor,
            cancellationToken);
        await runtime.SetExecutionTokensStatusAsync(
            cancelledIds,
            ExecutionTokenRecordStatuses.Cancelled,
            ExecutionTokenTerminationReasons.ErrorEnd,
            NodeExecutionCompletionReasons.ErrorEnd,
            faultActor,
            cancellationToken);
        await runtime.SetGatewayBranchesStatusAsync(
            gatewayBranches
                .Where(branch => triggeringBranchIds.Contains(branch.Id))
                .Select(branch => branch.Id)
                .ToArray(),
            GatewayBranchRecordStatuses.Completed,
            cancellationToken);
        await runtime.SetGatewayBranchesStatusAsync(
            gatewayBranches
                .Where(branch => !triggeringBranchIds.Contains(branch.Id))
                .Select(branch => branch.Id)
                .ToArray(),
            GatewayBranchRecordStatuses.Cancelled,
            cancellationToken);
        await runtime.SetGatewayExecutionsStatusAsync(
            gatewayExecutions.Select(execution => execution.Id).ToArray(),
            GatewayExecutionRecordStatuses.Cancelled,
            "errorEnd",
            null,
            triggeringTokenId,
            cancellationToken);
        await runtime.SetInstanceStatusAsync(instanceId, WorkflowInstanceStatuses.Faulted, cancellationToken);
    }

    private async Task CloseInactiveGatewayScopesAsync(
        long instanceId,
        string completionReason,
        CancellationToken cancellationToken)
    {
        await unitOfWork.SaveChangesAsync(cancellationToken);
        var activeTokens = await runtime.ListExecutionTokensAsync(
            instanceId, ExecutionTokenRecordStatuses.Active, cancellationToken);
        var executions = await runtime.ListGatewayExecutionsAsync(
            instanceId, GatewayExecutionRecordStatuses.Active, cancellationToken);
        var branches = await runtime.ListGatewayBranchesForInstanceAsync(
            instanceId, true, cancellationToken);
        var executionById = executions.ToDictionary(execution => execution.Id);
        var branchById = branches.ToDictionary(branch => branch.Id);
        var activeExecutionIds = new HashSet<long>();
        var activeBranchIds = new HashSet<long>();
        foreach (var token in activeTokens)
        {
            var ancestry = BuildGatewayAncestry(
                token.GatewayBranchId, branchById, executionById);
            activeBranchIds.UnionWith(ancestry.Select(branch => branch.Id));
            activeExecutionIds.UnionWith(ancestry.Select(branch => branch.ExecutionId));
        }
        var complexStates = await runtime.ListComplexGatewayStatesAsync(
            instanceId,
            cancellationToken);
        foreach (var state in complexStates.Where(state =>
                     state.Phase == ComplexGatewayStateRecordPhases.WaitingForReset))
        {
            GatewayExecutionRecord? activationExecution = null;
            if (state.ActiveExecutionId is long activationExecutionId)
            {
                executionById.TryGetValue(activationExecutionId, out activationExecution);
            }
            else
            {
                // A split starts routing immediately after its execution row is
                // created. A synchronous branch can close before the caller
                // stores ActiveExecutionId on the state, so protect the unique
                // active start firing by its immutable node/cycle identity.
                activationExecution = executions.SingleOrDefault(execution =>
                    execution.GatewayNodeId == state.GatewayNodeId
                    && BpmnFlowNodeTypes.IsComplexGateway(execution.GatewayType)
                    && execution.Phase == "start"
                    && execution.Cycle == state.Cycle);
            }
            if (activationExecution is null)
            {
                continue;
            }
            activeExecutionIds.Add(activationExecution.Id);
            var ancestry = BuildGatewayAncestry(
                activationExecution.ParentBranchId,
                branchById,
                executionById);
            activeBranchIds.UnionWith(ancestry.Select(branch => branch.Id));
            activeExecutionIds.UnionWith(ancestry.Select(branch => branch.ExecutionId));
        }
        await runtime.SetGatewayBranchesStatusAsync(
            branches
                .Where(branch =>
                    branch.Status == GatewayBranchRecordStatuses.Active
                    && !activeBranchIds.Contains(branch.Id))
                .Select(branch => branch.Id)
                .ToArray(),
            completionReason == "join"
                ? GatewayBranchRecordStatuses.Merged
                : completionReason == ExecutionTokenTerminationReasons.GatewayScopeCancelled
                    ? GatewayBranchRecordStatuses.Cancelled
                    : GatewayBranchRecordStatuses.Completed,
            cancellationToken);
        await runtime.SetGatewayExecutionsStatusAsync(
            executions
                .Where(execution =>
                    execution.Status == GatewayExecutionRecordStatuses.Active
                    && !activeExecutionIds.Contains(execution.Id))
                .Select(execution => execution.Id)
                .ToArray(),
            completionReason == "join"
                ? GatewayExecutionRecordStatuses.Joined
                : completionReason == ExecutionTokenTerminationReasons.GatewayScopeCancelled
                    ? GatewayExecutionRecordStatuses.Cancelled
                    : GatewayExecutionRecordStatuses.Completed,
            completionReason,
            null,
            null,
            cancellationToken);
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

        var tokens = await runtime.ListExecutionTokensAsync(
            instance.Id, ExecutionTokenRecordStatuses.Active, cancellationToken);
        foreach (var token in tokens)
        {
            var node = GetFlowNode(definition, token.NodeId);
            if (BpmnFlowNodeTypes.IsUserTask(node.Type) && node.MultiInstance is not null)
            {
                await EnsureMultiInstanceInitializedForTokenAsync(
                    instance, token, node, definition, actor, cancellationToken);
            }
        }
    }

    private async Task EnsureMultiInstanceInitializedForTokenAsync(
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord token,
        FlowNodeModel node,
        WorkflowModel definition,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        var multi = node.MultiInstance;
        if (multi is null)
        {
            return;
        }
        if (token.WaitState == ExecutionTokenWaitStates.AsyncBefore)
        {
            return;
        }
        if (await runtime.GetActiveMultiInstanceAsync(token.Id, false, cancellationToken) is not null)
        {
            return;
        }

        var configured = await engineSettings.GetByKeyAsync("Workflow.MultiInstance.MaxInstances", cancellationToken);
        var maxInstances = configured is not null && int.TryParse(configured.Value, out var parsed) && parsed > 0
            ? parsed
            : 1000;
        var stored = await LoadVariablesAsync(instance.Id, cancellationToken);
        var tokenInstance = instance with
        {
            ActiveTokenId = token.Id,
            CurrentStepId = token.NodeId
        };
        var context = WithContext(stored, actor, tokenInstance, definition, node);
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
        await runtime.AddVariableAsync(
            instance.Id,
            multi.ResultVariable,
            node.Id,
            actor.User,
            emptyResult,
            cancellationToken,
            actingFor: actor.ActingFor,
            delegationId: actor.DelegationId);
        var outcomeIds = OutgoingFlows(instance.WorkflowDefinitionId, definition, node.Id)
            .Where(f => f.IsSelectable && !f.IsDefault && !f.CancelRemainingInstances)
            .Select(f => f.Id).ToList();
        await runtime.AddMultiInstanceAsync(
            instance.Id,
            token.Id,
            ToSnapshot(node),
            multi,
            items,
            outcomeIds,
            ToNodeExecutionActor(actor),
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private async Task<WorkflowInstanceRecord> ApplyUserTaskOwnershipInheritanceAsync(
        WorkflowInstanceRecord instance,
        WorkflowModel definition,
        CancellationToken cancellationToken)
    {
        instance = await ApplyAssignmentInheritanceAsync(instance, definition, cancellationToken);
        return await ApplyClaimInheritanceAsync(instance, definition, cancellationToken);
    }

    // Auto-assigns a resting requiresAssignment user task from a completed work
    // item. The assignee expression is resolved while the task snapshot is built;
    // inheritance therefore runs only when the new task still has no assignee.
    private async Task<WorkflowInstanceRecord> ApplyAssignmentInheritanceAsync(
        WorkflowInstanceRecord instance,
        WorkflowModel definition,
        CancellationToken cancellationToken)
    {
        if (instance.Status != WorkflowInstanceStatuses.Running)
        {
            return instance;
        }

        var latestUpdate = instance.UpdatedAt;
        var tasks = await runtime.ListUserTasksAsync(
            instance.Id, UserTaskRecordStatuses.Active, cancellationToken);
        foreach (var task in tasks.OrderBy(task => task.Id))
        {
            var token = await runtime.GetExecutionTokenAsync(task.TokenId, false, cancellationToken);
            if (token is null
                || token.Status != ExecutionTokenRecordStatuses.Active
                || token.NodeId != task.NodeId)
            {
                continue;
            }

            var node = GetFlowNode(definition, task.NodeId);
            if (!BpmnFlowNodeTypes.IsUserTask(node.Type)
                || !node.RequiresAssignment
                || node.AssignmentMode == AssignmentModes.Fresh
                || node.MultiInstance is not null
                || !task.RequiresAssignment
                || task.Assignee is not null)
            {
                continue;
            }

            var sourceNodeId = node.AssignmentMode == AssignmentModes.FromNode
                ? node.InheritAssignmentFromNodeId
                : null;
            var source = await runtime.GetAssignmentInheritanceSourceAsync(
                instance.Id, sourceNodeId, cancellationToken);
            if (source is null)
            {
                logger.LogDebug(
                    "Instance {InstanceId}: assignment mode '{AssignmentMode}' found no completed source task for node #{NodeId}; leaving task unassigned.",
                    instance.Id, node.AssignmentMode, node.Id);
                continue;
            }

            string? candidate;
            string candidateField;
            if (!string.IsNullOrWhiteSpace(source.Assignee))
            {
                candidate = source.Assignee.Trim();
                candidateField = "assignee";
            }
            else if (!string.IsNullOrWhiteSpace(source.CompletedActingFor))
            {
                candidate = source.CompletedActingFor.Trim();
                candidateField = "completedActingFor";
            }
            else if (!string.IsNullOrWhiteSpace(source.CompletedBy))
            {
                candidate = source.CompletedBy.Trim();
                candidateField = "completedBy";
            }
            else
            {
                logger.LogDebug(
                    "Instance {InstanceId}: assignment mode '{AssignmentMode}' source task #{SourceTaskId} has neither an assignee nor completing actor; leaving task unassigned.",
                    instance.Id, node.AssignmentMode, source.UserTaskId);
                continue;
            }

            var updatedAt = await runtime.UpdateUserTaskAssignmentAsync(
                task.Id, candidate, false, cancellationToken);
            var auditPayload = new Dictionary<string, JsonElement>
            {
                ["operation"] = JsonSerializer.SerializeToElement(UserTaskAssignmentOperations.Assigned),
                ["previousOwnership"] = JsonSerializer.SerializeToElement(UserTaskOwnershipKinds.Unassigned),
                ["previousOwner"] = JsonSerializer.SerializeToElement<string?>(null),
                ["newOwnership"] = JsonSerializer.SerializeToElement(UserTaskOwnershipKinds.Assigned),
                ["newOwner"] = JsonSerializer.SerializeToElement(candidate),
                ["previousRequiresClaim"] = JsonSerializer.SerializeToElement(task.RequiresClaim),
                ["newRequiresClaim"] = JsonSerializer.SerializeToElement(false),
                ["reason"] = JsonSerializer.SerializeToElement<string?>(null),
                ["authority"] = JsonSerializer.SerializeToElement("assignmentInheritance"),
                ["assignmentMode"] = JsonSerializer.SerializeToElement(node.AssignmentMode),
                ["sourceTaskId"] = JsonSerializer.SerializeToElement(source.UserTaskId),
                ["sourceNodeId"] = JsonSerializer.SerializeToElement(source.NodeId),
                ["candidateField"] = JsonSerializer.SerializeToElement(candidateField)
            };
            await runtime.AddUserTaskHistoryAsync(
                instance.Id,
                task.TokenId,
                task.Id,
                task.MultiInstanceExecutionId,
                task.ItemIndex,
                task.NodeId,
                "system",
                auditPayload,
                "taskAssignment",
                cancellationToken);
            var instanceUpdatedAt = await runtime.TouchInstanceAsync(instance.Id, cancellationToken);
            latestUpdate = new[] { latestUpdate, updatedAt, instanceUpdatedAt }.Max();
            logger.LogDebug(
                "Instance {InstanceId}: assignment mode '{AssignmentMode}' assigned node #{NodeId} task #{TaskId} to '{Assignee}' from task #{SourceTaskId}.",
                instance.Id, node.AssignmentMode, node.Id, task.Id, candidate, source.UserTaskId);
        }

        return instance with { UpdatedAt = latestUpdate };
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

        var history = await runtime.ListHistoryAsync(instance.Id, cancellationToken);
        var latestUpdate = instance.UpdatedAt;
        string? representativeClaimant = null;
        var tasks = await runtime.ListUserTasksAsync(
            instance.Id, UserTaskRecordStatuses.Active, cancellationToken);
        foreach (var task in tasks.OrderBy(task => task.Id))
        {
            var token = await runtime.GetExecutionTokenAsync(task.TokenId, false, cancellationToken);
            if (token is null
                || token.Status != ExecutionTokenRecordStatuses.Active
                || token.NodeId != task.NodeId)
            {
                continue;
            }

            var node = GetFlowNode(definition, task.NodeId);
            if (!BpmnFlowNodeTypes.IsUserTask(node.Type)
                || node.ClaimMode == ClaimModes.Fresh
                || node.RequiresAssignment
                || node.MultiInstance is not null
                || task.Assignee is not null
                || !task.RequiresClaim)
            {
                continue;
            }

            // SequenceFlowId/ActionId is also populated for gateway audit rows, so
            // user-action detection must explicitly exclude automatic pass-through
            // notes. Null-note legacy user actions and multi-instance actor notes remain
            // eligible for claim inheritance.
            var userActions = history.Where(IsActorActionHistory);
            if (node.ClaimMode == ClaimModes.FromNode)
            {
                userActions = userActions.Where(h => h.FromStepId == node.InheritClaimFromNodeId);
            }

            var claimant = userActions
                .OrderByDescending(h => h.PerformedAt)
                .Select(h => h.ActingFor ?? h.PerformedBy)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(claimant))
            {
                logger.LogDebug(
                    "Instance {InstanceId}: claim mode '{ClaimMode}' found no prior actor to inherit for task #{TaskId}; leaving unclaimed.",
                    instance.Id, node.ClaimMode, task.Id);
                continue;
            }

            var updatedAt = await runtime.UpdateUserTaskClaimAsync(
                task.Id, claimant, cancellationToken);
            var instanceUpdatedAt = await runtime.TouchInstanceAsync(instance.Id, cancellationToken);
            latestUpdate = new[] { latestUpdate, updatedAt, instanceUpdatedAt }.Max();
            representativeClaimant ??= claimant;
            logger.LogDebug(
                "Instance {InstanceId}: claim mode '{ClaimMode}' inherited claim to user '{Claimant}' for node #{NodeId}, task #{TaskId}.",
                instance.Id, node.ClaimMode, claimant, node.Id, task.Id);
        }

        return instance with
        {
            ClaimedBy = representativeClaimant ?? instance.ClaimedBy,
            UpdatedAt = latestUpdate
        };
    }

    private static bool IsActorActionHistory(InstanceHistoryRecord history) =>
        history.ActionId is not null
        && !string.IsNullOrWhiteSpace(history.PerformedBy)
        && history.Note is not (
            "start" or
            "messageStart" or
            "automatic" or
            "service" or
            "script" or
            "gateway" or
            "parallelFork" or
            "parallelJoin" or
            "inclusiveSplit" or
            "inclusiveMerge" or
            "complexActivation" or
            "complexReset" or
            "scopedInterrupt" or
            "scopedInterruptSkipped" or
            "boundary" or
            "error" or
            "message" or
            "administrativeAction");

    private SequenceFlowModel SelectPassThroughFlow(
        long definitionId,
        WorkflowModel definition,
        FlowNodeModel node,
        IReadOnlyDictionary<string, JsonElement> variables,
        SequenceFlowInfoSnapshot? flowInfo)
    {
        var outgoing = OutgoingFlows(definitionId, definition, node.Id);

        if (BpmnFlowNodeTypes.IsEntry(node.Type))
        {
            EnsureEntryRuntimeContract(definition, node);
        }

        if (BpmnFlowNodeTypes.IsExclusiveGateway(node.Type))
        {
            if (outgoing.Count == 1)
            {
                return outgoing[0];
            }
            logger.LogDebug("Evaluating exclusive gateway #{NodeId} ({NodeName}) outgoing flows...", node.Id, node.Name);
            var match = outgoing
                .Where(f => !f.IsDefault && !string.IsNullOrWhiteSpace(f.Condition))
                .OrderBy(f => f.ConditionPriority ?? int.MaxValue)
                .FirstOrDefault(f =>
                    SequenceFlowConditionEvaluator.Evaluate(f.Condition, variables, flowInfo));
            if (match is not null)
            {
                logger.LogDebug("Exclusive gateway #{NodeId} evaluated priority {Priority} flow {FlowId} ({FlowName}) condition '{Condition}' as True", node.Id, match.ConditionPriority, match.Id, match.Name, match.Condition);
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

    private static IReadOnlyList<SequenceFlowModel> SelectInclusiveOutgoingFlows(
        IReadOnlyList<SequenceFlowModel> outgoing,
        IReadOnlyDictionary<string, JsonElement> variables,
        SequenceFlowInfoSnapshot? flowInfo,
        int gatewayNodeId)
    {
        var selected = outgoing
            .Where(flow => !flow.IsDefault
                           && SequenceFlowConditionEvaluator.Evaluate(
                               flow.Condition, variables, flowInfo))
            .OrderBy(flow => flow.Id)
            .ToList();
        if (selected.Count > 0)
        {
            return selected;
        }
        var fallback = outgoing.SingleOrDefault(flow => flow.IsDefault);
        return fallback is not null
            ? [fallback]
            : throw new WorkflowDomainException(
                $"Inclusive gateway #{gatewayNodeId} has no matching condition and no default flow.");
    }

    private static IReadOnlyList<SequenceFlowModel> SelectComplexOutgoingFlows(
        IReadOnlyList<SequenceFlowModel> outgoing,
        IReadOnlyDictionary<string, JsonElement> variables,
        GatewayConditionContext gatewayContext,
        SequenceFlowInfoSnapshot? flowInfo,
        bool failWhenEmpty,
        int gatewayNodeId)
    {
        var selected = outgoing
            .Where(flow => !flow.IsDefault
                           && SequenceFlowConditionEvaluator.EvaluateGateway(
                               flow.Condition, variables, gatewayContext, flowInfo))
            .OrderBy(flow => flow.Id)
            .ToList();
        if (selected.Count > 0)
        {
            return selected;
        }
        var fallback = outgoing.SingleOrDefault(flow => flow.IsDefault);
        if (fallback is not null)
        {
            return [fallback];
        }
        if (failWhenEmpty)
        {
            throw new WorkflowDomainException(
                $"Complex gateway #{gatewayNodeId} activation selected no outgoing flow and has no default.");
        }
        return [];
    }

    private static void EnsureEntryRuntimeContract(WorkflowModel definition, FlowNodeModel entry)
    {
        var kind = BpmnFlowNodeTypes.IsMessageStart(entry.Type)
            ? "Message start event"
            : "Start event";
        var incoming = definition.SequenceFlows.Count(flow => flow.TargetRef == entry.Id);
        var outgoing = definition.SequenceFlows.Where(flow => flow.SourceRef == entry.Id).ToList();

        if (incoming != 0)
        {
            throw new WorkflowDomainException($"{kind} #{entry.Id} cannot have incoming sequence flows.");
        }

        if (outgoing.Count != 1)
        {
            throw new WorkflowDomainException($"{kind} #{entry.Id} must have exactly one outgoing sequence flow.");
        }

        var flow = outgoing[0];
        if (!flow.IsSelectable
            || flow.IsDefault
            || flow.CanActWithoutClaim
            || flow.CanActWithoutClaimRoles.Count > 0
            || flow.CancelRemainingInstances
            || flow.Roles.Count > 0
            || flow.Variables.Count > 0
            || !string.IsNullOrWhiteSpace(flow.Condition)
            || !string.IsNullOrWhiteSpace(flow.CompletionCondition)
            || flow.CompletionPriority is not null)
        {
            throw new WorkflowDomainException(
                $"The outgoing sequence flow from {kind.ToLowerInvariant()} #{entry.Id} must be unconditional and cannot define action or multi-instance metadata.");
        }

        var processNames = definition.Variables
            .Select(variable => variable.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entryNames = BpmnFlowNodeTypes.IsMessageStart(entry.Type)
            ? entry.Message?.OutputMappings.Select(mapping => mapping.Variable) ?? []
            : entry.Variables.Select(variable => variable.Name);
        var collision = entryNames
            .Concat(entry.Idempotency is null ? [] : [entry.Idempotency.Variable])
            .Concat(entry.BusinessKey is null ? [] : [entry.BusinessKey.Variable])
            .FirstOrDefault(processNames.Contains);
        if (collision is not null)
        {
            throw new WorkflowDomainException(
                $"Entry event #{entry.Id} variable '{collision}' collides with a process variable; variable names are case-insensitive.");
        }
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

        if (!string.Equals(service.Type, ServiceConnectorTypes.Rest, StringComparison.Ordinal))
        {
            return await FailServiceTaskBeforeInvocationAsync(
                instance.Id,
                instance.CurrentNodeExecutionId,
                node.Id,
                actor.User,
                service,
                storedOverlay,
                $"Service task #{node.Id} has unsupported connector type '{service.Type ?? "null"}'.",
                actor,
                cancellationToken);
        }

        return await ExecuteRestServiceTaskAsync(
            instance,
            node,
            definition,
            actor,
            service,
            variables,
            storedOverlay,
            cancellationToken);
    }

    private async Task<TaskExecutionOutcome> ExecuteRestServiceTaskAsync(
        WorkflowInstanceRecord instance,
        FlowNodeModel node,
        WorkflowModel definition,
        ActorContext actor,
        ServiceTaskModel service,
        IReadOnlyDictionary<string, JsonElement> variables,
        Dictionary<string, JsonElement> storedOverlay,
        CancellationToken cancellationToken)
    {
        if (!ServiceTaskTemplating.TrySubstituteScalarStrict(
                service.Url,
                variables,
                out var url,
                out var missingUrlVariable))
        {
            return await FailServiceTaskBeforeInvocationAsync(
                instance.Id,
                instance.CurrentNodeExecutionId,
                node.Id,
                actor.User,
                service,
                storedOverlay,
                $"Service task #{node.Id} URL references missing variable '{missingUrlVariable}'.",
                actor,
                cancellationToken);
        }

        var headers = new List<ServiceTaskHeader>(service.Headers.Count);
        foreach (var header in service.Headers)
        {
            if (!ServiceTaskTemplating.TrySubstituteScalarStrict(
                    header.Value,
                    variables,
                    out var value,
                    out var missingHeaderVariable))
            {
                return await FailServiceTaskBeforeInvocationAsync(
                    instance.Id,
                    instance.CurrentNodeExecutionId,
                    node.Id,
                    actor.User,
                    service,
                    storedOverlay,
                    $"Service task #{node.Id} header '{header.Name}' references missing variable '{missingHeaderVariable}'.",
                    actor,
                    cancellationToken);
            }

            headers.Add(new ServiceTaskHeader(header.Name, value));
        }

        var body = string.IsNullOrEmpty(service.Body)
            ? null
            : ServiceTaskTemplating.SubstituteJson(service.Body, variables);

        if (body is not null && IsJsonRequest(headers))
        {
            try
            {
                using var _ = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                return await FailServiceTaskBeforeInvocationAsync(
                    instance.Id,
                    instance.CurrentNodeExecutionId,
                    node.Id,
                    actor.User,
                    service,
                    storedOverlay,
                    $"Service task #{node.Id} rendered body is not valid JSON.",
                    actor,
                    cancellationToken);
            }
        }

        var request = new ServiceTaskRequest(service.Method, url, headers, body, service.TimeoutSeconds);
        logger.LogInformation("Service task #{NodeId} on instance {InstanceId}: invoking REST method {Method}.", node.Id, instance.Id, service.Method);
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
                instance.CurrentNodeExecutionId,
                actor,
                cancellationToken);
            if (mappingFailure is not null)
            {
                // A required output mapping could not be resolved from the 2xx
                // response body. Treat it like a service failure: write the status
                // variable (so an error path can branch on it), then fail the task
                // so the pass-through loop routes out an attached errorBoundaryEvent
                // (or, with no boundary, rolls back and returns 400).
                logger.LogWarning("Service task #{NodeId} on instance {InstanceId}: output mapping failed: {Reason}", node.Id, instance.Id, mappingFailure);
                await WriteStatusVariableAsync(
                    instance.Id,
                    node.Id,
                    performedBy,
                    service,
                    result.StatusCode,
                    storedOverlay,
                    instance.CurrentNodeExecutionId,
                    actor,
                    cancellationToken);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return TaskExecutionOutcome.Fail(mappingFailure);
            }

            logger.LogInformation("Service task #{NodeId} on instance {InstanceId} succeeded with HTTP {StatusCode}.", node.Id, instance.Id, result.StatusCode);
            await WriteStatusVariableAsync(
                instance.Id,
                node.Id,
                performedBy,
                service,
                result.StatusCode,
                storedOverlay,
                instance.CurrentNodeExecutionId,
                actor,
                cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return TaskExecutionOutcome.Ok();
        }

        // On failure the HTTP status (0 on transport error) is still written to
        // the optional statusVariable so the error path can branch on it. If no
        // errorBoundaryEvent is attached the loop throws (rollback + 400) and this
        // write rolls back with the transaction; if a boundary catches, it persists.
        logger.LogWarning("Service task #{NodeId} on instance {InstanceId} failed with HTTP {StatusCode}: {Reason}", node.Id, instance.Id, result.StatusCode, result.Error);
        await WriteStatusVariableAsync(
            instance.Id,
            node.Id,
            performedBy,
            service,
            result.StatusCode,
            storedOverlay,
            instance.CurrentNodeExecutionId,
            actor,
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var reason = result.Error ?? $"HTTP status {result.StatusCode}";
        return TaskExecutionOutcome.Fail($"Service task #{node.Id} REST call failed ({reason}).");
    }

    private static bool IsJsonRequest(IReadOnlyList<ServiceTaskHeader> headers)
    {
        var contentType = headers.FirstOrDefault(header =>
            string.Equals(header.Name, "Content-Type", StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return true;
        }

        var mediaType = contentType.Split(';', 2)[0].Trim();
        return string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<TaskExecutionOutcome> FailServiceTaskBeforeInvocationAsync(
        long instanceId,
        long? nodeExecutionId,
        int nodeId,
        string? setBy,
        ServiceTaskModel service,
        Dictionary<string, JsonElement> storedOverlay,
        string reason,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        // Preflight configuration/template failures have no HTTP response, so
        // expose status 0 consistently when an attached boundary catches them.
        await WriteStatusVariableAsync(
            instanceId,
            nodeId,
            setBy,
            service,
            0,
            storedOverlay,
            nodeExecutionId,
            actor,
            cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return TaskExecutionOutcome.Fail(reason);
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
        long? nodeExecutionId,
        ActorContext actor,
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
            logger.LogWarning(
                "Service task #{NodeId} received an invalid JSON response ({ExceptionType}).",
                nodeId,
                ex.GetType().Name);
            return $"Service task #{nodeId} response body was not valid JSON.";
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
                    await runtime.AddVariableAsync(
                        instanceId,
                        pair.Key,
                        nodeId,
                        setBy,
                        pair.Value,
                        cancellationToken,
                        nodeExecutionId,
                        actor.ActingFor,
                        actor.DelegationId);
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
        long? nodeExecutionId,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(service.StatusVariable))
        {
            return;
        }

        var value = JsonSerializer.SerializeToElement(statusCode);
        await runtime.AddVariableAsync(
            instanceId,
            service.StatusVariable,
            nodeId,
            setBy,
            value,
            cancellationToken,
            nodeExecutionId,
            actor.ActingFor,
            actor.DelegationId);
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
        SequenceFlowInfoSnapshot? flowInfo,
        CancellationToken cancellationToken)
    {
        var byName = definition.Variables
            .Where(v => !string.IsNullOrWhiteSpace(v.Name))
            .ToDictionary(v => v.Name!, StringComparer.OrdinalIgnoreCase);

        var overlay = new Dictionary<string, JsonElement>(variables, StringComparer.OrdinalIgnoreCase);
        var writes = new List<(VariableModel Target, JsonElement Value)>();

        try
        {
            if (string.Equals(node.ScriptFormat, ScriptFormats.JavaScript, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(node.Script))
                {
                    return TaskExecutionOutcome.Fail(
                        $"Script task #{node.Id} failed: the JavaScript body is missing.");
                }

                var context = new EngineScriptContext(overlay, byName, writes, flowInfo);
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
                    if (assignment is null)
                    {
                        throw new WorkflowDomainException(
                            $"Script task #{node.Id} has a null assignment entry.");
                    }

                    if (string.IsNullOrWhiteSpace(assignment.Variable))
                    {
                        throw new WorkflowDomainException($"Script task #{node.Id} has an assignment with no variable name.");
                    }

                    if (!byName.TryGetValue(assignment.Variable, out var target))
                    {
                        throw new WorkflowDomainException(
                            $"Script task #{node.Id} assigns '{assignment.Variable}' which is not a declared process variable.");
                    }

                    var result = SequenceFlowConditionEvaluator.EvaluateValue(
                        assignment.Expression,
                        overlay,
                        flowInfo: flowInfo);
                    var coerced = CoerceScriptValue(result, target);
                    overlay[target.Name!] = coerced;
                    writes.Add((target, coerced));
                }
            }
        }
        catch (WorkflowDomainException ex)
        {
            logger.LogWarning(ex,
                "Script task #{NodeId} on instance {InstanceId} rejected a process-variable write.",
                node.Id,
                instance.Id);
            return TaskExecutionOutcome.Fail($"Script task #{node.Id} failed: {ex.Message}");
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

            if (target.Nullable
                && overlay.TryGetValue(target.Name, out var currentValue)
                && (currentValue.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined))
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
            await runtime.AddVariableAsync(
                instance.Id,
                target.Name!,
                node.Id,
                performedBy,
                value,
                cancellationToken,
                instance.CurrentNodeExecutionId,
                actor.ActingFor,
                actor.DelegationId);
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
        List<(VariableModel Target, JsonElement Value)> writes,
        SequenceFlowInfoSnapshot? flowInfo) : IScriptContext
    {
        public bool TryGetVariable(string name, out JsonElement value) => overlay.TryGetValue(name, out value);

        public bool HasVariable(string name) => overlay.ContainsKey(name);

        public IReadOnlyDictionary<string, JsonElement> GetVariables() => overlay;

        public SequenceFlowRuntimeSummary GetFlowInfo(int flowId)
        {
            if (flowInfo is null)
            {
                throw new WorkflowDomainException(
                    "execution.getFlowInfo is not available because this workflow definition does not use FlowInfo.");
            }

            if (!flowInfo.TryGetSummary(flowId, out var summary))
            {
                throw new WorkflowDomainException(
                    $"execution.getFlowInfo references unknown sequence flow #{flowId}.");
            }

            return summary;
        }

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
    // (a genuine JS array or a list returned by FlowInfo) has each element coerced
    // to the declared element type.
    private static JsonElement CoerceScriptValue(object? result, VariableModel variable)
    {
        if (result is null)
        {
            var nullValue = JsonSerializer.SerializeToElement<object?>(null);
            EnsureProcessVariableValueAllowed(variable, nullValue);
            return nullValue;
        }

        JsonElement coerced;
        if (variable.IsArray)
        {
            if (result is JsonElement { ValueKind: JsonValueKind.Array } arrayElement)
            {
                var items = new List<object?>();
                foreach (var item in arrayElement.EnumerateArray())
                {
                    items.Add(CoerceScriptScalar(JsonElementToObject(item), variable.DataType));
                }

                coerced = JsonSerializer.SerializeToElement(items);
            }
            else if (result is IEnumerable enumerable
                     && result is not string
                     && result is not IDictionary)
            {
                var items = new List<object?>();
                foreach (var item in enumerable)
                {
                    var raw = item is JsonElement element ? JsonElementToObject(element) : item;
                    items.Add(CoerceScriptScalar(raw, variable.DataType));
                }

                coerced = JsonSerializer.SerializeToElement(items);
            }
            else
            {
                // A scalar result is wrapped so a declared array variable still
                // receives a single-element value.
                coerced = JsonSerializer.SerializeToElement(
                    new[] { CoerceScriptScalar(result, variable.DataType) });
            }
        }
        else
        {
            coerced = JsonSerializer.SerializeToElement(CoerceScriptScalar(result, variable.DataType));
        }

        EnsureProcessVariableValueAllowed(variable, coerced);
        return coerced;
    }

    private static void EnsureProcessVariableValueAllowed(VariableModel variable, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            if (variable.Nullable)
            {
                return;
            }

            throw new WorkflowDomainException(
                $"Process variable '{variable.Name}' does not allow null.");
        }

        if (!TypedOutputValueValidator.IsValid(value, variable.DataType, variable.IsArray))
        {
            throw new WorkflowDomainException(
                $"Process variable '{variable.Name}' must be {TypedOutputValueValidator.DescribeExpected(variable.DataType, variable.IsArray)}.");
        }
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
        var stored = await runtime.LoadLatestVariableVersionsAsync(
            instanceId,
            cancellationToken);
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in stored)
        {
            result[variable.Name] = variable.Value;
        }

        return result;
    }

    // Returns a copy of the stored variables overlaid with read-only context
    // (sys.*/config.*/setting.*). Context wins on collision so it can never be
    // spoofed by a stored variable. The result is for evaluation only and is
    // never persisted.
    private Dictionary<string, JsonElement> WithContext(
        IReadOnlyDictionary<string, JsonElement> stored,
        ActorContext actor,
        WorkflowInstanceRecord instance,
        WorkflowModel definition,
        FlowNodeModel currentNode,
        DateTimeOffset? capturedAt = null)
    {
        var merged = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in stored)
        {
            merged[pair.Key] = pair.Value;
        }
        foreach (var pair in BuildContextMap(
                     actor, instance, definition, currentNode, capturedAt))
        {
            merged[pair.Key] = pair.Value;
        }

        return merged;
    }

    private Dictionary<string, JsonElement> BuildContextMap(
        ActorContext actor,
        WorkflowInstanceRecord instance,
        WorkflowModel definition,
        FlowNodeModel currentNode,
        DateTimeOffset? capturedAt = null)
    {
        var now = (capturedAt ?? timeProvider.GetUtcNow()).UtcDateTime;
        var map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        void Put(string key, object? value) => map[key] = JsonSerializer.SerializeToElement(value);

        Put("sys.now", now.ToString("o", CultureInfo.InvariantCulture));
        Put("sys.today", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Put("sys.user", actor.User ?? string.Empty);
        Put("sys.roles", actor.Roles.ToArray());
        Put("sys.actingFor", actor.ActingFor);
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
        Dictionary<string, JsonElement> contextBase,
        bool enforceRequired = true,
        bool materializeNullableNullDefaults = false)
    {
        var resolved = ResolveVariables(
            variables,
            variableValues,
            contextBase,
            materializeNullableNullDefaults);
        ValidateVariableValues(variables, resolved, enforceRequired);
        ValidateResolvedVariableRules(variables, resolved, contextBase);
        return resolved;
    }

    private static Dictionary<string, JsonElement> ResolveVariables(
        IReadOnlyList<VariableModel> variables,
        Dictionary<string, JsonElement>? variableValues,
        Dictionary<string, JsonElement> contextBase,
        bool materializeNullableNullDefaults = false)
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
            else if (TryResolveDefault(
                variable,
                working,
                materializeNullableNullDefaults,
                out var defaultValue))
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

            if (variable.Nullable
                && TryGetValue(resolved, variable.Name, out var currentValue)
                && (currentValue.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined))
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
        bool materializeNullableNullDefault,
        out JsonElement value)
    {
        value = default;
        if (variable.DefaultValue is not { } raw
            || raw.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            if (!materializeNullableNullDefault || !variable.Nullable)
            {
                return false;
            }

            value = JsonSerializer.SerializeToElement<object?>(null);
            return true;
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
        if (variable.IsArray)
        {
            if (raw.ValueKind != JsonValueKind.Array)
            {
                return raw.Clone();
            }

            var array = new JsonArray();
            foreach (var item in raw.EnumerateArray())
            {
                array.Add(JsonNode.Parse(CoerceDefaultScalar(item, variable.DataType).GetRawText()));
            }

            return JsonSerializer.SerializeToElement(array);
        }

        return CoerceDefaultScalar(raw, variable.DataType);
    }

    private static JsonElement CoerceDefaultScalar(JsonElement raw, string dataType)
    {
        // Already-typed values pass through. Loosely typed number/boolean defaults
        // are accepted by the authoring contract and normalized to their JSON kind.
        if (raw.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
        {
            return raw.Clone();
        }

        if (raw.ValueKind == JsonValueKind.String)
        {
            var text = raw.GetString();
            switch (dataType)
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
        var versionChanges = await BuildVersionChangeAuditDtosAsync(id, cancellationToken);
        IReadOnlyList<InstanceVariableUpdateAuditDto> variableUpdateAudits = variableUpdates is null
            ? []
            : await BuildVariableUpdateAuditDtosAsync(id, cancellationToken);
        var node = GetFlowNode(workflow.Definition, instance.CurrentStepId);
        var projection = await BuildExecutionProjectionAsync(
            instance,
            cancellationToken,
            includeHistory: true);
        var multiProgress = projection.MultiInstances
            .FirstOrDefault(progress =>
                progress.Status == MultiInstanceRecordStatuses.Active
                && projection.ExecutionPositions.Any(position =>
                    position.TokenId == instance.ActiveTokenId
                    && position.MultiInstanceExecutionId == progress.ExecutionId))
            ?? projection.MultiInstances.FirstOrDefault(progress =>
                progress.Status == MultiInstanceRecordStatuses.Active);
        var workSummaries = await runtime.GetUserTaskWorkSummariesAsync([id], cancellationToken);
        var userTasks = workSummaries.TryGetValue(id, out var workSummary)
            ? ToUserTaskWorkSummary(workSummary)
            : null;

        return new InstanceDetailDto(
            instance.Id,
            ToRuntimeWorkflowDetail(workflow),
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
                v.SetAt)
            {
                ActingFor = v.ActingFor,
                DelegationId = v.DelegationId,
                InstanceVariableUpdateAuditId = v.InstanceVariableUpdateAuditId
            }).ToList(),
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
                h.PerformedAt)
            {
                ActingFor = h.ActingFor,
                DelegationId = h.DelegationId,
                Reason = h.Reason,
                AdministrativeActionBatchId = h.AdministrativeActionBatchId
            }).ToList(),
            multiProgress,
            userTasks,
            ToFault(instance.Status, instance.FaultCode, instance.FaultDescription, node.Name))
        {
            ExecutionPositions = projection.ExecutionPositions,
            MultiInstances = projection.MultiInstances,
            GatewayExecutions = projection.GatewayExecutions,
            ComplexGatewayStates = projection.ComplexGatewayStates,
            Completion = projection.Completion,
            VersionChanges = versionChanges,
            VariableUpdates = variableUpdateAudits
        };
    }

    private async Task<IReadOnlyList<InstanceVariableUpdateAuditDto>>
        BuildVariableUpdateAuditDtosAsync(
            long instanceId,
            CancellationToken cancellationToken)
    {
        var records = await variableUpdates!.ListByInstanceAsync(
            instanceId,
            cancellationToken);
        return records.Select(record => new InstanceVariableUpdateAuditDto(
            record.Id,
            record.InstanceId,
            record.WorkflowDefinitionId,
            record.PerformedBy,
            record.PerformedByRoles,
            record.Reason,
            record.Result.Deserialize<
                IReadOnlyList<InstanceVariableUpdateOutcomeDto>>(
                    InstanceVariableUpdateJsonOptions) ?? [],
            record.PerformedAt,
            record.IdempotencyKey,
            record.BatchId,
            record.BatchItemId)).ToArray();
    }

    private async Task<InstanceExecutionProjection> BuildExecutionProjectionAsync(
        WorkflowInstanceRecord instance,
        CancellationToken cancellationToken,
        bool includeHistory = false)
    {
        var tokens = includeHistory
            ? await runtime.ListExecutionTokensAsync(instance.Id, null, cancellationToken)
            : await runtime.ListCurrentExecutionTokensAsync(
                instance.Id,
                instance.ActiveTokenId,
                cancellationToken);
        var tasks = includeHistory
            ? await runtime.ListUserTasksAsync(instance.Id, null, cancellationToken)
            : await runtime.ListCurrentUserTasksAsync(instance.Id, cancellationToken);
        var multiExecutions = includeHistory
            ? await runtime.ListMultiInstancesAsync(instance.Id, null, cancellationToken)
            : await runtime.ListCurrentMultiInstancesAsync(instance.Id, cancellationToken);

        var taskByTokenAndNode = tasks
            .GroupBy(task => (task.TokenId, task.NodeId))
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(task => task.Status == UserTaskRecordStatuses.Active ? 0
                        : task.Status == UserTaskRecordStatuses.Pending ? 1 : 2)
                    .ThenByDescending(task => task.UpdatedAt)
                    .ThenByDescending(task => task.Id)
                    .First());
        var multiByTokenAndNode = multiExecutions
            .GroupBy(execution => (execution.TokenId, execution.NodeId))
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(execution => execution.Status == MultiInstanceRecordStatuses.Active ? 0 : 1)
                    .ThenByDescending(execution => execution.UpdatedAt)
                    .ThenByDescending(execution => execution.Id)
                    .First());

        var positions = tokens
            .Where(token => token.Status != ExecutionTokenRecordStatuses.Merged)
            .OrderBy(token => token.Id)
            .Select(token =>
            {
                taskByTokenAndNode.TryGetValue((token.Id, token.NodeId), out var task);
                multiByTokenAndNode.TryGetValue((token.Id, token.NodeId), out var multi);
                return new ExecutionPositionDto(
                    token.Id,
                    token.NodeId,
                    token.NodeName,
                    token.NodeExternalId,
                    token.NodeType,
                    token.Status,
                    token.ArrivedViaFlowId,
                    token.TerminationReason,
                    task?.Id,
                    multi?.Id,
                    token.ActivationId == Guid.Empty ? null : token.ActivationId,
                    token.WaitState,
                    token.WaitingJobId,
                    token.WaitingTimerSubscriptionId);
            })
            .ToList();

        var progressById = await BuildProgressAsync(
            multiExecutions.Select(execution => execution.Id).ToList(),
            cancellationToken);
        var multiProgress = multiExecutions
            .OrderBy(execution => execution.Id)
            .Select(execution => progressById.GetValueOrDefault(execution.Id))
            .Where(progress => progress is not null)
            .Cast<MultiInstanceProgressDto>()
            .ToList();

        var gatewayExecutions = includeHistory
            ? await runtime.ListGatewayExecutionsAsync(
                instance.Id, null, cancellationToken)
            : await runtime.ListCurrentGatewayExecutionsAsync(
                instance.Id, cancellationToken);
        var branches = includeHistory
            ? await runtime.ListGatewayBranchesForInstanceAsync(
                instance.Id, false, cancellationToken)
            : await runtime.ListGatewayBranchesForExecutionsAsync(
                gatewayExecutions.Select(execution => execution.Id).ToArray(),
                cancellationToken);
        var branchExecutionIds = branches.ToDictionary(branch => branch.Id, branch => branch.ExecutionId);
        var branchesByExecution = branches
            .GroupBy(branch => branch.ExecutionId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var gatewayDtos = gatewayExecutions
            .OrderBy(execution => execution.Id)
            .Select(execution =>
            {
                var executionBranches = branchesByExecution.GetValueOrDefault(execution.Id) ?? [];
                return new GatewayExecutionDto(
                    execution.Id,
                    execution.GatewayNodeId,
                    execution.GatewayType,
                    execution.Direction,
                    execution.Phase,
                    execution.Cycle,
                    execution.SelectedFlowIds,
                    execution.ParentBranchId is long parentBranchId
                        ? branchExecutionIds.GetValueOrDefault(parentBranchId)
                        : null,
                    execution.Status,
                    execution.CompletionReason,
                    execution.InterruptingNodeId,
                    execution.InterruptingTokenId,
                    executionBranches.Count,
                    executionBranches.Count(branch =>
                        branch.Status == GatewayBranchRecordStatuses.Active),
                    executionBranches.Count(branch =>
                        branch.Status == GatewayBranchRecordStatuses.Completed),
                    executionBranches.Count(branch =>
                        branch.Status == GatewayBranchRecordStatuses.Merged),
                    executionBranches.Count(branch =>
                        branch.Status == GatewayBranchRecordStatuses.Interrupted),
                    executionBranches.Count(branch =>
                        branch.Status == GatewayBranchRecordStatuses.Cancelled),
                    execution.CreatedAt,
                    execution.UpdatedAt,
                    execution.CompletedAt);
            })
            .ToList();
        var complexStateDtos = (await runtime.ListComplexGatewayStatesAsync(
                instance.Id, cancellationToken))
            .Select(state => new ComplexGatewayStateDto(
                state.GatewayNodeId,
                state.Phase,
                state.Cycle,
                state.ContributingFlowIds,
                state.RemainingFlowIds,
                state.DrainingTokenIds,
                state.ActiveExecutionId,
                state.UpdatedAt))
            .ToList();

        CompletionInfoDto? completion = null;
        if (instance.Status == WorkflowInstanceStatuses.Completed)
        {
            var terminal = tokens
                .Where(token => token.Status == ExecutionTokenRecordStatuses.Completed
                                && token.TerminationReason is
                                    ExecutionTokenTerminationReasons.NormalEnd
                                    or ExecutionTokenTerminationReasons.TerminateEnd)
                .OrderByDescending(token =>
                    token.TerminationReason == ExecutionTokenTerminationReasons.TerminateEnd)
                .ThenByDescending(token => token.UpdatedAt)
                .ThenByDescending(token => token.Id)
                .FirstOrDefault();
            if (terminal is not null)
            {
                completion = new CompletionInfoDto(
                    terminal.TerminationReason == ExecutionTokenTerminationReasons.TerminateEnd
                        ? WorkflowCompletionKinds.Terminate
                        : WorkflowCompletionKinds.Normal,
                    terminal.Id,
                    terminal.NodeId,
                    terminal.NodeName,
                    terminal.NodeExternalId,
                    terminal.UpdatedAt);
            }
        }

        return new InstanceExecutionProjection(
            positions,
            multiProgress,
            gatewayDtos,
            complexStateDtos,
            completion);
    }

    private sealed record InstanceExecutionProjection(
        IReadOnlyList<ExecutionPositionDto> ExecutionPositions,
        IReadOnlyList<MultiInstanceProgressDto> MultiInstances,
        IReadOnlyList<GatewayExecutionDto> GatewayExecutions,
        IReadOnlyList<ComplexGatewayStateDto> ComplexGatewayStates,
        CompletionInfoDto? Completion);

    private static WorkflowDetailDto ToRuntimeWorkflowDetail(WorkflowDefinitionRecord workflow)
    {
        var definition = JsonSerializer.Deserialize<WorkflowModel>(
            JsonSerializer.Serialize(workflow.Definition))
            ?? throw new InvalidOperationException("Unable to clone the workflow definition.");
        foreach (var node in definition.FlowNodes)
        {
            if (node.Message is null)
            {
                continue;
            }

            node.Message.ClientSecret = RedactedSecret;
            node.Message.HeaderValue = RedactedSecret;
        }
        if (definition.TaskDistribution is not null)
        {
            definition.TaskDistribution.ClientSecret = RedactedSecret;
        }

        return new WorkflowDetailDto(
            workflow.Id,
            workflow.Name,
            workflow.WorkflowKey,
            workflow.Version,
            workflow.IsPublished,
            workflow.IsDefault,
            workflow.CreatedAt,
            definition);
    }

    private const string RedactedSecret = "[redacted]";

    private async Task<WorkflowDefinitionRecord> GetPublishedWorkflowAsync(
        long id,
        CancellationToken cancellationToken)
    {
        return await definitions.GetPublishedAsync(id, cancellationToken)
            ?? throw new WorkflowDomainException("Workflow must be published before instances can be started.");
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

    private async Task EnsureRequiredAssignmentFamilyStartableAsync(
        WorkflowDefinitionRecord workflow,
        CancellationToken cancellationToken)
    {
        if (!workflow.Definition.FlowNodes.Any(node =>
                BpmnFlowNodeTypes.IsUserTask(node.Type) && node.RequiresAssignment))
        {
            return;
        }

        if (workflow.Definition.TaskAssignmentRoles.Any(role =>
                !string.IsNullOrWhiteSpace(role)))
        {
            return;
        }

        var currentDefault = await definitions.GetDefaultByWorkflowKeyAsync(
            workflow.WorkflowKey,
            cancellationToken);
        if (currentDefault?.Definition.TaskDistribution is null)
        {
            throw new WorkflowDomainException(
                "A workflow containing required-assignment tasks without taskAssignmentRoles cannot start unless its current default published version configures taskDistribution credentials.");
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

    private static IReadOnlyList<string>? ToLegacySort(
        IReadOnlyList<SearchSortDto>? sort) =>
        sort?.Select(static criterion =>
            criterion is null
                ? string.Empty
                : $"{criterion.Field}:{criterion.Direction}").ToArray();

    private static IReadOnlyList<InstanceSortCriterion> ParseInstanceSort(IReadOnlyList<string>? sort)
    {
        if (sort is null || sort.Count == 0)
        {
            return [new InstanceSortCriterion(InstanceSortField.UpdatedAt, SortDirection.Descending)];
        }

        return ParseSort(
            sort,
            field => field.ToLowerInvariant() switch
            {
                "id" => InstanceSortField.Id,
                "createdat" => InstanceSortField.CreatedAt,
                "updatedat" => InstanceSortField.UpdatedAt,
                _ => throw new WorkflowDomainException(
                    $"Unknown instance sort field '{field}'. Allowed fields: id, createdAt, updatedAt.")
            },
            static (field, direction) => new InstanceSortCriterion(field, direction));
    }

    private static IReadOnlyList<InboxSortCriterion> ParseInboxSort(IReadOnlyList<string>? sort)
    {
        if (sort is null || sort.Count == 0)
        {
            return [new InboxSortCriterion(InboxSortField.TaskUpdatedAt, SortDirection.Descending)];
        }

        return ParseSort(
            sort,
            field => field.ToLowerInvariant() switch
            {
                "usertaskid" => InboxSortField.UserTaskId,
                "instanceid" => InboxSortField.InstanceId,
                "taskcreatedat" => InboxSortField.TaskCreatedAt,
                "taskupdatedat" => InboxSortField.TaskUpdatedAt,
                "instancecreatedat" => InboxSortField.InstanceCreatedAt,
                "instanceupdatedat" => InboxSortField.InstanceUpdatedAt,
                _ => throw new WorkflowDomainException(
                    $"Unknown inbox sort field '{field}'. Allowed fields: userTaskId, instanceId, taskCreatedAt, taskUpdatedAt, instanceCreatedAt, instanceUpdatedAt.")
            },
            static (field, direction) => new InboxSortCriterion(field, direction));
    }

    private static IReadOnlyList<TCriterion> ParseSort<TField, TCriterion>(
        IReadOnlyList<string> sort,
        Func<string, TField> parseField,
        Func<TField, SortDirection, TCriterion> createCriterion)
        where TField : struct, Enum
    {
        const int maxSortCriteria = 3;
        if (sort.Count > maxSortCriteria)
        {
            throw new WorkflowDomainException($"At most {maxSortCriteria} sort clauses are allowed.");
        }

        var result = new List<TCriterion>(sort.Count);
        var fields = new HashSet<TField>();
        foreach (var raw in sort)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new WorkflowDomainException("Sort clauses must not be blank. Expected format 'field:asc' or 'field:desc'.");
            }

            var separator = raw.IndexOf(':');
            if (separator <= 0 || separator == raw.Length - 1 || raw.IndexOf(':', separator + 1) >= 0)
            {
                throw new WorkflowDomainException(
                    $"Invalid sort clause '{raw}'. Expected format 'field:asc' or 'field:desc'.");
            }

            var fieldText = raw[..separator].Trim();
            var directionText = raw[(separator + 1)..].Trim();
            if (fieldText.Length == 0 || directionText.Length == 0)
            {
                throw new WorkflowDomainException(
                    $"Invalid sort clause '{raw}'. Expected format 'field:asc' or 'field:desc'.");
            }

            var field = parseField(fieldText);
            if (!fields.Add(field))
            {
                throw new WorkflowDomainException($"Sort field '{fieldText}' was specified more than once.");
            }

            var direction = directionText.ToLowerInvariant() switch
            {
                "asc" => SortDirection.Ascending,
                "desc" => SortDirection.Descending,
                _ => throw new WorkflowDomainException(
                    $"Unknown sort direction '{directionText}'. Allowed directions: asc, desc.")
            };
            result.Add(createCriterion(field, direction));
        }

        return result;
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
            row.Variables,
            ToFault(row.Status, row.FaultCode, row.FaultDescription, row.CurrentNodeName))
        {
            ExecutionPositions = (row.ExecutionPositions ?? [])
                .Select(position => new ExecutionPositionDto(
                    position.TokenId,
                    position.NodeId,
                    position.NodeName,
                    position.NodeExternalId,
                    position.NodeType,
                    position.Status,
                    position.ArrivedViaFlowId,
                    position.TerminationReason,
                    position.UserTaskId,
                    position.MultiInstanceExecutionId,
                    position.ActivationId,
                    position.WaitState,
                    position.WaitingJobId,
                    position.WaitingTimerSubscriptionId))
                .ToArray(),
            Completion = row.Completion is null
                ? null
                : new CompletionInfoDto(
                    row.Completion.Kind,
                    row.Completion.TokenId,
                    row.Completion.NodeId,
                    row.Completion.NodeName,
                    row.Completion.NodeExternalId,
                    row.Completion.CompletedAt)
        };

    private async Task<UserTaskDto> BuildUserTaskDtoAsync(
        UserTaskRecord task,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        var progress = task.MultiInstanceExecutionId is long executionId
            ? await BuildProgressAsync(executionId, cancellationToken)
            : null;
        var presentation = await BuildUserTaskPresentationAsync(task, actor, cancellationToken);
        return ToUserTaskDto(
            task,
            progress,
            presentation.Capabilities,
            presentation.Attributes,
            actor);
    }

    private static UserTaskDto ToUserTaskDto(
        UserTaskRecord task,
        MultiInstanceProgressDto? progress,
        UserTaskCapabilitiesDto capabilities,
        IReadOnlyList<WorkflowAttributeModel> attributes,
        ActorContext? actor = null) =>
        new UserTaskDto(task.Id, task.InstanceId, task.TokenId, task.NodeId, task.NodeName,
            task.NodeExternalId, task.Roles, task.RequiresClaim, task.RequiresAssignment, task.Status, task.ClaimedBy,
            task.Assignee, task.ItemIndex, task.ItemValue, task.SelectedFlowId, task.CompletedBy,
            task.Result, capabilities, progress,
            task.CreatedAt, task.UpdatedAt, task.CompletedAt)
        {
            DelegatedAccess = actor?.DelegationId is long delegationId
                              && !string.IsNullOrWhiteSpace(actor.ActingFor)
                ? new DelegatedTaskAccessDto(delegationId, actor.ActingFor)
                : null,
            CompletedDelegatedAccess = task.CompletionDelegationId is long completionDelegationId
                                       && !string.IsNullOrWhiteSpace(task.CompletedActingFor)
                ? new DelegatedTaskAccessDto(completionDelegationId, task.CompletedActingFor)
                : null,
            CompletionKind = task.CompletionKind,
            CompletionReason = task.CompletionReason,
            AdministrativeActionBatchId = task.AdministrativeActionBatchId,
            Attributes = attributes
        };

    private sealed record UserTaskPresentation(
        UserTaskCapabilitiesDto Capabilities,
        IReadOnlyList<WorkflowAttributeModel> Attributes);

    private async Task<UserTaskPresentation> BuildUserTaskPresentationAsync(
        UserTaskRecord task,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        var user = EffectiveUser(actor);
        var claimedByMe = string.Equals(
            task.ClaimedBy, NormalizeUser(actor.User), StringComparison.OrdinalIgnoreCase);
        var disabled = new UserTaskCapabilitiesDto(claimedByMe, false, false, false);
        var instance = await runtime.GetInstanceAsync(task.InstanceId, cancellationToken);
        var workflow = instance is null
            ? null
            : await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken);
        var attributes = workflow is null
            ? []
            : NodeAttributes(workflow, task.NodeId);
        if (task.Status != UserTaskRecordStatuses.Active)
            return new UserTaskPresentation(disabled, attributes);

        await LoadSettingsAsync(cancellationToken);
        var token = await runtime.GetExecutionTokenAsync(task.TokenId, false, cancellationToken);
        if (instance is null || instance.Status != WorkflowInstanceStatuses.Running
                             || workflow is null
                             || token is null
                             || token.Status != ExecutionTokenRecordStatuses.Active
                             || token.NodeId != task.NodeId)
            return new UserTaskPresentation(disabled, attributes);

        var stored = await LoadVariablesAsync(instance.Id, cancellationToken);
        var executionsById = new Dictionary<long, MultiInstanceExecutionRecord>();
        IReadOnlyDictionary<long, MultiInstanceActorStateRecord> actorStates =
            new Dictionary<long, MultiInstanceActorStateRecord>();
        if (task.MultiInstanceExecutionId is long executionId)
        {
            var execution = await runtime.GetMultiInstanceAsync(executionId, false, cancellationToken);
            if (execution is not null)
            {
                executionsById[execution.Id] = execution;
                if (execution.OnePerActor)
                {
                    actorStates = await runtime.GetMultiInstanceActorStatesAsync(
                        [execution.Id], user, cancellationToken);
                }
            }
        }

        return new UserTaskPresentation(
            BuildUserTaskCapabilities(
                task, actor, instance, workflow, stored, executionsById, actorStates),
            attributes);
    }

    private UserTaskCapabilitiesDto BuildUserTaskCapabilities(
        UserTaskRecord task,
        ActorContext actor,
        WorkflowInstanceRecord instance,
        WorkflowDefinitionRecord workflow,
        IReadOnlyDictionary<string, JsonElement> stored,
        IReadOnlyDictionary<long, MultiInstanceExecutionRecord> executionsById,
        IReadOnlyDictionary<long, MultiInstanceActorStateRecord> actorStates,
        DateTimeOffset? capturedAt = null)
    {
        var actualUser = NormalizeUser(actor.User);
        var user = EffectiveUser(actor);
        var claimedByMe = string.Equals(task.ClaimedBy, actualUser, StringComparison.OrdinalIgnoreCase);
        var claimedByRepresentedOwner =
            string.Equals(task.ClaimedBy, user, StringComparison.OrdinalIgnoreCase);
        if (task.Status != UserTaskRecordStatuses.Active
            || instance.Status != WorkflowInstanceStatuses.Running)
            return new UserTaskCapabilitiesDto(claimedByMe, false, false, false);

        var node = GetFlowNode(workflow.Definition, task.NodeId);
        var canUnclaim = task.Assignee is null
                         && task.RequiresClaim
                         && !string.IsNullOrWhiteSpace(task.ClaimedBy)
                         && (claimedByRepresentedOwner
                             || HasUnclaimOverrideRole(workflow.Definition, actor));
        if (!CanUserTaskActor(task, node, actor))
            return new UserTaskCapabilitiesDto(claimedByMe, false, canUnclaim, false);

        MultiInstanceExecutionRecord? execution = null;
        if (task.MultiInstanceExecutionId is long executionId)
        {
            execution = executionsById.GetValueOrDefault(executionId);
            if (execution is null || execution.Status != MultiInstanceRecordStatuses.Active
                                  || execution.TokenId != task.TokenId
                                  || execution.NodeId != task.NodeId)
                return new UserTaskCapabilitiesDto(claimedByMe, false, canUnclaim, false);
            if (execution.OnePerActor)
            {
                var actorState = actorStates.GetValueOrDefault(execution.Id);
                if (actorState?.HasCompleted == true
                    || (actorState?.OwnedTaskId is long ownedTaskId && ownedTaskId != task.Id))
                    return new UserTaskCapabilitiesDto(claimedByMe, false, canUnclaim, false);
            }
        }

        var roles = NormalizeRoles(actor.Roles);
        var eligible = GetEligibleUserTaskFlows(
            instance, workflow, node, task, execution, actor, stored, capturedAt);
        var canClaim = task.Assignee is null
                       && actor.DelegationId is null
                       && task.RequiresClaim
                       && string.IsNullOrWhiteSpace(task.ClaimedBy)
                       && eligible.Count > 0;
        var canAct = eligible.Any(flow =>
            !task.RequiresClaim || claimedByRepresentedOwner || CanBypassClaim(flow, roles));
        return new UserTaskCapabilitiesDto(claimedByMe, canClaim, canUnclaim, canAct);
    }

    private async Task<IReadOnlyList<SequenceFlowModel>> GetEligibleUserTaskFlowsAsync(
        WorkflowInstanceRecord instance,
        WorkflowDefinitionRecord workflow,
        FlowNodeModel node,
        UserTaskRecord task,
        MultiInstanceExecutionRecord? execution,
        ActorContext actor,
        CancellationToken cancellationToken,
        DateTimeOffset? capturedAt = null)
    {
        var stored = await LoadVariablesAsync(instance.Id, cancellationToken);
        return GetEligibleUserTaskFlows(
            instance, workflow, node, task, execution, actor, stored, capturedAt);
    }

    private IReadOnlyList<SequenceFlowModel> GetEligibleUserTaskFlows(
        WorkflowInstanceRecord instance,
        WorkflowDefinitionRecord workflow,
        FlowNodeModel node,
        UserTaskRecord task,
        MultiInstanceExecutionRecord? execution,
        ActorContext actor,
        IReadOnlyDictionary<string, JsonElement> stored,
        DateTimeOffset? capturedAt = null)
    {
        var context = WithContext(
            stored, actor, instance, workflow.Definition, node, capturedAt);
        if (execution is not null) AddMultiInstanceContext(context, task, execution);
        var roles = NormalizeRoles(actor.Roles);
        return OutgoingFlows(workflow.Id, workflow.Definition, node.Id)
            .Where(flow => flow.IsSelectable && !flow.IsDefault
                           && RoleAllowed(flow.Roles, roles)
                           && (string.IsNullOrWhiteSpace(flow.Condition)
                               || SequenceFlowConditionEvaluator.Evaluate(flow.Condition, context)))
            .ToList();
    }

    private static ManagedUserTaskDto ToManagedUserTaskDto(
        ManagedUserTaskRecord task,
        MultiInstanceProgressDto? progress,
        IReadOnlyList<WorkflowAttributeModel> attributes)
    {
        var ownership = task.Assignee is not null
            ? UserTaskOwnershipKinds.Assigned
            : task.ClaimedBy is not null
                ? UserTaskOwnershipKinds.Claimed
                : UserTaskOwnershipKinds.Unassigned;
        return new ManagedUserTaskDto(
            task.UserTaskId,
            task.InstanceId,
            task.TokenId,
            task.WorkflowDefinitionId,
            task.WorkflowKey,
            task.WorkflowName,
            task.WorkflowVersion,
            task.BusinessKey,
            task.NodeId,
            task.NodeName,
            task.NodeExternalId,
            task.NodeRoles,
            task.RequiresClaim,
            task.RequiresAssignment,
            ownership,
            task.Assignee ?? task.ClaimedBy,
            task.MultiInstanceExecutionId,
            task.ItemIndex,
            task.ItemValue,
            progress,
            task.CreatedAt,
            task.UpdatedAt,
            task.Variables)
        {
            Attributes = attributes
        };
    }

    private async Task<PagedResult<ManagedUserTaskDto>> BuildManagedUserTaskPageAsync(
        PagedResult<ManagedUserTaskRecord> paged,
        CancellationToken cancellationToken)
    {
        var executionIds = paged.Items
            .Where(item => item.MultiInstanceExecutionId is not null)
            .Select(item => item.MultiInstanceExecutionId!.Value)
            .Distinct()
            .ToList();
        var progress = await BuildProgressAsync(executionIds, cancellationToken);
        var definitionIds = paged.Items
            .Select(item => item.WorkflowDefinitionId)
            .Distinct()
            .ToArray();
        var workflows = await definitions.GetManyAsync(definitionIds, cancellationToken);
        foreach (var definitionId in definitionIds)
        {
            if (!workflows.ContainsKey(definitionId))
            {
                throw new WorkflowDomainException(
                    $"Workflow definition #{definitionId} was not found.");
            }
        }
        var items = paged.Items.Select(item => ToManagedUserTaskDto(
            item,
            item.MultiInstanceExecutionId is long executionId
                ? progress.GetValueOrDefault(executionId)
                : null,
            NodeAttributes(workflows[item.WorkflowDefinitionId], item.NodeId))).ToList();
        return new PagedResult<ManagedUserTaskDto>(items, paged.Page, paged.PageSize, paged.TotalCount);
    }

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
            summary.SoleAssignee)
        {
            NormalTaskCount = summary.NormalTaskCount,
            MultiInstanceTaskCount = summary.MultiInstanceTaskCount,
            SoleUserTaskId = summary.SoleUserTaskId
        };

    private sealed record MultiInstanceParentInterruptResult(
        int SelectedFlowId,
        string CompletedBy,
        IReadOnlyList<string> UserRoles,
        DateTimeOffset CompletedAt,
        IReadOnlyDictionary<string, JsonElement> Variables,
        string? ActingFor,
        long? DelegationId);

    private async Task<JsonElement> BuildMultiInstanceResultAsync(
        long executionId,
        MultiInstanceParentInterruptResult? parentInterrupt,
        CancellationToken cancellationToken)
    {
        var tasks = await runtime.ListExecutionTasksAsync(executionId, cancellationToken);
        var results = tasks.OrderBy(t => t.ItemIndex).Select(t => (object)new
        {
            kind = "item",
            index = t.ItemIndex,
            item = t.ItemValue,
            userTaskId = (long?)t.Id,
            status = t.Status,
            selectedFlowId = t.SelectedFlowId,
            completedBy = t.CompletedBy,
            actingFor = t.CompletedActingFor,
            delegationId = t.CompletionDelegationId,
            userRoles = t.CompletedByRoles,
            completedAt = t.CompletedAt,
            variables = t.Result
        }).ToList();
        if (parentInterrupt is not null)
        {
            results.Add(new
            {
                kind = "parentInterrupt",
                index = (int?)null,
                item = (JsonElement?)null,
                userTaskId = (long?)null,
                status = MultiInstanceRecordStatuses.Interrupted,
                selectedFlowId = (int?)parentInterrupt.SelectedFlowId,
                completedBy = parentInterrupt.CompletedBy,
                actingFor = parentInterrupt.ActingFor,
                delegationId = parentInterrupt.DelegationId,
                userRoles = parentInterrupt.UserRoles,
                completedAt = (DateTimeOffset?)parentInterrupt.CompletedAt,
                variables = parentInterrupt.Variables
            });
        }
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

    /// <summary>
    /// One authoritative authorization result for actor-scoped task operations.
    /// The actual JWT actor is never replaced; delegated access adds only the
    /// represented owner and immutable grant id.
    /// </summary>
    private sealed record ResolvedUserTaskAccess(
        string ActualActor,
        string RepresentedOwner,
        long? DelegationId,
        string AccessKind,
        ActorContext ExecutionActor)
    {
        public const string Direct = "direct";
        public const string Delegated = "delegated";

        public DelegatedTaskAccessDto? ToDto() =>
            DelegationId is long id
                ? new DelegatedTaskAccessDto(id, RepresentedOwner)
                : null;
    }

    private async Task<ResolvedUserTaskAccess?> ResolveUserTaskAccessAsync(
        UserTaskRecord task,
        WorkflowInstanceRecord instance,
        FlowNodeModel node,
        ActorContext actor,
        bool forUpdate,
        CancellationToken cancellationToken,
        DateTimeOffset? asOf = null)
    {
        var actualActor = NormalizeUser(actor.User);
        if (!BpmnFlowNodeTypes.IsUserTask(node.Type)
            || !RoleAllowed(node, NormalizeRoles(actor.Roles)))
        {
            return null;
        }

        var owner = !string.IsNullOrWhiteSpace(task.Assignee)
            ? task.Assignee.Trim()
            : !string.IsNullOrWhiteSpace(task.ClaimedBy)
                ? task.ClaimedBy.Trim()
                : null;
        if (owner is null)
        {
            return CanUserTaskActor(task, node, actor)
                ? new ResolvedUserTaskAccess(
                    actualActor, actualActor, null, ResolvedUserTaskAccess.Direct, actor)
                : null;
        }

        // Literal ownership always wins, even if an overlapping grant happens to
        // exist in retained historical data.
        if (string.Equals(owner, actualActor, StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedUserTaskAccess(
                actualActor, actualActor, null, ResolvedUserTaskAccess.Direct, actor);
        }

        var delegation = await delegations.ResolveActiveAsync(
            actualActor,
            owner,
            instance.WorkflowKey,
            asOf ?? timeProvider.GetUtcNow(),
            forUpdate,
            cancellationToken);
        if (delegation is not null)
        {
            var delegatedActor = actor with
            {
                ActingFor = owner,
                DelegationId = delegation.Id
            };
            if (CanUserTaskActor(task, node, delegatedActor))
            {
                return new ResolvedUserTaskAccess(
                    actualActor,
                    owner,
                    delegation.Id,
                    ResolvedUserTaskAccess.Delegated,
                    delegatedActor);
            }
        }

        // Preserve direct claim-bypass/read behavior for a role-authorized actor
        // who is not using a grant.
        return CanUserTaskActor(task, node, actor)
            ? new ResolvedUserTaskAccess(
                actualActor, actualActor, null, ResolvedUserTaskAccess.Direct, actor)
            : null;
    }

    private static ResolvedUserTaskAccess ResolveProjectedUserTaskAccess(
        UserTaskRecord task,
        ActorContext actor,
        string? actingFor,
        long? delegationId)
    {
        var actualActor = NormalizeUser(actor.User);
        if (delegationId is long id && !string.IsNullOrWhiteSpace(actingFor))
        {
            var owner = actingFor.Trim();
            return new ResolvedUserTaskAccess(
                actualActor,
                owner,
                id,
                ResolvedUserTaskAccess.Delegated,
                actor with { ActingFor = owner, DelegationId = id });
        }

        return new ResolvedUserTaskAccess(
            actualActor, actualActor, null, ResolvedUserTaskAccess.Direct, actor);
    }

    private static bool CanUserTaskActor(UserTaskRecord task, FlowNodeModel node, ActorContext actor)
    {
        var user = EffectiveUser(actor);
        return BpmnFlowNodeTypes.IsUserTask(node.Type)
               && (!task.RequiresAssignment || task.Assignee is not null)
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
                        "User task #{NodeId} assignee expression '{Expression}' did not resolve to a non-empty string of at most {MaxLength} characters for instance {InstanceId}; creating the task in the {AssignmentFallback}.",
                        node.Id, node.AssigneeExpression, UserTaskConstraints.MaxActorNameLength, instanceId,
                        node.RequiresAssignment ? "hidden assignment queue" : "shared pool");
                }
            }
            catch (WorkflowDomainException ex)
            {
                logger.LogWarning(ex,
                    "User task #{NodeId} assignee expression '{Expression}' failed for instance {InstanceId}; creating the task in the {AssignmentFallback}.",
                    node.Id, node.AssigneeExpression, instanceId,
                    node.RequiresAssignment ? "hidden assignment queue" : "shared pool");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "User task #{NodeId} assignee expression '{Expression}' failed unexpectedly for instance {InstanceId}; creating the task in the {AssignmentFallback}.",
                    node.Id, node.AssigneeExpression, instanceId,
                    node.RequiresAssignment ? "hidden assignment queue" : "shared pool");
            }
        }

        return new CurrentNodeSnapshot(
            node.Id,
            node.Name,
            node.ExternalId,
            node.Type,
            node.Roles,
            node.RequiresClaim,
            node.RequiresAssignment,
            assignee,
            node.MultiInstance is not null,
            BpmnFlowNodeTypes.IsErrorEnd(node.Type) ? node.ErrorCode : null,
            BpmnFlowNodeTypes.IsErrorEnd(node.Type) ? node.ErrorDescription ?? node.Name : null,
            node.AsyncBefore);
    }

    private static FaultInfoDto? ToFault(
        string status,
        string? code,
        string? description,
        string nodeName) =>
        status == WorkflowInstanceStatuses.Faulted
            ? new FaultInfoDto(code, string.IsNullOrWhiteSpace(description) ? nodeName : description)
            : null;

    private static FlowNodeModel GetFlowNode(WorkflowModel definition, int nodeId) =>
        definition.FlowNodes.SingleOrDefault(n => n.Id == nodeId)
        ?? throw new WorkflowDomainException($"Flow node #{nodeId} was not found in workflow '{definition.Name}'.");

    private static IReadOnlyList<WorkflowAttributeModel> NodeAttributes(
        WorkflowDefinitionRecord workflow,
        int nodeId) =>
        workflow.Definition.FlowNodes.SingleOrDefault(node => node.Id == nodeId)?.Attributes
        ?? [];

    private static string StatusForTargetNode(FlowNodeModel node) =>
        BpmnFlowNodeTypes.IsErrorEnd(node.Type)
            ? WorkflowInstanceStatuses.Faulted
            : BpmnFlowNodeTypes.IsEnd(node.Type)
                ? WorkflowInstanceStatuses.Completed
                : WorkflowInstanceStatuses.Running;

    private static IReadOnlyList<SequenceFlowModel> OutgoingFlows(
        long definitionId,
        WorkflowModel definition,
        int nodeId) =>
        GatewayTopologyCache.Get(definitionId, definition).OutgoingFlows(nodeId);

    private static IReadOnlyList<SequenceFlowModel> IncomingFlows(
        long definitionId,
        WorkflowModel definition,
        int nodeId) =>
        GatewayTopologyCache.Get(definitionId, definition).IncomingFlows(nodeId);

    // Resolves the errorBoundaryEvent attached to a host activity, or null when
    // none is attached. ValidateDefinition enforces at most one boundary per host;
    // FirstOrDefault keeps this defensive against a hand-seeded definition that
    // somehow violates that invariant (avoids an uncaught InvalidOperationException).
    private static FlowNodeModel? FindErrorBoundary(WorkflowModel definition, int hostNodeId) =>
        definition.FlowNodes.FirstOrDefault(n =>
            BpmnFlowNodeTypes.IsErrorBoundary(n.Type) && n.AttachedToRef == hostNodeId);

    // FlowInfo is opt-in per definition. Definitions that never reference it keep
    // the historical hot path: no summary query and no occurrence/summary writes.
    // NCalc remains expression-driven; JavaScript uses an explicit capability flag.
    private static bool DefinitionUsesSequenceFlowInfo(WorkflowModel definition)
    {
        var gatewayIds = definition.FlowNodes
            .Where(node => BpmnFlowNodeTypes.IsGateway(node.Type))
            .Select(node => node.Id)
            .ToHashSet();

        if (definition.SequenceFlows.Any(flow =>
                SequenceFlowConditionEvaluator.ContainsFlowInfoReference(flow.CompletionCondition)
                || (gatewayIds.Contains(flow.SourceRef)
                    && SequenceFlowConditionEvaluator.ContainsFlowInfoReference(flow.Condition))))
        {
            return true;
        }

        return definition.FlowNodes
            .Where(node => BpmnFlowNodeTypes.IsScriptTask(node.Type))
            .Any(node =>
                node.Assignments.Any(assignment => assignment is not null
                    && SequenceFlowConditionEvaluator.ContainsFlowInfoReference(assignment.Expression))
                || (string.Equals(node.ScriptFormat, ScriptFormats.JavaScript, StringComparison.Ordinal)
                    && node.UsesFlowInfo == true));
    }

    private async Task<SequenceFlowInfoSnapshot?> LoadSequenceFlowInfoAsync(
        long instanceId,
        WorkflowModel definition,
        CancellationToken cancellationToken,
        bool force = false)
    {
        if (!force && !DefinitionUsesSequenceFlowInfo(definition))
        {
            return null;
        }

        var stored = await runtime.ListSequenceFlowSummariesAsync(instanceId, cancellationToken);
        return new SequenceFlowInfoSnapshot(
            definition.SequenceFlows.Select(flow => flow.Id),
            stored.Values.Select(ToRuntimeSequenceFlowSummary));
    }

    private async Task RecordSequenceFlowOccurrenceAsync(
        SequenceFlowInfoSnapshot? flowInfo,
        long instanceId,
        long? tokenId,
        long? userTaskId,
        long? multiInstanceExecutionId,
        int? itemIndex,
        SequenceFlowModel flow,
        string kind,
        bool isAction,
        bool isTraversal,
        ActorContext actor,
        Dictionary<string, JsonElement>? values,
        CancellationToken cancellationToken,
        AdministrativeBatchFlowContext? administrativeBatch = null)
    {
        if (flowInfo is null && administrativeBatch is null)
        {
            return;
        }

        var roles = SnapshotRoles(actor.Roles);
        var updated = await runtime.AppendSequenceFlowOccurrenceAsync(
            new SequenceFlowOccurrenceWriteRecord(
                instanceId,
                flow.Id,
                flow.SourceRef,
                flow.TargetRef,
                tokenId,
                userTaskId,
                multiInstanceExecutionId,
                itemIndex,
                kind,
                isAction,
                isTraversal,
                NormalizeUser(actor.User),
                roles,
                CloneDictionary(values),
                timeProvider.GetUtcNow(),
                actor.ActingFor,
                actor.DelegationId)
            {
                AdministrativeAction = administrativeBatch is null
                    ? null
                    : ToSequenceFlowAdministrativeAction(administrativeBatch.Request)
            },
            cancellationToken);

        // Keep subsequent gateway/script evaluation in this transaction coherent
        // with the staged database write, including before SaveChanges is called.
        flowInfo?.SetSummary(ToRuntimeSequenceFlowSummary(updated));
    }

    private static SequenceFlowAdministrativeActionRecord ToSequenceFlowAdministrativeAction(
        AdministrativeActionRequest request) =>
        new(
            request.BatchId,
            request.ExpectedWorkflowDefinitionId,
            request.ActionKind,
            request.FlowId,
            request.BoundaryNodeId,
            request.ExpectedTimerSubscriptionId,
            request.MultiInstanceMode,
            NormalizeOptionalReason(request.Reason));

    private static SequenceFlowRuntimeSummary ToRuntimeSequenceFlowSummary(
        SequenceFlowSummaryRecord summary) =>
        new(
            summary.SequenceFlowId,
            new SequenceFlowRuntimeView(
                summary.ActionCount,
                ToRuntimeSequenceFlowEvidence(summary.LastAction)),
            new SequenceFlowRuntimeView(
                summary.TraversalCount,
                ToRuntimeSequenceFlowEvidence(summary.LastTraversal)));

    private static SequenceFlowLastOccurrence? ToRuntimeSequenceFlowEvidence(
        SequenceFlowEvidenceRecord? evidence) =>
        evidence is null
            ? null
            : new SequenceFlowLastOccurrence(
                evidence.User,
                evidence.UserRoles,
                evidence.OccurredAt,
                evidence.Kind,
                evidence.Values is null
                    ? null
                    : JsonSerializer.SerializeToElement(evidence.Values))
            {
                ActingFor = evidence.ActingFor,
                DelegationId = evidence.DelegationId,
                AdministrativeAction = evidence.AdministrativeAction
            };

    private static string SequenceFlowTraversalKind(string nodeType) => nodeType switch
    {
        var type when BpmnFlowNodeTypes.IsStart(type) => "start",
        var type when BpmnFlowNodeTypes.IsMessageStart(type) => "messageStart",
        var type when BpmnFlowNodeTypes.IsExclusiveGateway(type) => "gateway",
        var type when BpmnFlowNodeTypes.IsParallelGateway(type) => "parallelGateway",
        var type when BpmnFlowNodeTypes.IsInclusiveGateway(type) => "inclusiveGateway",
        var type when BpmnFlowNodeTypes.IsComplexGateway(type) => "complexGateway",
        var type when BpmnFlowNodeTypes.IsScopedInterrupt(type) => "scopedInterrupt",
        var type when BpmnFlowNodeTypes.IsServiceTask(type) => "serviceTask",
        var type when BpmnFlowNodeTypes.IsScriptTask(type) => "scriptTask",
        var type when BpmnFlowNodeTypes.IsErrorBoundary(type) => "errorBoundary",
        _ => "automaticTask"
    };

    private static void EnsureActionAllowedByClaim(
        UserTaskRecord task,
        SequenceFlowModel flow,
        ActorContext actor)
    {
        if (!task.RequiresClaim
            || string.Equals(task.ClaimedBy, EffectiveUser(actor), StringComparison.OrdinalIgnoreCase)
            || CanBypassClaim(flow, NormalizeRoles(actor.Roles)))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(task.ClaimedBy))
        {
            throw new WorkflowDomainException("The current flow node must be claimed before taking a sequence flow.");
        }

        if (!string.Equals(task.ClaimedBy, EffectiveUser(actor), StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkflowDomainException($"Only '{task.ClaimedBy}' can act on this flow node.");
        }
    }

    private static bool CanBypassClaim(SequenceFlowModel flow, IReadOnlySet<string> actorRoles) =>
        flow.CanActWithoutClaim && RoleAllowed(flow.CanActWithoutClaimRoles, actorRoles);

    private static bool HasUnclaimOverrideRole(WorkflowModel definition, ActorContext actor)
    {
        var roles = NormalizeRoles(actor.Roles);
        return definition.UnclaimRoles.Any(roles.Contains);
    }

    private static string NormalizeUser(string? user) =>
        string.IsNullOrWhiteSpace(user) ? "anonymous" : user.Trim();

    private static string EffectiveUser(ActorContext actor) =>
        string.IsNullOrWhiteSpace(actor.ActingFor)
            ? NormalizeUser(actor.User)
            : actor.ActingFor.Trim();

    private static HashSet<string> NormalizeRoles(IReadOnlyCollection<string> roles) =>
        roles
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static List<string> SnapshotRoles(IReadOnlyCollection<string> roles) =>
        NormalizeRoles(roles)
            .OrderBy(role => role, StringComparer.OrdinalIgnoreCase)
            .ThenBy(role => role, StringComparer.Ordinal)
            .ToList();

    private static NodeExecutionActorRecord ToNodeExecutionActor(ActorContext actor) =>
        new(
            string.IsNullOrWhiteSpace(actor.User) ? null : actor.User.Trim(),
            SnapshotRoles(actor.Roles))
        {
            ActingFor = actor.ActingFor,
            DelegationId = actor.DelegationId
        };

    private static void EnsureTaskAssignmentManager(WorkflowModel definition, ActorContext actor)
    {
        var actorRoles = NormalizeRoles(actor.Roles);
        var allowedRoles = definition.TaskAssignmentRoles ?? [];
        if (allowedRoles.Count == 0 || !allowedRoles.Any(actorRoles.Contains))
        {
            throw new WorkflowForbiddenException(
                $"'{NormalizeUser(actor.User)}' is not allowed to manage task assignments for this workflow.");
        }
    }

    private async Task<TaskDistributionAuthorization?> AuthenticateTaskDistributionAsync(
        string workflowKey,
        TaskDistributionCredentials credentials,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workflowKey))
        {
            throw new WorkflowDomainException("A workflow key is required.");
        }

        var workflow = await definitions.GetDefaultByWorkflowKeyAsync(
            workflowKey, cancellationToken);
        if (workflow is null)
        {
            return null;
        }

        var distribution = workflow.Definition.TaskDistribution;
        var trustedContext = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (contextOptions.Config is { } config)
        {
            foreach (var pair in config)
            {
                trustedContext[$"config.{pair.Key}"] = JsonSerializer.SerializeToElement(pair.Value);
            }
        }

        var currentSettings = await settings.LoadAllFreshAsync(cancellationToken);
        foreach (var pair in currentSettings)
        {
            trustedContext[pair.Key] = pair.Value.Clone();
        }

        var expectedClientId = ServiceTaskTemplating.SubstituteScalar(
            distribution?.ClientId, trustedContext);
        var expectedClientSecret = ServiceTaskTemplating.SubstituteScalar(
            distribution?.ClientSecret, trustedContext);
        var valid = distribution is not null
                    && expectedClientId.Length > 0
                    && expectedClientId.Length <= UserTaskConstraints.MaxActorNameLength
                    && expectedClientSecret.Length > 0
                    && string.Equals(
                        credentials.ClientId ?? string.Empty,
                        expectedClientId,
                        StringComparison.Ordinal)
                    && ConstantTimeEquals(
                        credentials.ClientSecret ?? string.Empty,
                        expectedClientSecret);
        if (!valid)
        {
            logger.LogWarning(
                "Task distribution request for workflowKey {WorkflowKey} rejected: invalid client credentials.",
                workflow.WorkflowKey);
            throw new WorkflowUnauthorizedException("Invalid client credentials.");
        }

        return new TaskDistributionAuthorization(workflow, expectedClientId);
    }

    private static (string Ownership, string? Owner) GetTaskOwnership(UserTaskRecord task) =>
        task.Assignee is not null
            ? (UserTaskOwnershipKinds.Assigned, task.Assignee)
            : task.ClaimedBy is not null
                ? (UserTaskOwnershipKinds.Claimed, task.ClaimedBy)
                : (UserTaskOwnershipKinds.Unassigned, null);

    private static string? NormalizeAssignmentReason(string? reason)
    {
        var normalized = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (normalized?.Length > 1000)
            throw new WorkflowDomainException("The assignment reason cannot exceed 1000 characters.");
        return normalized;
    }

    private static string NormalizeAssignmentTarget(string? actorId)
    {
        var target = actorId?.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new WorkflowDomainException("A non-empty target actor ID is required.");
        }

        if (target.Length > UserTaskConstraints.MaxActorNameLength)
        {
            throw new WorkflowDomainException(
                $"The target actor ID cannot exceed {UserTaskConstraints.MaxActorNameLength} characters.");
        }

        return target;
    }

    private static string? NormalizeTaskOwnershipFilter(string? ownership)
    {
        var normalized = string.IsNullOrWhiteSpace(ownership)
            ? null
            : ownership.Trim().ToLowerInvariant();
        if (normalized is not null
            && normalized is not (UserTaskOwnershipKinds.Assigned
                or UserTaskOwnershipKinds.Claimed
                or UserTaskOwnershipKinds.Unassigned))
        {
            throw new WorkflowDomainException(
                $"Unsupported task ownership filter '{ownership}'. Use assigned, claimed, or unassigned.");
        }

        return normalized;
    }

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
        Dictionary<string, JsonElement>? values,
        bool enforceRequired = true)
    {
        if (values is not null)
        {
            var duplicate = values.Keys
                .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1)?.Key;
            if (duplicate is not null)
            {
                throw new WorkflowDomainException(
                    $"Variable '{duplicate}' was supplied more than once using different casing.");
            }
        }

        foreach (var variable in variables.Where(v => enforceRequired && v.Required))
        {
            if (!TryGetValue(values, variable.Name, out var value) || IsEmpty(value))
            {
                throw new WorkflowDomainException($"Required variable '{variable.Name}' is missing.");
            }
        }

        foreach (var variable in variables)
        {
            if (TryGetValue(values, variable.Name, out var value)
                && !(variable.Nullable
                    && (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined))
                && !TypedOutputValueValidator.IsValid(value, variable.DataType, variable.IsArray))
            {
                throw new WorkflowDomainException(
                    $"Variable '{variable.Name}' must be {TypedOutputValueValidator.DescribeExpected(variable.DataType, variable.IsArray)}.");
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

    private sealed record TaskDistributionAuthorization(
        WorkflowDefinitionRecord Workflow,
        string ClientId);

    private sealed record TypedOutputRuntime(
        string Variable,
        string Path,
        bool Required,
        string DataType,
        bool IsArray,
        bool? ProcessNullable,
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
