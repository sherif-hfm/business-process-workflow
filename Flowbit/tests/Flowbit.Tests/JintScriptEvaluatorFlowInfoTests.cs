using System.Text.Json;
using Flowbit.Infrastructure.Scripting;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Flowbit.Tests;

public sealed class JintScriptEvaluatorFlowInfoTests
{
    [Fact]
    public void GetFlowInfo_ExposesCamelCaseNestedSummaryAndRoleArray()
    {
        var context = new TestScriptContext(new SequenceFlowRuntimeSummary(
            201,
            new SequenceFlowRuntimeView(
                1,
                new SequenceFlowLastOccurrence(
                    "alice",
                    ["Manager", "User"],
                    new DateTimeOffset(2026, 7, 18, 10, 30, 0, TimeSpan.Zero),
                    "userTaskAction",
                    JsonSerializer.SerializeToElement(new { decision = "confirm" }))),
            SequenceFlowRuntimeView.Empty));
        var evaluator = new JintScriptEvaluator(
            new ScriptOptions(),
            NullLogger<JintScriptEvaluator>.Instance);

        var result = evaluator.Evaluate(
            "const info = execution.getFlowInfo(201);" +
            "execution.setVariable('user', info.actions.last.user);" +
            "execution.setVariable('roles', info.actions.last.userRoles);" +
            "execution.setVariable('count', info.actions.count);" +
            "execution.setVariable('decision', info.actions.last.values.decision);",
            context,
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal("alice", context.Writes["user"].GetString());
        Assert.Equal(
            ["Manager", "User"],
            context.Writes["roles"].EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(1, context.Writes["count"].GetDouble());
        Assert.Equal("confirm", context.Writes["decision"].GetString());
    }

    private sealed class TestScriptContext(SequenceFlowRuntimeSummary summary) : IScriptContext
    {
        public Dictionary<string, JsonElement> Writes { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public bool TryGetVariable(string name, out JsonElement value)
        {
            value = default;
            return false;
        }

        public bool HasVariable(string name) => false;

        public IReadOnlyDictionary<string, JsonElement> GetVariables() =>
            new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        public SequenceFlowRuntimeSummary GetFlowInfo(int flowId) =>
            flowId == summary.FlowId
                ? summary
                : throw new KeyNotFoundException($"Unknown flow #{flowId}.");

        public void SetVariable(string name, JsonElement value) => Writes[name] = value.Clone();
    }
}
