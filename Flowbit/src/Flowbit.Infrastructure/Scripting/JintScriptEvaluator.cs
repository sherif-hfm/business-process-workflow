using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jint;
using Jint.Native;
using Jint.Runtime;
using Microsoft.Extensions.Logging;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Services;

namespace Flowbit.Infrastructure.Scripting;

/// <summary>
/// Evaluates scriptTask JavaScript bodies with Jint in a sandboxed <see cref="Engine"/>:
/// CLR access is never enabled (no <c>AllowClr()</c>), so a script cannot reach the
/// filesystem, network, or reflection - the only thing it can see is the single
/// <c>execution</c> host object bound below, backed by <see cref="IScriptContext"/>.
/// Execution is bounded by <see cref="ScriptOptions"/>. Jint constraints and
/// cancellation are cooperative safeguards for trusted workflow administrators;
/// they are not an out-of-process hostile-code boundary.
/// </summary>
public sealed class JintScriptEvaluator : IScriptEvaluator
{
    private readonly ScriptOptions options;
    private readonly ILogger<JintScriptEvaluator> logger;

    public JintScriptEvaluator(ScriptOptions options, ILogger<JintScriptEvaluator> logger)
    {
        options.Validate();
        this.options = options;
        this.logger = logger;
    }

    public ScriptResult Evaluate(string script, IScriptContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var engine = new Engine(o =>
        {
            o.LimitMemory(options.MemoryBytes)
                .TimeoutInterval(TimeSpan.FromSeconds(options.TimeoutSeconds))
                .MaxStatements(options.MaxStatements)
                .CancellationToken(linkedCts.Token)
                .LimitRecursion(options.MaxRecursionDepth)
                .DisableStringCompilation()
                .Strict();
            o.AgentCanSuspend = false;
            o.Constraints.MaxExecutionStackCount = options.MaxExecutionStackCount;
            o.Constraints.RegexTimeout = TimeSpan.FromMilliseconds(options.RegexTimeoutMilliseconds);
            o.Constraints.MaxArraySize = options.MaxArraySize;
            o.Constraints.MaxAtomicsPauseIterations = 0;
        });
        // Intentionally no .AllowClr(): the script has zero CLR surface beyond the
        // single `execution` object explicitly bound below.

        engine.SetValue("execution", new ExecutionBinding(context, options));

        logger.LogInformation("Executing Jint JavaScript sandbox script...");

        try
        {
            var prepared = Engine.PrepareScript(script, strict: true);
            engine.Execute(prepared);
            logger.LogInformation("Jint JavaScript sandbox script executed successfully.");
            return ScriptResult.Ok;
        }
        catch (WorkflowDomainException)
        {
            // Raised by ExecutionBinding -> IScriptContext.SetVariable (e.g. an
            // undeclared target). CLR exceptions from bound methods bubble through
            // Engine.Execute unchanged by default, so this is a real domain error,
            // not a script bug - propagate as-is so the caller's message survives.
            throw;
        }
        catch (ExecutionCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "JavaScript execution was cancelled by the caller.",
                ex,
                cancellationToken);
        }
        catch (ExecutionCanceledException)
        {
            logger.LogWarning("Jint script execution exceeded timeout of {TimeoutSeconds}s.", options.TimeoutSeconds);
            return ScriptResult.Fail($"Script execution exceeded the {options.TimeoutSeconds}s timeout.");
        }
        catch (JavaScriptException ex)
        {
            logger.LogWarning(ex, "Jint script encountered a JavaScriptException: {Message}", ex.Message);
            return ScriptResult.Fail(ex.Message);
        }
        catch (RegexMatchTimeoutException)
        {
            logger.LogWarning(
                "Jint regular expression exceeded timeout of {RegexTimeoutMilliseconds}ms.",
                options.RegexTimeoutMilliseconds);
            return ScriptResult.Fail(
                $"Script regular expression exceeded the {options.RegexTimeoutMilliseconds}ms timeout.");
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Jint script execution exceeded timeout of {TimeoutSeconds}s.", options.TimeoutSeconds);
            return ScriptResult.Fail($"Script execution exceeded the {options.TimeoutSeconds}s timeout.");
        }
        catch (MemoryLimitExceededException)
        {
            logger.LogWarning("Jint script execution exceeded the memory limit of {MemoryBytes} bytes.", options.MemoryBytes);
            return ScriptResult.Fail($"Script exceeded the {options.MemoryBytes}-byte memory limit.");
        }
        catch (StatementsCountOverflowException)
        {
            logger.LogWarning("Jint script execution exceeded the statement limit of {MaxStatements}.", options.MaxStatements);
            return ScriptResult.Fail($"Script exceeded the {options.MaxStatements}-statement limit.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The linked token was cancelled by the evaluator's elapsed-time
            // guard, not by the caller - report it as a Script Task timeout.
            logger.LogWarning("Jint script execution exceeded timeout of {TimeoutSeconds}s (cancelled by elapsed-time guard).", options.TimeoutSeconds);
            return ScriptResult.Fail($"Script execution exceeded the {options.TimeoutSeconds}s timeout.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Jint script execution failed with unexpected exception.");
            return ScriptResult.Fail(ex.Message);
        }
    }

