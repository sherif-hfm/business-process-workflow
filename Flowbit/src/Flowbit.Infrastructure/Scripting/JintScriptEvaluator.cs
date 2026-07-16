using System.Collections;
using System.Globalization;
using System.Text.Json;
using Jint;
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
/// Execution is bounded by <see cref="ScriptOptions"/> (Jint's own timeout /
/// max-statements / memory constraints), plus a hard wall-clock
/// <see cref="CancellationTokenSource.CancelAfter"/> backstop: a few Jint built-ins
/// perform bulk work in a single CLR call that bypasses per-step constraint checks
/// (sebastienros/jint#2486), so the in-engine limits alone cannot be fully trusted.
/// </summary>
public sealed class JintScriptEvaluator(ScriptOptions options, ILogger<JintScriptEvaluator> logger) : IScriptEvaluator
{
    public ScriptResult Evaluate(string script, IScriptContext context, CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var engine = new Engine(o => o
            .LimitMemory(options.MemoryBytes)
            .TimeoutInterval(TimeSpan.FromSeconds(options.TimeoutSeconds))
            .MaxStatements(options.MaxStatements)
            .CancellationToken(linkedCts.Token)
            .Strict());
        // Intentionally no .AllowClr(): the script has zero CLR surface beyond the
        // single `execution` object explicitly bound below.

        engine.SetValue("execution", new ExecutionBinding(context));

        logger.LogInformation("Executing Jint JavaScript sandbox script...");

        try
        {
            engine.Execute(script);
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
        catch (JavaScriptException ex)
        {
            logger.LogWarning(ex, "Jint script encountered a JavaScriptException: {Message}", ex.Message);
            return ScriptResult.Fail(ex.Message);
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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The linked token was cancelled by our own CancelAfter guard, not by
            // the caller's cancellation - treat it as a timeout.
            logger.LogWarning("Jint script execution exceeded timeout of {TimeoutSeconds}s (cancelled by CancelAfter guard).", options.TimeoutSeconds);
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
            _ = Engine.PrepareScript(script);
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
    /// access. Method parameters/return values use loosely-typed <c>object</c> so
    /// Jint applies its normal JS&lt;-&gt;CLR conversions (number-&gt;double,
    /// string, bool, Array-&gt;object[], JS object-&gt;IDictionary&lt;string,object&gt;).
    /// </summary>
    private sealed class ExecutionBinding(IScriptContext context)
    {
        public object? GetVariable(string name) =>
            context.TryGetVariable(name, out var value) ? JsonToClr(value) : null;

        public bool HasVariable(string name) => context.HasVariable(name);

        public IDictionary<string, object?> GetVariables()
        {
            var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in context.GetVariables())
            {
                result[pair.Key] = JsonToClr(pair.Value);
            }

            return result;
        }

        public void SetVariable(string name, object? value) =>
            context.SetVariable(name, ClrToJson(value));

        private static object? JsonToClr(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.Array => element.EnumerateArray().Select(JsonToClr).ToArray(),
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(p => p.Name, p => JsonToClr(p.Value), StringComparer.OrdinalIgnoreCase),
            _ => null
        };

        private static JsonElement ClrToJson(object? value)
        {
            switch (value)
            {
                case null:
                    return JsonSerializer.SerializeToElement((object?)null);
                case JsonElement je:
                    return je.Clone();
                case string s:
                    return JsonSerializer.SerializeToElement(s);
                case bool b:
                    return JsonSerializer.SerializeToElement(b);
                case double d:
                    return JsonSerializer.SerializeToElement(d);
                case DateTime dt:
                    return JsonSerializer.SerializeToElement(dt.ToString("o", CultureInfo.InvariantCulture));
            }

            // Check IDictionary<string,object> (covers Dictionary and ExpandoObject,
            // the two shapes Jint converts a JS object literal to) before the
            // general IEnumerable check, since Dictionary also implements it.
            if (value is IDictionary<string, object> dictionary)
            {
                var obj = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in dictionary)
                {
                    obj[pair.Key] = ClrToJson(pair.Value);
                }

                return JsonSerializer.SerializeToElement(obj);
            }

            if (value is IEnumerable enumerable and not string)
            {
                var list = new List<JsonElement>();
                foreach (var item in enumerable)
                {
                    list.Add(ClrToJson(item));
                }

                return JsonSerializer.SerializeToElement(list);
            }

            // Tolerate numeric CLR types other than double (Jint normally hands
            // back double for JS numbers, but be defensive).
            return value switch
            {
                sbyte or byte or short or ushort or int or uint or long or ulong or float or decimal =>
                    JsonSerializer.SerializeToElement(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
                _ => JsonSerializer.SerializeToElement(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)
            };
        }
    }
}
