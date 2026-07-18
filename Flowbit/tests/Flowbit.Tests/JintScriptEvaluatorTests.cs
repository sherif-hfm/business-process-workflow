using System.Text.Json;
using Flowbit.Infrastructure.Scripting;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Flowbit.Tests;

public sealed class JintScriptEvaluatorTests
{
    [Fact]
    public void IsValid_UsesRuntimeStrictModeWithoutExecutingCode()
    {
        var evaluator = CreateEvaluator();

        Assert.False(evaluator.IsValid("with ({ value: 1 }) { value; }", out var strictError));
        Assert.NotNull(strictError);
        Assert.True(evaluator.IsValid(
            "execution.setVariable('wouldRun', 1);",
            out var validError));
        Assert.Null(validError);
    }

    [Fact]
    public void Evaluate_ReadsItsWritesAndReturnsAllVariables()
    {
        var context = new TestScriptContext(new Dictionary<string, JsonElement>
        {
            ["amount"] = JsonSerializer.SerializeToElement(10)
        });

        var result = CreateEvaluator().Evaluate(
            "execution.setVariable('TOTAL', execution.getVariable('Amount') + 5);" +
            "const all = execution.getVariables();" +
            "execution.setVariable('copy', execution.getVariable('total'));" +
            "execution.setVariable('original', all.amount);",
            context,
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(15, context.Writes["total"].GetDouble());
        Assert.Equal(15, context.Writes["copy"].GetDouble());
        Assert.Equal(10, context.Writes["original"].GetDouble());
    }

    [Fact]
    public void Evaluate_PreservesCaseSensitiveNestedJsonKeys()
    {
        var context = new TestScriptContext();

        var result = CreateEvaluator().Evaluate(
            "execution.setVariable('json', { a: 1, A: 2 });",
            context,
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var value = context.Writes["json"];
        Assert.Equal(1, value.GetProperty("a").GetInt32());
        Assert.Equal(2, value.GetProperty("A").GetInt32());
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1n")]
    [InlineData("function () {}")]
    [InlineData("/pattern/")]
    public void Evaluate_RejectsNonJsonValues(string expression)
    {
        var error = Assert.Throws<WorkflowDomainException>(() =>
            CreateEvaluator().Evaluate(
                $"execution.setVariable('value', {expression});",
                new TestScriptContext(),
                CancellationToken.None));

        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void Evaluate_RejectsUndefinedInsteadOfSilentlyStoringIt()
    {
        var error = Assert.Throws<WorkflowDomainException>(() =>
            CreateEvaluator().Evaluate(
                "execution.setVariable('value', undefined);",
                new TestScriptContext(),
                CancellationToken.None));

        Assert.Contains("undefined", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_RejectsCyclicValues()
    {
        var context = new TestScriptContext();
        var result = CreateEvaluator().Evaluate(
            "const value = {}; value.self = value; execution.setVariable('value', value);",
            context,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.DoesNotContain("value", context.Writes.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_RejectsValuesBeyondDepthAndSizeBudgets()
    {
        var depthOptions = new ScriptOptions { MaxValueDepth = 2 };
        var depth = Assert.Throws<WorkflowDomainException>(() =>
            CreateEvaluator(depthOptions).Evaluate(
                "execution.setVariable('value', { a: { b: { c: 1 } } });",
                new TestScriptContext(),
                CancellationToken.None));
        Assert.Contains("depth", depth.Message, StringComparison.OrdinalIgnoreCase);

        var sizeOptions = new ScriptOptions { MaxValueBytes = 32 };
        var size = Assert.Throws<WorkflowDomainException>(() =>
            CreateEvaluator(sizeOptions).Evaluate(
                "execution.setVariable('value', 'abcdefghijklmnopqrstuvwxyz0123456789');",
                new TestScriptContext(),
                CancellationToken.None));
        Assert.Contains("size", size.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_RejectsValuesBeyondItemBudgetAndOversizedInputs()
    {
        var items = Assert.Throws<WorkflowDomainException>(() =>
            CreateEvaluator(new ScriptOptions { MaxValueItems = 2 }).Evaluate(
                "execution.setVariable('value', { a: 1, b: 2, c: 3 });",
                new TestScriptContext(),
                CancellationToken.None));
        Assert.Contains("item", items.Message, StringComparison.OrdinalIgnoreCase);

        var context = new TestScriptContext(new Dictionary<string, JsonElement>
        {
            ["large"] = JsonSerializer.SerializeToElement("abcdefghijklmnopqrstuvwxyz0123456789")
        });
        var inbound = Assert.Throws<WorkflowDomainException>(() =>
            CreateEvaluator(new ScriptOptions { MaxValueBytes = 32 }).Evaluate(
                "execution.getVariable('large');",
                context,
                CancellationToken.None));
        Assert.Contains("size", inbound.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_EnforcesStatementRecursionAndArrayLimits()
    {
        var statements = CreateEvaluator(new ScriptOptions { MaxStatements = 20 }).Evaluate(
            "let total = 0; for (let i = 0; i < 1000; i++) total += i;",
            new TestScriptContext(),
            CancellationToken.None);
        Assert.False(statements.Success);
        Assert.Contains("statement", statements.Error, StringComparison.OrdinalIgnoreCase);

        var recursion = CreateEvaluator(new ScriptOptions
        {
            MaxRecursionDepth = 8,
            MaxExecutionStackCount = 16
        }).Evaluate(
            "function recurse() { return recurse(); } recurse();",
            new TestScriptContext(),
            CancellationToken.None);
        Assert.False(recursion.Success);

        var array = CreateEvaluator(new ScriptOptions
        {
            MaxArraySize = 4,
            MaxValueItems = 4
        }).Evaluate(
            "execution.setVariable('value', new Array(5));",
            new TestScriptContext(),
            CancellationToken.None);
        Assert.False(array.Success);
    }

    [Theory]
    [InlineData("eval('1 + 1')")]
    [InlineData("new Function('return 1')()")]
    public void Evaluate_DisablesDynamicStringCompilation(string expression)
    {
        var result = CreateEvaluator().Evaluate(
            expression + ";",
            new TestScriptContext(),
            CancellationToken.None);

        Assert.False(result.Success);
    }

    [Theory]
    [InlineData("typeof System !== 'undefined'")]
    [InlineData("typeof execution.GetType !== 'undefined'")]
    public void Evaluate_DoesNotExposeClrOrReflection(string attackExpression)
    {
        var result = CreateEvaluator().Evaluate(
            $"if ({attackExpression}) throw new Error('CLR exposed');",
            new TestScriptContext(),
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public void Evaluate_PropagatesPreCancelledCallerToken()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            CreateEvaluator().Evaluate("while (true) {}", new TestScriptContext(), cancellation.Token));
    }

    [Fact]
    public void Evaluate_PropagatesMidExecutionCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var context = new TestScriptContext(onRead: () =>
        {
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        });

        Assert.ThrowsAny<OperationCanceledException>(() =>
            CreateEvaluator().Evaluate(
                "execution.getVariable('trigger');",
                context,
                cancellation.Token));
    }

    [Fact]
    public void ScriptOptions_RejectInvalidCrossFieldConfiguration()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new ScriptOptions { TimeoutSeconds = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new ScriptOptions { RegexTimeoutMilliseconds = 5_001 }.Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new ScriptOptions { MaxRecursionDepth = 300, MaxExecutionStackCount = 200 }.Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new ScriptOptions { MaxValueBytes = 8_000_001, MemoryBytes = 8_000_000 }.Validate());
    }

    private static JintScriptEvaluator CreateEvaluator(ScriptOptions? options = null) =>
        new(options ?? new ScriptOptions(), NullLogger<JintScriptEvaluator>.Instance);

    private sealed class TestScriptContext : IScriptContext
    {
        private readonly Dictionary<string, JsonElement> values;
        private readonly Action? onRead;

        public TestScriptContext(
            IReadOnlyDictionary<string, JsonElement>? initial = null,
            Action? onRead = null)
        {
            this.onRead = onRead;
            values = initial is null
                ? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                : initial.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        }

        public Dictionary<string, JsonElement> Writes { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public bool TryGetVariable(string name, out JsonElement value)
        {
            onRead?.Invoke();
            return values.TryGetValue(name, out value);
        }

        public bool HasVariable(string name) => values.ContainsKey(name);

        public IReadOnlyDictionary<string, JsonElement> GetVariables() => values;

        public SequenceFlowRuntimeSummary GetFlowInfo(int flowId) =>
            throw new KeyNotFoundException($"Unknown flow #{flowId}.");

        public void SetVariable(string name, JsonElement value)
        {
            var clone = value.Clone();
            Writes[name] = clone;
            values[name] = clone;
        }
    }
}
