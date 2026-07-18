using System.Text.Json;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Xunit;

namespace Flowbit.Tests;

public sealed class SequenceFlowConditionEvaluatorFlowInfoTests
{
    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyVariables =
        new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void FlowInfo_ResolvesEverySupportedActionAndTraversalPath()
    {
        var occurredAt = new DateTimeOffset(2026, 7, 18, 10, 30, 0, TimeSpan.Zero);
        var actionValues = JsonSerializer.SerializeToElement(new { decision = "confirm" });
        var traversalValues = JsonSerializer.SerializeToElement(new { route = "manager" });
        var snapshot = new SequenceFlowInfoSnapshot(
            [201],
            [
                new SequenceFlowRuntimeSummary(
                    201,
                    new SequenceFlowRuntimeView(
                        3,
                        new SequenceFlowLastOccurrence(
                            "alice",
                            ["Manager", "User"],
                            occurredAt,
                            "userTaskAction",
                            actionValues)),
                    new SequenceFlowRuntimeView(
                        2,
                        new SequenceFlowLastOccurrence(
                            "bob",
                            ["User"],
                            occurredAt.AddMinutes(1),
                            "gateway",
                            traversalValues)))
            ]);

        Assert.Equal(3L, EvaluateValue(snapshot, "actions.count"));
        Assert.Equal("alice", EvaluateValue(snapshot, "actions.last.user"));
        Assert.Equal(
            new[] { "Manager", "User" },
            Assert.IsAssignableFrom<IEnumerable<string>>(
                EvaluateValue(snapshot, "actions.last.userRoles")));
        Assert.Equal(occurredAt.ToString("O"), EvaluateValue(snapshot, "actions.last.occurredAt"));
        Assert.Equal("userTaskAction", EvaluateValue(snapshot, "actions.last.kind"));
        Assert.Equal(
            "confirm",
            Assert.IsType<JsonElement>(EvaluateValue(snapshot, "actions.last.values"))
                .GetProperty("decision")
                .GetString());

        Assert.Equal(2L, EvaluateValue(snapshot, "traversals.count"));
        Assert.Equal("bob", EvaluateValue(snapshot, "traversals.last.user"));
        Assert.Equal(
            new[] { "User" },
            Assert.IsAssignableFrom<IEnumerable<string>>(
                EvaluateValue(snapshot, "traversals.last.userRoles")));
        Assert.Equal(
            occurredAt.AddMinutes(1).ToString("O"),
            EvaluateValue(snapshot, "traversals.last.occurredAt"));
        Assert.Equal("gateway", EvaluateValue(snapshot, "traversals.last.kind"));
        Assert.Equal(
            "manager",
            Assert.IsType<JsonElement>(EvaluateValue(snapshot, "traversals.last.values"))
                .GetProperty("route")
                .GetString());

        var all = Assert.IsType<JsonElement>(EvaluateValue(snapshot, "all"));
        Assert.Equal(201, all.GetProperty("flowId").GetInt32());
        Assert.Equal(3, all.GetProperty("actions").GetProperty("count").GetInt64());
        Assert.Equal(
            "alice",
            all.GetProperty("actions").GetProperty("last").GetProperty("user").GetString());
        Assert.Equal(2, all.GetProperty("traversals").GetProperty("count").GetInt64());
    }

    [Fact]
    public void FlowInfo_IsCaseInsensitiveAndSupportsRoleRouting()
    {
        var snapshot = SnapshotWithLastAction("alice", ["User", "Manager"]);

        Assert.True(SequenceFlowConditionEvaluator.Evaluate(
            "Contains(fLoWiNfO(201, 'AcTiOnS.LaSt.UsErRoLeS'), 'manager')",
            EmptyVariables,
            snapshot));
        Assert.True(SequenceFlowConditionEvaluator.Evaluate(
            "FlowInfo(201, 'actions.last.user') == 'ALICE'",
            EmptyVariables,
            snapshot));
    }

