using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Flowbit.Infrastructure.Scripting;
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

            yield return new object[] { owner, WorkflowVariableTypes.Number, true, "[\"1\",\"2.5\"]" };
            yield return new object[] { owner, WorkflowVariableTypes.Boolean, true, "[\"true\",\"false\"]" };
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

    [Theory]
    [InlineData(BpmnFlowNodeTypes.EndEvent)]
    [InlineData(BpmnFlowNodeTypes.ErrorEndEvent)]
    public async Task CreateAsync_RejectsOutgoingFlowsFromTerminalEvents(string terminalType)
    {
        var model = LoadModel("votes-users-list.json");
        var terminal = model.FlowNodes.First(node => BpmnFlowNodeTypes.IsEnd(node.Type));
        terminal.Type = terminalType;
        terminal.ErrorCode = terminalType == BpmnFlowNodeTypes.ErrorEndEvent ? "INVALID_TERMINAL_FLOW" : null;
        model.SequenceFlows.Add(new SequenceFlowModel
        {
            Id = model.SequenceFlows.Max(flow => flow.Id) + 1,
            Name = "Invalid terminal flow",
            SourceRef = terminal.Id,
            TargetRef = terminal.Id
        });

        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));

        Assert.Contains($"End event #{terminal.Id} cannot have outgoing sequence flows", error.Message);
    }

    [Theory]
    [InlineData(BpmnFlowNodeTypes.EndEvent)]
    [InlineData(BpmnFlowNodeTypes.ErrorEndEvent)]
    public async Task CreateAsync_RejectsTerminalEventsWithoutIncomingFlows(string terminalType)
    {
        var model = CreateTerminalModel(terminalType);
        model.FlowNodes.Add(new FlowNodeModel { Id = 4, Name = "Reachable end", Type = BpmnFlowNodeTypes.EndEvent });
        model.SequenceFlows.Single(flow => flow.Id == 201).TargetRef = 4;

        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));

        Assert.Contains("must have at least one incoming", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(BpmnFlowNodeTypes.EndEvent)]
    [InlineData(BpmnFlowNodeTypes.ErrorEndEvent)]
    public async Task CreateAsync_AcceptsMultipleIncomingFlowsToTerminalEvents(string terminalType)
    {
        var model = CreateTerminalModel(terminalType);
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 202, Name = "Also finish", SourceRef = 2, TargetRef = 3 });

        await CreateService(out _).CreateAsync(model, false, CancellationToken.None);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("characters")]
    [InlineData("codeLength")]
    [InlineData("descriptionLength")]
    public async Task CreateAsync_RejectsInvalidErrorEndFaultMetadata(string invalid)
    {
        var model = CreateTerminalModel(BpmnFlowNodeTypes.ErrorEndEvent);
        var terminal = model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsErrorEnd(node.Type));
        if (invalid == "missing") terminal.ErrorCode = "   ";
        if (invalid == "characters") terminal.ErrorCode = "BAD CODE";
        if (invalid == "codeLength") terminal.ErrorCode = new string('A', ErrorEndConstraints.MaxCodeLength + 1);
        if (invalid == "descriptionLength") terminal.ErrorDescription = new string('x', ErrorEndConstraints.MaxDescriptionLength + 1);

        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));

        Assert.Contains(invalid == "descriptionLength" ? "errorDescription" : "errorCode", error.Message);
    }

    [Fact]
    public async Task CreateAsync_TrimsErrorEndMetadataAndAllowsSharedCodes()
    {
        var model = CreateTerminalModel(BpmnFlowNodeTypes.ErrorEndEvent);
        var first = model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsErrorEnd(node.Type));
        first.ErrorCode = "  SHARED.FAULT_1  ";
        first.ErrorDescription = "  A public description.  ";
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 4,
            Name = "Alternate fault",
            Type = BpmnFlowNodeTypes.ErrorEndEvent,
            ErrorCode = "SHARED.FAULT_1"
        });
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 202, Name = "Alternate", SourceRef = 2, TargetRef = 4 });

        await CreateService(out var repository).CreateAsync(model, false, CancellationToken.None);

        var saved = repository.Added!.Definition.FlowNodes.Single(node => node.Id == 3);
        Assert.Equal("SHARED.FAULT_1", saved.ErrorCode);
        Assert.Equal("A public description.", saved.ErrorDescription);
    }

    [Theory]
    [InlineData(BpmnFlowNodeTypes.EndEvent)]
    [InlineData(BpmnFlowNodeTypes.ErrorEndEvent)]
    public void Normalize_ClearsFieldsThatDoNotBelongToTerminalEvents(string terminalType)
    {
        var model = LoadModel("votes-users-list.json");
        var terminal = model.FlowNodes.First(node => BpmnFlowNodeTypes.IsEnd(node.Type));
        terminal.Type = terminalType;
        terminal.RequiresClaim = true;
        terminal.ClaimMode = ClaimModes.FromNode;
        terminal.InheritClaimFromNodeId = 2;
        terminal.Roles = ["Manager"];
        terminal.Variables =
        [
            new VariableModel { Id = 999, Name = "terminalInput", DataType = WorkflowVariableTypes.String }
        ];
        terminal.Service = new ServiceTaskModel
        {
            Url = "https://should-not-survive.invalid",
            Headers = [new ServiceHeaderModel { Name = "Authorization", Value = "secret-terminal-token" }]
        };
        terminal.Message = new MessageCatchModel
        {
            ClientId = "terminal-client",
            ClientSecret = "secret-terminal-client",
            HeaderName = "X-Terminal",
            HeaderValue = "terminal"
        };
        terminal.ScriptFormat = ScriptFormats.JavaScript;
        terminal.Assignments = [new AssignmentModel { Variable = "result", Expression = "1" }];
        terminal.Script = "execution.setVariable('result', 1);";
        terminal.AssigneeExpression = "'alice'";
        terminal.MultiInstance = new MultiInstanceModel();
        terminal.AttachedToRef = 2;
        terminal.ErrorVariable = "terminalError";
        terminal.BusinessKey = new BusinessKeyModel { Variable = "terminalKey", Uniqueness = BusinessKeyUniqueness.Active };
        terminal.Idempotency = new IdempotencyModel { HeaderName = "X-Terminal-Key", Variable = "terminalRequest" };
        terminal.ErrorCode = "  TERMINAL_FAULT  ";
        terminal.ErrorDescription = "  Terminal description.  ";

        WorkflowModelMigrator.Normalize(model);

        Assert.False(terminal.RequiresClaim);
        Assert.Equal(ClaimModes.Fresh, terminal.ClaimMode);
        Assert.Null(terminal.InheritClaimFromNodeId);
        Assert.Empty(terminal.Roles);
        Assert.Empty(terminal.Variables);
        Assert.Null(terminal.Service);
        Assert.Null(terminal.Message);
        Assert.Equal(ScriptFormats.NCalc, terminal.ScriptFormat);
        Assert.Empty(terminal.Assignments);
        Assert.Null(terminal.Script);
        Assert.Null(terminal.AssigneeExpression);
        Assert.Null(terminal.MultiInstance);
        Assert.Null(terminal.AttachedToRef);
        Assert.Null(terminal.ErrorVariable);
        Assert.Null(terminal.BusinessKey);
        Assert.Null(terminal.Idempotency);
        if (terminalType == BpmnFlowNodeTypes.ErrorEndEvent)
        {
            Assert.Equal("TERMINAL_FAULT", terminal.ErrorCode);
            Assert.Equal("Terminal description.", terminal.ErrorDescription);
        }
        else
        {
            Assert.Null(terminal.ErrorCode);
            Assert.Null(terminal.ErrorDescription);
        }
        var json = JsonSerializer.Serialize(model);
        Assert.DoesNotContain("secret-terminal", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_MapsLegacyErrorEndEventWithoutTurningItIntoAUserTask()
    {
        var model = new WorkflowModel
        {
            Id = "legacy-error-end",
            Name = "Legacy error end",
            LegacySteps =
            [
                new LegacyStepModel { Id = 1, Name = "Fault", Type = BpmnFlowNodeTypes.ErrorEndEvent }
            ]
        };

        WorkflowModelMigrator.Normalize(model);

        var terminal = Assert.Single(model.FlowNodes);
        Assert.Equal(BpmnFlowNodeTypes.ErrorEndEvent, terminal.Type);
        Assert.True(BpmnFlowNodeTypes.IsEnd(terminal.Type));
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

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateAsync_RejectsMissingWorkflowKey(string workflowKey)
    {
        var model = LoadModel("votes-users-list.json");
        model.Id = workflowKey;

        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));

        Assert.Contains("workflow id", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_RejectsWorkflowKeyOverThreeHundredUnicodeScalars()
    {
        var model = LoadModel("votes-users-list.json");
        model.Id = string.Concat(Enumerable.Repeat("😀", 301));

        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));

        Assert.Contains("300 Unicode scalar", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_RejectsInvalidInitialEventInsteadOfSilentlyReplacingIt()
    {
        var model = LoadModel("votes-users-list.json");
        model.InitialEventId = 999;

        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));

        Assert.Contains("initialEventId", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_RejectsIncomingOrConditionalStartFlows()
    {
        var model = LoadModel("votes-users-list.json");
        var incoming = Clone(model.SequenceFlows.First(flow => flow.SourceRef == 2));
        incoming.Id = 999;
        incoming.TargetRef = model.InitialEventId!.Value;
        model.SequenceFlows.Add(incoming);

        var incomingError = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));
        Assert.Contains("cannot have incoming", incomingError.Message, StringComparison.OrdinalIgnoreCase);

        model.SequenceFlows.Remove(incoming);
        model.SequenceFlows.Single(flow => flow.SourceRef == model.InitialEventId).Condition = "true";
        var conditionError = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));
        Assert.Contains("must be unconditional", conditionError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_RejectsEntryProcessVariableCollision()
    {
        var model = LoadModel("votes-users-list.json");
        model.FlowNodes.Single(node => node.Id == model.InitialEventId).Variables.Add(new VariableModel
        {
            Id = 99,
            Name = "VOTERS",
            DataType = WorkflowVariableTypes.String
        });

        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));

        Assert.Contains("collides with a process variable", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_RejectsInvalidAndRequiredDefaults()
    {
        var model = LoadModel("votes-users-list.json");
        var start = model.FlowNodes.Single(node => node.Id == model.InitialEventId);
        start.Variables.Add(new VariableModel
        {
            Id = 98,
            Name = "score",
            DataType = WorkflowVariableTypes.Number,
            DefaultValue = JsonSerializer.SerializeToElement("not-a-number")
        });

        var invalidType = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));
        Assert.Contains("defaultValue", invalidType.Message, StringComparison.OrdinalIgnoreCase);

        start.Variables[^1].DefaultValue = JsonSerializer.SerializeToElement(1);
        start.Variables[^1].Required = true;
        var requiredDefault = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));
        Assert.Contains("cannot define a defaultValue", requiredDefault.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_AcceptsNullableProcessVariablesAndStillValidatesConcreteDefaults()
    {
        var model = LoadModel("votes-users-list.json");
        model.Variables.Add(new VariableModel
        {
            Id = 98,
            Name = "optionalScore",
            DataType = WorkflowVariableTypes.Number,
            Nullable = true,
            Validation = "optionalScore > 0"
        });
        model.Variables.Add(new VariableModel
        {
            Id = 99,
            Name = "optionalReviewers",
            DataType = WorkflowVariableTypes.String,
            IsArray = true,
            Nullable = true,
            DefaultValue = JsonSerializer.SerializeToElement<string[]?>(null)
        });

        await CreateService(out _).CreateAsync(model, false, CancellationToken.None);

        model.Variables.Single(variable => variable.Name == "optionalScore").DefaultValue =
            JsonSerializer.SerializeToElement("not-a-number");
        var invalidConcreteDefault = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));
        Assert.Contains("defaultValue", invalidConcreteDefault.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_RejectsMissingNonNullableProcessDefaultAndNullableInputVariables()
    {
        var model = LoadModel("votes-users-list.json");
        model.Variables.Add(new VariableModel
        {
            Id = 98,
            Name = "missingInitialValue",
            DataType = WorkflowVariableTypes.String
        });

        var missingDefault = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));
        Assert.Contains("must have a defaultValue", missingDefault.Message, StringComparison.OrdinalIgnoreCase);

        model.Variables.RemoveAt(model.Variables.Count - 1);
        var start = model.FlowNodes.Single(node => node.Id == model.InitialEventId);
        start.Variables.Add(new VariableModel
        {
            Id = 98,
            Name = "nullableInput",
            DataType = WorkflowVariableTypes.String,
            Nullable = true
        });
        var nullableStart = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));
        Assert.Contains("only for process variables", nullableStart.Message, StringComparison.OrdinalIgnoreCase);

        start.Variables.Clear();
        model.SequenceFlows.First(flow => flow.SourceRef == 2 && flow.IsSelectable).Variables.Add(new VariableModel
        {
            Id = 98,
            Name = "nullableActionInput",
            DataType = WorkflowVariableTypes.String,
            Nullable = true
        });
        var nullableFlow = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));
        Assert.Contains("only for process variables", nullableFlow.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateNewVersionAsync_PreservesSourceWorkflowKeyAcrossRenameAndBodyIdChange()
    {
        var sourceModel = LoadModel("votes-users-list.json");
        var service = CreateService(out var repository);
        repository.Source = new WorkflowDefinitionRecord(
            42,
            sourceModel.Name,
            sourceModel.Id,
            3,
            sourceModel,
            true,
            true,
            DateTimeOffset.UtcNow);
        var updated = Clone(sourceModel);
        updated.Id = "different-family";
        updated.Name = "Renamed workflow";

        var created = await service.CreateNewVersionAsync(42, updated, false, CancellationToken.None);

        Assert.NotNull(created);
        Assert.Equal(sourceModel.Id, repository.Added!.WorkflowKey);
        Assert.Equal(sourceModel.Id, repository.Added.Definition.Id);
        Assert.Equal("Renamed workflow", repository.Added.Name);
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
        start.Message!.IdempotencyVariable = " requestId ";
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

    [Fact]
    public async Task CreateAsync_RejectsTrimmedLegacyIdempotencyVariableCollisionWithProcessVariable()
    {
        var model = CreateMessageStartModel();
        var start = model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsMessageStart(node.Type));
        start.Variables.Add(new VariableModel
        {
            Id = 90,
            Name = "requestId",
            DataType = WorkflowVariableTypes.String,
            Required = true
        });
        start.Message!.IdempotencyVariable = " requestId ";
        model.Variables.Add(new VariableModel
        {
            Id = 91,
            Name = "requestId",
            DataType = WorkflowVariableTypes.String,
            DefaultValue = JsonSerializer.SerializeToElement("seed")
        });

        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));

        Assert.Contains("collides with a process variable", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_RejectsNullMessageStartMappingAsDomainError()
    {
        var model = CreateMessageStartModel();
        model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsMessageStart(node.Type))
            .Message!.OutputMappings.Add(null!);

        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));

        Assert.Contains("null output mapping", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "X-Delivery-Id")]
    public async Task CreateAsync_RejectsCatchOnlyIdempotencyMetadataOnMessageStart(
        bool enabled,
        string? headerName)
    {
        var model = CreateMessageStartModel();
        var message = model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsMessageStart(node.Type)).Message!;
        message.DeliveryIdempotency = enabled;
        message.DeliveryIdempotencyHeaderName = headerName;

        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));

        Assert.Contains("node-level idempotency", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_RequiresCaseSensitiveUniqueExternalIdsForMultipleMessageStarts()
    {
        var model = CreateMessageStartModel();
        var first = model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsMessageStart(node.Type));
        first.ExternalId = "webhook";
        var second = Clone(first);
        second.Id = 90;
        second.ExternalId = "WEBHOOK";
        model.FlowNodes.Add(second);
        model.SequenceFlows.Add(new SequenceFlowModel
        {
            Id = 900,
            SourceRef = second.Id,
            TargetRef = model.SequenceFlows.Single(flow => flow.SourceRef == first.Id).TargetRef
        });

        await CreateService(out _).CreateAsync(model, false, CancellationToken.None);

        second.ExternalId = null;
        var missing = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));
        Assert.Contains("must have an externalId", missing.Message, StringComparison.OrdinalIgnoreCase);

        second.ExternalId = first.ExternalId;
        var duplicate = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));
        Assert.Contains("duplicated", duplicate.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MessageStartSample_IsCanonicalAndValid()
    {
        var model = LoadModel("workflow-message-start.json");

        await CreateService(out var repository).CreateAsync(model, false, CancellationToken.None);

        var saved = repository.Added!.Definition;
        var start = saved.FlowNodes.Single(node => BpmnFlowNodeTypes.IsMessageStart(node.Type));
        Assert.Null(saved.InitialEventId);
        Assert.Equal("message-start", start.ExternalId);
        Assert.Equal(IdempotencyHeaders.Standard, start.Idempotency!.HeaderName);
        Assert.Equal("requestId", start.Idempotency.Variable);
        Assert.Null(start.Message!.IdempotencyVariable);
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

    [Fact]
    public async Task CreateAsync_DefaultsLegacyServiceConnectorAndCanonicalizesRestCasing()
    {
        var legacy = CreateOutputMappingModel();
        var legacyService = legacy.FlowNodes.Single(node => BpmnFlowNodeTypes.IsServiceTask(node.Type)).Service!;
        Assert.Equal(ServiceConnectorTypes.Rest, legacyService.Type);

        legacyService.Type = "REST";
        await CreateService(out var repository).CreateAsync(legacy, false, CancellationToken.None);

        var saved = repository.Added!.Definition.FlowNodes
            .Single(node => BpmnFlowNodeTypes.IsServiceTask(node.Type)).Service!;
        Assert.Equal(ServiceConnectorTypes.Rest, saved.Type);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("soap")]
    public async Task CreateAsync_RejectsMissingOrUnsupportedServiceConnector(string? connectorType)
    {
        var model = CreateOutputMappingModel();
        model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsServiceTask(node.Type)).Service!.Type = connectorType;

        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));

        Assert.Contains("unsupported connector type", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("ftp://tests.local/work")]
    [InlineData("https://user:password@tests.local/work")]
    [InlineData("https://tests.local/work#fragment")]
    public async Task CreateAsync_RejectsUnsafeLiteralServiceUrls(string url)
    {
        var model = CreateOutputMappingModel();
        model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsServiceTask(node.Type)).Service!.Url = url;

        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));

        Assert.Contains("absolute HTTP(S)", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_AcceptsTemplatedServiceUrlForRuntimeResolution()
    {
        var model = CreateOutputMappingModel();
        model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsServiceTask(node.Type)).Service!.Url =
            "${serviceBaseUrl}/work";

        await CreateService(out _).CreateAsync(model, false, CancellationToken.None);
    }

    [Fact]
    public async Task CreateAsync_AllowsHttpServiceUrlWithoutAHostAllowlist()
    {
        var model = CreateOutputMappingModel();
        model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsServiceTask(node.Type)).Service!.Url =
            "http://internal-service.local/work";

        await CreateService(out _).CreateAsync(model, false, CancellationToken.None);
    }

    [Theory]
    [InlineData("Bad Header")]
    [InlineData("Host")]
    [InlineData("Content-Length")]
    public async Task CreateAsync_RejectsInvalidOrFramingServiceHeaders(string name)
    {
        var model = CreateOutputMappingModel();
        model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsServiceTask(node.Type)).Service!.Headers =
            [new ServiceHeaderModel { Name = name, Value = "value" }];

        await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_RejectsServiceHeaderLineBreaks()
    {
        var model = CreateOutputMappingModel();
        model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsServiceTask(node.Type)).Service!.Headers =
            [new ServiceHeaderModel { Name = "X-Test", Value = "safe\r\nInjected: true" }];

        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));

        Assert.Contains("line breaks", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_RejectsInvalidServiceContentType()
    {
        var model = CreateOutputMappingModel();
        model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsServiceTask(node.Type)).Service!.Headers =
            [new ServiceHeaderModel { Name = "Content-Type", Value = "not a media type" }];

        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));

        Assert.Contains("Content-Type", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_AcceptsTemplatedServiceContentTypeForRuntimeValidation()
    {
        var model = CreateOutputMappingModel();
        model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsServiceTask(node.Type)).Service!.Headers =
            [new ServiceHeaderModel { Name = "Content-Type", Value = "${contentType}" }];

        await CreateService(out _).CreateAsync(model, false, CancellationToken.None);
    }

    [Fact]
    public async Task CreateAsync_EnforcesConfiguredMaximumServiceTimeout()
    {
        var model = CreateOutputMappingModel();
        model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsServiceTask(node.Type)).Service!.TimeoutSeconds = 11;

        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _, new ServiceTaskOptions { MaxTimeoutSeconds = 10 }).CreateAsync(
                model,
                false,
                CancellationToken.None));

        Assert.Contains("between 1 and 10", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_ValidatesStatusVariableContractAndMappingCollision()
    {
        var model = CreateOutputMappingModel();
        var service = model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsServiceTask(node.Type)).Service!;
        service.StatusVariable = "decision";

        var typeError = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));
        Assert.Contains("scalar number", typeError.Message, StringComparison.OrdinalIgnoreCase);

        service.StatusVariable = "httpStatus";
        service.OutputMappings.Add(new ServiceOutputMappingModel
        {
            Variable = "HTTPSTATUS",
            Path = "status",
            DataType = WorkflowVariableTypes.Number,
            IsArray = false
        });
        var collision = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));
        Assert.Contains("cannot also be an output mapping", collision.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_CoalescesNullServiceCollectionsAndRejectsNullEntriesCleanly()
    {
        var model = CreateOutputMappingModel();
        var service = model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsServiceTask(node.Type)).Service!;
        service.Headers = null!;
        service.OutputMappings = null!;

        await CreateService(out _).CreateAsync(model, false, CancellationToken.None);

        model = CreateOutputMappingModel();
        service = model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsServiceTask(node.Type)).Service!;
        service.Headers = [null!];
        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));
        Assert.Contains("null header", error.Message, StringComparison.OrdinalIgnoreCase);

        model = CreateOutputMappingModel();
        service = model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsServiceTask(node.Type)).Service!;
        service.OutputMappings = [null!];
        error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));
        Assert.Contains("null output mapping", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Normalize_ServiceTaskClearsStaleConfigurationFromOtherNodeTypes()
    {
        var model = CreateOutputMappingModel();
        var node = model.FlowNodes.Single(candidate => BpmnFlowNodeTypes.IsServiceTask(candidate.Type));
        node.ScriptFormat = ScriptFormats.JavaScript;
        node.Script = "execution.setVariable('secret', true);";
        node.Assignments = [new AssignmentModel { Variable = "secret", Expression = "true" }];
        node.AssigneeExpression = "'alice'";
        node.AttachedToRef = 99;
        node.ErrorVariable = "staleError";
        node.Message = new MessageCatchModel { ClientSecret = "stale-secret" };

        WorkflowModelMigrator.Normalize(model);

        Assert.Equal(ScriptFormats.NCalc, node.ScriptFormat);
        Assert.Null(node.Script);
        Assert.Empty(node.Assignments);
        Assert.Null(node.AssigneeExpression);
        Assert.Null(node.AttachedToRef);
        Assert.Null(node.ErrorVariable);
        Assert.Null(node.Message);
    }

    [Fact]
    public async Task CreateAsync_RejectsBoundaryErrorVariableCollisionWithServiceOutputs()
    {
        var model = CreateOutputMappingModel();
        var service = model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsServiceTask(node.Type)).Service!;
        service.StatusVariable = "sharedResult";
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 5,
            Name = "Service error",
            Type = BpmnFlowNodeTypes.ErrorBoundaryEvent,
            AttachedToRef = 2,
            ErrorVariable = "SHAREDRESULT"
        });
        model.SequenceFlows.Add(new SequenceFlowModel
        {
            Id = 501,
            Name = "Handle service error",
            SourceRef = 5,
            TargetRef = 4
        });

        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));

        Assert.Contains("collides with a host service output target", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("selectable")]
    [InlineData("default")]
    [InlineData("condition")]
    [InlineData("roles")]
    [InlineData("variables")]
    [InlineData("bypass")]
    [InlineData("completion")]
    [InlineData("cancel")]
    public async Task CreateAsync_RejectsIgnoredErrorBoundaryFlowMetadata(string metadata)
    {
        var model = CreateOutputMappingModel();
        var flow = AddErrorBoundary(model);
        if (metadata == "selectable") flow.IsSelectable = false;
        if (metadata == "default") flow.IsDefault = true;
        if (metadata == "condition") flow.Condition = "false";
        if (metadata == "roles") flow.Roles = ["Operator"];
        if (metadata == "variables") flow.Variables = [new VariableModel { Id = 91, Name = "reason" }];
        if (metadata == "bypass") flow.CanActWithoutClaim = true;
        if (metadata == "completion") flow.CompletionCondition = "true";
        if (metadata == "cancel") flow.CancelRemainingInstances = true;

        await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_AcceptsUnconditionalErrorBoundaryFlow()
    {
        var model = CreateOutputMappingModel();
        AddErrorBoundary(model);

        await CreateService(out _).CreateAsync(model, false, CancellationToken.None);
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

    [Fact]
    public void Normalize_CanonicalizesClaimBypassAndAllRoleCollections()
    {
        var task = new FlowNodeModel
        {
            Id = 2,
            Name = "Review",
            Type = BpmnFlowNodeTypes.UserTask,
            RequiresClaim = true,
            Roles = null!,
            Variables = null!
        };
        var flow = new SequenceFlowModel
        {
            Id = 201,
            SourceRef = 2,
            TargetRef = 3,
            Roles = [" Reviewer ", "reviewer", "", "Approver"],
            Variables = null!,
            CanActWithoutClaim = true,
            CanActWithoutClaimRoles = [" Supervisor ", "supervisor", ""]
        };
        var model = new WorkflowModel
        {
            Id = "normalize-bypass",
            Name = "Normalize bypass",
            FlowNodes = [task],
            SequenceFlows = [flow],
            CancelRoles = [" Admin ", "admin", ""],
            UnclaimRoles = null!,
            TaskAssignmentRoles = [" Manager ", "manager"]
        };

        WorkflowModelMigrator.Normalize(model);

        Assert.Empty(task.Roles);
        Assert.Empty(task.Variables);
        Assert.Equal(new[] { "Reviewer", "Approver" }, flow.Roles);
        Assert.Equal(new[] { "Supervisor" }, flow.CanActWithoutClaimRoles);
        Assert.Empty(flow.Variables);
        Assert.Equal(new[] { "Admin" }, model.CancelRoles);
        Assert.Empty(model.UnclaimRoles);
        Assert.Equal(new[] { "Manager" }, model.TaskAssignmentRoles);

        task.RequiresClaim = false;
        WorkflowModelMigrator.Normalize(model);
        Assert.False(flow.CanActWithoutClaim);
        Assert.Empty(flow.CanActWithoutClaimRoles);
    }

    [Fact]
    public async Task CreateAsync_RejectsClaimBypassOnNonUserAndEngineOnlyFlows()
    {
        var nonUser = CreateOutputMappingModel();
        nonUser.SequenceFlows.Single(flow => flow.SourceRef == 1).CanActWithoutClaim = true;
        var nonUserError = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(nonUser, false, CancellationToken.None));
        Assert.Contains("not a user task", nonUserError.Message, StringComparison.OrdinalIgnoreCase);

        var engineOnly = LoadModel("votes-users-list.json");
        var fallback = engineOnly.SequenceFlows.Single(flow => !flow.IsSelectable);
        fallback.CanActWithoutClaim = true;
        fallback.CanActWithoutClaimRoles = ["Supervisor"];
        var engineOnlyError = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(engineOnly, false, CancellationToken.None));
        Assert.Contains("engine-only", engineOnlyError.Message, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public async Task CreateAsync_AllowsFlowInfoInExclusiveGatewayCondition()
    {
        var model = BuildGatewayFlowInfoModel();

        await CreateService(out _).CreateAsync(model, false, CancellationToken.None);
    }

    [Fact]
    public async Task CreateAsync_AllowsFlowInfoInMultiInstanceCompletionCondition()
    {
        var model = LoadModel("votes-users-list.json");
        model.SequenceFlows.Single(flow => flow.Id == 201).CompletionCondition =
            "CountFlow(201) >= requiredApprovals and FlowInfo(201, 'actions.count') >= 0";

        await CreateService(out _).CreateAsync(model, false, CancellationToken.None);
    }

    [Fact]
    public async Task CreateAsync_AllowsFlowInfoInNCalcScriptAssignment()
    {
        var model = BuildScriptFlowInfoModel();

        await CreateService(out _).CreateAsync(model, false, CancellationToken.None);
    }

    [Theory]
    [InlineData("FlowInfo(999, 'actions.count') > 0", "unknown sequence flow")]
    [InlineData("FlowInfo(201, 'actions.latest.user') == 'alice'", "not supported")]
    [InlineData("FlowInfo(201) > 0", "exactly two arguments")]
    [InlineData("FlowInfo(flowId, 'actions.count') > 0", "literal integer flow id")]
    [InlineData("FlowInfo(201, path) > 0", "literal property path")]
    [InlineData("FlowInfo(201, 'actions.count', 'extra') > 0", "exactly two arguments")]
    public async Task CreateAsync_RejectsInvalidFlowInfoArguments(
        string condition,
        string expectedMessage)
    {
        var model = BuildGatewayFlowInfoModel();
        model.SequenceFlows.Single(flow => flow.Id == 301).Condition = condition;

        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));

        Assert.Contains(expectedMessage, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("userTaskCondition")]
    [InlineData("assignee")]
    [InlineData("variableValidation")]
    [InlineData("gatewayDefault")]
    public async Task CreateAsync_RejectsFlowInfoOutsideSupportedContexts(string context)
    {
        var model = BuildGatewayFlowInfoModel();
        var expression = "FlowInfo(201, 'actions.count') > 0";

        switch (context)
        {
            case "userTaskCondition":
                model.SequenceFlows.Single(flow => flow.Id == 201).Condition = expression;
                break;
            case "assignee":
                model.FlowNodes.Single(node => node.Id == 2).AssigneeExpression =
                    "FlowInfo(201, 'actions.last.user')";
                break;
            case "variableValidation":
                model.Variables.Add(new VariableModel
                {
                    Id = 1,
                    Name = "routeCount",
                    DataType = WorkflowVariableTypes.Number,
                    DefaultValue = JsonSerializer.SerializeToElement(0),
                    Validation = expression
                });
                break;
            case "gatewayDefault":
                model.SequenceFlows.Single(flow => flow.Id == 302).Condition = expression;
                break;
        }

        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));

        Assert.Contains(
            context == "gatewayDefault" ? "cannot define a condition" : "not available",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("NCaLc", ScriptFormats.NCalc)]
    [InlineData("JaVaScRiPt", ScriptFormats.JavaScript)]
    public async Task CreateAsync_CanonicalizesKnownScriptFormatsWithoutDiscardingLogic(
        string authoredFormat,
        string expectedFormat)
    {
        var model = BuildScriptFlowInfoModel();
        var node = model.FlowNodes.Single(candidate => candidate.Id == 2);
        node.ScriptFormat = authoredFormat;
        if (string.Equals(expectedFormat, ScriptFormats.JavaScript, StringComparison.Ordinal))
        {
            node.Assignments = [];
            node.Script = "execution.setVariable('audit', { ok: true });";
            node.UsesFlowInfo = false;
        }
        var service = CreateService(out var repository);

        await service.CreateAsync(model, false, CancellationToken.None);

        var saved = repository.Added!.Definition.FlowNodes.Single(candidate => candidate.Id == 2);
        Assert.Equal(expectedFormat, saved.ScriptFormat);
        if (expectedFormat == ScriptFormats.JavaScript)
        {
            Assert.Contains("setVariable", saved.Script, StringComparison.Ordinal);
        }
        else
        {
            Assert.Single(saved.Assignments);
        }
    }

    [Theory]
    [InlineData("unknownFormat")]
    [InlineData("nullFormat")]
    [InlineData("mixedJavaScript")]
    [InlineData("mixedNCalc")]
    [InlineData("nullAssignments")]
    [InlineData("nullAssignmentEntry")]
    public async Task CreateAsync_RejectsMalformedAuthoredScriptPayloadBeforeNormalization(string scenario)
    {
        var model = BuildScriptFlowInfoModel();
        var node = model.FlowNodes.Single(candidate => candidate.Id == 2);
        switch (scenario)
        {
            case "unknownFormat":
                node.ScriptFormat = "python";
                break;
            case "nullFormat":
                node.ScriptFormat = null!;
                break;
            case "mixedJavaScript":
                node.ScriptFormat = ScriptFormats.JavaScript;
                node.Script = "execution.setVariable('audit', {});";
                break;
            case "mixedNCalc":
                node.Script = "execution.setVariable('audit', {});";
                break;
            case "nullAssignments":
                node.Assignments = null!;
                break;
            case "nullAssignmentEntry":
                node.Assignments = [null!];
                break;
        }

        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));

        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public async Task CreateAsync_UsesRealStrictJavaScriptParserAtAuthorTime()
    {
        var model = BuildScriptFlowInfoModel();
        var node = model.FlowNodes.Single(candidate => candidate.Id == 2);
        node.ScriptFormat = ScriptFormats.JavaScript;
        node.Assignments = [];
        node.Script = "with ({ value: 1 }) { execution.setVariable('audit', value); }";
        node.UsesFlowInfo = false;
        var evaluator = new JintScriptEvaluator(
            new ScriptOptions(),
            NullLogger<JintScriptEvaluator>.Instance);

        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _, scriptEvaluator: evaluator)
                .CreateAsync(model, false, CancellationToken.None));

        Assert.Contains("invalid JavaScript", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("blankTarget")]
    [InlineData("undeclaredTarget")]
    [InlineData("blankExpression")]
    [InlineData("invalidExpression")]
    public async Task CreateAsync_RejectsInvalidNCalcAssignments(string scenario)
    {
        var model = BuildScriptFlowInfoModel();
        var assignment = model.FlowNodes.Single(candidate => candidate.Id == 2).Assignments.Single();
        switch (scenario)
        {
            case "blankTarget": assignment.Variable = " "; break;
            case "undeclaredTarget": assignment.Variable = "missing"; break;
            case "blankExpression": assignment.Expression = " "; break;
            case "invalidExpression": assignment.Expression = "("; break;
        }

        await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));
    }

    [Theory]
    [InlineData("condition")]
    [InlineData("roles")]
    [InlineData("variables")]
    [InlineData("default")]
    [InlineData("engineOnly")]
    [InlineData("claimBypass")]
    [InlineData("completion")]
    public async Task CreateAsync_RejectsIgnoredMetadataOnScriptTaskOutgoingFlow(string scenario)
    {
        var model = BuildScriptFlowInfoModel();
        var flow = model.SequenceFlows.Single(candidate => candidate.SourceRef == 2);
        switch (scenario)
        {
            case "condition": flow.Condition = "true"; break;
            case "roles": flow.Roles = ["admin"]; break;
            case "variables": flow.Variables = [new VariableModel { Id = 2, Name = "input" }]; break;
            case "default": flow.IsDefault = true; break;
            case "engineOnly": flow.IsSelectable = false; break;
            case "claimBypass": flow.CanActWithoutClaim = true; break;
            case "completion": flow.CompletionPriority = 1; break;
        }

        await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_InfersLegacyDirectJavaScriptFlowInfoAndPersistsExplicitFlag()
    {
        var model = BuildScriptFlowInfoModel();
        var node = model.FlowNodes.Single(candidate => candidate.Id == 2);
        node.ScriptFormat = ScriptFormats.JavaScript;
        node.Assignments = [];
        node.Script = "execution.setVariable('audit', execution.getFlowInfo(101));";
        node.UsesFlowInfo = null;

        await CreateService(out var repository).CreateAsync(model, false, CancellationToken.None);

        Assert.True(repository.Added!.Definition.FlowNodes.Single(candidate => candidate.Id == 2).UsesFlowInfo);
    }

    [Fact]
    public async Task CreateAsync_RejectsDirectJavaScriptFlowInfoWhenExplicitlyDisabled()
    {
        var model = BuildScriptFlowInfoModel();
        var node = model.FlowNodes.Single(candidate => candidate.Id == 2);
        node.ScriptFormat = ScriptFormats.JavaScript;
        node.Assignments = [];
        node.Script = "execution.setVariable('audit', execution.getFlowInfo(101));";
        node.UsesFlowInfo = false;

        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));

        Assert.Contains("usesFlowInfo is false", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Normalize_RemainsTolerantForLegacyUnknownScriptFormat()
    {
        var model = BuildScriptFlowInfoModel();
        var node = model.FlowNodes.Single(candidate => candidate.Id == 2);
        node.ScriptFormat = "legacy-custom";
        node.Script = "legacy body";

        WorkflowModelMigrator.Normalize(model);

        Assert.Equal(ScriptFormats.NCalc, node.ScriptFormat);
        Assert.Null(node.Script);
        Assert.False(node.UsesFlowInfo);
    }

    [Fact]
    public async Task CreateAsync_AllowsSeveralIncomingPathsToExclusiveGateway()
    {
        var model = BuildGatewayFlowInfoModel();
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 6,
            Name = "Alternative incoming path",
            Type = BpmnFlowNodeTypes.Task
        });
        model.SequenceFlows.Add(new SequenceFlowModel
        {
            Id = 601,
            Name = "Merge into route",
            SourceRef = 6,
            TargetRef = 3
        });

        await CreateService(out _).CreateAsync(model, false, CancellationToken.None);
    }

    [Fact]
    public async Task CreateAsync_RejectsExclusiveGatewayWithoutExactlyOneDefault()
    {
        var model = BuildGatewayFlowInfoModel();
        var formerDefault = model.SequenceFlows.Single(flow => flow.Id == 302);
        formerDefault.IsDefault = false;
        formerDefault.Condition = "true";
        formerDefault.ConditionPriority = 2;

        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));

        Assert.Contains("exactly one default", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_RejectsExclusiveGatewayWithMultipleDefaults()
    {
        var model = BuildGatewayFlowInfoModel();
        model.SequenceFlows.Add(new SequenceFlowModel
        {
            Id = 303,
            SourceRef = 3,
            TargetRef = 5,
            IsDefault = true
        });

        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));

        Assert.Contains("exactly one default", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_RejectsPureMergeExclusiveGatewayWithOneOutgoingFlow()
    {
        var model = BuildGatewayFlowInfoModel();
        model.SequenceFlows.RemoveAll(flow => flow.Id == 301);

        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));

        Assert.Contains("at least two outgoing", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_RequiresExplicitConditionPriorityOnAuthoredGatewayFlows()
    {
        var model = BuildGatewayFlowInfoModel();
        model.SequenceFlows.Single(flow => flow.Id == 301).ConditionPriority = null;

        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));

        Assert.Contains("explicitly define", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("conditionPriority", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null, 1, "condition")]
    [InlineData("true", 0, "positive")]
    public async Task CreateAsync_RejectsMalformedConditionalGatewayFlow(
        string? condition,
        int priority,
        string expectedMessage)
    {
        var model = BuildGatewayFlowInfoModel();
        var flow = model.SequenceFlows.Single(candidate => candidate.Id == 301);
        flow.Condition = condition;
        flow.ConditionPriority = priority;

        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));

        Assert.Contains(expectedMessage, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_RejectsDuplicateGatewayConditionPriorities()
    {
        var model = BuildGatewayFlowInfoModel();
        model.SequenceFlows.Add(new SequenceFlowModel
        {
            Id = 303,
            SourceRef = 3,
            TargetRef = 4,
            Condition = "true",
            ConditionPriority = 1
        });

        var error = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(model, false, CancellationToken.None));

        Assert.Contains("duplicate conditionPriority", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_RejectsIgnoredGatewayAndNonGatewayPriorityMetadata()
    {
        var gatewayMetadata = BuildGatewayFlowInfoModel();
        gatewayMetadata.SequenceFlows.Single(flow => flow.Id == 301).Roles = ["Manager"];
        var gatewayError = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(gatewayMetadata, false, CancellationToken.None));
        Assert.Contains("user-action or multi-instance", gatewayError.Message, StringComparison.OrdinalIgnoreCase);

        var nonGatewayMetadata = BuildGatewayFlowInfoModel();
        nonGatewayMetadata.SequenceFlows.Single(flow => flow.Id == 101).ConditionPriority = 9;
        var nonGatewayError = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            CreateService(out _).CreateAsync(nonGatewayMetadata, false, CancellationToken.None));
        Assert.Contains("not an exclusive gateway", nonGatewayError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Normalize_DerivesLegacyGatewayPrioritiesFromSequenceFlowOrder()
    {
        var model = BuildGatewayFlowInfoModel();
        var first = model.SequenceFlows.Single(flow => flow.Id == 301);
        first.ConditionPriority = null;
        var second = model.SequenceFlows.Single(flow => flow.Id == 302);
        second.IsDefault = false;
        second.Condition = "false";
        second.ConditionPriority = null;
        model.SequenceFlows.Add(new SequenceFlowModel
        {
            Id = 303,
            SourceRef = 3,
            TargetRef = 5,
            IsDefault = true
        });

        WorkflowModelMigrator.Normalize(model);

        Assert.Equal(1, first.ConditionPriority);
        Assert.Equal(2, second.ConditionPriority);
        Assert.Null(model.SequenceFlows.Single(flow => flow.Id == 303).ConditionPriority);
    }

    [Fact]
    public void Normalize_DoesNotMaskPartiallyAuthoredGatewayPrioritiesOrAddLegacyDefault()
    {
        var model = BuildGatewayFlowInfoModel();
        var first = model.SequenceFlows.Single(flow => flow.Id == 301);
        var second = model.SequenceFlows.Single(flow => flow.Id == 302);
        second.IsDefault = false;
        second.Condition = "false";
        second.ConditionPriority = null;

        WorkflowModelMigrator.Normalize(model);

        Assert.Equal(1, first.ConditionPriority);
        Assert.Null(second.ConditionPriority);
        Assert.DoesNotContain(model.SequenceFlows, flow => flow.SourceRef == 3 && flow.IsDefault);
    }

    private static WorkflowModel BuildGatewayFlowInfoModel() => new()
    {
        Id = "flow-info-gateway",
        Name = "FlowInfo gateway",
        InitialEventId = 1,
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel { Id = 2, Name = "Confirm", Type = BpmnFlowNodeTypes.UserTask },
            new FlowNodeModel { Id = 3, Name = "Route", Type = BpmnFlowNodeTypes.ExclusiveGateway },
            new FlowNodeModel { Id = 4, Name = "Manager end", Type = BpmnFlowNodeTypes.EndEvent },
            new FlowNodeModel { Id = 5, Name = "User end", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, Name = "Begin", SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, Name = "Confirm", SourceRef = 2, TargetRef = 3 },
            new SequenceFlowModel
            {
                Id = 301,
                Name = "Manager",
                SourceRef = 3,
                TargetRef = 4,
                Condition = "Contains(FlowInfo(201, 'actions.last.userRoles'), 'Manager')",
                ConditionPriority = 1
            },
            new SequenceFlowModel
            {
                Id = 302,
                Name = "User",
                SourceRef = 3,
                TargetRef = 5,
                IsDefault = true
            }
        ]
    };

    private static WorkflowModel BuildScriptFlowInfoModel() => new()
    {
        Id = "flow-info-script",
        Name = "FlowInfo script",
        InitialEventId = 1,
        Variables =
        [
            new VariableModel
            {
                Id = 1,
                Name = "audit",
                DataType = WorkflowVariableTypes.Json,
                DefaultValue = JsonSerializer.SerializeToElement(new { })
            }
        ],
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel
            {
                Id = 2,
                Name = "Capture audit",
                Type = BpmnFlowNodeTypes.ScriptTask,
                ScriptFormat = ScriptFormats.NCalc,
                Assignments =
                [
                    new AssignmentModel { Variable = "audit", Expression = "FlowInfo(101, 'all')" }
                ]
            },
            new FlowNodeModel { Id = 3, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, Name = "To script", SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, Name = "Done", SourceRef = 2, TargetRef = 3 }
        ]
    };

    private static SequenceFlowModel AddErrorBoundary(WorkflowModel model)
    {
        var host = model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsServiceTask(node.Type));
        var target = model.FlowNodes.First(node => BpmnFlowNodeTypes.IsEnd(node.Type));
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 50,
            Name = "Service error",
            Type = BpmnFlowNodeTypes.ErrorBoundaryEvent,
            AttachedToRef = host.Id
        });
        var flow = new SequenceFlowModel
        {
            Id = 501,
            Name = "Handle error",
            SourceRef = 50,
            TargetRef = target.Id
        };
        model.SequenceFlows.Add(flow);
        return flow;
    }

    private static WorkflowModel CreateTerminalModel(string terminalType) => new()
    {
        Id = "terminal-validation",
        Name = "Terminal validation",
        InitialEventId = 1,
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel { Id = 2, Name = "Review", Type = BpmnFlowNodeTypes.UserTask },
            new FlowNodeModel
            {
                Id = 3,
                Name = "Terminal",
                Type = terminalType,
                ErrorCode = terminalType == BpmnFlowNodeTypes.ErrorEndEvent ? "TERMINAL_FAULT" : null
            }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, Name = "Begin", SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, Name = "Finish", SourceRef = 2, TargetRef = 3 }
        ]
    };

    private static WorkflowDefinitionService CreateService(
        out CapturingDefinitionRepository repository,
        ServiceTaskOptions? options = null,
        IScriptEvaluator? scriptEvaluator = null)
    {
        repository = new CapturingDefinitionRepository();
        return new WorkflowDefinitionService(
            repository,
            scriptEvaluator ?? new ParseOnlyScriptEvaluator(),
            options ?? new ServiceTaskOptions(),
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

        public WorkflowDefinitionRecord? Source { get; set; }

        public Task<IReadOnlyList<WorkflowDefinitionRecord>> ListLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkflowDefinitionRecord>>([]);

        public Task<IReadOnlyList<WorkflowDefinitionRecord>> ListVersionsByKeyAsync(
            string workflowKey,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkflowDefinitionRecord>>([]);

        public Task<WorkflowDefinitionRecord?> GetAsync(long id, CancellationToken cancellationToken) =>
            Task.FromResult(Source?.Id == id ? Source : null);

        public Task<IReadOnlyDictionary<long, WorkflowDefinitionRecord>> GetManyAsync(
            IReadOnlyCollection<long> ids,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<long, WorkflowDefinitionRecord>>(
                Source is not null && ids.Contains(Source.Id)
                    ? new Dictionary<long, WorkflowDefinitionRecord> { [Source.Id] = Source }
                    : new Dictionary<long, WorkflowDefinitionRecord>());

        public Task<WorkflowDefinitionRecord?> GetPublishedAsync(long id, CancellationToken cancellationToken) =>
            Task.FromResult<WorkflowDefinitionRecord?>(null);

        public Task<WorkflowDefinitionRecord?> GetDefaultByWorkflowKeyAsync(
            string workflowKey,
            CancellationToken cancellationToken) =>
            Task.FromResult<WorkflowDefinitionRecord?>(null);

        public Task LockFamilyForStartAsync(string workflowKey, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<WorkflowDefinitionRecord> AddAsync(
            string name,
            WorkflowModel definition,
            bool isPublished,
            CancellationToken cancellationToken)
        {
            Added = new WorkflowDefinitionRecord(
                1,
                name,
                definition.Id,
                1,
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
