using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

public sealed class DefinitionValidationTests
{
    public static IEnumerable<object[]> ValidTypedOutputDefaults()
    {
        var values = new (string Type, string Scalar, string Array)[]
        {
            (WorkflowVariableTypes.String, "\"value\"", "[\"a\",\"b\"]"),
            (WorkflowVariableTypes.Number, "12.5", "[1,2.5]"),
            (WorkflowVariableTypes.Boolean, "true", "[true,false]"),
            (WorkflowVariableTypes.Date, "\"2026-07-15\"", "[\"2026-07-15\",\"2026-07-16\"]"),
            (WorkflowVariableTypes.DateTime, "\"2026-07-15T10:30:00Z\"", "[\"2026-07-15T10:30:00Z\"]"),
            (WorkflowVariableTypes.Json, "{\"source\":\"test\"}", "[{\"id\":1},2]")
        };
        foreach (var owner in new[] { "service", "catch" })
        {
            foreach (var value in values)
            {
                yield return new object[] { owner, value.Type, false, value.Scalar };
                yield return new object[] { owner, value.Type, true, value.Array };
            }
        }
    }

    [Fact]
    public async Task CreateAsync_CanonicalizesKnownMultiInstanceValuesCaseInsensitively()
    {
        var model = LoadModel("votes-users-list.json");
        var multi = model.FlowNodes.Single(node => node.Id == 2).MultiInstance!;
        multi.Mode = "SeQuEnTiAl";
        multi.Source = "CoLlEcTiOn";
        multi.CompletionEvaluation = "AfTeRaLl";
        var service = CreateService(out var repository);

        await service.CreateAsync(model, false, CancellationToken.None);

        var saved = repository.Added!.Definition.FlowNodes.Single(node => node.Id == 2).MultiInstance!;
        Assert.Equal(MultiInstanceModes.Sequential, saved.Mode);
        Assert.Equal(MultiInstanceSources.Collection, saved.Source);
        Assert.Equal(MultiInstanceCompletionEvaluations.AfterAll, saved.CompletionEvaluation);
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("source")]
    [InlineData("completion")]
    public async Task CreateAsync_RejectsUnknownMultiInstanceEnums(string target)
    {
        var model = LoadModel("votes-users-list.json");
        var multi = model.FlowNodes.Single(node => node.Id == 2).MultiInstance!;
        if (target == "mode") multi.Mode = "sequentual";
        if (target == "source") multi.Source = "users";
        if (target == "completion") multi.CompletionEvaluation = "sometimes";
        var service = CreateService(out _);

        await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            service.CreateAsync(model, false, CancellationToken.None));
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("source")]
    [InlineData("completion")]
    public async Task CreateAsync_RejectsExplicitNullMultiInstanceEnums(string target)
    {
        var model = LoadModel("votes-users-list.json");
        var multi = model.FlowNodes.Single(node => node.Id == 2).MultiInstance!;
        if (target == "mode") multi.Mode = null!;
        if (target == "source") multi.Source = null!;
        if (target == "completion") multi.CompletionEvaluation = null!;
        var service = CreateService(out _);

        await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            service.CreateAsync(model, false, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_RejectsDuplicateNodeIdsBeforeSingleLookups()
    {
        var model = LoadModel("votes-users-list.json");
        model.FlowNodes.Add(Clone(model.FlowNodes[0]));
        var service = CreateService(out _);

        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            service.CreateAsync(model, false, CancellationToken.None));

        Assert.Contains("duplicated", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_RejectsDuplicateFlowIdsBeforeSingleLookups()
    {
        var model = LoadModel("votes-users-list.json");
        model.SequenceFlows.Add(Clone(model.SequenceFlows[0]));
        var service = CreateService(out _);

        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            service.CreateAsync(model, false, CancellationToken.None));

        Assert.Contains("duplicated", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_RejectsCaseInsensitiveVariableNameDuplicates()
    {
        var model = LoadModel("votes-users-list.json");
        model.Variables.Add(new VariableModel
        {
            Id = 99,
            Name = "VOTERS",
            DataType = WorkflowVariableTypes.String,
            IsArray = true,
            DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<string>())
        });
        var service = CreateService(out _);

        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            service.CreateAsync(model, false, CancellationToken.None));

        Assert.Contains("case-insensitive", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_CanonicalizesAndAcceptsValidBusinessKey()
    {
        var model = LoadModel("votes-users-list.json");
        var start = model.FlowNodes.Single(node => node.Id == 1);
        start.Variables.Add(new VariableModel
        {
            Id = 90,
            Name = "violationId",
            DataType = WorkflowVariableTypes.String,
            Required = true
        });
        start.BusinessKey = new BusinessKeyModel
        {
            Variable = "violationId",
            Uniqueness = "AlL"
        };
        var service = CreateService(out var repository);

        await service.CreateAsync(model, false, CancellationToken.None);

        Assert.Equal(BusinessKeyUniqueness.All,
            repository.Added!.Definition.FlowNodes.Single(node => node.Id == 1).BusinessKey!.Uniqueness);
    }

    [Theory]
    [InlineData("optional")]
    [InlineData("array")]
    [InlineData("number")]
    [InlineData("default")]
    public async Task CreateAsync_RejectsInvalidBusinessKeyVariable(string invalid)
    {
        var model = LoadModel("votes-users-list.json");
        var start = model.FlowNodes.Single(node => node.Id == 1);
        var variable = new VariableModel
        {
            Id = 90,
            Name = "violationId",
            DataType = invalid == "number" ? WorkflowVariableTypes.Number : WorkflowVariableTypes.String,
            Required = invalid != "optional",
            IsArray = invalid == "array",
            DefaultValue = invalid == "default" ? JsonSerializer.SerializeToElement("fallback") : null
        };
        start.Variables.Add(variable);
        start.BusinessKey = new BusinessKeyModel { Variable = variable.Name, Uniqueness = BusinessKeyUniqueness.Active };
        var service = CreateService(out _);

        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            service.CreateAsync(model, false, CancellationToken.None));

        Assert.Contains("required scalar string", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_RejectsPartialEntryCoverageAndMissingPolicy()
    {
        var model = LoadModel("votes-users-list.json");
        var start = model.FlowNodes.Single(node => node.Id == 1);
        start.Variables.Add(new VariableModel
        {
            Id = 90,
            Name = "violationId",
            DataType = WorkflowVariableTypes.String,
            Required = true
        });
        start.BusinessKey = new BusinessKeyModel
        {
            Variable = "violationId",
            Uniqueness = BusinessKeyUniqueness.Active
        };
        var secondStart = Clone(start);
        secondStart.Id = 50;
        secondStart.BusinessKey = null;
        model.FlowNodes.Add(secondStart);
        var secondFlow = Clone(model.SequenceFlows.Single(flow => flow.SourceRef == 1));
        secondFlow.Id = 500;
        secondFlow.SourceRef = 50;
        model.SequenceFlows.Add(secondFlow);
        var service = CreateService(out _);

        var coverage = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            service.CreateAsync(model, false, CancellationToken.None));
        Assert.Contains("must configure businessKey", coverage.Message, StringComparison.OrdinalIgnoreCase);

        secondStart.BusinessKey = new BusinessKeyModel
        {
            Variable = "violationId",
            Uniqueness = null!
        };
        var policy = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            service.CreateAsync(model, false, CancellationToken.None));
        Assert.Contains("unsupported businessKey.uniqueness", policy.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_NormalizesLegacyMessageStartVariablesIntoTypedMappings()
    {
        var model = CreateMessageStartModel();
        var start = model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsMessageStart(node.Type));
        start.Variables =
        [
            new VariableModel
            {
                Id = 90,
                Name = "amount",
                DataType = WorkflowVariableTypes.Number,
                Required = true,
                Validation = "amount > 0"
            },
            new VariableModel
            {
                Id = 91,
                Name = "country",
                DataType = WorkflowVariableTypes.String,
                DefaultValue = JsonSerializer.SerializeToElement("SA")
            },
            new VariableModel
            {
                Id = 92,
                Name = "requestId",
                DataType = WorkflowVariableTypes.String,
                Required = true
            },
            new VariableModel
            {
                Id = 93,
                Name = "unused",
                DataType = WorkflowVariableTypes.String
            }
        ];
        start.Message!.IdempotencyVariable = "requestId";
        start.Message.OutputMappings =
        [
            new MessageOutputMappingModel { Variable = "AMOUNT", Path = "order.amount" },
            new MessageOutputMappingModel { Variable = "raw", Path = "raw" }
        ];
        var service = CreateService(out var repository);

        await service.CreateAsync(model, false, CancellationToken.None);

        var saved = repository.Added!.Definition.FlowNodes.Single(node => BpmnFlowNodeTypes.IsMessageStart(node.Type));
        Assert.Empty(saved.Variables);
        var amount = saved.Message!.OutputMappings.Single(mapping => mapping.Variable == "amount");
        Assert.Equal(WorkflowVariableTypes.Number, amount.DataType);
        Assert.False(amount.IsArray);
        Assert.True(amount.Required);
        Assert.Equal("amount > 0", amount.Validation);
        var country = saved.Message.OutputMappings.Single(mapping => mapping.Variable == "country");
        Assert.Equal(string.Empty, country.Path);
        Assert.Equal("SA", country.DefaultValue!.Value.GetString());
        var raw = saved.Message.OutputMappings.Single(mapping => mapping.Variable == "raw");
        Assert.Equal(WorkflowVariableTypes.Json, raw.DataType);
        Assert.DoesNotContain(saved.Message.OutputMappings, mapping => mapping.Variable == "requestId");
        Assert.DoesNotContain(saved.Message.OutputMappings, mapping => mapping.Variable == "unused");
        Assert.Null(saved.Message.IdempotencyVariable);
        Assert.Equal(IdempotencyHeaders.Standard, saved.Idempotency!.HeaderName);
        Assert.Equal("requestId", saved.Idempotency.Variable);
        using var canonicalJson = JsonDocument.Parse(JsonSerializer.Serialize(saved));
        Assert.False(canonicalJson.RootElement.TryGetProperty("variables", out _));
        Assert.True(canonicalJson.RootElement.TryGetProperty("idempotency", out _));
        Assert.False(canonicalJson.RootElement.GetProperty("message").TryGetProperty("idempotencyVariable", out _));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("path")]
    [InlineData("type")]
    [InlineData("validation")]
    [InlineData("idempotency")]
    [InlineData("defaultType")]
    public async Task CreateAsync_RejectsInvalidTypedMessageStartMappings(string invalid)
    {
        var model = CreateMessageStartModel();
        var start = model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsMessageStart(node.Type));
        var mapping = start.Message!.OutputMappings.Single();
        if (invalid == "duplicate")
        {
            start.Message.OutputMappings.Add(new MessageOutputMappingModel
            {
                Variable = "VALUE",
                Path = "other",
                DataType = WorkflowVariableTypes.String,
                IsArray = false
            });
        }
        if (invalid == "path") mapping.Path = string.Empty;
        if (invalid == "type") mapping.DataType = "integer";
        if (invalid == "validation") mapping.Validation = "(";
        if (invalid == "idempotency")
        {
            start.Idempotency = new IdempotencyModel
            {
                HeaderName = IdempotencyHeaders.Standard,
                Variable = "Value"
            };
        }
        if (invalid == "defaultType")
        {
            mapping.Path = string.Empty;
            mapping.DataType = WorkflowVariableTypes.Number;
            mapping.DefaultValue = JsonSerializer.SerializeToElement(true);
        }
        var service = CreateService(out _);

        await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            service.CreateAsync(model, false, CancellationToken.None));
    }

    [Theory]
    [InlineData("blankHeader")]
    [InlineData("invalidHeader")]
    [InlineData("reservedHeader")]
    [InlineData("correlationHeader")]
    [InlineData("blankVariable")]
    [InlineData("businessKey")]
    public async Task CreateAsync_RejectsInvalidEntryIdempotencyConfiguration(string invalid)
    {
        var model = CreateMessageStartModel();
        var start = model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsMessageStart(node.Type));
        start.Idempotency = new IdempotencyModel
        {
            HeaderName = "X-Request-Id",
            Variable = "requestId"
        };

        if (invalid == "blankHeader") start.Idempotency.HeaderName = " ";
        if (invalid == "invalidHeader") start.Idempotency.HeaderName = "Bad Header";
        if (invalid == "reservedHeader") start.Idempotency.HeaderName = "Authorization";
        if (invalid == "correlationHeader") start.Idempotency.HeaderName = "x-correlation";
        if (invalid == "blankVariable") start.Idempotency.Variable = " ";
        if (invalid == "businessKey")
        {
            start.Idempotency.Variable = "value";
            start.BusinessKey = new BusinessKeyModel
            {
                Variable = "value",
                Uniqueness = BusinessKeyUniqueness.All
            };
        }

        await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_AcceptsCanonicalIdempotencyOnBothEntryTypes()
    {
        var model = LoadModel("votes-users-list.json");
        var normal = model.FlowNodes.Single(node => node.Id == model.InitialEventId);
        normal.Idempotency = new IdempotencyModel
        {
            HeaderName = "X-Request-Id",
            Variable = "normalRequestId"
        };
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 90,
            Name = "Webhook start",
            Type = BpmnFlowNodeTypes.MessageStartEvent,
            Idempotency = new IdempotencyModel
            {
                HeaderName = IdempotencyHeaders.Standard,
                Variable = "messageRequestId"
            },
            Message = new MessageCatchModel
            {
                ClientId = "client",
                ClientSecret = "secret",
                HeaderName = "X-Correlation",
                HeaderValue = "accepted"
            }
        });
        model.SequenceFlows.Add(new SequenceFlowModel
        {
            Id = 90,
            SourceRef = 90,
            TargetRef = 2
        });

        await CreateService(out _).CreateAsync(model, false, CancellationToken.None);
    }

    [Fact]
    public async Task CreateAsync_NormalizesLegacyServiceAndCatchMappingsToTypedContracts()
    {
        var model = CreateOutputMappingModel();
        var serviceNode = model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsServiceTask(node.Type));
        serviceNode.Service!.OutputMappings =
        [
            new ServiceOutputMappingModel { Variable = "DECISION", Path = "result.decision", Required = true },
            new ServiceOutputMappingModel { Variable = "rawService", Path = "raw" }
        ];
        var catchNode = model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsMessageCatch(node.Type));
        catchNode.Message!.OutputMappings =
        [
            new MessageOutputMappingModel { Variable = "decision", Path = "decision" },
            new MessageOutputMappingModel { Variable = "rawMessage", Path = "raw" }
        ];
        var service = CreateService(out var repository);

        await service.CreateAsync(model, false, CancellationToken.None);

        var savedService = repository.Added!.Definition.FlowNodes
            .Single(node => BpmnFlowNodeTypes.IsServiceTask(node.Type)).Service!;
        var decision = savedService.OutputMappings.Single(mapping => mapping.Variable == "decision");
        Assert.Equal(WorkflowVariableTypes.String, decision.DataType);
        Assert.False(decision.IsArray);
        var rawService = savedService.OutputMappings.Single(mapping => mapping.Variable == "rawService");
        Assert.Equal(WorkflowVariableTypes.Json, rawService.DataType);
        Assert.False(rawService.IsArray);

        var savedCatch = repository.Added.Definition.FlowNodes
            .Single(node => BpmnFlowNodeTypes.IsMessageCatch(node.Type)).Message!;
        Assert.Equal(WorkflowVariableTypes.String,
            savedCatch.OutputMappings.Single(mapping => mapping.Variable == "decision").DataType);
        Assert.Equal(WorkflowVariableTypes.Json,
            savedCatch.OutputMappings.Single(mapping => mapping.Variable == "rawMessage").DataType);
    }

    [Theory]
    [InlineData("service", "duplicate")]
    [InlineData("service", "path")]
    [InlineData("service", "type")]
    [InlineData("service", "default")]
    [InlineData("service", "validation")]
    [InlineData("service", "process")]
    [InlineData("catch", "duplicate")]
    [InlineData("catch", "path")]
    [InlineData("catch", "type")]
    [InlineData("catch", "default")]
    [InlineData("catch", "validation")]
    [InlineData("catch", "process")]
    public async Task CreateAsync_RejectsInvalidTypedServiceAndCatchMappings(string owner, string invalid)
    {
        var model = CreateOutputMappingModel();
        var isService = owner == "service";
        var serviceMapping = model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsServiceTask(node.Type))
            .Service!.OutputMappings[0];
        var catchMapping = model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsMessageCatch(node.Type))
            .Message!.OutputMappings[0];

        if (invalid == "duplicate")
        {
            if (isService)
            {
                model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsServiceTask(node.Type)).Service!.OutputMappings.Add(
                    new ServiceOutputMappingModel
                    {
                        Variable = "DECISION", Path = "duplicate", DataType = WorkflowVariableTypes.String, IsArray = false
                    });
            }
            else
            {
                model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsMessageCatch(node.Type)).Message!.OutputMappings.Add(
                    new MessageOutputMappingModel
                    {
                        Variable = "DECISION", Path = "duplicate", DataType = WorkflowVariableTypes.String, IsArray = false
                    });
            }
        }
        else
        {
            if (isService)
            {
                if (invalid == "path") serviceMapping.Path = string.Empty;
                if (invalid == "type") serviceMapping.DataType = "integer";
                if (invalid == "default")
                {
                    serviceMapping.Path = string.Empty;
                    serviceMapping.DefaultValue = JsonSerializer.SerializeToElement(true);
                }
                if (invalid == "validation") serviceMapping.Validation = "(";
                if (invalid == "process") serviceMapping.DataType = WorkflowVariableTypes.Number;
            }
            else
            {
                if (invalid == "path") catchMapping.Path = string.Empty;
                if (invalid == "type") catchMapping.DataType = "integer";
                if (invalid == "default")
                {
                    catchMapping.Path = string.Empty;
                    catchMapping.DefaultValue = JsonSerializer.SerializeToElement(true);
                }
                if (invalid == "validation") catchMapping.Validation = "(";
                if (invalid == "process") catchMapping.DataType = WorkflowVariableTypes.Number;
            }
        }

        var service = CreateService(out _);
        await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            service.CreateAsync(model, false, CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(ValidTypedOutputDefaults))]
    public async Task CreateAsync_AcceptsEveryTypedServiceAndCatchDefault(
        string owner,
        string dataType,
        bool isArray,
        string defaultJson)
    {
        var model = CreateOutputMappingModel();
        using var document = JsonDocument.Parse(defaultJson);
        var variable = $"typed_{dataType}_{(isArray ? "array" : "scalar")}";
        if (owner == "service")
        {
            model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsServiceTask(node.Type))
                .Service!.OutputMappings.Add(new ServiceOutputMappingModel
                {
                    Variable = variable,
                    Path = string.Empty,
                    DataType = dataType,
                    IsArray = isArray,
                    DefaultValue = document.RootElement.Clone()
                });
        }
        else
        {
            model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsMessageCatch(node.Type))
                .Message!.OutputMappings.Add(new MessageOutputMappingModel
                {
                    Variable = variable,
                    Path = string.Empty,
                    DataType = dataType,
                    IsArray = isArray,
                    DefaultValue = document.RootElement.Clone()
                });
        }

        await CreateService(out _).CreateAsync(model, false, CancellationToken.None);
    }

    private static WorkflowModel CreateMessageStartModel()
    {
        var model = LoadModel("votes-users-list.json");
        var start = model.FlowNodes.Single(node => node.Id == model.InitialEventId);
        start.Type = BpmnFlowNodeTypes.MessageStartEvent;
        start.Roles = [];
        start.Variables = [];
        start.Message = new MessageCatchModel
        {
            ClientId = "client",
            ClientSecret = "secret",
            HeaderName = "X-Correlation",
            HeaderValue = "accepted",
            OutputMappings =
            [
                new MessageOutputMappingModel
                {
                    Variable = "value",
                    Path = "value",
                    DataType = WorkflowVariableTypes.String,
                    IsArray = false,
                    Required = true
                }
            ]
        };
        model.InitialEventId = null;
        return model;
    }

    internal static WorkflowModel CreateOutputMappingModel()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = "typed-output-" + suffix,
            Name = "Typed output " + suffix,
            InitialEventId = 1,
            Variables =
            [
                new VariableModel
                {
                    Id = 1,
                    Name = "decision",
                    DataType = WorkflowVariableTypes.String,
                    DefaultValue = JsonSerializer.SerializeToElement("pending"),
                    Validation = "decision != 'forbidden'"
                }
            ],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Service",
                    Type = BpmnFlowNodeTypes.ServiceTask,
                    Service = new ServiceTaskModel
                    {
                        Url = "https://tests.local/typed",
                        OutputMappings =
                        [
                            new ServiceOutputMappingModel
                            {
                                Variable = "decision",
                                Path = "result.decision",
                                DataType = WorkflowVariableTypes.String,
                                IsArray = false,
                                Required = true
                            }
                        ]
                    }
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Message",
                    Type = BpmnFlowNodeTypes.IntermediateMessageCatchEvent,
                    Message = new MessageCatchModel
                    {
                        ClientId = "client",
                        ClientSecret = "secret",
                        HeaderName = "X-Correlation",
                        HeaderValue = "accepted",
                        OutputMappings =
                        [
                            new MessageOutputMappingModel
                            {
                                Variable = "decision",
                                Path = "decision",
                                DataType = WorkflowVariableTypes.String,
                                IsArray = false,
                                Required = true
                            }
                        ]
                    }
                },
                new FlowNodeModel { Id = 4, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4 }
            ]
        };
    }

    private static WorkflowDefinitionService CreateService(out CapturingDefinitionRepository repository)
    {
        repository = new CapturingDefinitionRepository();
        return new WorkflowDefinitionService(
            repository,
            new ParseOnlyScriptEvaluator(),
            NullLogger<WorkflowDefinitionService>.Instance);
    }

    internal static WorkflowModel LoadModel(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        return JsonSerializer.Deserialize<WorkflowModel>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("Workflow fixture did not deserialize.");
    }

    private static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))
        ?? throw new InvalidOperationException("Fixture clone failed.");

    private sealed class ParseOnlyScriptEvaluator : IScriptEvaluator
    {
        public ScriptResult Evaluate(string script, IScriptContext context, CancellationToken cancellationToken) =>
            new(true, null);

        public bool IsValid(string script, out string? error)
        {
            error = null;
            return true;
        }
    }

    private sealed class CapturingDefinitionRepository : IWorkflowDefinitionRepository
    {
        public Task<bool> IsBusinessKeyScopeActiveAsync(string workflowKey, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public WorkflowDefinitionRecord? Added { get; private set; }

        public Task<IReadOnlyList<WorkflowDefinitionRecord>> ListLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkflowDefinitionRecord>>([]);

        public Task<IReadOnlyList<WorkflowDefinitionRecord>> ListVersionsByKeyAsync(
            string workflowKey,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkflowDefinitionRecord>>([]);

        public Task<WorkflowDefinitionRecord?> GetAsync(long id, CancellationToken cancellationToken) =>
            Task.FromResult<WorkflowDefinitionRecord?>(null);

        public Task<WorkflowDefinitionRecord?> GetDefaultByWorkflowKeyAsync(
            string workflowKey,
            CancellationToken cancellationToken) =>
            Task.FromResult<WorkflowDefinitionRecord?>(null);

        public Task<int> GetLatestVersionAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task<WorkflowDefinitionRecord> AddAsync(
            string name,
            int version,
            WorkflowModel definition,
            bool isPublished,
            CancellationToken cancellationToken)
        {
            Added = new WorkflowDefinitionRecord(
                1,
                name,
                definition.Id,
                version,
                definition,
                isPublished,
                false,
                DateTimeOffset.UtcNow);
            return Task.FromResult(Added);
        }

        public Task<bool> SetPublishedAsync(long id, bool isPublished, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> SetDefaultAsync(long id, bool isDefault, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<bool> DeleteAsync(long id, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
}
