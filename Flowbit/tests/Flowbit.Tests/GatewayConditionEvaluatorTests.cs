using System.Text.Json;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Xunit;

namespace Flowbit.Tests;

public sealed class GatewayConditionEvaluatorTests
{
    private static readonly IReadOnlyDictionary<string, JsonElement> EmptyVariables =
        new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void EvaluateGateway_ProvidesIncomingCountsTotalAndStartPhase()
    {
        var context = new GatewayConditionContext(
            new Dictionary<int, int> { [101] = 2, [102] = 1 },
            WaitingForStart: true);

        var result = SequenceFlowConditionEvaluator.EvaluateGateway(
            "IncomingCount(101) == 2 and IncomingCount(102) == 1 "
            + "and TotalIncomingCount() == 3 and [gateway.waitingForStart]",
            EmptyVariables,
            context);

        Assert.True(result);
    }

    [Fact]
    public void EvaluateGateway_ExposesResetPhaseAndFailsClosedForInvalidHelperArity()
    {
        var context = new GatewayConditionContext(
            new Dictionary<int, int> { [101] = 1 },
            WaitingForStart: false);

        Assert.True(SequenceFlowConditionEvaluator.EvaluateGateway(
            "not [gateway.waitingForStart]",
            EmptyVariables,
            context));
        Assert.False(SequenceFlowConditionEvaluator.EvaluateGateway(
            "IncomingCount(101, 102) > 0",
            EmptyVariables,
            context));
        Assert.False(SequenceFlowConditionEvaluator.EvaluateGateway(
            "TotalIncomingCount(101) > 0",
            EmptyVariables,
            context));
    }

    [Theory]
    [InlineData("IncomingCount(101) > 0", true, false)]
    [InlineData("TotalIncomingCount() > 0", true, false)]
    [InlineData("[gateway.waitingForStart]", true, true)]
    [InlineData("'IncomingCount(999)' == 'IncomingCount(999)'", false, false)]
    public void TryValidateGatewayReferences_AcceptsOnlyTheConfiguredContext(
        string expression,
        bool helpersAllowed,
        bool waitingForStartAllowed)
    {
        var valid = SequenceFlowConditionEvaluator.TryValidateGatewayReferences(
            expression,
            new HashSet<int> { 101 },
            helpersAllowed,
            waitingForStartAllowed,
            out var error);

        Assert.True(valid, error);
    }

    [Theory]
    [InlineData("IncomingCount(flowId)", "literal")]
    [InlineData("IncomingCount(999)", "not incoming")]
    [InlineData("IncomingCount()", "exactly one")]
    [InlineData("TotalIncomingCount(101)", "no arguments")]
    [InlineData("[gateway.waitingForStart]", "outgoing-flow")]
    public void TryValidateGatewayReferences_RejectsInvalidReferences(
        string expression,
        string expectedError)
    {
        var valid = SequenceFlowConditionEvaluator.TryValidateGatewayReferences(
            expression,
            new HashSet<int> { 101 },
            helpersAllowed: true,
            waitingForStartAllowed: false,
            out var error);

        Assert.False(valid);
        Assert.Contains(expectedError, error, StringComparison.OrdinalIgnoreCase);
    }
}