    public bool IsValid(string script, out string? error)
    {
        try
        {
            // Parses without executing (no `execution` binding exists at this
            // point, and nothing in the script runs), so this is safe to call at
            // author time (ValidateDefinition) with untrusted-shaped input.
            _ = Engine.PrepareScript(script, strict: true);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// The CLR object bound to the JS global <c>execution</c>. Jint wraps this
    /// single instance for interop without granting the script any broader CLR
    /// access. Writes enter as <see cref="JsValue"/> so undefined and non-finite
    /// values can be rejected explicitly; all values cross a bounded,
    /// JSON-compatible bridge before reaching the workflow context.
    /// </summary>
    private sealed class ExecutionBinding(IScriptContext context, ScriptOptions options)
    {
        public object? GetVariable(string name)
        {
            if (!context.TryGetVariable(name, out var value))
            {
                return null;
            }

            var budget = new ValueConversionBudget(options);
            budget.AddBytes(value);
            return JsonToClr(value, budget, 0);
        }

        public bool HasVariable(string name) => context.HasVariable(name);

        public IDictionary<string, object?> GetVariables()
        {
            var budget = new ValueConversionBudget(options);
            var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in context.GetVariables())
            {
                budget.AddItem();
                budget.AddBytes(pair.Value);
                result[pair.Key] = JsonToClr(pair.Value, budget, 0);
            }

            return result;
        }

        public object GetFlowInfo(int flowId)
        {
            var value = context.GetFlowInfo(flowId).ToJsonElement();
            var budget = new ValueConversionBudget(options);
            budget.AddBytes(value);
            return JsonToClr(value, budget, 0)
                   ?? throw new WorkflowDomainException(
                       $"Sequence flow #{flowId} could not be represented for the script context.");
        }

        public void SetVariable(string name, JsValue value)
        {
            var budget = new ValueConversionBudget(options);
            var converted = JsToJson(value, budget);
            budget.AddBytes(converted);
            context.SetVariable(name, converted);
        }

        private static JsonElement JsToJson(JsValue value, ValueConversionBudget budget)
        {
            if (value.IsUndefined())
            {
                throw new WorkflowDomainException(
                    "JavaScript values must be JSON-compatible; undefined is not supported.");
            }

            if (value.IsNull()) return JsonSerializer.SerializeToElement((object?)null);
            if (value.IsBoolean()) return JsonSerializer.SerializeToElement(value.AsBoolean());
            if (value.IsString()) return JsonSerializer.SerializeToElement(value.AsString());
            if (value.IsNumber())
            {
                var number = value.AsNumber();
                EnsureFinite(number);
                return JsonSerializer.SerializeToElement(number);
            }
            if (value.IsBigInt()) throw Unsupported(value.ToObject() ?? value);

            return ClrToJson(value.ToObject(), budget, 0);
        }

        private static object? JsonToClr(
            JsonElement element,
            ValueConversionBudget budget,
            int depth)
        {
            budget.CheckDepth(depth);
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    return element.GetString();
                case JsonValueKind.Number:
                    return element.TryGetInt64(out var integer) ? integer : element.GetDouble();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Null:
                    return null;
                case JsonValueKind.Array:
                {
                    var result = new List<object?>();
                    foreach (var item in element.EnumerateArray())
                    {
                        budget.AddItem();
                        result.Add(JsonToClr(item, budget, depth + 1));
                    }

                    return result.ToArray();
                }
                case JsonValueKind.Object:
                {
                    var result = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var property in element.EnumerateObject())
                    {
                        budget.AddItem();
                        result[property.Name] = JsonToClr(property.Value, budget, depth + 1);
                    }

                    return result;
                }
                default:
                    throw new WorkflowDomainException(
                        $"JSON value kind '{element.ValueKind}' is not supported by JavaScript Script Tasks.");
            }
        }

