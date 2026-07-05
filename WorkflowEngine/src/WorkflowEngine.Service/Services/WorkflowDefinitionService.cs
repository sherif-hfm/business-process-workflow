using System.Text.Json;
using WorkflowEngine.Service.Abstractions;
using WorkflowEngine.Service.Models;
using WorkflowEngine.Shared.Dtos;
using WorkflowEngine.Shared.Models;

namespace WorkflowEngine.Service.Services;

public sealed class WorkflowDefinitionService(
    IWorkflowDefinitionRepository definitions,
    IScriptEvaluator scriptEvaluator)
    : IWorkflowDefinitionService
{
    public async Task<IReadOnlyList<WorkflowSummaryDto>> ListLatestAsync(CancellationToken cancellationToken)
    {
        var records = await definitions.ListLatestAsync(cancellationToken);
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
            return null;
        }

        WorkflowModelMigrator.Normalize(definition);
        ValidateDefinition(definition);
        var name = string.IsNullOrWhiteSpace(definition.Name) ? source.Name : definition.Name.Trim();
        var version = await definitions.GetLatestVersionAsync(name, cancellationToken) + 1;
        var created = await definitions.AddAsync(name, version, definition, publish, cancellationToken);
        return ToDetail(created);
    }

    public Task<bool> PublishAsync(long id, CancellationToken cancellationToken) =>
        definitions.SetPublishedAsync(id, true, cancellationToken);

    public Task<bool> DeleteAsync(long id, CancellationToken cancellationToken) =>
        definitions.DeleteAsync(id, cancellationToken);

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

        if (definition.InitialEventId is null || definition.FlowNodes.All(n => n.Id != definition.InitialEventId))
        {
            throw new WorkflowDomainException("Workflow initialEventId must reference an existing flow node.");
        }

        var initialNode = definition.FlowNodes.Single(n => n.Id == definition.InitialEventId);
        if (!BpmnFlowNodeTypes.IsStart(initialNode.Type))
        {
            throw new WorkflowDomainException("Workflow initialEventId must reference a start event.");
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

            if (BpmnFlowNodeTypes.IsEnd(node.Type) && outgoing.Count > 0)
            {
                throw new WorkflowDomainException($"End event #{node.Id} cannot have outgoing sequence flows.");
            }

            if ((BpmnFlowNodeTypes.IsStart(node.Type)
                    || BpmnFlowNodeTypes.IsAutomatic(node.Type)
                    || BpmnFlowNodeTypes.IsServiceTask(node.Type)
                    || BpmnFlowNodeTypes.IsScriptTask(node.Type))
                && outgoing.Count != 1)
            {
                var kind = BpmnFlowNodeTypes.IsStart(node.Type)
                    ? "Start event"
                    : BpmnFlowNodeTypes.IsServiceTask(node.Type)
                        ? "Service task"
                        : BpmnFlowNodeTypes.IsScriptTask(node.Type) ? "Script task" : "Automatic task";
                throw new WorkflowDomainException($"{kind} #{node.Id} must have exactly one outgoing sequence flow.");
            }

            if (BpmnFlowNodeTypes.IsServiceTask(node.Type))
            {
                ValidateServiceTask(node);
            }

            if (BpmnFlowNodeTypes.IsScriptTask(node.Type))
            {
                ValidateScriptTask(node, definition);
            }

            if (BpmnFlowNodeTypes.IsUserTask(node.Type) && outgoing.Count == 0)
            {
                throw new WorkflowDomainException($"User task #{node.Id} must have at least one outgoing sequence flow.");
            }

            if (BpmnFlowNodeTypes.IsUserTask(node.Type))
            {
                ValidateClaimMode(node, definition);
            }

            if (BpmnFlowNodeTypes.IsGateway(node.Type))
            {
                if (outgoing.Count < 2)
                {
                    throw new WorkflowDomainException($"Exclusive gateway #{node.Id} must have at least two outgoing sequence flows.");
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

        if (!string.Equals(service.OnError, ServiceTaskErrorModes.Fail, StringComparison.Ordinal)
            && !string.Equals(service.OnError, ServiceTaskErrorModes.Continue, StringComparison.Ordinal))
        {
            throw new WorkflowDomainException(
                $"Service task #{node.Id} onError must be '{ServiceTaskErrorModes.Fail}' or '{ServiceTaskErrorModes.Continue}'.");
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
        var allowedTypes = new HashSet<string>
        {
            WorkflowVariableTypes.String,
            WorkflowVariableTypes.Number,
            WorkflowVariableTypes.Boolean,
            WorkflowVariableTypes.Date,
            WorkflowVariableTypes.DateTime
        };

        foreach (var variable in variables)
        {
            if (string.IsNullOrWhiteSpace(variable.Name))
            {
                throw new WorkflowDomainException($"Variable name is required on {owner}.");
            }

            if (variable.Name.StartsWith("sys.", StringComparison.OrdinalIgnoreCase)
                || variable.Name.StartsWith("config.", StringComparison.OrdinalIgnoreCase))
            {
                throw new WorkflowDomainException(
                    $"Variable '{variable.Name}' on {owner} uses the reserved 'sys.'/'config.' prefix.");
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
        new(record.Id, record.Name, record.WorkflowKey, record.Version, record.IsPublished, record.CreatedAt);

    internal static WorkflowDetailDto ToDetail(WorkflowDefinitionRecord record) =>
        new(record.Id, record.Name, record.WorkflowKey, record.Version, record.IsPublished, record.CreatedAt, record.Definition);
}
