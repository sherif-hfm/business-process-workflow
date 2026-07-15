using System.Text.Json;
using System.Text.RegularExpressions;
using Jint;
using WorkflowEngine.Shared.Models;
using Xunit;

namespace WorkflowEngine.Tests;

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

    private static IReadOnlyList<string> Validate(WorkflowModel model)
    {
        var editorPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "workflow-editor.html");
        var html = File.ReadAllText(editorPath);
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

    private static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))
        ?? throw new InvalidOperationException("Fixture clone failed.");
}
