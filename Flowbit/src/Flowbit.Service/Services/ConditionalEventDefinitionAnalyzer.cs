using System.Collections.Immutable;
using System.Text;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;
using NCalc;
using NCalc.Exceptions;
using NCalc.Helpers;

namespace Flowbit.Service.Services;

/// <summary>
/// Extracts conditional-event dependencies from NCalc's parsed AST. Only
/// statically named, persisted instance-variable producers are observable.
/// </summary>
public sealed class ConditionalEventDefinitionAnalyzer
    : IConditionalEventDefinitionAnalyzer
{
    private const ExpressionOptions Options =
        ExpressionOptions.CaseInsensitiveStringComparer
        | ExpressionOptions.AllowNullParameter;

    private static readonly HashSet<string> AllowedFunctions = new(
        BuiltInFunctionHelper.GetBuiltInFunctionNames()
            .Concat([
                "Length", "Len", "IsNullOrEmpty", "IsNullOrWhiteSpace",
                "Contains", "StartsWith", "EndsWith", "Lower", "Upper",
                "Trim", "IsMatch"
            ]),
        StringComparer.OrdinalIgnoreCase);

    private static readonly string[] NonObservablePrefixes =
        ["sys.", "config.", "setting.", "mi.", "gateway."];

    public ConditionalEventDependencyPlan Analyze(WorkflowModel definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var conditionalNodes = (definition.FlowNodes ?? [])
            .Where(node => node is not null
                && BpmnFlowNodeTypes.IsConditionalCatch(node.Type))
            .OrderBy(node => node.Id)
            .ToList();
        if (conditionalNodes.Count == 0)
        {
            return ConditionalEventDependencyPlan.Empty;
        }

        var canonicalVariables = BuildCanonicalVariableMap(definition);
        var entries = ImmutableDictionary.CreateBuilder<int, ConditionalEventPlanEntry>();
        var inverse = new Dictionary<string, SortedSet<int>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var node in conditionalNodes)
        {
            var conditional = node.Conditional
                ?? throw new WorkflowDomainException(
                    $"Conditional catch event #{node.Id} must have a conditional configuration.");
            var condition = ConditionalDefinitionRules.NormalizeCondition(
                conditional.Condition);
            if (condition is null)
            {
                throw new WorkflowDomainException(
                    $"Conditional catch event #{node.Id} must define a condition.");
            }
            if (condition.EnumerateRunes()
                    .Take(ConditionalDefinitionRules.MaxConditionLength + 1)
                    .Count() > ConditionalDefinitionRules.MaxConditionLength)
            {
                throw new WorkflowDomainException(
                    $"Conditional catch event #{node.Id} condition must contain at most "
                    + $"{ConditionalDefinitionRules.MaxConditionLength} Unicode scalar values.");
            }

            var deliveryMode = ConditionalEventDeliveryModes.GetEffective(
                conditional.DeliveryMode);
            if (deliveryMode is not (ConditionalEventDeliveryModes.Atomic
                or ConditionalEventDeliveryModes.DurableAsync))
            {
                throw new WorkflowDomainException(
                    $"Conditional catch event #{node.Id} has unsupported deliveryMode "
                    + $"'{conditional.DeliveryMode}'.");
            }

            var parsed = new Expression(condition, Options);
            try
            {
                if (parsed.HasErrors())
                {
                    throw new WorkflowDomainException(
                        $"Conditional catch event #{node.Id} has an invalid condition: "
                        + $"'{conditional.Condition}'.");
                }

                var unknownFunction = parsed.GetFunctionNames()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(function => !AllowedFunctions.Contains(function));
                if (unknownFunction is not null)
                {
                    throw new WorkflowDomainException(
                        $"Conditional catch event #{node.Id} condition uses unsupported "
                        + $"or non-observable function '{unknownFunction}'.");
                }

                var dependencies = ResolveDependencies(
                    node.Id,
                    parsed.GetParameterNames(),
                    canonicalVariables);
                if (dependencies.Length == 0)
                {
                    throw new WorkflowDomainException(
                        $"Conditional catch event #{node.Id} condition must reference at "
                        + "least one declared stored instance variable.");
                }
                if (dependencies.Length > ConditionalDefinitionRules.MaxDependencies)
                {
                    throw new WorkflowDomainException(
                        $"Conditional catch event #{node.Id} condition may reference at most "
                        + $"{ConditionalDefinitionRules.MaxDependencies} stored variables.");
                }

                var entry = new ConditionalEventPlanEntry(
                    node.Id,
                    condition,
                    deliveryMode,
                    dependencies);
                if (!entries.TryAdd(node.Id, entry))
                {
                    throw new WorkflowDomainException(
                        $"Flow node id #{node.Id} is duplicated.");
                }

                foreach (var dependency in dependencies)
                {
                    if (!inverse.TryGetValue(dependency, out var nodeIds))
                    {
                        nodeIds = [];
                        inverse.Add(dependency, nodeIds);
                    }
                    nodeIds.Add(node.Id);
                }
            }
            catch (WorkflowDomainException)
            {
                throw;
            }
            catch (NCalcException)
            {
                throw new WorkflowDomainException(
                    $"Conditional catch event #{node.Id} has an invalid condition: "
                    + $"'{conditional.Condition}'.");
            }
        }

        var immutableInverse = inverse.ToImmutableDictionary(
            pair => pair.Key,
            pair => pair.Value.ToImmutableArray(),
            StringComparer.OrdinalIgnoreCase);
        return new ConditionalEventDependencyPlan(
            entries.ToImmutable(),
            immutableInverse);
    }

    private static ImmutableArray<string> ResolveDependencies(
        int nodeId,
        IEnumerable<string> parameters,
        IReadOnlyDictionary<string, string> canonicalVariables)
    {
        var dependencies = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawParameter in parameters.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var parameter = rawParameter.Trim();
            if (parameter.Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var prefix = NonObservablePrefixes.FirstOrDefault(candidate =>
                parameter.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));
            if (prefix is not null)
            {
                throw new WorkflowDomainException(
                    $"Conditional catch event #{nodeId} condition references "
                    + $"non-observable context parameter '{parameter}'.");
            }

            if (!canonicalVariables.TryGetValue(parameter, out var canonical))
            {
                throw new WorkflowDomainException(
                    $"Conditional catch event #{nodeId} condition references undeclared "
                    + $"stored variable '{parameter}'.");
            }
            dependencies.Add(canonical);
        }

        return dependencies.ToImmutableArray();
    }

    private static IReadOnlyDictionary<string, string> BuildCanonicalVariableMap(
        WorkflowModel definition)
    {
        var names = new List<(string Name, string Owner)>();

        AddVariables(definition.Variables, "process variables");
        foreach (var node in definition.FlowNodes ?? [])
        {
            if (node is null) continue;
            AddVariables(node.Variables, $"flow node #{node.Id}");
            AddTarget(node.Service?.StatusVariable, $"service task #{node.Id} statusVariable");
            foreach (var mapping in node.Service?.OutputMappings ?? [])
            {
                if (mapping is not null)
                {
                    AddTarget(mapping.Variable, $"service task #{node.Id} output mapping");
                }
            }
            foreach (var mapping in node.Message?.OutputMappings ?? [])
            {
                if (mapping is not null)
                {
                    AddTarget(mapping.Variable, $"message event #{node.Id} output mapping");
                }
            }
            AddTarget(node.ErrorVariable, $"error boundary event #{node.Id} errorVariable");
            AddTarget(node.Idempotency?.Variable, $"entry event #{node.Id} idempotency variable");
        }
        foreach (var flow in definition.SequenceFlows ?? [])
        {
            if (flow is not null)
            {
                AddVariables(flow.Variables, $"sequence flow #{flow.Id}");
            }
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in names.GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            var spellings = group.Select(item => item.Name)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (spellings.Length > 1)
            {
                throw new WorkflowDomainException(
                    $"Stored variable declarations for '{group.Key}' use ambiguous casing "
                    + $"({string.Join(", ", spellings.Select(value => $"'{value}'"))}); "
                    + "stored variable names are case-insensitive.");
            }
            result.Add(group.Key, spellings[0]);
        }

        return result;

        void AddVariables(IEnumerable<VariableModel>? variables, string owner)
        {
            foreach (var variable in variables ?? [])
            {
                if (variable is not null)
                {
                    AddTarget(variable.Name, owner);
                }
            }
        }

        void AddTarget(string? value, string owner)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                names.Add((value.Trim(), owner));
            }
        }
    }
}
