using System.Text.Json;
using System.Text.RegularExpressions;
using Jint;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

public sealed class EditorValidatorTests
{
    [Theory]
    [InlineData("parallel-gateway-simple.json")]
    [InlineData("parallel-gateway-complex.json")]
    public void Validator_AcceptsParallelGatewayExample(string fileName)
    {
        var model = DefinitionValidationTests.LoadModel(fileName);

        Assert.Empty(Validate(model));
    }

    [Theory]
    [InlineData("examples/01-async-before-after.json")]
    [InlineData("examples/02-intermediate-timer-delay.json")]
    [InlineData("examples/03-recurring-timer-start.json")]
    [InlineData("examples/04-absolute-timer-start.json")]
    [InlineData("examples/05-user-task-reminder-and-deadline.json")]
    [InlineData("examples/06-multi-instance-reminder.json")]
    public void Validator_AcceptsDurableAsyncAndTimerExample(string fileName)
    {
        var model = DefinitionValidationTests.LoadModel(fileName);

        Assert.Empty(Validate(model));
    }

    [Fact]
    public void EditorJavaScript_ParsesSuccessfully()
    {
        var html = ReadEditorSource();
        var matches = Regex.Matches(
            html,
            @"<script(?:\s[^>]*)?>(?<code>[\s\S]*?)</script>");
        Assert.NotEmpty(matches.Cast<Match>());

        Assert.All(matches.Cast<Match>(), match =>
        {
            var exception = Record.Exception(
                () => Engine.PrepareScript(match.Groups["code"].Value));
            Assert.Null(exception);
        });
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
    public void Validator_EnforcesRequiredAssignmentContracts()
    {
        var task = new FlowNodeModel
        {
            Id = 2,
            Name = "Review",
            Type = BpmnFlowNodeTypes.UserTask,
            RequiresAssignment = true,
            AssignmentMode = AssignmentModes.FromNode,
            InheritAssignmentFromNodeId = 2
        };
        var model = new WorkflowModel
        {
            Id = "editor-required-assignment",
            Name = "Editor required assignment",
            InitialEventId = 1,
            TaskDistribution = new TaskDistributionModel
            {
                ClientId = "distributor",
                ClientSecret = "secret"
            },
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                task,
                new FlowNodeModel { Id = 3, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 }
            ]
        };
        Assert.Empty(Validate(model));

        task.RequiresClaim = true;
        task.AssigneeExpression = "'alice'";
        model.TaskDistribution = null;
        var errors = Validate(model);
        Assert.Contains(errors, error => error.Contains("taskDistribution", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("requiresClaim", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("assignee expression", StringComparison.OrdinalIgnoreCase));
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
    public void Validator_RequiresUniqueMessageStartExternalIdsAndRejectsCatchIdempotency()
    {
        var model = DefinitionValidationTests.LoadModel("workflow-message-start.json");
        var first = model.FlowNodes.Single(node => BpmnFlowNodeTypes.IsMessageStart(node.Type));
        var second = JsonSerializer.Deserialize<FlowNodeModel>(JsonSerializer.Serialize(first))!;
        second.Id = 90;
        second.ExternalId = "MESSAGE-START";
        model.FlowNodes.Add(second);
        model.SequenceFlows.Add(new SequenceFlowModel
        {
            Id = 900,
            SourceRef = second.Id,
            TargetRef = model.SequenceFlows.Single(flow => flow.SourceRef == first.Id).TargetRef
        });

        Assert.Empty(Validate(model));

        second.ExternalId = null;
        Assert.Contains(Validate(model), error =>
            error.Contains("must have an externalId", StringComparison.OrdinalIgnoreCase));

        second.ExternalId = first.ExternalId;
        Assert.Contains(Validate(model), error =>
            error.Contains("duplicated", StringComparison.OrdinalIgnoreCase));

        second.ExternalId = "MESSAGE-START";
        first.Message!.DeliveryIdempotency = true;
        Assert.Contains(Validate(model), error =>
            error.Contains("node-level idempotency", StringComparison.OrdinalIgnoreCase));
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

    [Fact]
    public void Validator_AcceptsBoundaryTriggeredScopedInterrupt()
    {
        var model = new WorkflowModel
        {
            Id = "editor-boundary-scoped-interrupt",
            Name = "Editor boundary scoped interrupt",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Fork", Type = BpmnFlowNodeTypes.ParallelGateway },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Call service",
                    Type = BpmnFlowNodeTypes.ServiceTask,
                    Service = new ServiceTaskModel
                    {
                        Url = "https://tests.local/service",
                        Method = "POST",
                        TimeoutSeconds = 10
                    }
                },
                new FlowNodeModel { Id = 4, Name = "Sibling", Type = BpmnFlowNodeTypes.Task },
                new FlowNodeModel
                {
                    Id = 5,
                    Name = "Service error",
                    Type = BpmnFlowNodeTypes.ErrorBoundaryEvent,
                    AttachedToRef = 3
                },
                new FlowNodeModel
                {
                    Id = 6,
                    Name = "Interrupt",
                    Type = BpmnFlowNodeTypes.ScopedInterruptEvent,
                    GatewayRef = 2
                },
                new FlowNodeModel { Id = 7, Name = "Service end", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 8, Name = "Sibling end", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 9, Name = "Recovery end", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 202, SourceRef = 2, TargetRef = 4 },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 7 },
                new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 8 },
                new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 6 },
                new SequenceFlowModel { Id = 601, SourceRef = 6, TargetRef = 9 }
            ]
        };

