using System.Text.Json;
using System.Text.RegularExpressions;
using Jint;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

public sealed class EditorValidatorTests
{
    [Fact]
    public void Validator_AcceptsCanonicalMultiInstanceFixture()
    {
        var model = DefinitionValidationTests.LoadModel("votes-users-list.json");

        var errors = Validate(model);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validator_RequiresCompleteTaskDistributionCredentialsWhenEnabled()
    {
        var model = DefinitionValidationTests.LoadModel("votes-users-list.json");
        model.TaskDistribution = new TaskDistributionModel
        {
            ClientId = "${setting.taskDistribution.clientId}",
            ClientSecret = "${setting.taskDistribution.clientSecret}"
        };
        Assert.Empty(Validate(model));

        model.TaskDistribution.ClientSecret = "";
        Assert.Contains(Validate(model), error =>
            error.Contains("taskDistribution", StringComparison.OrdinalIgnoreCase)
            && error.Contains("clientSecret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_ReportsEnumIdentityAndResultConfigurationErrorsTogether()
    {
        var model = DefinitionValidationTests.LoadModel("votes-users-list.json");
        var multi = model.FlowNodes.Single(node => node.Id == 2).MultiInstance!;
        multi.Mode = "sequentual";
        model.FlowNodes.Add(Clone(model.FlowNodes[0]));
        model.Variables.Single(variable => variable.Name == "voteResults").DefaultValue =
            JsonSerializer.SerializeToElement("not-an-array");

        var errors = Validate(model);

        Assert.Contains(errors, error => error.Contains("unsupported multi-instance mode", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("Flow node id #1 is duplicated", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("defaultValue is a JSON array", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_ReportsImpureDefaultAndOverlongCollectionUser()
    {
        var model = DefinitionValidationTests.LoadModel("votes-users-list.json");
        var fallback = model.SequenceFlows.Single(flow => flow.IsDefault);
        fallback.IsSelectable = true;
        fallback.Roles = ["Manager"];
        model.Variables.Single(variable => variable.Name == "voters").DefaultValue =
            JsonSerializer.SerializeToElement(new[] { new string('x', UserTaskConstraints.MaxActorNameLength + 1) });

        var errors = Validate(model);

        Assert.Contains(errors, error => error.Contains("pure engine-only default", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("300-character", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_AcceptsValidBusinessKeyAndRejectsInvalidPolicy()
    {
        var model = DefinitionValidationTests.LoadModel("votes-users-list.json");
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
        Assert.Empty(Validate(model));

        start.BusinessKey.Uniqueness = "sometimes";
        Assert.Contains(Validate(model), error =>
            error.Contains("unsupported businessKey.uniqueness", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_ValidatesTypedMessageStartMappingsAndBusinessKey()
    {
        var model = DefinitionValidationTests.LoadModel("votes-users-list.json");
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
                    Variable = "violationId",
                    Path = "violation.id",
                    DataType = WorkflowVariableTypes.String,
                    IsArray = false,
                    Required = true,
                    Validation = "StartsWith(violationId, 'V-')"
                },
                new MessageOutputMappingModel
                {
                    Variable = "country",
                    Path = string.Empty,
                    DataType = WorkflowVariableTypes.String,
                    IsArray = false,
                    DefaultValue = JsonSerializer.SerializeToElement("SA")
                }
            ]
        };
        start.Idempotency = new IdempotencyModel
        {
            HeaderName = IdempotencyHeaders.Standard,
            Variable = "requestId"
        };
        start.BusinessKey = new BusinessKeyModel
        {
            Variable = "violationId",
            Uniqueness = BusinessKeyUniqueness.All
        };
        model.InitialEventId = null;

        Assert.Empty(Validate(model));

        start.Idempotency.HeaderName = "Authorization";
        Assert.Contains(Validate(model), error =>
            error.Contains("is reserved", StringComparison.OrdinalIgnoreCase));
        start.Idempotency.HeaderName = "X-Correlation";
        Assert.Contains(Validate(model), error =>
            error.Contains("must differ from the message correlation header", StringComparison.OrdinalIgnoreCase));
        start.Idempotency.HeaderName = IdempotencyHeaders.Standard;
        start.Idempotency.Variable = "VIOLATIONID";
        Assert.Contains(Validate(model), error =>
            error.Contains("cannot also be an entry variable or output mapping", StringComparison.OrdinalIgnoreCase));
        start.Idempotency.Variable = "requestId";

        start.Message.OutputMappings[1].DefaultValue = null;
        var errors = Validate(model);
        Assert.Contains(errors, error => error.Contains("needs a body path", StringComparison.OrdinalIgnoreCase));

        start.Message.OutputMappings[1].DefaultValue = JsonSerializer.SerializeToElement("SA");
        start.Message.OutputMappings.Add(new MessageOutputMappingModel
        {
            Variable = "VIOLATIONID",
            Path = "duplicate",
            DataType = WorkflowVariableTypes.String,
            IsArray = false
        });
        Assert.Contains(Validate(model), error =>
            error.Contains("duplicated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_ValidatesTypedServiceAndMessageCatchMappings()
    {
        var model = DefinitionValidationTests.CreateOutputMappingModel();
        Assert.Empty(Validate(model));

        var service = model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsServiceTask(node.Type)).Service!;
        service.OutputMappings[0].Path = string.Empty;
        Assert.Contains(Validate(model), error =>
            error.Contains("response path", StringComparison.OrdinalIgnoreCase));

        service.OutputMappings[0].DefaultValue = JsonSerializer.SerializeToElement("approved");
        Assert.Empty(Validate(model));

        service.OutputMappings[0].DataType = WorkflowVariableTypes.Number;
        Assert.Contains(Validate(model), error =>
            error.Contains("must match process variable", StringComparison.OrdinalIgnoreCase));

        service.OutputMappings[0].DataType = WorkflowVariableTypes.String;
        var message = model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsMessageCatch(node.Type)).Message!;
        message.OutputMappings.Add(new MessageOutputMappingModel
        {
            Variable = "DECISION",
            Path = "duplicate",
            DataType = WorkflowVariableTypes.String,
            IsArray = false
        });
        Assert.Contains(Validate(model), error =>
            error.Contains("duplicated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_EnforcesWorkflowKeyInitialEventAndStartTopology()
    {
        var model = DefinitionValidationTests.LoadModel("votes-users-list.json");
        model.Id = string.Empty;
        model.InitialEventId = 999;
        var incoming = Clone(model.SequenceFlows.First(flow => flow.SourceRef == 2));
        incoming.Id = 999;
        incoming.TargetRef = 1;
        model.SequenceFlows.Add(incoming);
        model.SequenceFlows.Single(flow => flow.SourceRef == 1).Condition = "true";

        var errors = Validate(model);

        Assert.Contains(errors, error => error.Contains("Workflow id is required", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("initialEventId", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("cannot have incoming", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("must be unconditional", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(BpmnFlowNodeTypes.EndEvent)]
    [InlineData(BpmnFlowNodeTypes.ErrorEndEvent)]
    public void Validator_RejectsOutgoingFlowsFromTerminalEvents(string terminalType)
    {
        var model = DefinitionValidationTests.LoadModel("votes-users-list.json");
        var terminal = model.FlowNodes.First(node => BpmnFlowNodeTypes.IsEnd(node.Type));
        terminal.Type = terminalType;
        model.SequenceFlows.Add(new SequenceFlowModel
        {
            Id = model.SequenceFlows.Max(flow => flow.Id) + 1,
            Name = "Invalid terminal flow",
            SourceRef = terminal.Id,
            TargetRef = terminal.Id
        });

        Assert.Contains(Validate(model), error =>
            error.Contains($"End event #{terminal.Id} cannot have outgoing sequence flows", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(BpmnFlowNodeTypes.EndEvent)]
    [InlineData(BpmnFlowNodeTypes.ErrorEndEvent)]
    public void TypeInvariants_ClearFieldsThatDoNotBelongToTerminalEvents(string terminalType)
    {
        var html = ReadEditorSource();
        var match = Regex.Match(
            html,
            @"function applyTypeInvariants\(node\) \{[\s\S]*?(?=function nextCompletionPriority)");
        Assert.True(match.Success, "The editor type-invariant function was not found.");

        var node = new
        {
            type = terminalType,
            requiresClaim = true,
            claimMode = "fromNode",
            inheritClaimFromNodeId = 9,
            roles = new[] { "Manager" },
            variables = new[] { new { id = 1, name = "secret" } },
            assignee = "'alice'",
            multiInstance = new { mode = "parallel" },
            service = new { clientSecret = "secret-terminal" },
            assignments = new[] { new { variable = "result", expression = "1" } },
            scriptFormat = "javascript",
            script = "execution.setVariable('result', 1);",
            attachedToRef = 2,
            errorVariable = "failure",
            message = new { clientSecret = "secret-terminal" },
            businessKey = new { variable = "caseId", uniqueness = "all" },
            idempotency = new { headerName = "Idempotency-Key", variable = "requestId" }
        };

        var engine = new Engine();
        engine.Execute("""
            const NODE_TYPE = {
              START_EVENT: 'startEvent', MESSAGE_START_EVENT: 'messageStartEvent',
              END_EVENT: 'endEvent', ERROR_END_EVENT: 'errorEndEvent',
              USER_TASK: 'userTask', TASK: 'task', SERVICE_TASK: 'serviceTask',
              SCRIPT_TASK: 'scriptTask', EXCLUSIVE_GATEWAY: 'exclusiveGateway',
              ERROR_BOUNDARY_EVENT: 'errorBoundaryEvent', MESSAGE_CATCH_EVENT: 'intermediateMessageCatchEvent'
            };
            const CLAIM_MODE = { FRESH: 'fresh' };
            function isStartEventType(type) { return type === NODE_TYPE.START_EVENT; }
            function isMessageStartEventType(type) { return type === NODE_TYPE.MESSAGE_START_EVENT; }
            function isUserTaskType(type) { return type === NODE_TYPE.USER_TASK; }
            function isEndEventType(type) { return type === NODE_TYPE.END_EVENT || type === NODE_TYPE.ERROR_END_EVENT; }
            function isAutomaticType(type) { return type === NODE_TYPE.TASK; }
            function isGatewayType(type) { return type === NODE_TYPE.EXCLUSIVE_GATEWAY; }
            function isServiceTaskType(type) { return type === NODE_TYPE.SERVICE_TASK; }
            function isScriptTaskType(type) { return type === NODE_TYPE.SCRIPT_TASK; }
            function isBoundaryEventType(type) { return type === NODE_TYPE.ERROR_BOUNDARY_EVENT; }
            function isMessageCatchEventType(type) { return type === NODE_TYPE.MESSAGE_CATCH_EVENT; }
            function isSingleOutgoingType() { return false; }
            """);
        engine.Execute(match.Value);
        engine.SetValue("nodeJson", JsonSerializer.Serialize(node));
        using var normalized = JsonDocument.Parse(engine.Evaluate(
            "const node = JSON.parse(nodeJson); applyTypeInvariants(node); JSON.stringify(node);").AsString());
        var root = normalized.RootElement;

        Assert.False(root.GetProperty("requiresClaim").GetBoolean());
        Assert.Equal("fresh", root.GetProperty("claimMode").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("inheritClaimFromNodeId").ValueKind);
        Assert.Empty(root.GetProperty("roles").EnumerateArray());
        Assert.Empty(root.GetProperty("variables").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("assignee").ValueKind);
        Assert.Empty(root.GetProperty("assignments").EnumerateArray());
        foreach (var property in new[]
                 {
                     "multiInstance", "service", "scriptFormat", "script", "attachedToRef",
                     "errorVariable", "message", "businessKey", "idempotency"
                 })
        {
            Assert.False(root.TryGetProperty(property, out _), $"Terminal node retained '{property}'.");
        }
        Assert.DoesNotContain("secret-terminal", root.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void PlainEndEvent_RendersWithoutAnInnerIconAndConversionIsGuarded()
    {
        var html = ReadEditorSource();
        var iconMap = Regex.Match(
            html,
            @"const NODE_ICON_SYMBOL = \{[\s\S]*?\};(?=\s*function appendSvgTitle)");
        var appendIcon = Regex.Match(
            html,
            @"function appendNodeIcon\([\s\S]*?(?=function appendMultiInstanceMarker)");
        Assert.True(iconMap.Success, "The editor node-icon map was not found.");
        Assert.True(appendIcon.Success, "The editor node-icon renderer was not found.");

        var engine = new Engine();
        engine.Execute("""
            const NODE_TYPE = {
              START_EVENT: 'startEvent', MESSAGE_START_EVENT: 'messageStartEvent',
              END_EVENT: 'endEvent', ERROR_END_EVENT: 'errorEndEvent',
              ERROR_BOUNDARY_EVENT: 'errorBoundaryEvent', MESSAGE_CATCH_EVENT: 'intermediateMessageCatchEvent',
              USER_TASK: 'userTask', TASK: 'task', SERVICE_TASK: 'serviceTask',
              SCRIPT_TASK: 'scriptTask', EXCLUSIVE_GATEWAY: 'exclusiveGateway'
            };
            let created = 0;
            let appended = 0;
            function el(name, attrs) { created++; return { name, attrs }; }
            const group = { appendChild: function() { appended++; } };
            """);
        engine.Execute(iconMap.Value);
        engine.Execute(appendIcon.Value);

        Assert.True(engine.Evaluate(
            "appendNodeIcon(group, 'endEvent', 0, 0, 12) === null && created === 0 && appended === 0").AsBoolean());
        Assert.True(engine.Evaluate(
            "const icon = appendNodeIcon(group, 'errorEndEvent', 0, 0, 12); icon.attrs.href === '#icon-error' && created === 1 && appended === 1").AsBoolean());

        Assert.Contains(
            "isEndEventType(v) && outgoingFlows(node.id).length > 0",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "Remove all outgoing sequence flows before changing this node to an end event.",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            ".node.selected.endEvent circle.body",
            html,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_EnforcesVariableDefaultsAndEntryProcessCollisions()
    {
        var model = DefinitionValidationTests.LoadModel("votes-users-list.json");
        var start = model.FlowNodes.Single(node => node.Id == model.InitialEventId);
        start.Variables.Add(new VariableModel
        {
            Id = 98,
            Name = "VOTERS",
            DataType = WorkflowVariableTypes.String
        });
        start.Variables.Add(new VariableModel
        {
            Id = 99,
            Name = "score",
            DataType = WorkflowVariableTypes.Number,
            DefaultValue = JsonSerializer.SerializeToElement("invalid")
        });

        var errors = Validate(model);

        Assert.Contains(errors, error => error.Contains("collides with a process variable", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("defaultValue", StringComparison.OrdinalIgnoreCase)
            && error.Contains("score", StringComparison.OrdinalIgnoreCase));

        start.Variables[^1].DefaultValue = JsonSerializer.SerializeToElement(1);
        start.Variables[^1].Required = true;
        Assert.Contains(Validate(model), error =>
            error.Contains("cannot define a defaultValue", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> Validate(WorkflowModel model)
    {
        var html = ReadEditorSource();
        var match = Regex.Match(
            html,
            @"// BEGIN WORKFLOW SAVE VALIDATOR(?<code>[\s\S]*?)// END WORKFLOW SAVE VALIDATOR");
        Assert.True(match.Success, "The marked workflow save validator was not found.");

        var json = JsonSerializer.Serialize(model);
        var engine = new Engine();
        engine.Execute(match.Groups["code"].Value);
        engine.SetValue("candidateJson", json);
        var resultJson = engine.Evaluate(
            "JSON.stringify(validateModelForSave(JSON.parse(candidateJson)))").AsString();
        return JsonSerializer.Deserialize<List<string>>(resultJson) ?? [];
    }

    private static string ReadEditorSource()
    {
        var editorPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "flowbit-editor.html");
        return File.ReadAllText(editorPath);
    }

    private static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))
        ?? throw new InvalidOperationException("Fixture clone failed.");
}
