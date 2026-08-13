using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Flowbit.Tests;

public sealed class ConditionalEventDefinitionTests
{
    private readonly ConditionalEventDefinitionAnalyzer analyzer = new();

    [Theory]
    [InlineData(null, ConditionalEventDeliveryModes.Atomic)]
    [InlineData("   ", ConditionalEventDeliveryModes.Atomic)]
    [InlineData("AtOmIc", ConditionalEventDeliveryModes.Atomic)]
    [InlineData("DURABLEASYNC", ConditionalEventDeliveryModes.DurableAsync)]
    public void Analyze_AcceptsSupportedDeliveryModesAndAppliesAtomicDefault(
        string? authoredMode,
        string expectedMode)
    {
        var definition = CreateDefinition("[amount] >= 10", authoredMode);

        var plan = analyzer.Analyze(definition);

        var conditional = Assert.Single(plan.EventsByNodeId).Value;
        Assert.Equal(expectedMode, conditional.DeliveryMode);
        Assert.Equal(["Amount"], conditional.Dependencies.ToArray());
        Assert.Equal([2], plan.NodeIdsByVariable["AMOUNT"].ToArray());
    }

    [Theory]
    [InlineData("missingIncoming")]
    [InlineData("missingOutgoing")]
    [InlineData("multipleOutgoing")]
    public async Task CreateAsync_RejectsInvalidConditionalCatchTopology(string invalidShape)
    {
        var definition = CreateDefinition("Amount >= 10");
        switch (invalidShape)
        {
            case "missingIncoming":
                definition.SequenceFlows.Single(flow => flow.Id == 101).TargetRef = 3;
                break;
            case "missingOutgoing":
                definition.SequenceFlows.RemoveAll(flow => flow.Id == 201);
                break;
            case "multipleOutgoing":
                definition.FlowNodes.Add(new FlowNodeModel
                {
                    Id = 4,
                    Name = "Alternate end",
                    Type = BpmnFlowNodeTypes.EndEvent
                });
                definition.SequenceFlows.Add(new SequenceFlowModel
                {
                    Id = 202,
                    Name = "Alternate continuation",
                    SourceRef = 2,
                    TargetRef = 4
                });
                break;
        }

        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateDefinitionService().CreateAsync(
                definition,
                publish: false,
                CancellationToken.None));

