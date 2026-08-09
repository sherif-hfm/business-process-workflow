using System.Text.Json;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

public sealed class WorkflowVersionCompatibilityEvaluatorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Compatible_change_ignores_cosmetics_and_allows_new_user_task_actions()
    {
        var source = Definition(11, 1, BasicModel());
        var targetModel = Clone(source.Definition);
        targetModel.Name = "Renamed workflow";
        targetModel.FlowNodes.Single(node => node.Id == 2).Name = "Renamed task";
        targetModel.FlowNodes.Single(node => node.Id == 2).X += 500;
        targetModel.SequenceFlows.Single(flow => flow.Id == 20).Name = "Renamed action";
        targetModel.SequenceFlows.Single(flow => flow.Id == 20).Roles = ["approver"];
        var target = Definition(13, 3, targetModel);

        var result = WorkflowVersionCompatibilityEvaluator.Evaluate(
            Context(source, target, includeOpenTask: true));

        Assert.True(result.IsCompatible);
        Assert.Empty(result.Blockers);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Envelope_rejects_terminal_cross_family_unpublished_and_same_version_targets()
    {
        var source = Definition(11, 1, BasicModel());
        var target = source with { WorkflowKey = "other", IsPublished = false };
        var context = Context(source, target) with
        {
            Instance = Context(source, target).Instance with
            {
                Status = WorkflowInstanceStatuses.Completed
            }
        };

        var result = WorkflowVersionCompatibilityEvaluator.Evaluate(context);

        AssertCodes(
            result,
            WorkflowVersionCompatibilityCodes.InstanceNotRunning,
            WorkflowVersionCompatibilityCodes.SameDefinition,
            WorkflowVersionCompatibilityCodes.TargetNotPublished,
            WorkflowVersionCompatibilityCodes.WorkflowKeyMismatch);
    }

    [Fact]
    public void Active_node_requires_same_numeric_identity_type_and_external_id()
    {
        var source = Definition(11, 1, BasicModel());
        var changedType = Clone(source.Definition);
        changedType.FlowNodes.Single(node => node.Id == 2).Type = BpmnFlowNodeTypes.Task;
        changedType.FlowNodes.Single(node => node.Id == 2).ExternalId = "new-id";

        var result = WorkflowVersionCompatibilityEvaluator.Evaluate(
            Context(source, Definition(12, 2, changedType)));

        AssertCodes(
            result,
            WorkflowVersionCompatibilityCodes.ActiveNodeExternalIdChanged,
            WorkflowVersionCompatibilityCodes.ActiveNodeTypeChanged);
    }

    [Fact]
    public void Open_ordinary_task_requires_access_and_attached_timer_contracts()
    {
        var sourceModel = BasicModel();
        sourceModel.FlowNodes.Add(TimerBoundary(4, 2, "PT10M"));
        sourceModel.SequenceFlows.Add(Flow(30, 4, 3));
        var targetModel = Clone(sourceModel);
        var targetTask = targetModel.FlowNodes.Single(node => node.Id == 2);
        targetTask.RequiresClaim = true;
        targetTask.ClaimMode = ClaimModes.Previous;
        targetModel.FlowNodes.Single(node => node.Id == 4).Timer!.TimeDuration = "PT20M";

        var result = WorkflowVersionCompatibilityEvaluator.Evaluate(
            Context(
                Definition(11, 1, sourceModel),
                Definition(12, 2, targetModel),
                includeOpenTask: true));

        AssertCodes(
            result,
            WorkflowVersionCompatibilityCodes.AttachedTimerContractChanged,
            WorkflowVersionCompatibilityCodes.UserTaskContractChanged);
    }

    [Fact]
    public void Active_message_catch_contract_change_is_a_warning_not_a_blocker()
    {
        var sourceModel = BasicModel(BpmnFlowNodeTypes.IntermediateMessageCatchEvent);
        sourceModel.FlowNodes.Single(node => node.Id == 2).Message = Message("secret-a");
        var targetModel = Clone(sourceModel);
        targetModel.FlowNodes.Single(node => node.Id == 2).Message = Message("secret-b");

        var result = WorkflowVersionCompatibilityEvaluator.Evaluate(
            Context(Definition(11, 1, sourceModel), Definition(12, 2, targetModel)));

        Assert.True(result.IsCompatible);
        Assert.Empty(result.Blockers);
        Assert.Equal(
            WorkflowVersionCompatibilityCodes.MessageCatchContractChanged,
            Assert.Single(result.Warnings).Code);
    }

    [Fact]
    public void Active_multi_instance_requires_exact_configuration_and_outcomes()
    {
        var sourceModel = BasicModel();
        sourceModel.FlowNodes.Single(node => node.Id == 2).MultiInstance = new MultiInstanceModel
        {
            Mode = MultiInstanceModes.Parallel,
            Source = MultiInstanceSources.Cardinality,
            CardinalityExpression = "3",
            OnePerActor = true,
            CompletionEvaluation = MultiInstanceCompletionEvaluations.AfterEach,
            ResultVariable = "votes"
        };
        sourceModel.SequenceFlows.Single(flow => flow.Id == 20).CompletionCondition =
            "PercentFlow(20) >= 50";
        sourceModel.SequenceFlows.Single(flow => flow.Id == 20).CompletionPriority = 1;
        var targetModel = Clone(sourceModel);
        targetModel.FlowNodes.Single(node => node.Id == 2).MultiInstance!.CompletionEvaluation =
            MultiInstanceCompletionEvaluations.AfterAll;
        targetModel.SequenceFlows.Single(flow => flow.Id == 20).CompletionCondition =
            "PercentFlow(20) >= 75";

        var source = Definition(11, 1, sourceModel);
        var target = Definition(12, 2, targetModel);
        var context = Context(source, target) with
        {
            ActiveMultiInstanceExecutions = [MultiInstanceExecution()]
        };

        var result = WorkflowVersionCompatibilityEvaluator.Evaluate(context);

        AssertCodes(
            result,
            WorkflowVersionCompatibilityCodes.MultiInstanceContractChanged,
            WorkflowVersionCompatibilityCodes.MultiInstanceOutcomeChanged);
    }

    [Fact]
    public void Multiple_tokens_require_complete_topology_and_gateway_contract()
    {
        var sourceModel = GatewayModel();
        var targetModel = Clone(sourceModel);
        targetModel.FlowNodes.Add(new FlowNodeModel
        {
            Id = 9,
            Type = BpmnFlowNodeTypes.Task,
            ExternalId = "new-node"
        });
        targetModel.SequenceFlows.Single(flow => flow.Id == 30).TargetRef = 5;
        targetModel.FlowNodes.Single(node => node.Id == 2).ActivationCondition = "false";
        var source = Definition(11, 1, sourceModel);
        var target = Definition(12, 2, targetModel);
        var context = Context(source, target, activeNodeId: 3) with
        {
            ActiveTokens =
            [
                Token(101, 3, BpmnFlowNodeTypes.UserTask, "left"),
                Token(102, 4, BpmnFlowNodeTypes.UserTask, "right")
            ]
        };

        var result = WorkflowVersionCompatibilityEvaluator.Evaluate(context);

        AssertCodes(
            result,
            WorkflowVersionCompatibilityCodes.GatewayContractChanged,
            WorkflowVersionCompatibilityCodes.TopologyFlowEndpointsChanged,
            WorkflowVersionCompatibilityCodes.TopologyNodeAdded);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Active_branch_state_blocks_join_cancellation_contract_changes(bool removePolicy)
    {
        var sourceModel = GatewayModel();
        var innerSplit = sourceModel.FlowNodes.Single(node => node.Id == 2);
        innerSplit.Type = BpmnFlowNodeTypes.ParallelGateway;
        innerSplit.ActivationCondition = null;
        sourceModel.SequenceFlows.Single(flow => flow.Id == 10).TargetRef = 8;
        sourceModel.FlowNodes.Add(new FlowNodeModel
        {
            Id = 8,
            Name = "Outer split",
            Type = BpmnFlowNodeTypes.ParallelGateway,
            ExternalId = "outer-split"
        });
        sourceModel.FlowNodes.Add(new FlowNodeModel
        {
            Id = 9,
            Name = "Outer end",
            Type = BpmnFlowNodeTypes.EndEvent,
            ExternalId = "outer-end"
        });
        sourceModel.SequenceFlows.Add(Flow(11, 8, 2));
        sourceModel.SequenceFlows.Add(Flow(12, 8, 9));
        sourceModel.FlowNodes.Add(new FlowNodeModel
        {
            Id = 6,
            Name = "Join",
            Type = BpmnFlowNodeTypes.ParallelGateway,
            ExternalId = "join",
            JoinCancellation = new JoinCancellationModel { GatewayRef = 2 }
        });
        sourceModel.SequenceFlows.Single(flow => flow.Id == 40).TargetRef = 6;
        sourceModel.SequenceFlows.Single(flow => flow.Id == 50).TargetRef = 6;
        sourceModel.SequenceFlows.Add(Flow(60, 6, 5));
        var targetModel = Clone(sourceModel);
        var targetCancellation = targetModel.FlowNodes.Single(node => node.Id == 6).JoinCancellation!;
        if (removePolicy)
        {
            targetModel.FlowNodes.Single(node => node.Id == 6).JoinCancellation = null;
        }
        else
        {
            targetCancellation.GatewayRef = 8;
        }
        var source = Definition(11, 1, sourceModel);
        var target = Definition(12, 2, targetModel);
        var context = Context(source, target, activeNodeId: 3) with
        {
            ActiveTokens =
            [
                Token(101, 3, BpmnFlowNodeTypes.UserTask, "left"),
                Token(102, 4, BpmnFlowNodeTypes.UserTask, "right")
            ]
        };

        var result = WorkflowVersionCompatibilityEvaluator.Evaluate(context);

        AssertCodes(result, WorkflowVersionCompatibilityCodes.GatewayContractChanged);
    }

    [Fact]
    public void Current_values_must_satisfy_target_type_and_validation()
    {
        var source = Definition(11, 1, BasicModel());
        var targetModel = Clone(source.Definition);
        targetModel.Variables =
        [
            new VariableModel
            {
                Id = 1,
                Name = "amount",
                DataType = WorkflowVariableTypes.Number,
                Validation = "amount > 0"
            },
            new VariableModel
            {
                Id = 2,
                Name = "approved",
                DataType = WorkflowVariableTypes.Boolean
            }
        ];
        var context = Context(source, Definition(12, 2, targetModel)) with
        {
            CurrentVariables = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["amount"] = Json(-1),
                ["approved"] = Json("yes")
            }
        };

        var result = WorkflowVersionCompatibilityEvaluator.Evaluate(context);

        AssertCodes(
            result,
            WorkflowVersionCompatibilityCodes.VariableTypeIncompatible,
            WorkflowVersionCompatibilityCodes.VariableValidationFailed);
    }

    [Fact]
    public void Variable_validation_uses_supplied_trusted_context()
    {
        var source = Definition(11, 1, BasicModel());
        var targetModel = Clone(source.Definition);
        targetModel.Variables =
        [
            new VariableModel
            {
                Id = 1,
                Name = "amount",
                DataType = WorkflowVariableTypes.Number,
                Validation = "amount <= [config.limit]"
            }
        ];
        var context = Context(source, Definition(12, 2, targetModel)) with
        {
            CurrentVariables = new Dictionary<string, JsonElement>
            {
                ["amount"] = Json(5)
            },
            VariableValidationContext = new Dictionary<string, JsonElement>
            {
                ["config.limit"] = Json(10)
            }
        };

        var result = WorkflowVersionCompatibilityEvaluator.Evaluate(context);

        Assert.True(result.IsCompatible);
    }

    [Fact]
    public void Observed_flow_must_keep_id_and_endpoints()
    {
        var source = Definition(11, 1, BasicModel());
        var targetModel = Clone(source.Definition);
        targetModel.SequenceFlows.Single(flow => flow.Id == 20).TargetRef = 1;
        var context = Context(source, Definition(12, 2, targetModel)) with
        {
            ObservedFlows = [new ObservedSequenceFlowIdentity(20, 2, 3)]
        };

        var result = WorkflowVersionCompatibilityEvaluator.Evaluate(context);

        AssertCodes(result, WorkflowVersionCompatibilityCodes.ObservedFlowEndpointsChanged);
    }

    [Fact]
    public void FlowInfo_cannot_be_introduced_after_a_committed_traversal()
    {
        var source = Definition(11, 1, BasicModel());
        var targetModel = Clone(source.Definition);
        targetModel.FlowNodes.Add(new FlowNodeModel
        {
            Id = 8,
            Type = BpmnFlowNodeTypes.ScriptTask,
            ExternalId = "audit-script",
            ScriptFormat = ScriptFormats.JavaScript,
            Script = "execution.getFlowInfo(10);",
            UsesFlowInfo = true
        });
        var context = Context(source, Definition(12, 2, targetModel)) with
        {
            HasCommittedTraversals = true
        };

        var result = WorkflowVersionCompatibilityEvaluator.Evaluate(context);

        AssertCodes(result, WorkflowVersionCompatibilityCodes.FlowInfoHistoryIncomplete);
    }

    [Fact]
    public void Open_job_requires_exact_executing_node_and_retry_contract()
    {
        var sourceModel = BasicModel(BpmnFlowNodeTypes.ServiceTask);
        var service = sourceModel.FlowNodes.Single(node => node.Id == 2);
        service.AsyncBefore = true;
        service.Service = new ServiceTaskModel
        {
            Type = ServiceConnectorTypes.Rest,
            Method = "GET",
            Url = "https://source.example/work"
        };
        var targetModel = Clone(sourceModel);
        targetModel.FlowNodes.Single(node => node.Id == 2).Service!.Url =
            "https://target.example/work";
        var source = Definition(11, 1, sourceModel);
        var context = Context(source, Definition(12, 2, targetModel)) with
        {
            OpenJobs = [Job(source)]
        };

        var result = WorkflowVersionCompatibilityEvaluator.Evaluate(context);

        AssertCodes(result, WorkflowVersionCompatibilityCodes.OpenJobContractChanged);
    }

    [Fact]
    public void Attribute_only_changes_do_not_change_durable_node_or_flow_contracts()
    {
        var sourceModel = BasicModel(BpmnFlowNodeTypes.ServiceTask);
        var service = sourceModel.FlowNodes.Single(node => node.Id == 2);
        service.AsyncBefore = true;
        service.Service = new ServiceTaskModel
        {
            Type = ServiceConnectorTypes.Rest,
            Method = "GET",
            Url = "https://source.example/work"
        };
        var targetModel = Clone(sourceModel);
        targetModel.FlowNodes.Single(node => node.Id == 2).Attributes =
        [
            new WorkflowAttributeModel { Key = "integration.owner", Value = "orders" }
        ];
        targetModel.SequenceFlows.Single(flow => flow.Id == 20).Attributes =
        [
            new WorkflowAttributeModel { Key = "integration.route", Value = "complete" }
        ];
        var source = Definition(11, 1, sourceModel);
        var context = Context(source, Definition(12, 2, targetModel)) with
        {
            OpenJobs = [Job(source)]
        };

        var result = WorkflowVersionCompatibilityEvaluator.Evaluate(context);

        Assert.True(result.IsCompatible);
        Assert.Empty(result.Blockers);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Open_timer_requires_same_schedule_and_persisted_descriptor()
    {
        var sourceModel = BasicModel(BpmnFlowNodeTypes.IntermediateTimerCatchEvent);
        sourceModel.FlowNodes.Single(node => node.Id == 2).Timer = new TimerDefinitionModel
        {
            TimeDuration = "PT10M"
        };
        var targetModel = Clone(sourceModel);
        targetModel.FlowNodes.Single(node => node.Id == 2).Timer!.TimeDuration = "PT20M";
        var source = Definition(11, 1, sourceModel);
        var context = Context(source, Definition(12, 2, targetModel)) with
        {
            OpenTimers = [Timer(source)]
        };

        var result = WorkflowVersionCompatibilityEvaluator.Evaluate(context);

        AssertCodes(result, WorkflowVersionCompatibilityCodes.OpenTimerContractChanged);
    }

    private static WorkflowVersionCompatibilityContext Context(
        WorkflowDefinitionRecord source,
        WorkflowDefinitionRecord target,
        bool includeOpenTask = false,
        int activeNodeId = 2)
    {
        var node = source.Definition.FlowNodes.Single(item => item.Id == activeNodeId);
        var instance = new WorkflowInstanceRecord(
            Id: 7,
            WorkflowDefinitionId: source.Id,
            WorkflowKey: source.WorkflowKey,
            IdempotencyKey: null,
            BusinessKey: null,
            BusinessKeyUniqueness: null,
            ActiveTokenId: 101,
            CurrentStepId: activeNodeId,
            ActiveUserTaskId: includeOpenTask ? 201 : null,
            Status: WorkflowInstanceStatuses.Running,
            ClaimedBy: null,
            StartedBy: "alice",
            CreatedAt: Now,
            UpdatedAt: Now);

        return new WorkflowVersionCompatibilityContext
        {
            Instance = instance,
            SourceDefinition = source,
            TargetDefinition = target,
            ActiveTokens = [Token(101, node.Id, node.Type, node.ExternalId)],
            OpenUserTasks = includeOpenTask ? [Task(node)] : []
        };
    }

    private static WorkflowDefinitionRecord Definition(long id, int version, WorkflowModel model) =>
        new(
            id,
            model.Name,
            "purchase",
            version,
            model,
            IsPublished: true,
            IsDefault: version == 1,
            CreatedAt: Now.AddDays(version));

    private static WorkflowModel BasicModel(string activeType = BpmnFlowNodeTypes.UserTask) =>
        new()
        {
            Id = "purchase",
            Name = "Purchase",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel
                {
                    Id = 1,
                    Name = "Start",
                    ExternalId = "start",
                    Type = BpmnFlowNodeTypes.StartEvent
                },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Review",
                    ExternalId = "review",
                    Type = activeType,
                    Roles = activeType == BpmnFlowNodeTypes.UserTask ? ["approver"] : []
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "End",
                    ExternalId = "end",
                    Type = BpmnFlowNodeTypes.EndEvent
                }
            ],
            SequenceFlows =
            [
                Flow(10, 1, 2),
                Flow(20, 2, 3)
            ]
        };

    private static WorkflowModel GatewayModel() =>
        new()
        {
            Id = "purchase",
            Name = "Gateway",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Type = BpmnFlowNodeTypes.StartEvent, ExternalId = "start" },
                new FlowNodeModel
                {
                    Id = 2,
                    Type = BpmnFlowNodeTypes.ComplexGateway,
                    ExternalId = "fork",
                    ActivationCondition = "true"
                },
                new FlowNodeModel { Id = 3, Type = BpmnFlowNodeTypes.UserTask, ExternalId = "left" },
                new FlowNodeModel { Id = 4, Type = BpmnFlowNodeTypes.UserTask, ExternalId = "right" },
                new FlowNodeModel { Id = 5, Type = BpmnFlowNodeTypes.EndEvent, ExternalId = "end" }
            ],
            SequenceFlows =
            [
                Flow(10, 1, 2),
                Flow(20, 2, 3),
                Flow(30, 2, 4),
                Flow(40, 3, 5),
                Flow(50, 4, 5)
            ]
        };

    private static SequenceFlowModel Flow(int id, int source, int target) =>
        new()
        {
            Id = id,
            Name = $"Flow {id}",
            SourceRef = source,
            TargetRef = target
        };

    private static FlowNodeModel TimerBoundary(int id, int hostId, string duration) =>
        new()
        {
            Id = id,
            Name = "Reminder",
            ExternalId = "reminder",
            Type = BpmnFlowNodeTypes.TimerBoundaryEvent,
            AttachedToRef = hostId,
            CancelActivity = false,
            Timer = new TimerDefinitionModel { TimeDuration = duration }
        };

    private static MessageCatchModel Message(string secret) =>
        new()
        {
            ClientId = "client",
            ClientSecret = secret,
            HeaderName = "X-Hook",
            HeaderValue = "purchase"
        };

    private static ExecutionTokenRecord Token(
        long id,
        int nodeId,
        string nodeType,
        string? externalId) =>
        new(
            Id: id,
            InstanceId: 7,
            NodeId: nodeId,
            NodeName: $"Node {nodeId}",
            NodeExternalId: externalId,
            NodeType: nodeType,
            FaultCode: null,
            FaultDescription: null,
            Status: ExecutionTokenRecordStatuses.Active,
            GatewayBranchId: null,
            ArrivedViaFlowId: 10,
            ComplexGatewayStateId: null,
            ComplexGatewayCycle: null,
            ComplexDrainStateIds: [],
            TerminationReason: null,
            CreatedAt: Now,
            UpdatedAt: Now);

    private static UserTaskRecord Task(FlowNodeModel node) =>
        new(
            Id: 201,
            InstanceId: 7,
            TokenId: 101,
            NodeId: node.Id,
            NodeName: node.Name,
            NodeExternalId: node.ExternalId,
            Roles: node.Roles,
            RequiresClaim: node.RequiresClaim,
            RequiresAssignment: node.RequiresAssignment,
            Status: UserTaskRecordStatuses.Active,
            ClaimedBy: null,
            MultiInstanceExecutionId: null,
            ItemIndex: null,
            ItemValue: null,
            Assignee: null,
            SelectedFlowId: null,
            Result: null,
            CompletedBy: null,
            CompletedByRoles: null,
            CreatedAt: Now,
            UpdatedAt: Now,
            CompletedAt: null);

    private static MultiInstanceExecutionRecord MultiInstanceExecution() =>
        new(
            Id: 301,
            InstanceId: 7,
            TokenId: 101,
            NodeId: 2,
            Mode: MultiInstanceModes.Parallel,
            Source: MultiInstanceSources.Cardinality,
            OnePerActor: true,
            ResultVariable: "votes",
            Status: MultiInstanceRecordStatuses.Active,
            TotalCount: 3,
            CompletedCount: 1,
            CancelledCount: 0,
            WinningFlowId: null,
            CompletionReason: null,
            CreatedAt: Now,
            UpdatedAt: Now,
            CompletedAt: null);

    private static WorkflowJobRecord Job(WorkflowDefinitionRecord source) =>
        new(
            Id: 401,
            InstanceId: 7,
            WorkflowDefinitionId: source.Id,
            WorkflowKey: source.WorkflowKey,
            TokenId: 101,
            MultiInstanceExecutionId: null,
            UserTaskId: null,
            TimerSubscriptionId: null,
            ActivationId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            NodeId: 2,
            NodeName: "Service",
            NodeType: BpmnFlowNodeTypes.ServiceTask,
            Kind: WorkflowJobKinds.AsyncBefore,
            QueueClass: WorkflowJobClasses.Activity,
            Phase: WorkflowJobKinds.AsyncBefore,
            Status: WorkflowJobStatuses.Queued,
            Priority: 0,
            AttemptCount: 0,
            MaxAttempts: 4,
            FailureHandling: WorkflowJobFailureHandling.BoundaryFirst,
            RetryDelays: [TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)],
            DueAt: Now,
            ScheduledOccurrenceAt: null,
            Payload: null,
            SnapshotId: null,
            WorkerId: null,
            LeaseToken: null,
            LeaseGeneration: 0,
            LeaseExpiresAt: null,
            HeartbeatAt: null,
            Result: null,
            Error: null,
            LastFailureCode: null,
            LastFailureDescription: null,
            ResultReadyAt: null,
            IncidentId: null,
            CreatedAt: Now,
            UpdatedAt: Now,
            StartedAt: null,
            CompletedAt: null);

    private static TimerSubscriptionRecord Timer(WorkflowDefinitionRecord source) =>
        new(
            Id: 501,
            InstanceId: 7,
            WorkflowDefinitionId: source.Id,
            WorkflowKey: source.WorkflowKey,
            TokenId: 101,
            ActivationId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
            TimerNodeId: 2,
            TimerNodeName: "Wait",
            AttachedToNodeId: null,
            ScheduleKind: TimerScheduleKinds.Duration,
            ScheduleExpression: "PT10M",
            CancelActivity: true,
            Status: TimerSubscriptionStatuses.Active,
            NextDueAt: Now.AddMinutes(10),
            Occurrence: 0,
            CreatedAt: Now,
            UpdatedAt: Now,
            CompletedAt: null);

    private static WorkflowModel Clone(WorkflowModel model) =>
        JsonSerializer.Deserialize<WorkflowModel>(JsonSerializer.Serialize(model))!;

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);

    private static void AssertCodes(
        WorkflowVersionCompatibilityResult result,
        params string[] expected) =>
        Assert.Equal(
            expected.OrderBy(code => code, StringComparer.Ordinal),
            result.Blockers.Select(issue => issue.Code).Distinct(StringComparer.Ordinal));
}