        private static JsonElement ClrToJson(
            object? value,
            ValueConversionBudget budget,
            int depth)
        {
            budget.CheckDepth(depth);
            switch (value)
            {
                case null:
                    return JsonSerializer.SerializeToElement((object?)null);
                case JsonElement element:
                    ValidateJsonElement(element, budget, depth);
                    return element.Clone();
                case string s:
                    return JsonSerializer.SerializeToElement(s);
                case bool b:
                    return JsonSerializer.SerializeToElement(b);
                case double d:
                    EnsureFinite(d);
                    return JsonSerializer.SerializeToElement(d);
                case float f:
                    EnsureFinite(f);
                    return JsonSerializer.SerializeToElement(f);
                case DateTime dt:
                    return JsonSerializer.SerializeToElement(dt.ToString("o", CultureInfo.InvariantCulture));
                case DateTimeOffset dto:
                    return JsonSerializer.SerializeToElement(dto.ToString("o", CultureInfo.InvariantCulture));
                case BigInteger:
                    throw Unsupported(value);
                case sbyte or byte or short or ushort or int or uint or long or ulong or decimal:
                    return JsonSerializer.SerializeToElement(value, value.GetType());
            }

            // Check IDictionary<string,object> (covers Dictionary and ExpandoObject,
            // the two shapes Jint converts a JS object literal to) before the
            // general IEnumerable check, since Dictionary also implements it.
            if (value is IDictionary<string, object?> dictionary)
            {
                budget.Enter(value);
                try
                {
                    var obj = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    foreach (var pair in dictionary)
                    {
                        budget.AddItem();
                        obj[pair.Key] = ClrToJson(pair.Value, budget, depth + 1);
                    }

                    return JsonSerializer.SerializeToElement(obj);
                }
                finally
                {
                    budget.Exit(value);
                }
            }

            if (value is IEnumerable enumerable and not string)
            {
                budget.Enter(value);
                try
                {
                    var list = new List<JsonElement>();
                    foreach (var item in enumerable)
                    {
                        budget.AddItem();
                        list.Add(ClrToJson(item, budget, depth + 1));
                    }

                    return JsonSerializer.SerializeToElement(list);
                }
                finally
                {
                    budget.Exit(value);
                }
            }

            throw Unsupported(value);
        }

        private static void ValidateJsonElement(
            JsonElement element,
            ValueConversionBudget budget,
            int depth)
        {
            budget.CheckDepth(depth);
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    budget.AddItem();
                    ValidateJsonElement(item, budget, depth + 1);
                }
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    budget.AddItem();
                    ValidateJsonElement(property.Value, budget, depth + 1);
                }
            }
            else if (element.ValueKind == JsonValueKind.Number
                     && element.TryGetDouble(out var number))
            {
                EnsureFinite(number);
            }
            else if (element.ValueKind == JsonValueKind.Undefined)
            {
                throw new WorkflowDomainException(
                    "JavaScript values must be JSON-compatible; undefined is not supported.");
            }
        }

        private static void EnsureFinite(double value)
        {
            if (!double.IsFinite(value))
            {
                throw new WorkflowDomainException(
                    "JavaScript values must be JSON-compatible; NaN and Infinity are not supported.");
            }
        }

        private static WorkflowDomainException Unsupported(object value) =>
            new(
                $"JavaScript value type '{value.GetType().Name}' is not supported; "
                + "use null, boolean, finite number, string, Date, array, or plain object values.");
    }

    private sealed class ValueConversionBudget(ScriptOptions options)
    {
        private readonly HashSet<object> activeReferences = new(ReferenceEqualityComparer.Instance);
        private int items;
        private int bytes;

        public void CheckDepth(int depth)
        {
            if (depth > options.MaxValueDepth)
            {
                throw new WorkflowDomainException(
                    $"JavaScript value exceeds the maximum depth of {options.MaxValueDepth}.");
            }
        }

        public void AddItem()
        {
            items++;
            if (items > options.MaxValueItems)
            {
                throw new WorkflowDomainException(
                    $"JavaScript value exceeds the maximum item count of {options.MaxValueItems}.");
            }
        }

        public void AddBytes(JsonElement value)
        {
            bytes = checked(bytes + Encoding.UTF8.GetByteCount(value.GetRawText()));
            if (bytes > options.MaxValueBytes)
            {
                throw new WorkflowDomainException(
                    $"JavaScript value exceeds the maximum JSON size of {options.MaxValueBytes} bytes.");
            }
        }

        public void Enter(object value)
        {
            if (!activeReferences.Add(value))
            {
                throw new WorkflowDomainException(
                    "JavaScript values must be JSON-compatible; cyclic values are not supported.");
            }
        }

        public void Exit(object value) => activeReferences.Remove(value);
    }
}
