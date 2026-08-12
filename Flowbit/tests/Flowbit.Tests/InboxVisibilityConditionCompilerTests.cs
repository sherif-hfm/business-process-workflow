using System.Text.Json;
using System.Reflection;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Flowbit.Tests;

public sealed class InboxVisibilityConditionCompilerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Compile_emits_typed_canonical_postfix_program()
    {
        var definition = Definition(
            Variable("amount", WorkflowVariableTypes.Number, 12),
            Variable("tax", WorkflowVariableTypes.Number, 3),
            Variable("approved", WorkflowVariableTypes.Boolean, true));

        var compiled = Assert.IsType<InboxVisibilityConditionCompilation>(
            InboxVisibilityConditionCompiler.Compile(
                "([amount] + [tax]) > Number([config.ApprovalLimit]) and [approved] == true",
                definition));

        Assert.Equal(InboxVisibilityConditionCompiler.CurrentProgramVersion, compiled.ProgramVersion);
        Assert.Equal(["amount", "approved", "tax"], compiled.VariableNames);
        Assert.Equal(["config.approvallimit"], compiled.ExternalReferences);
        Assert.Equal(64, compiled.SemanticFingerprint.Length);
        Assert.Equal(1, compiled.Program.GetProperty("version").GetInt32());

        var instructions = compiled.Program.GetProperty("instructions")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(
            ["variable", "variable", "add", "external", "number", "greater",
                "variable", "literal", "equal", "and"],
            instructions.Select(item => item.GetProperty("op").GetString()!).ToArray());
        Assert.Equal(
            WorkflowVariableTypes.Number,
            instructions[5].GetProperty("type").GetString());
        Assert.Equal(
            WorkflowVariableTypes.Boolean,
            instructions[8].GetProperty("type").GetString());
    }

    [Fact]
    public void Canonical_fingerprint_ignores_whitespace_casing_aliases_and_redundant_parentheses()
    {
        var definition = Definition(
            Variable("Amount", WorkflowVariableTypes.Number, 12),
            Variable("Tax", WorkflowVariableTypes.Number, 3),
            Variable("Approved", WorkflowVariableTypes.Boolean, true));

        var first = InboxVisibilityConditionCompiler.Compile(
            "([amount]+[tax]) > Number([config.Limit]) AND ([approved] == TRUE)",
            definition)!;
        var second = InboxVisibilityConditionCompiler.Compile(
            "[AMOUNT] + [TAX] > number([CONFIG.limit]) && [APPROVED] == true",
            definition)!;

        Assert.Equal(first.SemanticFingerprint, second.SemanticFingerprint);
        Assert.Equal(first.Program.GetRawText(), second.Program.GetRawText());
    }

    [Fact]
    public void Compiler_supports_variable_comparisons_dates_datetimes_and_three_external_kinds()
    {
        var definition = Definition(
            Variable("requested", WorkflowVariableTypes.Number, 12),
            Variable("limit", WorkflowVariableTypes.Number, 20),
            Variable("startDate", WorkflowVariableTypes.Date, "2026-08-01"),
            Variable("submittedAt", WorkflowVariableTypes.DateTime, "2026-08-01T12:00:00Z"));

        var compiled = InboxVisibilityConditionCompiler.Compile(
            "[requested] < [limit] and [startDate] <= '2026-08-11' "
            + "and [submittedAt] < [sys.now] and [sys.claim.department] == [setting.department]",
            definition)!;

        Assert.Equal(
            ["setting.department", "sys.claim.department", "sys.now"],
            compiled.ExternalReferences);
        var comparisonTypes = compiled.Program.GetProperty("instructions")
            .EnumerateArray()
            .Where(item => item.TryGetProperty("type", out _)
                && item.GetProperty("op").GetString() is not "literal")
            .Select(item => item.GetProperty("type").GetString()!)
            .ToArray();
        Assert.Equal(
            [WorkflowVariableTypes.Number, WorkflowVariableTypes.Date,
                WorkflowVariableTypes.DateTime, WorkflowVariableTypes.String],
            comparisonTypes);
    }

    [Fact]
    public void Number_is_the_only_string_to_number_conversion()
    {
        var definition = Definition(Variable("amount", WorkflowVariableTypes.Number, 12));

        Assert.Throws<WorkflowDomainException>(() =>
            InboxVisibilityConditionCompiler.Compile("[amount] > [config.limit]", definition));

        var compiled = InboxVisibilityConditionCompiler.Compile(
            "[amount] > Number([config.limit])",
            definition);
        Assert.NotNull(compiled);
    }

    [Theory]
    [InlineData("[setting.first] > [setting.second]")]
    [InlineData("[sys.user] + 1 > 2")]
    [InlineData("Min(1, 2) == 1")]
    [InlineData("[sys.roles] == 'admin'")]
    [InlineData("[missing] == 1")]
    [InlineData("(not [setting.flag]) + 1 > 0")]
    [InlineData("([setting.flag] and true) + 1 > 0")]
    [InlineData("([setting.amount] + 1) == '2'")]
    [InlineData("1")]
    [InlineData("'nonempty'")]
    [InlineData("null == null")]
    public void Compiler_rejects_unsupported_or_untyped_constructs(string condition)
    {
        var definition = Definition();

        Assert.Throws<WorkflowDomainException>(() =>
            InboxVisibilityConditionCompiler.Compile(condition, definition));
    }

    [Fact]
    public void Shared_conformance_corpus_matches_the_server_compiler()
    {
        var definition = Definition(
            Variable("department", WorkflowVariableTypes.String, "sales"),
            Variable("amount", WorkflowVariableTypes.Number, 10),
            Variable("tax", WorkflowVariableTypes.Number, 2),
            Variable("requestedAmount", WorkflowVariableTypes.Number, 12),
            Variable("startDate", WorkflowVariableTypes.Date, "2026-08-01"),
            Variable("endDate", WorkflowVariableTypes.Date, "2026-08-31"),
            Variable("submittedAt", WorkflowVariableTypes.DateTime, "2026-08-01T10:00:00Z"),
            Variable("blocked", WorkflowVariableTypes.Boolean, false),
            Variable("approved", WorkflowVariableTypes.Boolean, true),
            Variable("reviewers", WorkflowVariableTypes.String, null, isArray: true),
            Variable("payload", WorkflowVariableTypes.Json, new { value = 1 }));
        var corpus = JsonSerializer.Deserialize<List<InboxVisibilityConformanceCase>>(
            File.ReadAllText(Path.Combine(
                AppContext.BaseDirectory,
                "Fixtures",
                "inbox-visibility-conformance.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        foreach (var testCase in corpus)
        {
            var exception = Xunit.Record.Exception(() =>
                InboxVisibilityConditionCompiler.Compile(testCase.Expression, definition));
            if (testCase.Valid)
            {
                Assert.True(exception is null, $"{testCase.Name}: {exception?.Message}");
            }
            else
            {
                Assert.IsType<WorkflowDomainException>(exception);
            }
        }
    }

    [Fact]
    public void Boolean_scalar_or_dynamic_setting_may_be_the_exact_boolean_root()
    {
        var definition = Definition(Variable("enabled", WorkflowVariableTypes.Boolean, true));

        Assert.NotNull(InboxVisibilityConditionCompiler.Compile("[enabled]", definition));
        Assert.NotNull(InboxVisibilityConditionCompiler.Compile("not [setting.disabled]", definition));
    }

    [Fact]
    public void Array_json_and_conflicting_variable_producers_are_rejected_when_referenced()
    {
        var arrayDefinition = Definition(Variable("items", WorkflowVariableTypes.String, null, isArray: true));
        Assert.Throws<WorkflowDomainException>(() =>
            InboxVisibilityConditionCompiler.Compile("[items] == 'one'", arrayDefinition));

        var jsonDefinition = Definition(Variable("payload", WorkflowVariableTypes.Json, new { }));
        Assert.Throws<WorkflowDomainException>(() =>
            InboxVisibilityConditionCompiler.Compile("[payload] == 'one'", jsonDefinition));

        var spellingDefinition = Definition(Variable("Amount", WorkflowVariableTypes.Number, 1));
        spellingDefinition.FlowNodes[1].Variables.Add(Variable("amount", WorkflowVariableTypes.Number, null));
        Assert.Throws<WorkflowDomainException>(() =>
            InboxVisibilityConditionCompiler.Compile("[amount] > 0", spellingDefinition));

        var typeDefinition = Definition(Variable("amount", WorkflowVariableTypes.Number, 1));
        typeDefinition.FlowNodes[1].Variables.Add(Variable("amount", WorkflowVariableTypes.String, null));
        Assert.Throws<WorkflowDomainException>(() =>
            InboxVisibilityConditionCompiler.Compile("[amount] > 0", typeDefinition));
    }

    [Fact]
    public void Reference_escaping_preserves_the_declared_variable_name()
    {
        var definition = Definition(Variable("closing]balance\\usd", WorkflowVariableTypes.Number, 1));

        var compiled = InboxVisibilityConditionCompiler.Compile(
            "[closing\\]balance\\\\usd] == 1",
            definition)!;

        Assert.Equal(["closing]balance\\usd"], compiled.VariableNames);
    }

    [Fact]
    public void Entry_idempotency_key_is_a_declared_scalar_string_producer()
    {
        var definition = Definition();
        definition.FlowNodes[0].Idempotency = new IdempotencyModel
        {
            HeaderName = IdempotencyHeaders.Standard,
            Variable = "requestKey"
        };

        var compiled = InboxVisibilityConditionCompiler.Compile(
            "[requestKey] == 'request-123'",
            definition)!;

        Assert.Equal(["requestKey"], compiled.VariableNames);
    }

    [Fact]
    public void Literal_numeric_and_expression_bounds_are_enforced()
    {
        var definition = Definition(
            Variable("left", WorkflowVariableTypes.Number, 1),
            Variable("right", WorkflowVariableTypes.Number, 2),
            Variable("flag", WorkflowVariableTypes.Boolean, true));

        Assert.Throws<WorkflowDomainException>(() =>
            InboxVisibilityConditionCompiler.Compile("1e101 == 1", definition));
        Assert.Throws<WorkflowDomainException>(() =>
            InboxVisibilityConditionCompiler.Compile(
                $"{new string('9', 30)}e100 == 1",
                definition));
        Assert.Throws<WorkflowDomainException>(() =>
            InboxVisibilityConditionCompiler.Compile(
                $"[sys.user] == '{new string('x', InboxVisibilityConditionCompiler.MaxStringLiteralUtf8Bytes + 1)}'",
                definition));
        Assert.Throws<WorkflowDomainException>(() =>
            InboxVisibilityConditionCompiler.Compile(
                new string(' ', InboxVisibilityConditionCompiler.MaxUtf8Bytes) + "true",
                definition));

        var tooDeep = string.Concat(
            Enumerable.Repeat("not ", InboxVisibilityConditionCompiler.MaxExpressionDepth)) + "[flag]";
        Assert.Throws<WorkflowDomainException>(() =>
            InboxVisibilityConditionCompiler.Compile(tooDeep, definition));
        Assert.Throws<WorkflowDomainException>(() =>
            InboxVisibilityConditionCompiler.Compile(
                new string('(', InboxVisibilityConditionCompiler.MaxExpressionDepth + 1)
                + "[flag]"
                + new string(')', InboxVisibilityConditionCompiler.MaxExpressionDepth + 1),
                definition));
        Assert.Throws<WorkflowDomainException>(() =>
            InboxVisibilityConditionCompiler.Compile(new string('!', 1_000) + "[flag]", definition));

        var tooManyComparisons = Balanced(
            Enumerable.Repeat("[left] > [right]", InboxVisibilityConditionCompiler.MaxComparisons + 1)
                .ToArray(),
            "and");
        Assert.Throws<WorkflowDomainException>(() =>
            InboxVisibilityConditionCompiler.Compile(tooManyComparisons, definition));

        var tooManyInstructions = Balanced(
            Enumerable.Repeat("[flag]", 40).ToArray(),
            "or");
        Assert.Throws<WorkflowDomainException>(() =>
            InboxVisibilityConditionCompiler.Compile(tooManyInstructions, definition));
    }

    [Fact]
    public void Distinct_variable_external_and_literal_limits_are_enforced()
    {
        var variableDefinition = Definition(
            Enumerable.Range(1, InboxVisibilityConditionCompiler.MaxVariableReferences + 1)
                .Select(index => Variable($"flag{index}", WorkflowVariableTypes.Boolean, true))
                .ToArray());
        Assert.Throws<WorkflowDomainException>(() =>
            InboxVisibilityConditionCompiler.Compile(
                Balanced(variableDefinition.Variables.Select(variable => $"[{variable.Name}]").ToArray(), "or"),
                variableDefinition));

        var externalTerms = Enumerable.Range(1, InboxVisibilityConditionCompiler.MaxExternalReferences + 1)
            .Select(index => $"[setting.flag{index}]")
            .ToArray();
        Assert.Throws<WorkflowDomainException>(() =>
            InboxVisibilityConditionCompiler.Compile(Balanced(externalTerms, "or"), Definition()));

        var literalTerms = Enumerable.Range(0, InboxVisibilityConditionCompiler.MaxLiterals + 1)
            .Select(index => index % 2 == 0 ? "true" : "false")
            .ToArray();
        Assert.Throws<WorkflowDomainException>(() =>
            InboxVisibilityConditionCompiler.Compile(Balanced(literalTerms, "or"), Definition()));
    }

    [Fact]
    public void CompileAll_returns_only_authored_user_tasks_and_rejects_other_node_types()
    {
        var definition = Definition(Variable("flag", WorkflowVariableTypes.Boolean, true));
        definition.FlowNodes[1].InboxVisibilityCondition = " [flag] ";

        var compiled = InboxVisibilityConditionCompiler.CompileAll(definition);

        Assert.Equal(2, Assert.Single(compiled).Key);
        definition.FlowNodes[0].InboxVisibilityCondition = "true";
        Assert.Throws<WorkflowDomainException>(() =>
            InboxVisibilityConditionCompiler.CompileAll(definition));
    }

    [Fact]
    public void CompileAll_enforces_source_size_and_unicode_bounds()
    {
        var oversized = Definition();
        oversized.FlowNodes[1].InboxVisibilityCondition =
            new string(' ', InboxVisibilityConditionCompiler.MaxUtf8Bytes) + "true";
        Assert.Throws<WorkflowDomainException>(() =>
            InboxVisibilityConditionCompiler.CompileAll(oversized));

        var invalidUnicode = Definition();
        invalidUnicode.FlowNodes[1].InboxVisibilityCondition = "'\ud800' == 'x'";
        Assert.Throws<WorkflowDomainException>(() =>
            InboxVisibilityConditionCompiler.CompileAll(invalidUnicode));
    }

    [Fact]
    public void Migrator_trims_user_task_condition_and_clears_it_from_other_nodes()
    {
        var definition = Definition();
        definition.FlowNodes[0].InboxVisibilityCondition = " true ";
        definition.FlowNodes[1].InboxVisibilityCondition = "  [setting.visible]  ";

        WorkflowModelMigrator.Normalize(definition);

        Assert.Null(definition.FlowNodes[0].InboxVisibilityCondition);
        Assert.Equal("[setting.visible]", definition.FlowNodes[1].InboxVisibilityCondition);
    }

    [Fact]
    public void Json_model_round_trips_inbox_visibility_condition()
    {
        var definition = Definition();
        definition.FlowNodes[1].InboxVisibilityCondition = "[sys.user] == 'alice'";

        var json = JsonSerializer.Serialize(definition);
        var roundTripped = JsonSerializer.Deserialize<WorkflowModel>(json)!;

        Assert.Contains("\"inboxVisibilityCondition\"", json, StringComparison.Ordinal);
        Assert.Equal(
            "[sys.user] == 'alice'",
            roundTripped.FlowNodes[1].InboxVisibilityCondition);
    }

    [Fact]
    public async Task Definition_service_rejects_condition_on_a_non_user_task_before_normalization()
    {
        var definition = Definition();
        definition.FlowNodes[0].InboxVisibilityCondition = "true";

        var exception = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            DefinitionService().CreateAsync(definition, publish: false, CancellationToken.None));

        Assert.Contains("is not a user task", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Definition_service_rejects_oversized_raw_condition_before_normalization_trims_it()
    {
        var definition = Definition();
        definition.FlowNodes[1].InboxVisibilityCondition =
            new string(' ', InboxVisibilityConditionCompiler.MaxUtf8Bytes) + "true";

        var exception = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            DefinitionService().CreateAsync(definition, publish: false, CancellationToken.None));

        Assert.Contains("inboxVisibilityCondition", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            InboxVisibilityConditionCompiler.MaxUtf8Bytes.ToString(),
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Definition_service_runs_typed_condition_validation()
    {
        var definition = Definition(Variable("amount", WorkflowVariableTypes.Number, 1));
        definition.FlowNodes[1].InboxVisibilityCondition = "[amount] > [config.limit]";

        var exception = await Assert.ThrowsAsync<WorkflowDomainException>(() =>
            DefinitionService().CreateAsync(definition, publish: false, CancellationToken.None));

        Assert.Contains("inboxVisibilityCondition", exception.Message, StringComparison.Ordinal);
        Assert.Contains("incompatible", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Version_compatibility_uses_semantic_condition_fingerprint_for_open_tasks()
    {
        var sourceModel = Definition(Variable("amount", WorkflowVariableTypes.Number, 1));
        sourceModel.FlowNodes[1].InboxVisibilityCondition = "[amount] > Number([config.limit])";
        var equivalentModel = Clone(sourceModel);
        equivalentModel.FlowNodes[1].InboxVisibilityCondition =
            "([AMOUNT] > number([CONFIG.LIMIT]))";
        var changedModel = Clone(sourceModel);
        changedModel.FlowNodes[1].InboxVisibilityCondition = "[amount] >= Number([config.limit])";

        var source = Record(11, 1, sourceModel);
        var equivalent = WorkflowVersionCompatibilityEvaluator.Evaluate(
            CompatibilityContext(source, Record(12, 2, equivalentModel)));
        var changed = WorkflowVersionCompatibilityEvaluator.Evaluate(
            CompatibilityContext(source, Record(13, 3, changedModel)));

        Assert.DoesNotContain(
            equivalent.Blockers,
            issue => issue.Code == WorkflowVersionCompatibilityCodes.UserTaskContractChanged);
        Assert.Contains(
            changed.Blockers,
            issue => issue.Code == WorkflowVersionCompatibilityCodes.UserTaskContractChanged);
    }

    [Fact]
    public void Version_compatibility_tracks_definition_owned_visibility_context()
    {
        var workflowIdSource = Definition();
        workflowIdSource.FlowNodes[1].InboxVisibilityCondition =
            "[sys.workflowId] == [sys.workflowId]";
        var workflowIdTarget = Clone(workflowIdSource);

        var workflowNameSource = Definition();
        workflowNameSource.FlowNodes[1].InboxVisibilityCondition =
            "[sys.workflowName] == [sys.workflowName]";
        var workflowNameTarget = Clone(workflowNameSource);
        workflowNameTarget.Name = "Renamed workflow";

        var nodeNameSource = Definition();
        nodeNameSource.FlowNodes[1].InboxVisibilityCondition =
            "[sys.nodeName] == [sys.nodeName]";
        var nodeNameTarget = Clone(nodeNameSource);
        nodeNameTarget.FlowNodes[1].Name = "Renamed review";

        var results = new[]
        {
            WorkflowVersionCompatibilityEvaluator.Evaluate(CompatibilityContext(
                Record(21, 1, workflowIdSource), Record(22, 2, workflowIdTarget))),
            WorkflowVersionCompatibilityEvaluator.Evaluate(CompatibilityContext(
                Record(31, 1, workflowNameSource), Record(32, 2, workflowNameTarget))),
            WorkflowVersionCompatibilityEvaluator.Evaluate(CompatibilityContext(
                Record(41, 1, nodeNameSource), Record(42, 2, nodeNameTarget)))
        };

        Assert.All(results, result => Assert.Contains(
            result.Blockers,
            issue => issue.Code == WorkflowVersionCompatibilityCodes.UserTaskContractChanged));
    }

    private static WorkflowModel Definition(params VariableModel[] variables) => new()
    {
        Id = "inbox-visibility",
        Name = "Inbox visibility",
        InitialEventId = 1,
        Variables = variables.ToList(),
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
                Type = BpmnFlowNodeTypes.UserTask,
                Roles = ["approver"]
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
            new SequenceFlowModel { Id = 10, Name = "Begin", SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 20, Name = "Finish", SourceRef = 2, TargetRef = 3 }
        ]
    };

    private static VariableModel Variable(
        string name,
        string type,
        object? defaultValue,
        bool isArray = false) =>
        new()
        {
            Name = name,
            DataType = type,
            IsArray = isArray,
            Nullable = defaultValue is null,
            DefaultValue = defaultValue is null
                ? null
                : JsonSerializer.SerializeToElement(defaultValue)
        };

    private static string Balanced(IReadOnlyList<string> terms, string operation)
    {
        if (terms.Count == 1)
        {
            return terms[0];
        }

        var midpoint = terms.Count / 2;
        return $"({Balanced(terms.Take(midpoint).ToArray(), operation)} {operation} "
            + $"{Balanced(terms.Skip(midpoint).ToArray(), operation)})";
    }

    private sealed record InboxVisibilityConformanceCase(
        string Name,
        string Expression,
        bool Valid);

    private static WorkflowDefinitionRecord Record(long id, int version, WorkflowModel definition) =>
        new(
            id,
            definition.Name,
            definition.Id,
            version,
            definition,
            IsPublished: true,
            IsDefault: version == 1,
            CreatedAt: Now.AddDays(version));

    private static WorkflowVersionCompatibilityContext CompatibilityContext(
        WorkflowDefinitionRecord source,
        WorkflowDefinitionRecord target)
    {
        var node = source.Definition.FlowNodes[1];
        return new WorkflowVersionCompatibilityContext
        {
            Instance = new WorkflowInstanceRecord(
                Id: 7,
                WorkflowDefinitionId: source.Id,
                WorkflowKey: source.WorkflowKey,
                IdempotencyKey: null,
                BusinessKey: null,
                BusinessKeyUniqueness: null,
                ActiveTokenId: 101,
                CurrentStepId: node.Id,
                ActiveUserTaskId: 201,
                Status: WorkflowInstanceStatuses.Running,
                ClaimedBy: null,
                StartedBy: "alice",
                CreatedAt: Now,
                UpdatedAt: Now),
            SourceDefinition = source,
            TargetDefinition = target,
            ActiveTokens =
            [
                new ExecutionTokenRecord(
                    Id: 101,
                    InstanceId: 7,
                    NodeId: node.Id,
                    NodeName: node.Name,
                    NodeExternalId: node.ExternalId,
                    NodeType: node.Type,
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
                    UpdatedAt: Now)
            ],
            OpenUserTasks =
            [
                new UserTaskRecord(
                    Id: 201,
                    InstanceId: 7,
                    TokenId: 101,
                    NodeId: node.Id,
                    NodeName: node.Name,
                    NodeExternalId: node.ExternalId,
                    Roles: node.Roles,
                    RequiresClaim: false,
                    RequiresAssignment: false,
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
                    CompletedAt: null)
            ]
        };
    }

    private static WorkflowModel Clone(WorkflowModel model) =>
        JsonSerializer.Deserialize<WorkflowModel>(JsonSerializer.Serialize(model))!;

    private static WorkflowDefinitionService DefinitionService() =>
        new(
            DispatchProxy.Create<IWorkflowDefinitionRepository, UnexpectedProxy>(),
            DispatchProxy.Create<IScriptEvaluator, UnexpectedProxy>(),
            new ServiceTaskOptions(),
            NullLogger<WorkflowDefinitionService>.Instance);

    public class UnexpectedProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException(
                $"Unexpected test dependency call to {targetMethod?.DeclaringType?.Name}.{targetMethod?.Name}.");
    }
}
