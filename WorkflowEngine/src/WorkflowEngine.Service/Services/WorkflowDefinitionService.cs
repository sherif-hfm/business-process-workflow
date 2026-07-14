using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WorkflowEngine.Service.Abstractions;
using WorkflowEngine.Service.Models;
using WorkflowEngine.Shared.Dtos;
using WorkflowEngine.Shared.Models;

namespace WorkflowEngine.Service.Services;

public sealed class WorkflowDefinitionService(
    IWorkflowDefinitionRepository definitions,
    IScriptEvaluator scriptEvaluator,
    ILogger<WorkflowDefinitionService> logger)
    : IWorkflowDefinitionService
{
    public async Task<IReadOnlyList<WorkflowSummaryDto>> ListLatestAsync(CancellationToken cancellationToken)
    {
        var records = await definitions.ListLatestAsync(cancellationToken);
        return records.Select(ToSummary).ToList();
    }

    public async Task<IReadOnlyList<WorkflowSummaryDto>> ListVersionsAsync(string workflowKey, CancellationToken cancellationToken)
    {
        var records = await definitions.ListVersionsByKeyAsync(workflowKey, cancellationToken);
        return records.Select(ToSummary).ToList();
    }

    public async Task<WorkflowDetailDto?> GetAsync(long id, CancellationToken cancellationToken)
    {
        var record = await definitions.GetAsync(id, cancellationToken);
        return record is null ? null : ToDetail(record);
    }

    public async Task<WorkflowDetailDto> CreateAsync(
        WorkflowModel definition,
        bool publish,
        CancellationToken cancellationToken)
    {
        WorkflowModelMigrator.Normalize(definition);
        ValidateDefinition(definition);
        var name = definition.Name.Trim();
        var version = await definitions.GetLatestVersionAsync(name, cancellationToken) + 1;
        var created = await definitions.AddAsync(name, version, definition, publish, cancellationToken);
        logger.LogInformation("Created workflow definition {WorkflowId} '{Name}' v{Version} (published={Published}, default={Default}).", created.Id, name, version, publish, created.IsDefault);
        return ToDetail(created);
    }

    public async Task<WorkflowDetailDto?> CreateNewVersionAsync(
        long sourceWorkflowId,
        WorkflowModel definition,
        bool publish,
        CancellationToken cancellationToken)
    {
        var source = await definitions.GetAsync(sourceWorkflowId, cancellationToken);
        if (source is null)
        {
            logger.LogInformation("Create new version from workflow {WorkflowId}: source not found.", sourceWorkflowId);
            return null;
        }

        WorkflowModelMigrator.Normalize(definition);
        ValidateDefinition(definition);
        var name = string.IsNullOrWhiteSpace(definition.Name) ? source.Name : definition.Name.Trim();
        var version = await definitions.GetLatestVersionAsync(name, cancellationToken) + 1;
        var created = await definitions.AddAsync(name, version, definition, publish, cancellationToken);
        logger.LogInformation("Created new workflow version {WorkflowId} '{Name}' v{Version} from source {SourceWorkflowId} (published={Published}, default={Default}).", created.Id, name, version, sourceWorkflowId, publish, created.IsDefault);
        return ToDetail(created);
    }

    public async Task<bool> PublishAsync(long id, CancellationToken cancellationToken)
    {
        var published = await definitions.SetPublishedAsync(id, true, cancellationToken);
        if (published)
        {
            logger.LogInformation("Workflow definition {WorkflowId} published.", id);
        }
        else
        {
            logger.LogInformation("Publish workflow {WorkflowId}: definition not found.", id);
        }
        return published;
    }

    public async Task<bool> UnpublishAsync(long id, CancellationToken cancellationToken)
    {
        var unpublished = await definitions.SetPublishedAsync(id, false, cancellationToken);
        if (unpublished)
        {
            logger.LogInformation("Workflow definition {WorkflowId} unpublished.", id);
        }
        else
        {
            logger.LogInformation("Unpublish workflow {WorkflowId}: definition not found.", id);
        }
        return unpublished;
    }

    public async Task<bool> SetDefaultAsync(long id, CancellationToken cancellationToken)
    {
        var set = await definitions.SetDefaultAsync(id, true, cancellationToken);
        if (set)
        {
            logger.LogInformation("Workflow definition {WorkflowId} set as default.", id);
        }
        else
        {
            logger.LogInformation("Set default workflow {WorkflowId}: definition not found.", id);
        }
        return set;
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken cancellationToken)
    {
        var deleted = await definitions.DeleteAsync(id, cancellationToken);
        if (deleted)
        {
            logger.LogInformation("Workflow definition {WorkflowId} deleted.", id);
        }
        else
        {
            logger.LogInformation("Delete workflow {WorkflowId}: definition not found.", id);
        }
        return deleted;
    }

    internal void ValidateDefinition(WorkflowModel definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            throw new WorkflowDomainException("Workflow name is required.");
        }

        if (definition.FlowNodes.Count == 0)
        {
            throw new WorkflowDomainException("Workflow must contain at least one flow node.");
        }

        ValidateUniqueIdentifiers(definition);

        // initialEventId is optional: a workflow whose only entry is a
        // messageStartEvent (system-started) has no user-facing default start.
        // When set it must reference an existing node that is a user startEvent
        // (a messageStartEvent is a valid entry but cannot be the default).
        if (definition.InitialEventId is not null)
        {
            if (definition.FlowNodes.All(n => n.Id != definition.InitialEventId))
            {
                throw new WorkflowDomainException("Workflow initialEventId must reference an existing flow node.");
            }

            var initialNode = definition.FlowNodes.Single(n => n.Id == definition.InitialEventId);
            if (!BpmnFlowNodeTypes.IsStart(initialNode.Type))
            {
                throw new WorkflowDomainException("Workflow initialEventId must reference a start event.");
            }
        }

        // A workflow must have at least one entry event (a user startEvent started
        // via POST /api/instances, or a messageStartEvent started via the webhook).
        if (!definition.FlowNodes.Any(n => BpmnFlowNodeTypes.IsEntry(n.Type)))
        {
            throw new WorkflowDomainException("Workflow must have at least one entry event (startEvent or messageStartEvent).");
        }

        ValidateProcessVariables(definition.Variables);

        var nodeIds = definition.FlowNodes.Select(n => n.Id).ToHashSet();

        foreach (var flow in definition.SequenceFlows)
        {
            if (!nodeIds.Contains(flow.SourceRef))
            {
                throw new WorkflowDomainException($"Sequence flow #{flow.Id} has a missing sourceRef #{flow.SourceRef}.");
            }

            if (!nodeIds.Contains(flow.TargetRef))
            {
                throw new WorkflowDomainException($"Sequence flow #{flow.Id} has a missing targetRef #{flow.TargetRef}.");
            }

            var sourceNode = definition.FlowNodes.Single(n => n.Id == flow.SourceRef);
            if (flow.Variables is not null && flow.Variables.Count > 0 && !BpmnFlowNodeTypes.IsUserTask(sourceNode.Type))
            {
                throw new WorkflowDomainException($"Sequence flow #{flow.Id} has variables but its source node is not a user task.");
            }

            if (flow.CanActWithoutClaim && !BpmnFlowNodeTypes.IsUserTask(sourceNode.Type))
            {
                throw new WorkflowDomainException($"Sequence flow #{flow.Id} is marked to act without claim, but its source node is not a user task.");
            }

            ValidateVariables(flow.Variables ?? [], $"sequence flow #{flow.Id}");
        }

        foreach (var node in definition.FlowNodes)
        {
            if (string.IsNullOrWhiteSpace(node.Name))
            {
                throw new WorkflowDomainException($"Flow node #{node.Id} name is required.");
            }

            if (!BpmnFlowNodeTypes.IsSupported(node.Type))
            {
                throw new WorkflowDomainException($"Flow node #{node.Id} has an unsupported type '{node.Type}'.");
            }

            var outgoing = definition.SequenceFlows.Where(f => f.SourceRef == node.Id).ToList();
            var incoming = definition.SequenceFlows.Where(f => f.TargetRef == node.Id).ToList();

            if (outgoing.Any(f => !f.IsSelectable)
                && (!BpmnFlowNodeTypes.IsUserTask(node.Type) || node.MultiInstance is null))
            {
                throw new WorkflowDomainException(
                    $"Flow isSelectable=false is supported only on multi-instance user task #{node.Id}.");
            }

            // errorEndEvent is covered by IsEnd (no outgoing). errorBoundaryEvent
            // has exactly one outgoing (the error path) and no incoming flows
            // (it is attached, not reached via a normal sequence flow).
            if (BpmnFlowNodeTypes.IsEnd(node.Type) && outgoing.Count > 0)
            {
                throw new WorkflowDomainException($"End event #{node.Id} cannot have outgoing sequence flows.");
            }

            if ((BpmnFlowNodeTypes.IsStart(node.Type)
                    || BpmnFlowNodeTypes.IsAutomatic(node.Type)
                    || BpmnFlowNodeTypes.IsServiceTask(node.Type)
                    || BpmnFlowNodeTypes.IsScriptTask(node.Type)
                    || BpmnFlowNodeTypes.IsErrorBoundary(node.Type)
                    || BpmnFlowNodeTypes.IsMessageCatch(node.Type)
                    || BpmnFlowNodeTypes.IsMessageStart(node.Type))
                && outgoing.Count != 1)
            {
                var kind = BpmnFlowNodeTypes.IsStart(node.Type)
                    ? "Start event"
                    : BpmnFlowNodeTypes.IsServiceTask(node.Type)
                        ? "Service task"
                        : BpmnFlowNodeTypes.IsScriptTask(node.Type)
                            ? "Script task"
                            : BpmnFlowNodeTypes.IsErrorBoundary(node.Type)
                                ? "Error boundary event"
                                : BpmnFlowNodeTypes.IsMessageCatch(node.Type)
                                    ? "Message catch event"
                                    : BpmnFlowNodeTypes.IsMessageStart(node.Type)
                                        ? "Message start event"
                                        : "Automatic task";
                throw new WorkflowDomainException($"{kind} #{node.Id} must have exactly one outgoing sequence flow.");
            }

            if (BpmnFlowNodeTypes.IsErrorBoundary(node.Type))
            {
                ValidateErrorBoundary(node, definition, incoming);
            }

            if (BpmnFlowNodeTypes.IsServiceTask(node.Type))
            {
                ValidateServiceTask(node);
            }

            if (BpmnFlowNodeTypes.IsScriptTask(node.Type))
            {
                ValidateScriptTask(node, definition);
            }

            if (BpmnFlowNodeTypes.IsMessageCatch(node.Type))
            {
                ValidateMessageCatch(node);
            }

            if (BpmnFlowNodeTypes.IsMessageStart(node.Type))
            {
                ValidateMessageStart(node);
            }

            if (!BpmnFlowNodeTypes.IsUserTask(node.Type)
                && !string.IsNullOrWhiteSpace(node.AssigneeExpression))
            {
                throw new WorkflowDomainException(
                    $"Flow node #{node.Id} has an assignee expression but is not a user task.");
            }

            if (BpmnFlowNodeTypes.IsUserTask(node.Type) && outgoing.Count == 0)
            {
                throw new WorkflowDomainException($"User task #{node.Id} must have at least one outgoing sequence flow.");
            }

            if (BpmnFlowNodeTypes.IsUserTask(node.Type))
            {
                ValidateClaimMode(node, definition);

                if (node.MultiInstance is not null)
                {
                    if (!string.IsNullOrWhiteSpace(node.AssigneeExpression))
                    {
                        throw new WorkflowDomainException(
                            $"Multi-instance user task #{node.Id} cannot define an assignee expression.");
                    }
                    ValidateMultiInstance(node, outgoing, definition);
                }
                else
                {
                    if (outgoing.Any(f => f.CancelRemainingInstances
                                          || f.CompletionPriority is not null
                                          || !string.IsNullOrWhiteSpace(f.CompletionCondition)))
                    {
                        throw new WorkflowDomainException(
                            $"User task #{node.Id} has multi-instance flow settings but no multiInstance configuration.");
                    }

                    if (outgoing.Any(f => f.IsDefault))
                    {
                        throw new WorkflowDomainException(
                            $"User task #{node.Id} cannot define a default flow unless it is multi-instance.");
                    }
                }

                if (!string.IsNullOrWhiteSpace(node.AssigneeExpression)
                    && !SequenceFlowConditionEvaluator.IsValid(node.AssigneeExpression))
                {
                    throw new WorkflowDomainException(
                        $"User task #{node.Id} has an invalid assignee expression: '{node.AssigneeExpression}'.");
                }

                foreach (var flow in outgoing.Where(f => !string.IsNullOrWhiteSpace(f.Condition)))
                {
                    if (!SequenceFlowConditionEvaluator.IsValid(flow.Condition))
                    {
                        throw new WorkflowDomainException(
                            $"Sequence flow #{flow.Id} has an invalid condition expression: '{flow.Condition}'.");
                    }
                }
            }

            if (BpmnFlowNodeTypes.IsGateway(node.Type))
            {
                if (outgoing.Count < 2)
                {
                    throw new WorkflowDomainException($"Exclusive gateway #{node.Id} must have at least two outgoing sequence flows.");
                }

                var gatewayDefaultCount = outgoing.Count(f => f.IsDefault);
                if (gatewayDefaultCount > 1)
                {
                    throw new WorkflowDomainException(
                        $"Exclusive gateway #{node.Id} has {gatewayDefaultCount} default flows; at most one allowed.");
                }

                var hasDefault = outgoing.Any(f => f.IsDefault);
                var conditioned = outgoing.Where(f => !f.IsDefault).All(f => !string.IsNullOrWhiteSpace(f.Condition));
                if (!hasDefault && !conditioned)
                {
                    throw new WorkflowDomainException(
                        $"Exclusive gateway #{node.Id} must have a default flow or a condition on every non-default flow.");
                }

                foreach (var flow in outgoing.Where(f => !f.IsDefault && !string.IsNullOrWhiteSpace(f.Condition)))
                {
                    if (!SequenceFlowConditionEvaluator.IsValid(flow.Condition))
                    {
                        throw new WorkflowDomainException(
                            $"Sequence flow #{flow.Id} has an invalid condition expression: '{flow.Condition}'.");
                    }
                }
            }

            ValidateVariables(node.Variables, $"flow node #{node.Id}");
        }
    }

    private static void ValidateServiceTask(FlowNodeModel node)
    {
        var service = node.Service
            ?? throw new WorkflowDomainException($"Service task #{node.Id} must have a service configuration.");

        if (string.IsNullOrWhiteSpace(service.Url))
        {
            throw new WorkflowDomainException($"Service task #{node.Id} must have a URL.");
        }

        var allowedMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "GET", "POST", "PUT", "PATCH", "DELETE"
        };
        if (!allowedMethods.Contains(service.Method))
        {
            throw new WorkflowDomainException(
                $"Service task #{node.Id} has an unsupported HTTP method '{service.Method}'.");
        }

        if (service.TimeoutSeconds <= 0)
        {
            throw new WorkflowDomainException($"Service task #{node.Id} timeout must be greater than zero.");
        }

        foreach (var header in service.Headers)
        {
            if (string.IsNullOrWhiteSpace(header.Name))
            {
                throw new WorkflowDomainException($"Service task #{node.Id} has a header with no name.");
            }
        }

        foreach (var mapping in service.OutputMappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.Variable))
            {
                throw new WorkflowDomainException(
                    $"Service task #{node.Id} has an output mapping with no variable name.");
            }

            if (string.IsNullOrWhiteSpace(mapping.Path))
            {
                throw new WorkflowDomainException(
                    $"Service task #{node.Id} output mapping for '{mapping.Variable}' must have a response path.");
            }
        }
    }

    // An intermediateMessageCatchEvent rests until a message is delivered via
    // POST /api/instances/{id}/message. The delivery caller is authenticated
    // against the expected clientId/clientSecret and a required custom header
    // (headerName/headerValue); headerValidation is an optional NCalc rule
    // evaluated with the incoming header value bound as `header`. outputMappings
    // extract dotted-path values from the inbound JSON body. All scalar fields
    // are ${var}-templatable (only presence is checked at author time).
    // Shared validation for the message config on an intermediateMessageCatchEvent
    // and a messageStartEvent (creds/header/outputMappings). `kind` labels the
    // node in messages (e.g. "Message catch event", "Message start event").
    private static void ValidateMessageConfig(FlowNodeModel node, string kind)
    {
        var message = node.Message
            ?? throw new WorkflowDomainException($"{kind} #{node.Id} must have a message configuration.");

        if (string.IsNullOrWhiteSpace(message.ClientId))
        {
            throw new WorkflowDomainException($"{kind} #{node.Id} must have a client id.");
        }

        if (string.IsNullOrWhiteSpace(message.ClientSecret))
        {
            throw new WorkflowDomainException($"{kind} #{node.Id} must have a client secret.");
        }

        if (string.IsNullOrWhiteSpace(message.HeaderName))
        {
            throw new WorkflowDomainException($"{kind} #{node.Id} must have a header name.");
        }

        if (string.IsNullOrWhiteSpace(message.HeaderValue))
        {
            throw new WorkflowDomainException($"{kind} #{node.Id} must have a header value.");
        }

        if (!string.IsNullOrWhiteSpace(message.HeaderValidation)
            && !SequenceFlowConditionEvaluator.IsValid(message.HeaderValidation))
        {
            throw new WorkflowDomainException(
                $"{kind} #{node.Id} has an invalid header validation expression: '{message.HeaderValidation}'.");
        }

        foreach (var mapping in message.OutputMappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.Variable))
            {
                throw new WorkflowDomainException(
                    $"{kind} #{node.Id} has an output mapping with no variable name.");
            }

            if (string.IsNullOrWhiteSpace(mapping.Path))
            {
                throw new WorkflowDomainException(
                    $"{kind} #{node.Id} output mapping for '{mapping.Variable}' must have a path.");
            }
        }
    }

    private static void ValidateMessageCatch(FlowNodeModel node)
        => ValidateMessageConfig(node, "Message catch event");

    // A messageStartEvent carries start variables + a message config. The config
    // is validated like a catch event; additionally, when idempotencyVariable is
    // set it must name one of the node's declared start variables (the engine
    // stores the mapped value as an instance variable and dedupes a retried
    // webhook by searching for an existing instance carrying that key value).
    private static void ValidateMessageStart(FlowNodeModel node)
    {
        ValidateMessageConfig(node, "Message start event");

        if (!string.IsNullOrWhiteSpace(node.Message!.IdempotencyVariable))
        {
            var idempotencyVariable = node.Message!.IdempotencyVariable!;
            var variable = node.Variables.SingleOrDefault(v =>
                string.Equals(v.Name, idempotencyVariable, StringComparison.Ordinal));
            if (variable is null)
            {
                throw new WorkflowDomainException(
                    $"Message start event #{node.Id} idempotencyVariable '{idempotencyVariable}' is not a declared start variable on the node.");
            }

            // The dedupe search looks up the variable by exact string match
            // (lower(ValueJson #>> '{}') = lower(@value)); a non-string dataType
            // (e.g. date/number) can store a different textual representation than
            // the raw mapped value, causing a missed dedupe on retry. Restrict to
            // string to keep the search predictably exact.
            if (!string.Equals(variable.DataType, WorkflowVariableTypes.String, StringComparison.Ordinal))
            {
                throw new WorkflowDomainException(
                    $"Message start event #{node.Id} idempotencyVariable '{idempotencyVariable}' must have dataType 'string'.");
            }
        }
    }

    // An errorBoundaryEvent is attached to exactly one serviceTask/scriptTask
    // (attachedToRef), has no incoming sequence flows (it is reached by the
    // engine's error routing, not a normal flow), and at most one boundary may
    // be attached to a given host.
    private static void ValidateErrorBoundary(FlowNodeModel node, WorkflowModel definition, List<SequenceFlowModel> incoming)
    {
        if (node.AttachedToRef is null)
        {
            throw new WorkflowDomainException($"Error boundary event #{node.Id} must reference a host via attachedToRef.");
        }

        var host = definition.FlowNodes.SingleOrDefault(n => n.Id == node.AttachedToRef);
        if (host is null)
        {
            throw new WorkflowDomainException(
                $"Error boundary event #{node.Id} attachedToRef #{node.AttachedToRef} does not reference an existing flow node.");
        }

        if (!BpmnFlowNodeTypes.IsServiceTask(host.Type) && !BpmnFlowNodeTypes.IsScriptTask(host.Type))
        {
            throw new WorkflowDomainException(
                $"Error boundary event #{node.Id} attachedToRef #{node.AttachedToRef} must reference a service task or script task.");
        }

        if (incoming.Count != 0)
        {
            throw new WorkflowDomainException(
                $"Error boundary event #{node.Id} cannot have incoming sequence flows.");
        }

        var siblings = definition.FlowNodes.Count(n =>
            BpmnFlowNodeTypes.IsErrorBoundary(n.Type) && n.AttachedToRef == node.AttachedToRef);
        if (siblings > 1)
        {
            throw new WorkflowDomainException(
                $"Host node #{host.Id} has {siblings} error boundary events; at most one is allowed.");
        }
    }

    private static void ValidateClaimMode(FlowNodeModel node, WorkflowModel definition)
    {
        var mode = node.ClaimMode;
        if (mode != ClaimModes.Fresh && mode != ClaimModes.Previous && mode != ClaimModes.FromNode)
        {
            throw new WorkflowDomainException(
                $"User task #{node.Id} has an unsupported claimMode '{mode}'.");
        }

        if (mode == ClaimModes.Fresh)
        {
            return;
        }

        if (!node.RequiresClaim)
        {
            throw new WorkflowDomainException(
                $"User task #{node.Id} claimMode '{mode}' requires requiresClaim to be true.");
        }

        if (mode == ClaimModes.FromNode)
        {
            if (node.InheritClaimFromNodeId is null)
            {
                throw new WorkflowDomainException(
                    $"User task #{node.Id} claimMode 'fromNode' requires inheritClaimFromNodeId.");
            }

            var source = definition.FlowNodes.SingleOrDefault(n => n.Id == node.InheritClaimFromNodeId);
            if (source is null)
            {
                throw new WorkflowDomainException(
                    $"User task #{node.Id} inheritClaimFromNodeId #{node.InheritClaimFromNodeId} does not reference an existing flow node.");
            }

            if (!BpmnFlowNodeTypes.IsUserTask(source.Type))
            {
                throw new WorkflowDomainException(
                    $"User task #{node.Id} inheritClaimFromNodeId #{node.InheritClaimFromNodeId} must reference a user task.");
            }
        }
    }

    private static void ValidateMultiInstance(
        FlowNodeModel node,
        IReadOnlyList<SequenceFlowModel> outgoing,
        WorkflowModel definition)
    {
        var multi = node.MultiInstance!;
        if (multi.Mode is not (MultiInstanceModes.Parallel or MultiInstanceModes.Sequential))
        {
            throw new WorkflowDomainException($"User task #{node.Id} has unsupported multi-instance mode '{multi.Mode}'.");
        }
        if (multi.Source is not (MultiInstanceSources.Collection or MultiInstanceSources.Cardinality))
        {
            throw new WorkflowDomainException($"User task #{node.Id} has unsupported multi-instance source '{multi.Source}'.");
        }
        if (multi.CompletionEvaluation is not (MultiInstanceCompletionEvaluations.AfterEach
                                                or MultiInstanceCompletionEvaluations.AfterAll))
        {
            throw new WorkflowDomainException(
                $"User task #{node.Id} has unsupported multi-instance completionEvaluation '{multi.CompletionEvaluation}'.");
        }

        var result = definition.Variables.SingleOrDefault(v =>
            string.Equals(v.Name, multi.ResultVariable, StringComparison.OrdinalIgnoreCase));
        if (result is null || result.DataType != WorkflowVariableTypes.Json || result.IsArray
            || result.DefaultValue is null || result.DefaultValue.Value.ValueKind != JsonValueKind.Array)
        {
            throw new WorkflowDomainException(
                $"User task #{node.Id} resultVariable must reference a declared json process variable initialized to [].");
        }

        if (multi.Source == MultiInstanceSources.Collection)
        {
            if (!string.IsNullOrWhiteSpace(multi.CardinalityExpression))
            {
                throw new WorkflowDomainException($"User task #{node.Id} collection source cannot define cardinalityExpression.");
            }
            var collection = definition.Variables.SingleOrDefault(v =>
                string.Equals(v.Name, multi.CollectionVariable, StringComparison.OrdinalIgnoreCase));
            if (collection is null || collection.DataType != WorkflowVariableTypes.String || !collection.IsArray)
            {
                throw new WorkflowDomainException(
                    $"User task #{node.Id} collectionVariable must reference a declared string[] process variable.");
            }
            if (string.Equals(collection.Name, result.Name, StringComparison.OrdinalIgnoreCase))
            {
                throw new WorkflowDomainException($"User task #{node.Id} collectionVariable and resultVariable must be different.");
            }
            if (node.RequiresClaim || node.ClaimMode != ClaimModes.Fresh)
            {
                throw new WorkflowDomainException(
                    $"Collection multi-instance user task #{node.Id} must use requiresClaim=false and claimMode='fresh'.");
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(multi.CollectionVariable)
                || !SequenceFlowConditionEvaluator.IsValid(multi.CardinalityExpression))
            {
                throw new WorkflowDomainException(
                    $"User task #{node.Id} cardinality source requires a valid cardinalityExpression and no collectionVariable.");
            }
            if (node.ClaimMode != ClaimModes.Fresh)
            {
                throw new WorkflowDomainException($"Cardinality multi-instance user task #{node.Id} must use claimMode='fresh'.");
            }
        }

        var outcomes = outgoing.Where(f => !f.CancelRemainingInstances).ToList();
        var interrupts = outgoing.Where(f => f.CancelRemainingInstances).ToList();
        if (outcomes.Count == 0 || outcomes.Count(f => f.IsDefault) != 1)
        {
            throw new WorkflowDomainException(
                $"Multi-instance user task #{node.Id} requires at least one outcome flow and exactly one default outcome flow.");
        }
        var defaultOutcome = outcomes.Single(f => f.IsDefault);
        var conditionedOutcomes = outcomes.Where(f => !f.IsDefault).ToList();
        if (defaultOutcome.IsSelectable)
        {
            throw new WorkflowDomainException(
                $"The default flow from multi-instance user task #{node.Id} must be engine-only.");
        }
        if (!string.IsNullOrWhiteSpace(defaultOutcome.CompletionCondition)
            || defaultOutcome.CompletionPriority is not null)
        {
            throw new WorkflowDomainException(
                $"The default flow from multi-instance user task #{node.Id} cannot define a completion condition or priority.");
        }
        if (!conditionedOutcomes.Any(f => f.IsSelectable))
        {
            throw new WorkflowDomainException(
                $"Multi-instance user task #{node.Id} requires at least one selectable outcome flow.");
        }
        if (interrupts.Any(f => !f.IsSelectable))
        {
            throw new WorkflowDomainException(
                $"Interrupting flows from multi-instance user task #{node.Id} must be selectable.");
        }
        var engineOnly = outcomes.Where(f => !f.IsSelectable).ToList();
        if (engineOnly.Any(f => f.Roles.Count > 0 || f.Variables.Count > 0
                                || !string.IsNullOrWhiteSpace(f.Condition) || f.CanActWithoutClaim))
        {
            throw new WorkflowDomainException(
                $"Engine-only flows from multi-instance user task #{node.Id} cannot define roles, action variables, condition, or canActWithoutClaim.");
        }
        if (interrupts.Any(f => f.IsDefault || f.CompletionPriority is not null
                                || !string.IsNullOrWhiteSpace(f.CompletionCondition)))
        {
            throw new WorkflowDomainException(
                $"Interrupting flows from multi-instance user task #{node.Id} cannot be default or define completion rules.");
        }
        if (conditionedOutcomes.Any(f => f.CompletionPriority is null or <= 0)
            || conditionedOutcomes.Select(f => f.CompletionPriority!.Value).Distinct().Count()
            != conditionedOutcomes.Count)
        {
            throw new WorkflowDomainException(
                $"Non-default outcome flows from multi-instance user task #{node.Id} require unique positive completionPriority values.");
        }
        if (conditionedOutcomes.Any(f => string.IsNullOrWhiteSpace(f.CompletionCondition)))
        {
            throw new WorkflowDomainException(
                $"Every non-default outcome flow from multi-instance user task #{node.Id} requires completionCondition.");
        }

        var selectableOutcomeIds = outcomes.Where(f => f.IsSelectable).Select(f => f.Id).ToHashSet();
        foreach (var flow in conditionedOutcomes)
        {
            if (!SequenceFlowConditionEvaluator.IsValid(flow.CompletionCondition))
            {
                throw new WorkflowDomainException($"Sequence flow #{flow.Id} has an invalid completionCondition.");
            }

            foreach (Match match in Regex.Matches(
                         flow.CompletionCondition!,
                         @"(?i)\b(?:CountFlow|PercentFlow)\s*\(\s*([^\)]+)\s*\)"))
            {
                if (!int.TryParse(match.Groups[1].Value.Trim(), out var referencedFlowId)
                    || !selectableOutcomeIds.Contains(referencedFlowId))
                {
                    throw new WorkflowDomainException(
                        $"Sequence flow #{flow.Id} completionCondition references a non-selectable outcome flow.");
                }
            }
        }
    }

    private static void ValidateVariables(IEnumerable<VariableModel> variables, string owner)
    {
        ValidateVariables(variables, owner, requireDefault: false);
    }

    // Process-level variables are computed (never user-supplied), so each one must
    // declare a defaultValue that initializes it at instance start. The shared
    // name/type/prefix/validation checks are reused via the requireDefault path.
    private static void ValidateProcessVariables(IEnumerable<VariableModel> variables)
    {
        ValidateVariables(variables, "process variables", requireDefault: true);
    }

    private static void ValidateVariables(
        IEnumerable<VariableModel> variables,
        string owner,
        bool requireDefault)
    {
        var materialized = variables.ToList();
        var duplicateName = materialized
            .Where(variable => !string.IsNullOrWhiteSpace(variable.Name))
            .GroupBy(variable => variable.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateName is not null)
        {
            throw new WorkflowDomainException(
                $"Variable name '{duplicateName}' is duplicated on {owner}; variable names are case-insensitive.");
        }

        var allowedTypes = new HashSet<string>
        {
            WorkflowVariableTypes.String,
            WorkflowVariableTypes.Number,
            WorkflowVariableTypes.Boolean,
            WorkflowVariableTypes.Date,
            WorkflowVariableTypes.DateTime,
            WorkflowVariableTypes.Json
        };

        foreach (var variable in materialized)
        {
            if (string.IsNullOrWhiteSpace(variable.Name))
            {
                throw new WorkflowDomainException($"Variable name is required on {owner}.");
            }

            if (variable.Name.StartsWith("sys.", StringComparison.OrdinalIgnoreCase)
                || variable.Name.StartsWith("config.", StringComparison.OrdinalIgnoreCase)
                || variable.Name.StartsWith("setting.", StringComparison.OrdinalIgnoreCase)
                || variable.Name.StartsWith("mi.", StringComparison.OrdinalIgnoreCase))
            {
                throw new WorkflowDomainException(
                    $"Variable '{variable.Name}' on {owner} uses a reserved context prefix.");
            }

            if (!allowedTypes.Contains(variable.DataType))
            {
                throw new WorkflowDomainException($"Variable '{variable.Name}' on {owner} has unsupported type '{variable.DataType}'.");
            }

            if (requireDefault
                && (variable.DefaultValue is null
                    || variable.DefaultValue.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined))
            {
                throw new WorkflowDomainException(
                    $"Process variable '{variable.Name}' on {owner} must have a defaultValue.");
            }

            if (!string.IsNullOrWhiteSpace(variable.Validation)
                && !SequenceFlowConditionEvaluator.IsValid(variable.Validation))
            {
                throw new WorkflowDomainException(
                    $"Variable '{variable.Name}' on {owner} has an invalid validation expression: '{variable.Validation}'.");
            }
        }
    }

    private static void ValidateUniqueIdentifiers(WorkflowModel definition)
    {
        var duplicateNodeId = definition.FlowNodes
            .GroupBy(node => node.Id)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateNodeId is not null)
        {
            throw new WorkflowDomainException($"Flow node id #{duplicateNodeId} is duplicated.");
        }

        var duplicateFlowId = definition.SequenceFlows
            .GroupBy(flow => flow.Id)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateFlowId is not null)
        {
            throw new WorkflowDomainException($"Sequence flow id #{duplicateFlowId} is duplicated.");
        }
    }

    // Validates a scriptTask's authoring mode. Exactly one of the two payloads may
    // be populated per scriptFormat:
    //   - "ncalc" (default): each assignment must target a declared process
    //     variable with a parse-checkable NCalc expression; `script` must be empty.
    //   - "javascript": `script` is required and syntax-checked (parse-only, no
    //     execution) via IScriptEvaluator; `assignments` must be empty. setVariable
    //     targets inside the script body cannot be fully checked at author time
    //     since JS is dynamic - that remains a runtime check (WorkflowEngineService).
    private void ValidateScriptTask(FlowNodeModel node, WorkflowModel definition)
    {
        if (node.ScriptFormat != ScriptFormats.NCalc && node.ScriptFormat != ScriptFormats.JavaScript)
        {
            throw new WorkflowDomainException(
                $"Script task #{node.Id} has an unsupported scriptFormat '{node.ScriptFormat}'.");
        }

        if (node.ScriptFormat == ScriptFormats.JavaScript)
        {
            if (node.Assignments.Count > 0)
            {
                throw new WorkflowDomainException(
                    $"Script task #{node.Id} uses scriptFormat 'javascript' and must not have assignments.");
            }

            if (string.IsNullOrWhiteSpace(node.Script))
            {
                throw new WorkflowDomainException($"Script task #{node.Id} must have a script body.");
            }

            if (!scriptEvaluator.IsValid(node.Script, out var error))
            {
                throw new WorkflowDomainException(
                    $"Script task #{node.Id} has an invalid JavaScript body: {error}");
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(node.Script))
        {
            throw new WorkflowDomainException(
                $"Script task #{node.Id} uses scriptFormat 'ncalc' and must not have a script body.");
        }

        var declared = definition.Variables
            .Where(v => !string.IsNullOrWhiteSpace(v.Name))
            .Select(v => v.Name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var assignment in node.Assignments)
        {
            if (string.IsNullOrWhiteSpace(assignment.Variable))
            {
                throw new WorkflowDomainException(
                    $"Script task #{node.Id} has an assignment with no variable name.");
            }

            if (!declared.Contains(assignment.Variable))
            {
                throw new WorkflowDomainException(
                    $"Script task #{node.Id} assigns '{assignment.Variable}' which is not a declared process variable.");
            }

            if (string.IsNullOrWhiteSpace(assignment.Expression))
            {
                throw new WorkflowDomainException(
                    $"Script task #{node.Id} assignment for '{assignment.Variable}' must have an expression.");
            }

            if (!SequenceFlowConditionEvaluator.IsValid(assignment.Expression))
            {
                throw new WorkflowDomainException(
                    $"Script task #{node.Id} assignment for '{assignment.Variable}' has an invalid expression: '{assignment.Expression}'.");
            }
        }
    }

    internal static WorkflowSummaryDto ToSummary(WorkflowDefinitionRecord record) =>
        new(record.Id, record.Name, record.WorkflowKey, record.Version, record.IsPublished, record.IsDefault, record.CreatedAt);

    internal static WorkflowDetailDto ToDetail(WorkflowDefinitionRecord record) =>
        new(record.Id, record.Name, record.WorkflowKey, record.Version, record.IsPublished, record.IsDefault, record.CreatedAt, record.Definition);
}
