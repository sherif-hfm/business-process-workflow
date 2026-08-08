using System.Text.Json;
using Flowbit.Service.Models;

namespace Flowbit.Infrastructure.Repositories;

internal static class JsonMapping
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static JsonDocument ToJsonDocument(JsonElement value) =>
        JsonDocument.Parse(value.GetRawText());

    public static JsonDocument? ToJsonDocument(Dictionary<string, JsonElement>? values)
    {
        if (values is null)
        {
            return null;
        }

        var json = JsonSerializer.Serialize(values, Options);
        return JsonDocument.Parse(json);
    }

    public static JsonDocument ToJsonDocument(IReadOnlyList<string> values)
    {
        var json = JsonSerializer.Serialize(values, Options);
        return JsonDocument.Parse(json);
    }

    public static JsonDocument? ToJsonDocument(
        SequenceFlowAdministrativeActionRecord? value)
    {
        if (value is null)
        {
            return null;
        }

        return JsonDocument.Parse(JsonSerializer.Serialize(value, Options));
    }

    public static Dictionary<string, JsonElement>? ToDictionary(JsonDocument? document)
    {
        if (document is null || document.RootElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(document.RootElement.GetRawText(), Options)?
            .ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
    }

    public static IReadOnlyList<string>? ToStringList(JsonDocument? document)
    {
        if (document is null || document.RootElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return JsonSerializer.Deserialize<List<string>>(document.RootElement.GetRawText(), Options) ?? [];
    }

    public static SequenceFlowAdministrativeActionRecord? ToAdministrativeAction(
        JsonDocument? document)
    {
        if (document is null
            || document.RootElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return JsonSerializer.Deserialize<SequenceFlowAdministrativeActionRecord>(
            document.RootElement.GetRawText(),
            Options);
    }
}
