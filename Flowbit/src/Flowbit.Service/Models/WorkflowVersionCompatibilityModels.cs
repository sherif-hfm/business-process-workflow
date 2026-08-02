using System.Text.Json;

namespace Flowbit.Service.Models;

/// <summary>
/// Immutable input to the workflow-version compatibility evaluator. The caller
/// is responsible for loading one transactionally consistent snapshot when the
/// result is used to authorize an in-place version change.
/// </summary>
public sealed record WorkflowVersionCompatibilityContext
{
    public required WorkflowInstanceRecord Instance { get; init; }

    public required WorkflowDefinitionRecord SourceDefinition { get; init; }

    public required WorkflowDefinitionRecord TargetDefinition { get; init; }

    public IReadOnlyList<ExecutionTokenRecord> ActiveTokens { get; init; } = [];

    public IReadOnlyList<UserTaskRecord> OpenUserTasks { get; init; } = [];

    public IReadOnlyList<MultiInstanceExecutionRecord> ActiveMultiInstanceExecutions { get; init; } = [];

    public IReadOnlyList<GatewayExecutionRecord> ActiveGatewayExecutions { get; init; } = [];

    public IReadOnlyList<GatewayBranchRecord> ActiveGatewayBranches { get; init; } = [];

    public IReadOnlyList<ComplexGatewayStateRecord> ActiveComplexGatewayStates { get; init; } = [];

    /// <summary>
    /// Latest persisted value for every instance variable, keyed
    /// case-insensitively by the runtime repository.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> CurrentVariables { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Trusted evaluation-only parameters (for example sys.*, config.*, and
    /// setting.*) needed by target variable validation rules. Persisted current
    /// variables are overlaid by the evaluator and win on a name collision.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> VariableValidationContext { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Endpoint identities snapshotted by committed history/evidence. Repeated
    /// identities are harmless; conflicting identities are evaluated separately.
    /// </summary>
    public IReadOnlyList<ObservedSequenceFlowIdentity> ObservedFlows { get; init; } = [];

    /// <summary>
    /// Flow summaries are also used to identify observed flow ids when a caller
    /// has no occurrence rows. A zero-count summary does not make a flow observed.
    /// </summary>
    public IReadOnlyList<SequenceFlowSummaryRecord> FlowSummaries { get; init; } = [];

    public IReadOnlyList<WorkflowJobRecord> OpenJobs { get; init; } = [];

    public IReadOnlyList<TimerSubscriptionRecord> OpenTimers { get; init; } = [];

    /// <summary>
    /// True when any traversal committed before this snapshot, even when the
    /// source definition did not opt into flow-evidence persistence.
    /// </summary>
    public bool HasCommittedTraversals { get; init; }
}

/// <summary>
/// Historical sequence-flow identity used to ensure that a numeric id is never
/// reinterpreted after an instance changes definition version.
/// </summary>
public sealed record ObservedSequenceFlowIdentity(
    int FlowId,
    int SourceNodeId,
    int TargetNodeId);

/// <summary>
/// One stable, machine-readable compatibility diagnostic. Optional identities
/// allow API callers to render useful detail without parsing <see cref="Message"/>.
/// </summary>
public sealed record WorkflowVersionCompatibilityIssue(
    string Code,
    string Message,
    int? NodeId = null,
    int? FlowId = null,
    long? RuntimeId = null,
    string? VariableName = null);

/// <summary>
/// Pure compatibility result shared by preview and authoritative execution.
/// </summary>
public sealed record WorkflowVersionCompatibilityResult(
    IReadOnlyList<WorkflowVersionCompatibilityIssue> Blockers,
    IReadOnlyList<WorkflowVersionCompatibilityIssue> Warnings)
{
    public bool IsCompatible => Blockers.Count == 0;
}

/// <summary>
/// Stable issue codes returned by <c>WorkflowVersionCompatibilityEvaluator</c>.
/// Values are wire-safe and must not be repurposed for a different condition.
/// </summary>
public static class WorkflowVersionCompatibilityCodes
{
    public const string InstanceNotRunning = "instance_not_running";
    public const string SourceDefinitionMismatch = "source_definition_mismatch";
    public const string SameDefinition = "same_definition";
    public const string WorkflowKeyMismatch = "workflow_key_mismatch";
    public const string TargetNotPublished = "target_not_published";

    public const string ActiveNodeMissing = "active_node_missing";
    public const string ActiveNodeTypeChanged = "active_node_type_changed";
    public const string ActiveNodeExternalIdChanged = "active_node_external_id_changed";
    public const string UserTaskContractChanged = "user_task_contract_changed";
    public const string AttachedTimerContractChanged = "attached_timer_contract_changed";
    public const string MessageCatchContractChanged = "message_catch_contract_changed";
    public const string MultiInstanceContractChanged = "multi_instance_contract_changed";
    public const string MultiInstanceOutcomeChanged = "multi_instance_outcome_changed";

    public const string TopologyNodeMissing = "topology_node_missing";
    public const string TopologyNodeAdded = "topology_node_added";
    public const string TopologyNodeTypeChanged = "topology_node_type_changed";
    public const string TopologyFlowMissing = "topology_flow_missing";
    public const string TopologyFlowAdded = "topology_flow_added";
    public const string TopologyFlowEndpointsChanged = "topology_flow_endpoints_changed";
    public const string GatewayContractChanged = "gateway_contract_changed";
    public const string ScopedInterruptContractChanged = "scoped_interrupt_contract_changed";

    public const string VariableTypeIncompatible = "variable_type_incompatible";
    public const string VariableValidationFailed = "variable_validation_failed";
    public const string VariableUndeclaredInTarget = "variable_undeclared_in_target";

    public const string ObservedFlowMissing = "observed_flow_missing";
    public const string ObservedFlowEndpointsChanged = "observed_flow_endpoints_changed";
    public const string FlowInfoHistoryIncomplete = "flow_info_history_incomplete";

    public const string OpenJobNodeMissing = "open_job_node_missing";
    public const string OpenJobContractChanged = "open_job_contract_changed";
    public const string OpenTimerNodeMissing = "open_timer_node_missing";
    public const string OpenTimerContractChanged = "open_timer_contract_changed";
}