    [Fact]
    public void FlowInfo_KnownUnusedFlowReturnsCanonicalEmptySummary()
    {
        var snapshot = new SequenceFlowInfoSnapshot([201, 202]);

        Assert.Equal(0L, EvaluateValue(snapshot, "actions.count", 202));
        Assert.Null(EvaluateValue(snapshot, "actions.last.user", 202));
        Assert.Equal(0L, EvaluateValue(snapshot, "traversals.count", 202));

        var all = Assert.IsType<JsonElement>(EvaluateValue(snapshot, "all", 202));
        Assert.Equal(202, all.GetProperty("flowId").GetInt32());
        Assert.Equal(0, all.GetProperty("actions").GetProperty("count").GetInt64());
        Assert.Equal(JsonValueKind.Null, all.GetProperty("actions").GetProperty("last").ValueKind);
        Assert.Equal(0, all.GetProperty("traversals").GetProperty("count").GetInt64());
        Assert.Equal(JsonValueKind.Null, all.GetProperty("traversals").GetProperty("last").ValueKind);
    }

    [Fact]
    public void FlowInfo_IsUnavailableWithoutSnapshotAndRejectsUnknownRuntimeFlow()
    {
        Assert.False(SequenceFlowConditionEvaluator.Evaluate(
            "FlowInfo(201, 'actions.count') > 0",
            EmptyVariables));
        Assert.Throws<WorkflowDomainException>(() =>
            SequenceFlowConditionEvaluator.EvaluateValue(
                "FlowInfo(201, 'actions.count')",
                EmptyVariables));

        var snapshot = new SequenceFlowInfoSnapshot([201]);
        Assert.False(SequenceFlowConditionEvaluator.Evaluate(
            "FlowInfo(999, 'actions.count') > 0",
            EmptyVariables,
            snapshot));
        Assert.Throws<WorkflowDomainException>(() =>
            SequenceFlowConditionEvaluator.EvaluateValue(
                "FlowInfo(999, 'actions.count')",
                EmptyVariables,
                flowInfo: snapshot));
    }

    [Fact]
    public void ContainsFlowInfoReference_IgnoresStringsAndMatchesFunctionCaseInsensitively()
    {
        Assert.True(SequenceFlowConditionEvaluator.ContainsFlowInfoReference(
            "fLoWiNfO (201, 'actions.count') > 0"));
        Assert.False(SequenceFlowConditionEvaluator.ContainsFlowInfoReference(
            "note == 'FlowInfo(201, actions.count)'"));
        Assert.False(SequenceFlowConditionEvaluator.ContainsFlowInfoReference("myFlowInfo(201)"));
    }

    [Fact]
    public void EvaluateCompletion_PreservesCountFlowAndPercentFlowBehaviorWithFlowInfo()
    {
        var counts = new Dictionary<int, int> { [201] = 3, [202] = 2 };
        var snapshot = SnapshotWithLastAction("alice", ["Manager"]);

        Assert.True(SequenceFlowConditionEvaluator.EvaluateCompletion(
            "CountFlow(201) == 3 and PercentFlow(201) == 60 " +
            "and Contains(FlowInfo(201, 'actions.last.userRoles'), 'Manager')",
            EmptyVariables,
            counts,
            5,
            snapshot));
        Assert.False(SequenceFlowConditionEvaluator.EvaluateCompletion(
            "CountFlow(202) == 3",
            EmptyVariables,
            counts,
            5,
            snapshot));
    }

    private static object? EvaluateValue(
        SequenceFlowInfoSnapshot snapshot,
        string path,
        int flowId = 201) =>
        SequenceFlowConditionEvaluator.EvaluateValue(
            $"FlowInfo({flowId}, '{path}')",
            EmptyVariables,
            flowInfo: snapshot);

    private static SequenceFlowInfoSnapshot SnapshotWithLastAction(
        string user,
        IReadOnlyList<string> roles) =>
        new(
            [201],
            [
                new SequenceFlowRuntimeSummary(
                    201,
                    new SequenceFlowRuntimeView(
                        1,
                        new SequenceFlowLastOccurrence(
                            user,
                            roles,
                            new DateTimeOffset(2026, 7, 18, 10, 30, 0, TimeSpan.Zero),
                            "userTaskAction",
                            JsonSerializer.SerializeToElement(new { }))),
                    SequenceFlowRuntimeView.Empty)
            ]);
}
