using System.Text.Json;
using System.Text.RegularExpressions;
using Jint;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

public sealed class EditorValidatorTests
{
    [Fact]
    public void EditorJavaScript_ParsesSuccessfully()
    {
        var html = ReadEditorSource();
        var match = Regex.Match(html, @"<script>(?<code>[\s\S]*?)</script>");
        Assert.True(match.Success, "The editor script was not found.");

        var exception = Record.Exception(() => Engine.PrepareScript(match.Groups["code"].Value));

        Assert.Null(exception);
    }

    [Fact]
    public void GatewayPriorityNormalization_DerivesOnlyAllMissingLegacyPriorities()
    {
        var html = ReadEditorSource();
        var match = Regex.Match(
            html,
            @"function nextConditionPriority[\s\S]*?(?=function migrateLegacyUserTaskDefaultFlows)");
        Assert.True(match.Success, "The gateway-priority normalization helpers were not found.");

        var engine = new Engine();
        engine.Execute("""
            function isGatewayType(type) { return type === 'exclusiveGateway'; }
            function outgoingFlows(sourceId) { return model.sequenceFlows.filter(flow => flow.sourceRef === sourceId); }
            let model = {
              flowNodes: [{ id: 3, type: 'exclusiveGateway' }],
              sequenceFlows: [
                { id: 301, sourceRef: 3, isDefault: false, conditionPriority: null },
                { id: 302, sourceRef: 3, isDefault: false, conditionPriority: null },
                { id: 303, sourceRef: 3, isDefault: true, conditionPriority: null }
              ]
            };
            """);
        engine.Execute(match.Value);
        using var derived = JsonDocument.Parse(engine.Evaluate(
            "normalizeExclusiveGatewayPriorities(); JSON.stringify(model.sequenceFlows);").AsString());
        Assert.Equal(1, derived.RootElement[0].GetProperty("conditionPriority").GetInt32());
        Assert.Equal(2, derived.RootElement[1].GetProperty("conditionPriority").GetInt32());
        Assert.Equal(JsonValueKind.Null, derived.RootElement[2].GetProperty("conditionPriority").ValueKind);

        engine.Execute("model.sequenceFlows[0].conditionPriority = 7; model.sequenceFlows[1].conditionPriority = null;");
        using var partial = JsonDocument.Parse(engine.Evaluate(
            "normalizeExclusiveGatewayPriorities(); JSON.stringify(model.sequenceFlows);").AsString());
        Assert.Equal(7, partial.RootElement[0].GetProperty("conditionPriority").GetInt32());
        Assert.Equal(JsonValueKind.Null, partial.RootElement[1].GetProperty("conditionPriority").ValueKind);
        Assert.Equal(8, engine.Evaluate("nextConditionPriority(3)").AsNumber());
    }

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
    public void Validator_RejectsClaimBypassRolesOnAnEngineOnlyFlow()
    {
        var model = DefinitionValidationTests.LoadModel("votes-users-list.json");
        var fallback = model.SequenceFlows.Single(flow => !flow.IsSelectable);
        fallback.CanActWithoutClaimRoles = ["Supervisor"];

        var errors = Validate(model);

        Assert.Contains(errors, error =>
            error.Contains("pure engine-only default", StringComparison.OrdinalIgnoreCase));
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
    public void Validator_ValidatesMessageCatchAuthenticationTopologyAndIdempotency()
    {
        var model = DefinitionValidationTests.CreateOutputMappingModel();
        var catchNode = model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsMessageCatch(node.Type));
        catchNode.Message!.DeliveryIdempotency = true;
        catchNode.Message.DeliveryIdempotencyHeaderName = IdempotencyHeaders.Standard;
        Assert.Empty(Validate(model));

        catchNode.Message.HeaderName = IdempotencyHeaders.Standard;
        Assert.Contains(Validate(model), error =>
            error.Contains("differ", StringComparison.OrdinalIgnoreCase));

        catchNode.Message.HeaderName = "X-Correlation";
        catchNode.Message.DeliveryIdempotencyHeaderName = "X-Delivery-Id";
        Assert.Empty(Validate(model));

        catchNode.Message.DeliveryIdempotencyHeaderName = "X-Client-Id";
        Assert.Contains(Validate(model), error =>
            error.Contains("reserved", StringComparison.OrdinalIgnoreCase));

        catchNode.Message.DeliveryIdempotencyHeaderName = "X-Delivery-Id";
        catchNode.Message.ClientSecret = "";
        Assert.Contains(Validate(model), error =>
            error.Contains("clientSecret", StringComparison.OrdinalIgnoreCase));

        catchNode.Message.ClientSecret = "secret";
        model.SequenceFlows.Single(flow => flow.SourceRef == catchNode.Id).Condition = "true";
        Assert.Contains(Validate(model), error =>
            error.Contains("unconditional", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ServiceTaskInspector_ExposesExtensibleConnectorDropdownWithRestSelected()
    {
        var html = ReadEditorSource();

        Assert.Contains("Connector type", html, StringComparison.Ordinal);
        Assert.Contains("{ value: SERVICE_CONNECTOR_TYPE.REST, label: \"REST\" }", html, StringComparison.Ordinal);
        Assert.Contains("type: SERVICE_CONNECTOR_TYPE.REST", html, StringComparison.Ordinal);
        Assert.Contains("{ value: NODE_TYPE.SERVICE_TASK, label: \"Service Task\" }", html, StringComparison.Ordinal);
        Assert.DoesNotContain("label: \"Service Task (REST)\"", html, StringComparison.Ordinal);
        Assert.Contains("delete node.attachedToRef;", html, StringComparison.Ordinal);
        Assert.Contains("delete node.errorVariable;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_DefaultsMissingLegacyConnectorAndRejectsUnsupportedConnector()
    {
        var model = DefinitionValidationTests.CreateOutputMappingModel();
        var service = model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsServiceTask(node.Type)).Service!;
        service.Type = null;

        Assert.Contains(Validate(model), error =>
            error.Contains("unsupported connector type", StringComparison.OrdinalIgnoreCase));

        service.Type = "soap";
        Assert.Contains(Validate(model), error =>
            error.Contains("unsupported connector type", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_RejectsInvalidRestTransportConfiguration()
    {
        var model = DefinitionValidationTests.CreateOutputMappingModel();
        var service = model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsServiceTask(node.Type)).Service!;
        service.Url = "ftp://tests.local/work";
        service.Method = "TRACE";
        service.TimeoutSeconds = 0;
        service.Headers =
        [
            new ServiceHeaderModel { Name = "Bad Header", Value = "value" },
            new ServiceHeaderModel { Name = "Content-Length", Value = "10" },
            new ServiceHeaderModel { Name = "Content-Type", Value = "invalid" }
        ];

        var errors = Validate(model);

        Assert.Contains(errors, error => error.Contains("absolute HTTP(S)", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("unsupported HTTP method", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("positive integer", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("header name", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("request-framing", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("Content-Type", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_AcceptsTemplatedRestContentTypeForRuntimeValidation()
    {
        var model = DefinitionValidationTests.CreateOutputMappingModel();
        model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsServiceTask(node.Type)).Service!.Headers =
            [new ServiceHeaderModel { Name = "Content-Type", Value = "${contentType}" }];

        Assert.Empty(Validate(model));
    }

    [Fact]
    public void Validator_RejectsServiceOutputTargetCollisionsAndWrongProcessTypes()
    {
        var model = DefinitionValidationTests.CreateOutputMappingModel();
        var service = model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsServiceTask(node.Type)).Service!;
        service.StatusVariable = "decision";

        Assert.Contains(Validate(model), error =>
            error.Contains("scalar number", StringComparison.OrdinalIgnoreCase));

        service.StatusVariable = "httpStatus";
        service.OutputMappings.Add(new ServiceOutputMappingModel
        {
            Variable = "HTTPSTATUS",
            Path = "status",
            DataType = WorkflowVariableTypes.Number,
            IsArray = false
        });
        Assert.Contains(Validate(model), error =>
            error.Contains("cannot also be an output mapping", StringComparison.OrdinalIgnoreCase));
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
        terminal.ErrorCode = terminalType == BpmnFlowNodeTypes.ErrorEndEvent ? "INVALID_TERMINAL_FLOW" : null;
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
    public void Validator_RejectsTerminalEventsWithoutIncomingFlows(string terminalType)
    {
        var model = DefinitionValidationTests.LoadModel("votes-users-list.json");
        var terminal = model.FlowNodes.First(node => BpmnFlowNodeTypes.IsEnd(node.Type));
        terminal.Type = terminalType;
        terminal.ErrorCode = terminalType == BpmnFlowNodeTypes.ErrorEndEvent ? "ORPHAN_FAULT" : null;
        var replacement = new FlowNodeModel { Id = 999, Name = "Reachable end", Type = BpmnFlowNodeTypes.EndEvent };
        model.FlowNodes.Add(replacement);
        foreach (var flow in model.SequenceFlows.Where(flow => flow.TargetRef == terminal.Id)) flow.TargetRef = replacement.Id;

        Assert.Contains(Validate(model), error =>
            error.Contains($"End event #{terminal.Id} must have at least one incoming", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_ValidatesErrorEndCodeAndDescription()
    {
        var model = DefinitionValidationTests.LoadModel("votes-users-list.json");
        var terminal = model.FlowNodes.First(node => BpmnFlowNodeTypes.IsEnd(node.Type));
        terminal.Type = BpmnFlowNodeTypes.ErrorEndEvent;
        terminal.ErrorCode = "BAD CODE";
        terminal.ErrorDescription = new string('x', ErrorEndConstraints.MaxDescriptionLength + 1);

        var errors = Validate(model);

        Assert.Contains(errors, error => error.Contains("errorCode", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("errorDescription", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("default")]
    [InlineData("condition")]
    [InlineData("roles")]
    [InlineData("completion")]
    [InlineData("cancel")]
    public void Validator_RejectsIgnoredErrorBoundaryFlowMetadata(string metadata)
    {
        var model = CreateBoundaryModel();
        var flow = model.SequenceFlows.Single(candidate => candidate.SourceRef == 3);
        if (metadata == "default") flow.IsDefault = true;
        if (metadata == "condition") flow.Condition = "false";
        if (metadata == "roles") flow.Roles = ["Operator"];
        if (metadata == "completion") flow.CompletionCondition = "true";
        if (metadata == "cancel") flow.CancelRemainingInstances = true;

        Assert.Contains(Validate(model), error =>
            error.Contains("Error boundary event #3", StringComparison.OrdinalIgnoreCase)
            && error.Contains("unconditional", StringComparison.OrdinalIgnoreCase));
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
            errorCode = "TERMINAL_FAULT",
            errorDescription = "Terminal description.",
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
            function isErrorEndEventType(type) { return type === NODE_TYPE.ERROR_END_EVENT; }
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
        if (terminalType == BpmnFlowNodeTypes.ErrorEndEvent)
        {
            Assert.Equal("TERMINAL_FAULT", root.GetProperty("errorCode").GetString());
            Assert.Equal("Terminal description.", root.GetProperty("errorDescription").GetString());
        }
        else
        {
            Assert.False(root.TryGetProperty("errorCode", out _));
            Assert.False(root.TryGetProperty("errorDescription", out _));
        }
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
            "const icon = appendNodeIcon(group, 'errorEndEvent', 0, 0, 12); icon.attrs.href === '#icon-error-throw' && created === 1 && appended === 1").AsBoolean());
        Assert.True(engine.Evaluate(
            "const boundaryIcon = appendNodeIcon(group, 'errorBoundaryEvent', 0, 0, 12); boundaryIcon.attrs.href === '#icon-error-catch' && created === 2 && appended === 2").AsBoolean());
        Assert.Contains("id=\"icon-error-throw\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"icon-error-catch\"", html, StringComparison.Ordinal);
        Assert.Matches("icon-error-throw[^>]*>[\\s\\S]*?fill=\"currentColor\"", html);
        Assert.Matches("icon-error-catch[^>]*>[\\s\\S]*?fill=\"none\"", html);

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

    [Fact]
    public void Validator_EnforcesNullableProcessVariableContract()
    {
        var model = DefinitionValidationTests.LoadModel("votes-users-list.json");
        model.Variables.Add(new VariableModel
        {
            Id = 98,
            Name = "optionalScore",
            DataType = WorkflowVariableTypes.Number,
            Nullable = true,
            Validation = "optionalScore > 0"
        });
        Assert.Empty(Validate(model));

        var processVariable = model.Variables.Single(variable => variable.Name == "optionalScore");
        processVariable.Nullable = false;
        Assert.Contains(Validate(model), error =>
            error.Contains("must have a defaultValue", StringComparison.OrdinalIgnoreCase));

        processVariable.Nullable = true;
        processVariable.DefaultValue = JsonSerializer.SerializeToElement("invalid");
        Assert.Contains(Validate(model), error =>
            error.Contains("does not match number", StringComparison.OrdinalIgnoreCase));

        processVariable.DefaultValue = null;
        var start = model.FlowNodes.Single(node => node.Id == model.InitialEventId);
        start.Variables.Add(new VariableModel
        {
            Id = 99,
            Name = "nullableInput",
            DataType = WorkflowVariableTypes.String,
            Nullable = true
        });
        Assert.Contains(Validate(model), error =>
            error.Contains("only for process variables", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_AcceptsFlowInfoInGatewayCompletionAndNCalcScriptExpressions()
    {
        var model = DefinitionValidationTests.LoadModel("votes-users-list.json");
        model.SequenceFlows.Single(flow => flow.Id == 201).CompletionCondition =
            "CountFlow(201) >= requiredApprovals and FlowInfo(201, 'actions.count') >= 1";

        var route = new FlowNodeModel
        {
            Id = 6,
            Name = "Route by confirmer",
            Type = BpmnFlowNodeTypes.ExclusiveGateway
        };
        var audit = new FlowNodeModel
        {
            Id = 7,
            Name = "Capture flow evidence",
            Type = BpmnFlowNodeTypes.ScriptTask,
            ScriptFormat = ScriptFormats.NCalc,
            Assignments =
            [
                new AssignmentModel
                {
                    Variable = "voteResults",
                    Expression = "FlowInfo(201, 'all')"
                }
            ]
        };
        model.FlowNodes.Add(route);
        model.FlowNodes.Add(audit);

        model.SequenceFlows.Single(flow => flow.Id == 204).TargetRef = route.Id;
        model.SequenceFlows.Add(new SequenceFlowModel
        {
            Id = 206,
            Name = "Manager route",
            SourceRef = route.Id,
            TargetRef = audit.Id,
            Condition = "Contains(FlowInfo(201, 'actions.last.userRoles'), 'Manager')",
            ConditionPriority = 1
        });
        model.SequenceFlows.Add(new SequenceFlowModel
        {
            Id = 208,
            Name = "Default route",
            SourceRef = route.Id,
            TargetRef = audit.Id,
            IsDefault = true
        });
        model.SequenceFlows.Add(new SequenceFlowModel
        {
            Id = 207,
            Name = "Continue",
            SourceRef = audit.Id,
            TargetRef = 2
        });

        Assert.Empty(Validate(model));
    }

    [Fact]
    public void Validator_RejectsInvalidFlowInfoSignatureIdAndPath()
    {
        var model = DefinitionValidationTests.LoadModel("votes-users-list.json");
        model.SequenceFlows.Single(flow => flow.Id == 201).CompletionCondition =
            "FlowInfo(201) or FlowInfo(flowId, 'actions.count') or " +
            "FlowInfo(999, 'actions.count') or FlowInfo(201, 'actions.users')";

        var errors = Validate(model);

        Assert.Contains(errors, error =>
            error.Contains("exactly two literal arguments", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error =>
            error.Contains("unknown sequence flow #999", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error =>
            error.Contains("path 'actions.users' is not supported", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_RejectsFlowInfoInUnsupportedNCalcContexts()
    {
        var model = DefinitionValidationTests.LoadModel("votes-users-list.json");
        model.Variables.Single(variable => variable.Name == "requiredApprovals").Validation =
            "FlowInfo(201, 'actions.count') > 0";
        model.FlowNodes.Single(node => node.Id == 5).AssigneeExpression =
            "FlowInfo(201, 'actions.last.user')";
        model.SequenceFlows.Single(flow => flow.Id == 204).Condition =
            "FlowInfo(201, 'traversals.count') > 0";

        var errors = Validate(model);

        Assert.Contains(errors, error =>
            error.Contains("Validation for variable 'requiredApprovals'", StringComparison.OrdinalIgnoreCase)
            && error.Contains("cannot use FlowInfo", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error =>
            error.Contains("assignee expression", StringComparison.OrdinalIgnoreCase)
            && error.Contains("cannot use FlowInfo", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error =>
            error.Contains("Sequence flow #204 condition", StringComparison.OrdinalIgnoreCase)
            && error.Contains("cannot use FlowInfo", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_IgnoresFlowInfoTextInsideStringLiterals()
    {
        var model = DefinitionValidationTests.LoadModel("votes-users-list.json");
        model.Variables.Single(variable => variable.Name == "requiredApprovals").Validation =
            "label == 'FlowInfo(201, ''actions.count'')'";

        Assert.Empty(Validate(model));
    }

    [Fact]
    public void Validator_AcceptsFlowInfoPathsCaseInsensitivelyLikeTheRuntime()
    {
        var model = DefinitionValidationTests.LoadModel("votes-users-list.json");
        model.SequenceFlows.Single(flow => flow.Id == 201).CompletionCondition =
            "Contains(FlowInfo(201, 'AcTiOnS.LaSt.UsErRoLeS'), 'Manager')";

        Assert.Empty(Validate(model));
    }

    [Fact]
    public void EditorHints_ShowFlowInfoForEachSupportedAuthoringSurface()
    {
        var html = ReadEditorSource();

        Assert.Contains("Contains(FlowInfo(201, 'actions.last.userRoles'), 'Manager')", html, StringComparison.Ordinal);
        Assert.Contains("FlowInfo(201, 'actions.last.userRoles')", html, StringComparison.Ordinal);
        Assert.Contains("execution.getFlowInfo(201).actions.last.userRoles", html, StringComparison.Ordinal);
        Assert.Contains("CountFlow/PercentFlow use this multi-instance execution", html, StringComparison.Ordinal);
        Assert.Contains("action-time userRoles", html, StringComparison.Ordinal);
        Assert.Contains("parentInterrupt row", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_AcceptsCanonicalNCalcAndJavaScriptScriptTasks()
    {
        var ncalc = BuildScriptTaskModel();
        Assert.Empty(Validate(ncalc));

        var javascript = BuildScriptTaskModel();
        var node = javascript.FlowNodes.Single(candidate => candidate.Id == 2);
        node.ScriptFormat = ScriptFormats.JavaScript;
        node.Assignments = [];
        node.Script = "execution.setVariable('result', 42);";
        node.UsesFlowInfo = false;
        Assert.Empty(Validate(javascript));
    }

    [Theory]
    [InlineData("unknownFormat")]
    [InlineData("mixedJavaScript")]
    [InlineData("missingJavaScriptBody")]
    [InlineData("missingFlag")]
    [InlineData("disabledDirectFlowInfo")]
    [InlineData("undeclaredAssignment")]
    [InlineData("blankExpression")]
    [InlineData("ncalcFlowInfoFlag")]
    [InlineData("conditionalExit")]
    [InlineData("roleExit")]
    public void Validator_RejectsMalformedScriptTaskAuthoring(string scenario)
    {
        var model = BuildScriptTaskModel();
        var node = model.FlowNodes.Single(candidate => candidate.Id == 2);
        var flow = model.SequenceFlows.Single(candidate => candidate.SourceRef == 2);
        switch (scenario)
        {
            case "unknownFormat":
                node.ScriptFormat = "python";
                break;
            case "mixedJavaScript":
                node.ScriptFormat = ScriptFormats.JavaScript;
                node.Script = "execution.setVariable('result', 1);";
                node.UsesFlowInfo = false;
                break;
            case "missingJavaScriptBody":
                node.ScriptFormat = ScriptFormats.JavaScript;
                node.Assignments = [];
                node.Script = " ";
                node.UsesFlowInfo = false;
                break;
            case "missingFlag":
                node.ScriptFormat = ScriptFormats.JavaScript;
                node.Assignments = [];
                node.Script = "execution.setVariable('result', 1);";
                node.UsesFlowInfo = null;
                break;
            case "disabledDirectFlowInfo":
                node.ScriptFormat = ScriptFormats.JavaScript;
                node.Assignments = [];
                node.Script = "execution.setVariable('result', execution.getFlowInfo(101).actions.count);";
                node.UsesFlowInfo = false;
                break;
            case "undeclaredAssignment":
                node.Assignments.Single().Variable = "missing";
                break;
            case "blankExpression":
                node.Assignments.Single().Expression = " ";
                break;
            case "ncalcFlowInfoFlag":
                node.UsesFlowInfo = true;
                break;
            case "conditionalExit":
                flow.Condition = "result > 0";
                break;
            case "roleExit":
                flow.Roles = ["admin"];
                break;
        }

        Assert.NotEmpty(Validate(model));
    }

    [Fact]
    public void Editor_ExposesExplicitJavaScriptFlowInfoCapability()
    {
        var html = ReadEditorSource();

        Assert.Contains("Enable instance-wide FlowInfo evidence", html, StringComparison.Ordinal);
        Assert.Contains("usesFlowInfo", html, StringComparison.Ordinal);
        Assert.Contains("Dynamic <code>eval</code>/<code>Function</code> compilation is disabled", html, StringComparison.Ordinal);
    }

    private static WorkflowModel BuildScriptTaskModel() => new()
    {
        Id = "editor-script-task",
        Name = "Editor script task",
        InitialEventId = 1,
        Variables =
        [
            new VariableModel
            {
                Id = 1,
                Name = "result",
                DataType = WorkflowVariableTypes.Number,
                DefaultValue = JsonSerializer.SerializeToElement(0)
            }
        ],
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel
            {
                Id = 2,
                Name = "Calculate",
                Type = BpmnFlowNodeTypes.ScriptTask,
                ScriptFormat = ScriptFormats.NCalc,
                Assignments = [new AssignmentModel { Variable = "result", Expression = "40 + 2" }],
                UsesFlowInfo = false
            },
            new FlowNodeModel { Id = 3, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 }
        ]
    };

    [Fact]
    public void Validator_AcceptsExclusiveGatewayWithSeveralIncomingPathsAndOrderedFallback()
    {
        var model = CreateExclusiveGatewayModel();
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 6,
            Name = "Alternative incoming",
            Type = BpmnFlowNodeTypes.Task
        });
        model.SequenceFlows.Add(new SequenceFlowModel
        {
            Id = 601,
            SourceRef = 6,
            TargetRef = 3
        });

        Assert.Empty(Validate(model));
    }

    [Fact]
    public void Validator_RejectsExclusiveGatewayWithoutExactlyOneDefaultOrTwoOutputs()
    {
        var noDefault = CreateExclusiveGatewayModel();
        var formerDefault = noDefault.SequenceFlows.Single(flow => flow.Id == 302);
        formerDefault.IsDefault = false;
        formerDefault.Condition = "false";
        formerDefault.ConditionPriority = 2;
        Assert.Contains(Validate(noDefault), error =>
            error.Contains("exactly one default", StringComparison.OrdinalIgnoreCase));

        var pureMerge = CreateExclusiveGatewayModel();
        pureMerge.SequenceFlows.RemoveAll(flow => flow.Id == 301);
        Assert.Contains(Validate(pureMerge), error =>
            error.Contains("at least two outgoing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_RejectsMissingInvalidAndDuplicateGatewayBranchMetadata()
    {
        var missing = CreateExclusiveGatewayModel();
        missing.SequenceFlows.Single(flow => flow.Id == 301).Condition = null;
        missing.SequenceFlows.Single(flow => flow.Id == 301).ConditionPriority = null;
        var missingErrors = Validate(missing);
        Assert.Contains(missingErrors, error => error.Contains("must define a condition", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(missingErrors, error => error.Contains("positive integer conditionPriority", StringComparison.OrdinalIgnoreCase));

        var duplicate = CreateExclusiveGatewayModel();
        duplicate.SequenceFlows.Add(new SequenceFlowModel
        {
            Id = 303,
            SourceRef = 3,
            TargetRef = 4,
            Condition = "true",
            ConditionPriority = 1
        });
        Assert.Contains(Validate(duplicate), error =>
            error.Contains("duplicate conditionPriority", StringComparison.OrdinalIgnoreCase));

        var defaultMetadata = CreateExclusiveGatewayModel();
        defaultMetadata.SequenceFlows.Single(flow => flow.Id == 302).ConditionPriority = 2;
        Assert.Contains(Validate(defaultMetadata), error =>
            error.Contains("cannot define a condition or conditionPriority", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_RejectsIgnoredGatewayMetadataAndPriorityOnOtherNodeTypes()
    {
        var gatewayMetadata = CreateExclusiveGatewayModel();
        gatewayMetadata.SequenceFlows.Single(flow => flow.Id == 301).Roles = ["Manager"];
        Assert.Contains(Validate(gatewayMetadata), error =>
            error.Contains("user-action or multi-instance metadata", StringComparison.OrdinalIgnoreCase));

        var nonGatewayPriority = CreateExclusiveGatewayModel();
        nonGatewayPriority.SequenceFlows.Single(flow => flow.Id == 101).ConditionPriority = 9;
        Assert.Contains(Validate(nonGatewayPriority), error =>
            error.Contains("only when leaving an exclusive gateway", StringComparison.OrdinalIgnoreCase));
    }

    private static WorkflowModel CreateExclusiveGatewayModel() => new()
    {
        Id = "editor-exclusive-gateway",
        Name = "Editor exclusive gateway",
        InitialEventId = 1,
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel { Id = 2, Name = "Prepare", Type = BpmnFlowNodeTypes.Task },
            new FlowNodeModel { Id = 3, Name = "Route", Type = BpmnFlowNodeTypes.ExclusiveGateway },
            new FlowNodeModel { Id = 4, Name = "Matched", Type = BpmnFlowNodeTypes.EndEvent },
            new FlowNodeModel { Id = 5, Name = "Fallback", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
            new SequenceFlowModel
            {
                Id = 301,
                SourceRef = 3,
                TargetRef = 4,
                Condition = "true",
                ConditionPriority = 1
            },
            new SequenceFlowModel { Id = 302, SourceRef = 3, TargetRef = 5, IsDefault = true }
        ]
    };

    private static WorkflowModel CreateBoundaryModel() => new()
    {
        Id = "editor-boundary",
        Name = "Editor boundary",
        InitialEventId = 1,
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel
            {
                Id = 2,
                Name = "Call service",
                Type = BpmnFlowNodeTypes.ServiceTask,
                Service = new ServiceTaskModel
                {
                    Url = "https://tests.local/service",
                    Method = "POST",
                    TimeoutSeconds = 10
                }
            },
            new FlowNodeModel
            {
                Id = 3,
                Name = "Service error",
                Type = BpmnFlowNodeTypes.ErrorBoundaryEvent,
                AttachedToRef = 2
            },
            new FlowNodeModel { Id = 4, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 4 },
            new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4 }
        ]
    };

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