        Assert.Contains("Conditional catch event #2", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MissingValue > 0", "undeclared stored variable 'MissingValue'")]
    [InlineData("[sys.now] != null", "non-observable context parameter 'sys.now'")]
    [InlineData("[config.limit] > Amount", "non-observable context parameter 'config.limit'")]
    [InlineData("Mystery(Amount)", "function 'Mystery'")]
    [InlineData("FlowInfo(101, 'all') and Amount > 0", "function 'FlowInfo'")]
    [InlineData("IncomingCount(101) > Amount", "function 'IncomingCount'")]
    public void Analyze_RejectsUndeclaredNonObservableAndUnknownDependencies(
        string condition,
        string expectedMessage)
    {
        var error = Assert.Throws<WorkflowDomainException>(() =>
            analyzer.Analyze(CreateDefinition(condition)));

        Assert.Contains(expectedMessage, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_ExtractsAstDependenciesOnceAndUsesDeclaredCasing()
    {
        var definition = CreateDefinition(
            "Length([amount]) > 0 and ([APPROVED] or [amount] >= 10)");

        var plan = analyzer.Analyze(definition);

        var conditional = plan.EventsByNodeId[2];
        Assert.Equal(["Amount", "Approved"], conditional.Dependencies.ToArray());
        Assert.Equal([2], plan.NodeIdsByVariable["amount"].ToArray());
        Assert.Equal([2], plan.NodeIdsByVariable["approved"].ToArray());
    }

    [Fact]
    public void Analyze_RecognizesEveryStaticPersistedVariableProducer()
    {
        var definition = CreateDefinition("ProcessValue");
        definition.Variables = [Variable("ProcessValue", 0)];
        definition.FlowNodes.Insert(1, new FlowNodeModel
        {
            Id = 10,
            Name = "Entry producer",
            Type = BpmnFlowNodeTypes.StartEvent,
            Variables = [Variable("StartInput", 0)],
            Idempotency = new IdempotencyModel { Variable = "TransportKey" }
        });
        definition.FlowNodes.Insert(2, new FlowNodeModel
        {
            Id = 11,
            Name = "Node producer",
            Type = BpmnFlowNodeTypes.UserTask,
            Variables = [Variable("NodeInput", 0)]
        });
        definition.FlowNodes.Insert(3, new FlowNodeModel
        {
            Id = 12,
            Name = "Service producer",
            Type = BpmnFlowNodeTypes.ServiceTask,
            Service = new ServiceTaskModel
            {
                StatusVariable = "HttpStatus",
                OutputMappings =
                [
                    new ServiceOutputMappingModel { Variable = "ServiceResult" }
                ]
            }
        });
        definition.FlowNodes.Insert(4, new FlowNodeModel
        {
            Id = 13,
            Name = "Message producer",
            Type = BpmnFlowNodeTypes.IntermediateMessageCatchEvent,
            Message = new MessageCatchModel
            {
                OutputMappings =
                [
                    new MessageOutputMappingModel { Variable = "MessageResult" }
                ]
            }
        });
        definition.FlowNodes.Insert(5, new FlowNodeModel
        {
            Id = 14,
            Name = "Error producer",
            Type = BpmnFlowNodeTypes.ErrorBoundaryEvent,
            ErrorVariable = "FailureReason"
        });
        definition.SequenceFlows.Add(new SequenceFlowModel
        {
            Id = 999,
            Name = "Flow producer",
            SourceRef = 10,
            TargetRef = 11,
            Variables = [Variable("FlowInput", 0)]
        });
        definition.FlowNodes.Single(node => node.Id == 2).Conditional!.Condition =
            "ProcessValue and StartInput and TransportKey and NodeInput "
            + "and HttpStatus and ServiceResult and MessageResult "
            + "and FailureReason and FlowInput";

        var dependencies = analyzer.Analyze(definition)
            .EventsByNodeId[2].Dependencies;

        Assert.Equal(
            new[]
            {
                "FailureReason", "FlowInput", "HttpStatus", "MessageResult",
                "NodeInput", "ProcessValue", "ServiceResult", "StartInput",
                "TransportKey"
            },
            dependencies.ToArray());
    }

    [Fact]
    public void Analyze_RejectsAmbiguousStoredVariableDeclarationCasing()
    {
        var definition = CreateDefinition("Amount > 0");
        definition.FlowNodes.Add(new FlowNodeModel
        {
            Id = 50,
            Name = "Producer",
            Type = BpmnFlowNodeTypes.ServiceTask,
            Service = new ServiceTaskModel
            {
                OutputMappings =
                [
                    new ServiceOutputMappingModel { Variable = "amount" }
                ]
            }
        });

        var error = Assert.Throws<WorkflowDomainException>(() =>
            analyzer.Analyze(definition));

        Assert.Contains("ambiguous casing", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeAndJsonRoundTrip_PreserveConditionalShapeAndCanonicalGrammar()
    {
        var atomic = CreateDefinition("Amount > 0");
        var atomicJson = JsonSerializer.Serialize(atomic);
        using (var document = JsonDocument.Parse(atomicJson))
        {
            var conditional = document.RootElement.GetProperty("flowNodes")[1]
                .GetProperty("conditional");
            Assert.False(conditional.TryGetProperty("deliveryMode", out _));
        }

        var roundTripped = JsonSerializer.Deserialize<WorkflowModel>(atomicJson)!;
        Assert.Equal(
            ConditionalEventDeliveryModes.Atomic,
            roundTripped.FlowNodes[1].Conditional!.EffectiveDeliveryMode);

        var durable = CreateDefinition(
            "  ${ [amount] >= 10 and [APPROVED] }  ",
            "  DURABLEASYNC  ");
        WorkflowModelMigrator.Normalize(durable);

        var normalized = durable.FlowNodes.Single(node => node.Id == 2).Conditional!;
        Assert.Equal("[amount] >= 10 and [APPROVED]", normalized.Condition);
        Assert.Equal(ConditionalEventDeliveryModes.DurableAsync, normalized.DeliveryMode);
        Assert.Equal(
            ["Amount", "Approved"],
            analyzer.Analyze(durable).EventsByNodeId[2].Dependencies.ToArray());
    }

    [Fact]
    public void Cache_EvictsOldestPlanAndRebuildsItOnDemand()
    {
        var counting = new CountingAnalyzer(analyzer);
        var cache = new ConditionalEventDependencyPlanCache(counting);
        var definition = CreateDefinition("Amount > 0");

        for (var id = 1L; id <= ConditionalEventDependencyPlanCache.MaximumEntries + 1L; id++)
        {
            _ = cache.GetOrAdd(id, definition);
        }

        Assert.Equal(ConditionalEventDependencyPlanCache.MaximumEntries + 1, counting.Count);
        Assert.False(cache.TryGet(1, out _));
        Assert.True(cache.TryGet(2, out _));

        var rebuilt = cache.GetOrAdd(1, definition);
        Assert.Equal(ConditionalEventDependencyPlanCache.MaximumEntries + 2, counting.Count);
        Assert.Equal(["Amount"], rebuilt.EventsByNodeId[2].Dependencies.ToArray());

        cache.Remove(1);
        _ = cache.GetOrAdd(1, definition);
        Assert.Equal(ConditionalEventDependencyPlanCache.MaximumEntries + 3, counting.Count);
    }

    private static WorkflowModel CreateDefinition(
        string condition,
        string? deliveryMode = null) => new()
    {
        Id = "conditional-definition-tests",
        Name = "Conditional definition tests",
        InitialEventId = 1,
        Variables =
        [
            Variable("Amount", 0),
            new VariableModel
            {
                Name = "Approved",
                DataType = WorkflowVariableTypes.Boolean,
                DefaultValue = JsonSerializer.SerializeToElement(false)
            }
        ],
        FlowNodes =
        [
            new FlowNodeModel
            {
                Id = 1,
                Name = "Start",
                Type = BpmnFlowNodeTypes.StartEvent
            },
            new FlowNodeModel
            {
                Id = 2,
                Name = "Wait for condition",
                Type = BpmnFlowNodeTypes.IntermediateConditionalCatchEvent,
                Conditional = new ConditionalDefinitionModel
                {
                    Condition = condition,
                    DeliveryMode = deliveryMode
                }
            },
            new FlowNodeModel
            {
                Id = 3,
                Name = "End",
                Type = BpmnFlowNodeTypes.EndEvent
            }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel
            {
                Id = 101,
                Name = "Wait",
                SourceRef = 1,
                TargetRef = 2
            },
            new SequenceFlowModel
            {
                Id = 201,
                Name = "Continue",
                SourceRef = 2,
                TargetRef = 3
            }
        ]
    };

    private static VariableModel Variable(string name, int defaultValue) => new()
    {
        Name = name,
        DataType = WorkflowVariableTypes.Number,
        DefaultValue = JsonSerializer.SerializeToElement(defaultValue)
    };

    private static WorkflowDefinitionService CreateDefinitionService() => new(
        null!,
        new ParseOnlyScriptEvaluator(),
        new ServiceTaskOptions(),
        NullLogger<WorkflowDefinitionService>.Instance,
        conditionalEventAnalyzer: new ConditionalEventDefinitionAnalyzer());

    private sealed class ParseOnlyScriptEvaluator : IScriptEvaluator
    {
        public ScriptResult Evaluate(
            string script,
            IScriptContext context,
            CancellationToken cancellationToken) => new(true, null);

        public bool IsValid(string script, out string? error)
        {
            error = null;
            return true;
        }
    }

    private sealed class CountingAnalyzer(IConditionalEventDefinitionAnalyzer inner)
        : IConditionalEventDefinitionAnalyzer
    {
        public int Count { get; private set; }

        public ConditionalEventDependencyPlan Analyze(WorkflowModel definition)
        {
            Count++;
            return inner.Analyze(definition);
        }
    }
}
