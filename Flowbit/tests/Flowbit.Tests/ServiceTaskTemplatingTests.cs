using System.Text.Json;
using Flowbit.Service.Services;
using Xunit;

namespace Flowbit.Tests;

public sealed class ServiceTaskTemplatingTests
{
    [Fact]
    public void TrySubstituteScalarStrict_SubstitutesAllScalarKinds()
    {
        var variables = Variables(
            ("tenant", "acme"),
            ("attempt", 3),
            ("enabled", true));

        var success = ServiceTaskTemplating.TrySubstituteScalarStrict(
            "https://api.test/${tenant}?attempt=${attempt}&enabled=${enabled}",
            variables,
            out var result,
            out var missing);

        Assert.True(success);
        Assert.Null(missing);
        Assert.Equal("https://api.test/acme?attempt=3&enabled=true", result);
    }

    [Fact]
    public void TrySubstituteScalarStrict_ReportsFirstMissingVariable()
    {
        var variables = Variables(("present", "yes"));

        var success = ServiceTaskTemplating.TrySubstituteScalarStrict(
            "Bearer ${missing}-${present}-${later}",
            variables,
            out var result,
            out var missing);

        Assert.False(success);
        Assert.Equal("missing", missing);
        Assert.Equal("Bearer -yes-", result);
    }

    [Fact]
    public void SubstituteJson_KeepsMissingBodyPlaceholdersPermissiveAndValid()
    {
        var variables = Variables(("name", "Alice \"QA\""));

        var result = ServiceTaskTemplating.SubstituteJson(
            "{\"message\":\"Hi ${name}${missingText}\",\"optional\":${missingValue}}",
            variables);

        using var parsed = JsonDocument.Parse(result!);
        Assert.Equal("Hi Alice \"QA\"", parsed.RootElement.GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Null, parsed.RootElement.GetProperty("optional").ValueKind);
    }

    [Fact]
    public void TryExtract_NavigatesObjectsAndArrayIndexes()
    {
        using var document = JsonDocument.Parse("{\"orders\":[{\"id\":17}]}");

        var found = ServiceTaskTemplating.TryExtract(
            document.RootElement,
            "orders.0.id",
            out var value);

        Assert.True(found);
        Assert.Equal(17, value.GetInt32());
        Assert.False(ServiceTaskTemplating.TryExtract(document.RootElement, "orders.1.id", out _));
    }

    private static IReadOnlyDictionary<string, JsonElement> Variables(
        params (string Name, object Value)[] values) =>
        values.ToDictionary(
            pair => pair.Name,
            pair => JsonSerializer.SerializeToElement(pair.Value),
            StringComparer.OrdinalIgnoreCase);
}