        Assert.Empty(Validate(model));
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
            requiresAssignment = true,
            assignmentMode = "fromNode",
            inheritAssignmentFromNodeId = 9,
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
              TIMER_START_EVENT: 'timerStartEvent',
              END_EVENT: 'endEvent', ERROR_END_EVENT: 'errorEndEvent',
              USER_TASK: 'userTask', TASK: 'task', SERVICE_TASK: 'serviceTask',
              SCRIPT_TASK: 'scriptTask', EXCLUSIVE_GATEWAY: 'exclusiveGateway',
              PARALLEL_GATEWAY: 'parallelGateway', INCLUSIVE_GATEWAY: 'inclusiveGateway',
              COMPLEX_GATEWAY: 'complexGateway', SCOPED_INTERRUPT_EVENT: 'scopedInterruptEvent',
              ERROR_BOUNDARY_EVENT: 'errorBoundaryEvent',
              TIMER_BOUNDARY_EVENT: 'timerBoundaryEvent',
              MESSAGE_CATCH_EVENT: 'intermediateMessageCatchEvent',
              TIMER_CATCH_EVENT: 'intermediateTimerCatchEvent'
            };
            const CLAIM_MODE = { FRESH: 'fresh' };
            const ASSIGNMENT_MODE = { FRESH: 'fresh', PREVIOUS: 'previous', FROM_NODE: 'fromNode' };
            function isStartEventType(type) { return type === NODE_TYPE.START_EVENT; }
            function isMessageStartEventType(type) { return type === NODE_TYPE.MESSAGE_START_EVENT; }
            function isUserTaskType(type) { return type === NODE_TYPE.USER_TASK; }
            function isEndEventType(type) { return type === NODE_TYPE.END_EVENT || type === NODE_TYPE.ERROR_END_EVENT; }
            function isErrorEndEventType(type) { return type === NODE_TYPE.ERROR_END_EVENT; }
            function isAutomaticType(type) { return type === NODE_TYPE.TASK; }
            function isGatewayType(type) { return type === NODE_TYPE.EXCLUSIVE_GATEWAY; }
            function isComplexGatewayType(type) { return type === NODE_TYPE.COMPLEX_GATEWAY; }
            function isScopedInterruptEventType(type) { return type === NODE_TYPE.SCOPED_INTERRUPT_EVENT; }
            function isServiceTaskType(type) { return type === NODE_TYPE.SERVICE_TASK; }
            function isScriptTaskType(type) { return type === NODE_TYPE.SCRIPT_TASK; }
            function isAsyncCapableTaskType(type) {
              return isUserTaskType(type) || isAutomaticType(type) ||
                isServiceTaskType(type) || isScriptTaskType(type);
            }
            function isTimerStartEventType(type) { return type === NODE_TYPE.TIMER_START_EVENT; }
            function isTimerBoundaryEventType(type) { return type === NODE_TYPE.TIMER_BOUNDARY_EVENT; }
            function isTimerCatchEventType(type) { return type === NODE_TYPE.TIMER_CATCH_EVENT; }
            function isTimerEventType(type) {
              return isTimerStartEventType(type) || isTimerBoundaryEventType(type) ||
                isTimerCatchEventType(type);
            }
            function isErrorBoundaryEventType(type) { return type === NODE_TYPE.ERROR_BOUNDARY_EVENT; }
            function isBoundaryEventType(type) {
              return isErrorBoundaryEventType(type) || isTimerBoundaryEventType(type);
            }
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
        Assert.False(root.GetProperty("requiresAssignment").GetBoolean());
        Assert.Equal("fresh", root.GetProperty("assignmentMode").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("inheritAssignmentFromNodeId").ValueKind);
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
    public void Validator_RejectsGatewayThatCombinesMergeAndSplitTopology()
    {
        var model = CreateExclusiveGatewayModel();
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 6,
            Name = "Alternative incoming",
            Type = BpmnFlowNodeTypes.StartEvent
        });
        model.SequenceFlows.Add(new SequenceFlowModel
        {
            Id = 601,
            SourceRef = 6,
            TargetRef = 3
        });

        Assert.Contains(Validate(model), error =>
            error.Contains("use two adjacent gateways", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_RequiresDefaultForExclusiveSplitAndAcceptsExclusiveMerge()
    {
        var noDefault = CreateExclusiveGatewayModel();
        var formerDefault = noDefault.SequenceFlows.Single(flow => flow.Id == 302);
        formerDefault.IsDefault = false;
        formerDefault.Condition = "false";
        formerDefault.ConditionPriority = 2;
        Assert.Contains(Validate(noDefault), error =>
            error.Contains("exactly one default", StringComparison.OrdinalIgnoreCase));

        var pureMerge = CreateExclusiveGatewayModel();
        pureMerge.FlowNodes.Add(new FlowNodeModel
        {
            Id = 6,
            Name = "Alternative incoming",
            Type = BpmnFlowNodeTypes.StartEvent
        });
        pureMerge.SequenceFlows.Add(new SequenceFlowModel { Id = 601, SourceRef = 6, TargetRef = 3 });
        pureMerge.SequenceFlows.RemoveAll(flow => flow.Id == 301);
        pureMerge.FlowNodes.RemoveAll(node => node.Id == 4);
        var continuation = pureMerge.SequenceFlows.Single(flow => flow.Id == 302);
        continuation.IsDefault = false;
        Assert.Empty(Validate(pureMerge));
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

    [Fact]
    public void Editor_ExposesInclusiveComplexAndScopedInterruptAuthoring()
    {
        var html = ReadEditorSource();

        Assert.Contains("{ value: NODE_TYPE.INCLUSIVE_GATEWAY, label: \"Inclusive Gateway\" }", html, StringComparison.Ordinal);
        Assert.Contains("{ value: NODE_TYPE.COMPLEX_GATEWAY, label: \"Complex Gateway\" }", html, StringComparison.Ordinal);
        Assert.Contains("{ value: NODE_TYPE.SCOPED_INTERRUPT_EVENT, label: \"Scoped Interrupt Event\" }", html, StringComparison.Ordinal);
        Assert.Contains("id=\"icon-inclusive-gateway\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"icon-complex-gateway\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"icon-scoped-interrupt\"", html, StringComparison.Ordinal);
        Assert.Contains("IncomingCount(301)", html, StringComparison.Ordinal);
        Assert.Contains("TotalIncomingCount()", html, StringComparison.Ordinal);
        Assert.Contains("gateway.waitingForStart", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Loader_RoundTripsComplexAndScopedInterruptFieldsWithoutLegacyMetadata()
    {
        var html = ReadEditorSource();
        var match = Regex.Match(
            html,
            @"function loadFromObject\(obj\) \{[\s\S]*?(?=/\* ---------- DOM helper ---------- \*/)");
        Assert.True(match.Success, "The editor workflow loader was not found.");

        var engine = new Engine();
        engine.Execute(
            """
            const NODE_TYPE = {
              START_EVENT: 'startEvent', USER_TASK: 'userTask',
              COMPLEX_GATEWAY: 'complexGateway',
              SCOPED_INTERRUPT_EVENT: 'scopedInterruptEvent'
            };
            const CLAIM_MODE = { FRESH: 'fresh' };
            const CLAIM_MODE_OPTIONS = [{ value: 'fresh' }];
            const ASSIGNMENT_MODE = { FRESH: 'fresh' };
            const ASSIGNMENT_MODE_OPTIONS = [{ value: 'fresh' }];
            const SCRIPT_FORMAT = { NCALC: 'ncalc', JAVASCRIPT: 'javascript' };
            let model = null;
            function normalizeVariable(value) { return value; }
            function normalizeRoles(value) { return Array.isArray(value) ? value : []; }
            function isServiceTaskType(type) { return type === 'serviceTask'; }
            function isScriptTaskType(type) { return type === 'scriptTask'; }
            function isErrorEndEventType(type) { return type === 'errorEndEvent'; }
            function isComplexGatewayType(type) { return type === NODE_TYPE.COMPLEX_GATEWAY; }
            function isScopedInterruptEventType(type) { return type === NODE_TYPE.SCOPED_INTERRUPT_EVENT; }
            function isMessageCatchEventType(type) { return type === 'intermediateMessageCatchEvent'; }
            function isMessageStartEventType(type) { return type === 'messageStartEvent'; }
            function isStartEventType(type) { return type === NODE_TYPE.START_EVENT; }
            function normalizeMultiInstanceForLoad(value) { return value; }
            function normalizeService(value) { return value; }
            function normalizeAssignments(value) { return value || []; }
            function containsDirectJavaScriptFlowInfoCall() { return false; }
            function normalizeMessage(value) { return value; }
            function normalizeBusinessKeyForLoad(value) { return value; }
            function normalizeIdempotencyForLoad(value) { return value; }
            function migrateLegacyToNew() { throw new Error('legacy path not expected'); }
            function normalizeLegacyMessageStartNode() {}
            function migrateLegacyUserTaskDefaultFlows() {}
            function normalizeExclusiveGatewayPriorities() {}
            function clearSelection() {}
            function resetHistory() {}
            """);
        engine.Execute(match.Value);
        engine.SetValue(
            "candidateJson",
            """
            {
              "id": "round-trip",
              "name": "Round trip",
              "initialEventId": 1,
              "variables": [],
              "lanes": [],
              "flowNodes": [
                { "id": 1, "name": "Start", "type": "startEvent" },
                {
                  "id": 2,
                  "name": "Activate",
                  "type": "complexGateway",
                  "activationCondition": "IncomingCount(101) > 0",
                  "gatewayRef": 999
                },
                {
                  "id": 3,
                  "name": "Interrupt",
                  "type": "scopedInterruptEvent",
                  "gatewayRef": 2,
                  "activationCondition": "invalid"
                }
              ],
              "sequenceFlows": [],
              "cancelRoles": [],
              "unclaimRoles": [],
              "taskAssignmentRoles": []
            }
            """);
        using var loaded = JsonDocument.Parse(engine.Evaluate(
            "loadFromObject(JSON.parse(candidateJson)); JSON.stringify(model.flowNodes);").AsString());

        var complex = loaded.RootElement[1];
        Assert.Equal("IncomingCount(101) > 0", complex.GetProperty("activationCondition").GetString());
        Assert.False(complex.TryGetProperty("gatewayRef", out _));
        var interrupt = loaded.RootElement[2];
        Assert.Equal(2, interrupt.GetProperty("gatewayRef").GetInt32());
        Assert.False(interrupt.TryGetProperty("activationCondition", out _));
        Assert.DoesNotContain("parallelGatewayRef", loaded.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void TypeInvariants_KeepOnlyTheGatewaySpecificNodeField()
    {
        var html = ReadEditorSource();
        var match = Regex.Match(
            html,
            @"function applyTypeInvariants\(node\) \{[\s\S]*?(?=function nextCompletionPriority)");
        Assert.True(match.Success, "The editor type-invariant function was not found.");

        var engine = new Engine();
        engine.Execute(
            """
            const NODE_TYPE = {
              START_EVENT: 'startEvent', MESSAGE_START_EVENT: 'messageStartEvent',
              TIMER_START_EVENT: 'timerStartEvent',
              END_EVENT: 'endEvent', ERROR_END_EVENT: 'errorEndEvent',
              TERMINATE_END_EVENT: 'terminateEndEvent', USER_TASK: 'userTask',
              TASK: 'task', SERVICE_TASK: 'serviceTask', SCRIPT_TASK: 'scriptTask',
              EXCLUSIVE_GATEWAY: 'exclusiveGateway', PARALLEL_GATEWAY: 'parallelGateway',
              INCLUSIVE_GATEWAY: 'inclusiveGateway', COMPLEX_GATEWAY: 'complexGateway',
              SCOPED_INTERRUPT_EVENT: 'scopedInterruptEvent',
              ERROR_BOUNDARY_EVENT: 'errorBoundaryEvent',
              TIMER_BOUNDARY_EVENT: 'timerBoundaryEvent',
              MESSAGE_CATCH_EVENT: 'intermediateMessageCatchEvent',
              TIMER_CATCH_EVENT: 'intermediateTimerCatchEvent'
            };
            const CLAIM_MODE = { FRESH: 'fresh' };
            const ASSIGNMENT_MODE = { FRESH: 'fresh' };
            function isScriptTaskType(type) { return type === NODE_TYPE.SCRIPT_TASK; }
            function isComplexGatewayType(type) { return type === NODE_TYPE.COMPLEX_GATEWAY; }
            function isScopedInterruptEventType(type) { return type === NODE_TYPE.SCOPED_INTERRUPT_EVENT; }
            function isUserTaskType(type) { return type === NODE_TYPE.USER_TASK; }
            function isErrorEndEventType(type) { return type === NODE_TYPE.ERROR_END_EVENT; }
            function isStartEventType(type) { return type === NODE_TYPE.START_EVENT; }
            function isMessageStartEventType(type) { return type === NODE_TYPE.MESSAGE_START_EVENT; }
            function isEndEventType(type) {
              return type === NODE_TYPE.END_EVENT || type === NODE_TYPE.ERROR_END_EVENT ||
                type === NODE_TYPE.TERMINATE_END_EVENT;
            }
            function isAutomaticType(type) { return type === NODE_TYPE.TASK; }
            function isGatewayType(type) {
              return type === NODE_TYPE.EXCLUSIVE_GATEWAY ||
                type === NODE_TYPE.PARALLEL_GATEWAY ||
                type === NODE_TYPE.INCLUSIVE_GATEWAY ||
                type === NODE_TYPE.COMPLEX_GATEWAY;
            }
            function isServiceTaskType(type) { return type === NODE_TYPE.SERVICE_TASK; }
            function isAsyncCapableTaskType(type) {
              return isUserTaskType(type) || isAutomaticType(type) ||
                isServiceTaskType(type) || isScriptTaskType(type);
            }
            function isTimerStartEventType(type) { return type === NODE_TYPE.TIMER_START_EVENT; }
            function isTimerBoundaryEventType(type) { return type === NODE_TYPE.TIMER_BOUNDARY_EVENT; }
            function isTimerCatchEventType(type) { return type === NODE_TYPE.TIMER_CATCH_EVENT; }
            function isTimerEventType(type) {
              return isTimerStartEventType(type) || isTimerBoundaryEventType(type) ||
                isTimerCatchEventType(type);
            }
            function isErrorBoundaryEventType(type) { return type === NODE_TYPE.ERROR_BOUNDARY_EVENT; }
            function isBoundaryEventType(type) {
              return isErrorBoundaryEventType(type) || isTimerBoundaryEventType(type);
            }
            function isMessageCatchEventType(type) { return type === NODE_TYPE.MESSAGE_CATCH_EVENT; }
            function isSingleOutgoingType() { return false; }
            """);
        engine.Execute(match.Value);

        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            const complex = {
              type: 'complexGateway',
              activationCondition: 'IncomingCount(101) > 0',
              gatewayRef: 9,
              roles: [], variables: []
            };
            const interrupt = {
              type: 'scopedInterruptEvent',
              activationCondition: 'invalid',
              gatewayRef: 2,
              roles: [], variables: []
            };
            applyTypeInvariants(complex);
            applyTypeInvariants(interrupt);
            JSON.stringify({ complex, interrupt });
            """).AsString());

        var complex = result.RootElement.GetProperty("complex");
        Assert.Equal("IncomingCount(101) > 0", complex.GetProperty("activationCondition").GetString());
        Assert.False(complex.TryGetProperty("gatewayRef", out _));
        var interrupt = result.RootElement.GetProperty("interrupt");
        Assert.Equal(2, interrupt.GetProperty("gatewayRef").GetInt32());
        Assert.False(interrupt.TryGetProperty("activationCondition", out _));
    }

    [Fact]
    public void Validator_AcceptsInclusiveSplitWithScopedInterrupt()
    {
        var model = new WorkflowModel
        {
            Id = "editor-inclusive-scoped-interrupt",
            Name = "Editor inclusive scoped interrupt",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Select", Type = BpmnFlowNodeTypes.InclusiveGateway },
                new FlowNodeModel { Id = 3, Name = "Interrupt", Type = BpmnFlowNodeTypes.ScopedInterruptEvent, GatewayRef = 2 },
                new FlowNodeModel { Id = 4, Name = "Selected end", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 5, Name = "Fallback end", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3, Condition = "true" },
                new SequenceFlowModel { Id = 202, SourceRef = 2, TargetRef = 5, IsDefault = true },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4 }
            ]
        };

        Assert.Empty(Validate(model));
    }

    [Fact]
    public void Validator_AllowsCrossTypeParallelSplitAndInclusiveMerge()
    {
        var model = new WorkflowModel
        {
            Id = "editor-cross-type-gateways",
            Name = "Editor cross-type gateways",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Fork", Type = BpmnFlowNodeTypes.ParallelGateway },
                new FlowNodeModel { Id = 3, Name = "Branch A", Type = BpmnFlowNodeTypes.Task },
                new FlowNodeModel { Id = 4, Name = "Branch B", Type = BpmnFlowNodeTypes.Task },
                new FlowNodeModel { Id = 5, Name = "Merge", Type = BpmnFlowNodeTypes.InclusiveGateway },
                new FlowNodeModel { Id = 6, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 202, SourceRef = 2, TargetRef = 4 },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 5 },
                new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 5 },
                new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 6 }
            ]
        };

        Assert.Empty(Validate(model));

        model.SequenceFlows.Single(flow => flow.Id == 501).Condition = "true";
        Assert.Contains(Validate(model), error =>
            error.Contains("must be unconditional", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_ValidatesComplexActivationHelpersAndRouting()
    {
        var model = new WorkflowModel
        {
            Id = "editor-complex-gateway",
            Name = "Editor complex gateway",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Activate",
                    Type = BpmnFlowNodeTypes.ComplexGateway,
                    ActivationCondition = "IncomingCount(101) >= 1 and TotalIncomingCount() >= 1"
                },
                new FlowNodeModel { Id = 3, Name = "Start output", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 4, Name = "Fallback output", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel
                {
                    Id = 201,
                    SourceRef = 2,
                    TargetRef = 3,
                    Condition = "[gateway.waitingForStart]"
                },
                new SequenceFlowModel { Id = 202, SourceRef = 2, TargetRef = 4, IsDefault = true }
            ]
        };

        Assert.Empty(Validate(model));

        model.FlowNodes.Single(node => node.Id == 2).ActivationCondition =
            "IncomingCount(flowId) > 0 or IncomingCount(999) > 0 or FlowInfo(101, 'traversals.count') > 0";
        var errors = Validate(model);
        Assert.Contains(errors, error => error.Contains("literal integer incoming-flow id", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("not incoming", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("cannot use FlowInfo", StringComparison.OrdinalIgnoreCase));

        model.FlowNodes.Single(node => node.Id == 2).ActivationCondition =
            "[gateway.waitingForStart]";
        Assert.Contains(Validate(model), error =>
            error.Contains("only in Complex gateway outgoing-flow conditions", StringComparison.OrdinalIgnoreCase));

        model.FlowNodes.Single(node => node.Id == 2).ActivationCondition =
            "TotalIncomingCount() >= 1";
        model.SequenceFlows.Single(flow => flow.Id == 201).Condition =
            "IncomingCount(notLiteral) > 0";
        Assert.Contains(Validate(model), error =>
            error.Contains("literal integer incoming-flow id", StringComparison.OrdinalIgnoreCase));

        model.SequenceFlows.Single(flow => flow.Id == 201).Condition =
            "IncomingCount(101) > 0 and [gateway.waitingForStart]";
        Assert.Empty(Validate(model));
    }

    [Fact]
    public void Validator_RejectsComplexOnlyExpressionContextOutsideComplexGateway()
    {
        var model = CreateExclusiveGatewayModel();
        model.SequenceFlows.Single(flow => flow.Id == 301).Condition =
            "IncomingCount(201) > 0 or [gateway.waitingForStart]";

        var errors = Validate(model);

        Assert.Contains(errors, error =>
            error.Contains("available only in Complex gateway expressions", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error =>
            error.Contains("only in Complex gateway outgoing-flow conditions", StringComparison.OrdinalIgnoreCase));
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

    [Fact]
    public void Validator_AcceptsNonInterruptingReminderTimerBoundary()
    {
        var model = new WorkflowModel
        {
            Id = "timer-reminder-editor",
            Name = "Timer reminder",
            InitialEventId = 1,
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
                    Name = "Approval",
                    Type = BpmnFlowNodeTypes.UserTask
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Reminder after two days",
                    Type = BpmnFlowNodeTypes.TimerBoundaryEvent,
                    AttachedToRef = 2,
                    CancelActivity = false,
                    Timer = new TimerDefinitionModel { TimeDuration = "P2D" }
                },
                new FlowNodeModel
                {
                    Id = 4,
                    Name = "Done",
                    Type = BpmnFlowNodeTypes.EndEvent
                }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 4 },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4 }
            ]
        };

        Assert.Empty(Validate(model));
    }

    [Fact]
    public void Validator_RejectsCalendarTimerAndJobPolicyWithoutAsyncBoundary()
    {
        var model = new WorkflowModel
        {
            Id = "bad-timer-editor",
            Name = "Bad timer",
            FlowNodes =
            [
                new FlowNodeModel
                {
                    Id = 1,
                    Name = "Scheduled",
                    Type = BpmnFlowNodeTypes.TimerStartEvent,
                    Timer = new TimerDefinitionModel { TimeDuration = "P1M" }
                },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Review",
                    Type = BpmnFlowNodeTypes.UserTask,
                    Job = new JobPolicyModel
                    {
                        RetryDelays = ["PT10S"]
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
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 }
            ]
        };

        var errors = Validate(model);
        Assert.Contains(errors, error =>
            error.Contains("fixed-unit ISO-8601", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error =>
            error.Contains("requires asyncBefore or asyncAfter", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_RejectsCalendarRolloverAndCycleCountsBeyondInt32()
    {
        var model = CreateTimerStartModel(
            new TimerDefinitionModel { TimeDate = "2026-02-30T10:00:00Z" });

        Assert.Contains(Validate(model), error =>
            error.Contains("explicit UTC offset", StringComparison.OrdinalIgnoreCase));

        model.FlowNodes[0].Timer = new TimerDefinitionModel
        {
            TimeCycle = "R2147483648/PT1S"
        };
        Assert.Contains(Validate(model), error =>
            error.Contains("positive fixed-unit ISO-8601 duration", StringComparison.OrdinalIgnoreCase));

        model.FlowNodes[0].Timer = new TimerDefinitionModel
        {
            TimeCycle = "R2147483647/PT1S"
        };
        Assert.Empty(Validate(model));
    }

    [Fact]
    public void DurationValidator_MatchesRuntimeTickPrecisionAndRange()
    {
        var engine = CreateValidatorEngine();
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            JSON.stringify({
              belowOneTick: validatorFixedDurationSeconds('PT0.000000001S'),
              roundsToOneTick: validatorFixedDurationSeconds('PT0.00000005S'),
              maximum: validatorFixedDurationSeconds('PT922337203685.4775807S'),
              aboveMaximum: validatorFixedDurationSeconds('PT922337203685.4775808S'),
              excessiveDays: validatorFixedDurationSeconds('P999999999999D'),
              belowOneSecondCycle: validatorTimeCycle('R/PT0.5S')
            })
            """).AsString());

        Assert.Equal(
            JsonValueKind.Null,
            result.RootElement.GetProperty("belowOneTick").ValueKind);
        Assert.True(
            result.RootElement.GetProperty("roundsToOneTick").GetDouble() > 0);
        Assert.True(
            result.RootElement.GetProperty("maximum").GetDouble() > 0);
        Assert.Equal(
            JsonValueKind.Null,
            result.RootElement.GetProperty("aboveMaximum").ValueKind);
        Assert.Equal(
            JsonValueKind.Null,
            result.RootElement.GetProperty("excessiveDays").ValueKind);
        Assert.Equal(
            0.5,
            result.RootElement.GetProperty("belowOneSecondCycle").GetDouble(),
            precision: 7);
    }

    [Fact]
    public void Validator_RejectsMalformedImportedAsyncAndTimerMetadata()
    {
        var errors = ValidateJson(
            """
            {
              "id": "malformed-durable-editor",
              "name": "Malformed durable editor",
              "initialEventId": 1,
              "variables": [],
              "lanes": [],
              "flowNodes": [
                { "id": 1, "name": "Start", "type": "startEvent", "variables": [] },
                {
                  "id": 2,
                  "name": "Async host",
                  "type": "task",
                  "asyncBefore": "false",
                  "asyncAfter": false,
                  "job": {
                    "failureHandling": 5,
                    "retryDelays": "PT1S"
                  }
                },
                {
                  "id": 3,
                  "name": "Timer",
                  "type": "timerBoundaryEvent",
                  "attachedToRef": 2,
                  "cancelActivity": "false",
                  "timer": {
                    "timeDate": 5,
                    "timeDuration": "PT1H",
                    "timeCycle": null
                  }
                },
                { "id": 4, "name": "End", "type": "endEvent" }
              ],
              "sequenceFlows": [
                { "id": 101, "sourceRef": 1, "targetRef": 2 },
                { "id": 201, "sourceRef": 2, "targetRef": 4 },
                { "id": 301, "sourceRef": 3, "targetRef": 4 }
              ]
            }
            """);

        Assert.Contains(errors, error =>
            error.Contains("asyncBefore must be a boolean", StringComparison.Ordinal));
        Assert.Contains(errors, error =>
            error.Contains("failureHandling", StringComparison.Ordinal));
        Assert.Contains(errors, error =>
            error.Contains("retryDelays must be an array", StringComparison.Ordinal));
        Assert.Contains(errors, error =>
            error.Contains("cancelActivity must be a boolean", StringComparison.Ordinal));
        Assert.Contains(errors, error =>
            error.Contains("timeDate must be a string", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_ReportsTimerBoundaryHostLimitOnce()
    {
        var model = new WorkflowModel
        {
            Id = "timer-boundary-limit-editor",
            Name = "Timer boundary limit",
            InitialEventId = 1,
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
                    Name = "Wait",
                    Type = BpmnFlowNodeTypes.UserTask
                },
                new FlowNodeModel
                {
                    Id = 20,
                    Name = "End",
                    Type = BpmnFlowNodeTypes.EndEvent
                }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 20 }
            ]
        };
        for (var index = 0; index < 9; index++)
        {
            var nodeId = 3 + index;
            model.FlowNodes.Add(new FlowNodeModel
            {
                Id = nodeId,
                Name = $"Timer {index + 1}",
                Type = BpmnFlowNodeTypes.TimerBoundaryEvent,
                AttachedToRef = 2,
                CancelActivity = false,
                Timer = new TimerDefinitionModel { TimeDuration = "PT1H" }
            });
            model.SequenceFlows.Add(new SequenceFlowModel
            {
                Id = 300 + index,
                SourceRef = nodeId,
                TargetRef = 20
            });
        }

        var errors = Validate(model);

        Assert.Single(errors, error =>
            error.Contains("more than eight timer boundary events",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TimerNormalization_ConvertsValidOffsetsToUtcAndPreservesInvalidDates()
    {
        var html = ReadEditorSource();
        var validator = Regex.Match(
            html,
            @"// BEGIN WORKFLOW SAVE VALIDATOR(?<code>[\s\S]*?)// END WORKFLOW SAVE VALIDATOR");
        var normalization = Regex.Match(
            html,
            @"function normalizeTimerDefinition\(value\) \{[\s\S]*?(?=function applyTypeInvariants)");
        Assert.True(validator.Success, "The marked workflow save validator was not found.");
        Assert.True(normalization.Success, "The timer normalization helper was not found.");

        var engine = new Engine();
        engine.Execute(validator.Groups["code"].Value);
        engine.Execute(normalization.Value);
        using var result = JsonDocument.Parse(engine.Evaluate(
            """
            JSON.stringify({
              offset: normalizeTimerDefinition({
                timeDate: '2026-07-30T12:34:56.1234567+03:00'
              }).timeDate,
              noSeconds: normalizeTimerDefinition({
                timeDate: '2026-07-30T00:15-01:30'
              }).timeDate,
              leapDay: normalizeTimerDefinition({
                timeDate: '2024-02-29T23:00:00+02:00'
              }).timeDate,
              invalid: normalizeTimerDefinition({
                timeDate: ' 2026-02-30T10:00:00Z '
              }).timeDate,
              malformedMember: normalizeTimerDefinition({
                timeDate: 5,
                timeDuration: 'PT1H'
              }).timeDate,
              malformedShape: normalizeTimerDefinition(5),
              missing: normalizeTimerDefinition(null)
            })
            """).AsString());

        Assert.Equal(
            "2026-07-30T09:34:56.1234567Z",
            result.RootElement.GetProperty("offset").GetString());
        Assert.Equal(
            "2026-07-30T01:45:00Z",
            result.RootElement.GetProperty("noSeconds").GetString());
        Assert.Equal(
            "2024-02-29T21:00:00Z",
            result.RootElement.GetProperty("leapDay").GetString());
        Assert.Equal(
            "2026-02-30T10:00:00Z",
            result.RootElement.GetProperty("invalid").GetString());
        Assert.Equal(
            5,
            result.RootElement.GetProperty("malformedMember").GetInt32());
        Assert.Equal(
            5,
            result.RootElement.GetProperty("malformedShape").GetInt32());
        var missing = result.RootElement.GetProperty("missing");
        Assert.Equal(JsonValueKind.Null, missing.GetProperty("timeDate").ValueKind);
        Assert.Equal(JsonValueKind.Null, missing.GetProperty("timeDuration").ValueKind);
        Assert.Equal(JsonValueKind.Null, missing.GetProperty("timeCycle").ValueKind);
    }

    [Fact]
    public void JobPolicyNormalization_PreservesUnsupportedFailureHandlingForValidation()
    {
        var html = ReadEditorSource();
        var canonicalization = Regex.Match(
            html,
            @"function canonicalizeKnownValue\(value, supported, fallback\) \{[\s\S]*?(?=function normalizeMultiInstanceForLoad)");
        var normalization = Regex.Match(
            html,
            @"function normalizeJobPolicy\(value\) \{[\s\S]*?(?=function normalizeTimerDefinition)");
        Assert.True(canonicalization.Success, "The enum canonicalization helper was not found.");
        Assert.True(normalization.Success, "The job policy normalization helper was not found.");

        var engine = new Engine();
        engine.Execute(canonicalization.Value);
        engine.Execute(normalization.Value);
        Assert.Equal(
            "eventuallyFirst",
            engine.Evaluate(
                "normalizeJobPolicy({ failureHandling: 'eventuallyFirst', retryDelays: [] }).failureHandling")
                .AsString());
        Assert.Equal(
            "retryFirst",
            engine.Evaluate(
                "normalizeJobPolicy({ failureHandling: 'RETRYFIRST', retryDelays: [] }).failureHandling")
                .AsString());
        Assert.Equal(
            5,
            engine.Evaluate("normalizeJobPolicy(5)").AsNumber());
        Assert.Equal(
            7,
            engine.Evaluate(
                "normalizeJobPolicy({ failureHandling: 7, retryDelays: 'PT1S' }).failureHandling")
                .AsNumber());
        Assert.Equal(
            "PT1S",
            engine.Evaluate(
                "normalizeJobPolicy({ failureHandling: 7, retryDelays: 'PT1S' }).retryDelays")
                .AsString());

        var model = new WorkflowModel
        {
            Id = "unsupported-job-policy-editor",
            Name = "Unsupported job policy",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Async task",
                    Type = BpmnFlowNodeTypes.Task,
                    AsyncBefore = true,
                    Job = new JobPolicyModel
                    {
                        FailureHandling = "eventuallyFirst",
                        RetryDelays = []
                    }
                },
                new FlowNodeModel { Id = 3, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 }
            ]
        };
        Assert.Contains(Validate(model), error =>
            error.Contains("failureHandling", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Editor_ExposesAsyncAndTimerAuthoring()
    {
        var html = ReadEditorSource();

        Assert.Contains("Timer Start Event (scheduled)", html, StringComparison.Ordinal);
        Assert.Contains("Timer Catch Event (delay)", html, StringComparison.Ordinal);
        Assert.Contains("+ Add timer boundary event", html, StringComparison.Ordinal);
        Assert.Contains("Async before", html, StringComparison.Ordinal);
        Assert.Contains("id=\"icon-timer\"", html, StringComparison.Ordinal);
        Assert.Contains("R/P2D", html, StringComparison.Ordinal);
        Assert.Contains("Duration after activation", html, StringComparison.Ordinal);
        Assert.Contains("Limited occurrences", html, StringComparison.Ordinal);
        Assert.Contains("Local date and time", html, StringComparison.Ordinal);
        Assert.Contains("+ Add retry delay", html, StringComparison.Ordinal);
        Assert.Contains("Advanced ISO value", html, StringComparison.Ordinal);
    }

    private static WorkflowModel CreateTimerStartModel(TimerDefinitionModel timer) => new()
    {
        Id = "timer-start-editor-validation",
        Name = "Timer start validation",
        InitialEventId = 2,
        FlowNodes =
        [
            new FlowNodeModel
            {
                Id = 1,
                Name = "Scheduled",
                Type = BpmnFlowNodeTypes.TimerStartEvent,
                Timer = timer
            },
            new FlowNodeModel { Id = 2, Name = "Manual start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel { Id = 3, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 3 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 }
        ]
    };

    private static Engine CreateValidatorEngine()
    {
        var html = ReadEditorSource();
        var match = Regex.Match(
            html,
            @"// BEGIN WORKFLOW SAVE VALIDATOR(?<code>[\s\S]*?)// END WORKFLOW SAVE VALIDATOR");
        Assert.True(match.Success, "The marked workflow save validator was not found.");

        var engine = new Engine();
        engine.Execute(match.Groups["code"].Value);
        return engine;
    }

    private static IReadOnlyList<string> Validate(WorkflowModel model) =>
        ValidateJson(JsonSerializer.Serialize(model));

    private static IReadOnlyList<string> ValidateJson(string json)
    {
        var engine = CreateValidatorEngine();
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
