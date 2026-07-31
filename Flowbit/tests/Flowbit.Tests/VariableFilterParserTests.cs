using System.Text.Json;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Xunit;

namespace Flowbit.Tests;

public sealed class VariableFilterParserTests
{
    [Fact]
    public void Parse_TreatsMissingNullAndEmptyRootAsNoFilter()
    {
        Assert.Null(VariableFilterParser.Parse(null));
        Assert.Null(Parse("null"));
        Assert.Null(Parse("{}"));
    }

    [Fact]
    public void Parse_NormalizesDottedFieldsAndImplicitAnd()
    {
        var expression = Assert.IsType<VariableFilterAllExpression>(Parse(
            """
            {
              "request.medicalCenter.id": { "$eq": "MC-1042" },
              "request.priority": { "$gte": 5, "$lt": 10 }
            }
            """));

        Assert.Equal(2, expression.Terms.Count);

        var center = Assert.IsType<VariableFilterComparisonExpression>(expression.Terms[0]);
        Assert.Equal(VariableFilterFieldScope.InstanceVariable, center.Field.Scope);
        Assert.Equal("request", center.Field.VariableName);
        Assert.Equal(["medicalCenter", "id"], center.Field.Path);
        Assert.Equal(VariableFilterComparisonOperator.Equal, center.Operator);
        Assert.Equal("MC-1042", center.Operand.GetString());

        var minimum = Assert.IsType<VariableFilterAllExpression>(expression.Terms[1]);
        Assert.Collection(
            minimum.Terms,
            term => Assert.Equal(
                VariableFilterComparisonOperator.GreaterThanOrEqual,
                Assert.IsType<VariableFilterComparisonExpression>(term).Operator),
            term => Assert.Equal(
                VariableFilterComparisonOperator.LessThan,
                Assert.IsType<VariableFilterComparisonExpression>(term).Operator));
    }

    [Fact]
    public void Parse_PreservesExplicitDottedVariableNameAndPathSegments()
    {
        var expression = Assert.IsType<VariableFilterComparisonExpression>(Parse(
            """
            {
              "$field": {
                "$var": "request.medicalCenter",
                "$path": ["identifier.with.dot", "$private"],
                "$eqIgnoreCase": "mc-1042"
              }
            }
            """));

        Assert.Equal(VariableFilterFieldScope.InstanceVariable, expression.Field.Scope);
        Assert.Equal("request.medicalCenter", expression.Field.VariableName);
        Assert.Equal(["identifier.with.dot", "$private"], expression.Field.Path);
        Assert.Equal(VariableFilterComparisonOperator.EqualIgnoreCase, expression.Operator);
    }

    [Fact]
    public void Parse_RepresentsLogicalOperatorsWithoutMixingAuthorizationIntoTheTree()
    {
        var expression = Assert.IsType<VariableFilterAllExpression>(Parse(
            """
            {
              "$and": [
                { "center": { "$eq": "A" } },
                {
                  "$or": [
                    { "priority": { "$gt": 5 } },
                    { "$not": { "closed": { "$eq": true } } }
                  ]
                }
              ]
            }
            """));

        Assert.Equal(2, expression.Terms.Count);
        var any = Assert.IsType<VariableFilterAnyExpression>(expression.Terms[1]);
        Assert.IsType<VariableFilterNotExpression>(any.Terms[1]);
    }

    [Theory]
    [InlineData("$eq", "null", VariableFilterComparisonOperator.Equal)]
    [InlineData("$eqIgnoreCase", "\"text\"", VariableFilterComparisonOperator.EqualIgnoreCase)]
    [InlineData("$ne", "false", VariableFilterComparisonOperator.NotEqual)]
    [InlineData("$in", "[1,2,3]", VariableFilterComparisonOperator.In)]
    [InlineData("$nin", "[\"a\",\"b\"]", VariableFilterComparisonOperator.NotIn)]
    [InlineData("$gt", "1.5", VariableFilterComparisonOperator.GreaterThan)]
    [InlineData("$gte", "1", VariableFilterComparisonOperator.GreaterThanOrEqual)]
    [InlineData("$lt", "2", VariableFilterComparisonOperator.LessThan)]
    [InlineData("$lte", "2", VariableFilterComparisonOperator.LessThanOrEqual)]
    [InlineData("$exists", "false", VariableFilterComparisonOperator.Exists)]
    [InlineData("$contains", "{\"status\":\"approved\"}", VariableFilterComparisonOperator.Contains)]
    [InlineData("$containsAny", "[\"a\",\"b\"]", VariableFilterComparisonOperator.ContainsAny)]
    [InlineData("$containsAll", "[\"a\",\"b\"]", VariableFilterComparisonOperator.ContainsAll)]
    public void Parse_SupportsEveryScalarOrContainmentOperator(
        string operatorName,
        string operand,
        VariableFilterComparisonOperator expected)
    {
        var expression = Assert.IsType<VariableFilterComparisonExpression>(
            Parse($"{{\"value\": {{\"{operatorName}\": {operand}}}}}"));

        Assert.Equal(expected, expression.Operator);
    }

    [Fact]
    public void Parse_ElemMatchUsesElementRelativeFieldsForOneNestedPredicate()
    {
        var expression = Assert.IsType<VariableFilterElementMatchExpression>(Parse(
            """
            {
              "request.approvals": {
                "$elemMatch": {
                  "actor.role": { "$eq": "doctor" },
                  "status": { "$eq": "approved" }
                }
              }
            }
            """));

        Assert.Equal("request", expression.Field.VariableName);
        Assert.Equal(["approvals"], expression.Field.Path);

        var nested = Assert.IsType<VariableFilterAllExpression>(expression.Predicate);
        var role = Assert.IsType<VariableFilterComparisonExpression>(nested.Terms[0]);
        Assert.Equal(VariableFilterFieldScope.Element, role.Field.Scope);
        Assert.Null(role.Field.VariableName);
        Assert.Equal(["actor", "role"], role.Field.Path);
    }

    [Fact]
    public void Parse_ElemMatchCanComparePrimitiveArrayElements()
    {
        var expression = Assert.IsType<VariableFilterElementMatchExpression>(Parse(
            """{ "scores": { "$elemMatch": { "$gte": 70, "$lt": 90 } } }"""));

        var nested = Assert.IsType<VariableFilterAllExpression>(expression.Predicate);
        Assert.All(nested.Terms, term =>
        {
            var comparison = Assert.IsType<VariableFilterComparisonExpression>(term);
            Assert.Equal(VariableFilterFieldScope.Element, comparison.Field.Scope);
            Assert.Empty(comparison.Field.Path);
        });
    }

    [Fact]
    public void Parse_ElemMatchExplicitFieldEscapesDottedElementKeys()
    {
        var expression = Assert.IsType<VariableFilterElementMatchExpression>(Parse(
            """
            {
              "items": {
                "$elemMatch": {
                  "$field": {
                    "$path": ["medical.center", "id"],
                    "$eq": "MC-1042"
                  }
                }
              }
            }
            """));

        var comparison = Assert.IsType<VariableFilterComparisonExpression>(
            expression.Predicate);
        Assert.Equal(VariableFilterFieldScope.Element, comparison.Field.Scope);
        Assert.Null(comparison.Field.VariableName);
        Assert.Equal(["medical.center", "id"], comparison.Field.Path);
    }

    [Theory]
    [InlineData("{ \"$field\": { \"$eq\": 1 } }")]
    [InlineData("{ \"$field\": { \"$var\": \"other\", \"$path\": [\"id\"], \"$eq\": 1 } }")]
    public void Parse_ElemMatchRejectsAmbiguousExplicitFieldShapes(string predicate)
    {
        Assert.Throws<WorkflowDomainException>(() =>
            Parse("{\"items\":{\"$elemMatch\":" + predicate + "}}"));
    }

    [Fact]
    public void Parse_ClonesOperandsFromTheInputDocument()
    {
        VariableFilterExpression? parsed;
        using (var document = JsonDocument.Parse("""{ "value": { "$eq": { "nested": true } } }"""))
        {
            parsed = VariableFilterParser.Parse(document.RootElement);
        }

        var comparison = Assert.IsType<VariableFilterComparisonExpression>(parsed);
        Assert.True(comparison.Operand.GetProperty("nested").GetBoolean());
    }

    [Fact]
    public void FromLegacy_PreservesWholeDottedNameAndScalarTextCompatibilityOperator()
    {
        var expression = Assert.IsType<VariableFilterAllExpression>(
            VariableFilterParser.FromLegacy(
            [
                new VariableFilter("request.center", "MC-1042"),
                new VariableFilter("number", "42")
            ]));

        var dotted = Assert.IsType<VariableFilterComparisonExpression>(expression.Terms[0]);
        Assert.Equal("request.center", dotted.Field.VariableName);
        Assert.Empty(dotted.Field.Path);
        Assert.Equal(VariableFilterComparisonOperator.LegacyEqualIgnoreCase, dotted.Operator);
        Assert.Equal("MC-1042", dotted.Operand.GetString());
    }

    [Fact]
    public void Parse_PreservesInjectionShapedNamesPathsAndValuesAsData()
    {
        const string json =
            """
            {
              "$field": {
                "$var": "request' OR TRUE --",
                "$path": ["x') OR pg_sleep(5) --"],
                "$eq": "value' OR TRUE --"
              }
            }
            """;

        var expression = Assert.IsType<VariableFilterComparisonExpression>(Parse(json));
        Assert.Equal("request' OR TRUE --", expression.Field.VariableName);
        Assert.Equal(["x') OR pg_sleep(5) --"], expression.Field.Path);
        Assert.Equal("value' OR TRUE --", expression.Operand.GetString());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("{ \"$where\": \"dangerous()\" }")]
    [InlineData("{ \"value\": { \"$regex\": \".*\" } }")]
    [InlineData("{ \"value\": 42 }")]
    [InlineData("{ \"$and\": [], \"value\": { \"$eq\": 1 } }")]
    [InlineData("{ \"$and\": [] }")]
    [InlineData("{ \"value\": { } }")]
    [InlineData("{ \"value.0\": { \"$eq\": 1 } }")]
    [InlineData("{ \"value.-1\": { \"$eq\": 1 } }")]
    [InlineData("{ \"$field\": { \"$var\": \"value\", \"$path\": [\"0\"], \"$eq\": 1 } }")]
    public void Parse_RejectsUnsupportedOrMalformedShapes(string json)
    {
        Assert.Throws<WorkflowDomainException>(() => Parse(json));
    }

    [Theory]
    [InlineData("$eqIgnoreCase", "1")]
    [InlineData("$in", "1")]
    [InlineData("$in", "[{}]")]
    [InlineData("$nin", "[[1]]")]
    [InlineData("$gt", "\"10\"")]
    [InlineData("$gte", "true")]
    [InlineData("$lt", "null")]
    [InlineData("$exists", "\"true\"")]
    [InlineData("$containsAny", "\"a\"")]
    [InlineData("$containsAll", "{}")]
    [InlineData("$elemMatch", "[]")]
    public void Parse_RejectsTypeInvalidOperands(string operatorName, string operand)
    {
        var json = $"{{\"value\": {{\"{operatorName}\": {operand}}}}}";
        Assert.Throws<WorkflowDomainException>(() => Parse(json));
    }

    [Fact]
    public void Parse_DistinguishesExplicitJsonNullFromNoFilter()
    {
        var expression = Assert.IsType<VariableFilterComparisonExpression>(
            Parse("""{ "value": { "$eq": null } }"""));

        Assert.Equal(JsonValueKind.Null, expression.Operand.ValueKind);
    }

    [Fact]
    public void Parse_RejectsDuplicateJsonMembers()
    {
        Assert.Throws<WorkflowDomainException>(() =>
            Parse("""{ "value": { "$eq": 1, "$eq": 2 } }"""));
    }

    [Fact]
    public void Parse_EnforcesLogicalDepthLimit()
    {
        var valid = """{ "value": { "$eq": 1 } }""";
        for (var index = 0; index < VariableFilterParser.MaxLogicalDepth; index++)
        {
            valid = "{\"$not\":" + valid + "}";
        }

        Assert.NotNull(Parse(valid));
        var tooDeep = "{\"$not\":" + valid + "}";
        Assert.Throws<WorkflowDomainException>(() => Parse(tooDeep));
    }

    [Fact]
    public void Parse_EnforcesComparisonLimitIncludingElemMatch()
    {
        var fields = Enumerable.Range(1, VariableFilterParser.MaxComparisonPredicates + 1)
            .Select(index => $"\"v{index}\":{{\"$eq\":{index}}}");
        var json = "{" + string.Join(',', fields) + "}";

        Assert.Throws<WorkflowDomainException>(() => Parse(json));
    }

    [Fact]
    public void Parse_EnforcesMembershipValueLimit()
    {
        var values = string.Join(',', Enumerable.Range(1, VariableFilterParser.MaxMembershipValues + 1));
        Assert.Throws<WorkflowDomainException>(() =>
            Parse("{\"value\":{\"$in\":[" + values + "]}}"));
    }

    [Fact]
    public void Parse_EnforcesPathSegmentLimit()
    {
        var field = "value." + string.Join(
            '.',
            Enumerable.Range(1, VariableFilterParser.MaxPathSegments + 1)
                .Select(index => $"p{index}"));

        Assert.Throws<WorkflowDomainException>(() =>
            Parse($"{{\"{field}\":{{\"$eq\":1}}}}"));
    }

    [Fact]
    public void Parse_EnforcesUtf8SizeLimit()
    {
        var oversized = new string('x', VariableFilterParser.MaxUtf8Bytes);
        var element = JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["value"] = new Dictionary<string, object> { ["$eq"] = oversized }
        });

        Assert.Throws<WorkflowDomainException>(() => VariableFilterParser.Parse(element));
    }

    [Theory]
    [InlineData("""{ "bad\u0000variable": { "$eq": 1 } }""")]
    [InlineData("""{ "$field": { "$var": "bad\u0000variable", "$eq": 1 } }""")]
    [InlineData("""{ "$field": { "$var": "value", "$path": ["bad\u0000path"], "$eq": 1 } }""")]
    [InlineData("""{ "value": { "$eqIgnoreCase": "bad\u0000value" } }""")]
    [InlineData("""{ "value": { "$eq": { "bad\u0000key": "ok" } } }""")]
    [InlineData("""{ "value": { "$eq": { "key": "bad\u0000value" } } }""")]
    [InlineData("""{ "value": { "$in": ["ok", "bad\u0000value"] } }""")]
    [InlineData("""{ "value": { "$nin": ["bad\u0000value"] } }""")]
    [InlineData("""{ "value": { "$containsAny": ["bad\u0000value"] } }""")]
    [InlineData("""{ "value": { "$containsAll": [{ "key": "bad\u0000value" }] } }""")]
    public void Parse_RejectsUnicodeNullThatPostgresCannotRepresent(string json)
    {
        Assert.Throws<WorkflowDomainException>(() => Parse(json));
    }

    [Fact]
    public void Parse_AllowsOtherEscapedControlCharactersSupportedByPostgres()
    {
        var expression = Assert.IsType<VariableFilterComparisonExpression>(
            Parse("""{ "value": { "$eqIgnoreCase": "allowed\u0001value" } }"""));

        Assert.Equal("allowed\u0001value", expression.Operand.GetString());
    }

    [Theory]
    [InlineData("""{ "value": { "$eq": 1e131072 } }""")]
    [InlineData("""{ "value": { "$gte": -1e131072 } }""")]
    [InlineData("""{ "value": { "$eq": 1e-16384 } }""")]
    [InlineData("""{ "value": { "$eq": { "nested": 9e131072 } } }""")]
    [InlineData("""{ "value": { "$in": [1, 1e131072] } }""")]
    [InlineData("""{ "value": { "$containsAll": [{ "nested": 1e-16384 }] } }""")]
    public void Parse_RejectsNumbersOutsidePostgresJsonbNumericRange(string json)
    {
        Assert.Throws<WorkflowDomainException>(() => Parse(json));
    }

    [Theory]
    [InlineData("1e308")]
    [InlineData("-9.99e1000")]
    [InlineData("1e131071")]
    [InlineData("0.1e131072")]
    [InlineData("-9e131071")]
    [InlineData("1e-16383")]
    public void Parse_AllowsNormalAndBoundaryPostgresNumericValues(string number)
    {
        var expression = Assert.IsType<VariableFilterComparisonExpression>(
            Parse("{\"value\":{\"$eq\":" + number + "}}"));

        Assert.Equal(JsonValueKind.Number, expression.Operand.ValueKind);
        Assert.Equal(number, expression.Operand.GetRawText());
    }

    private static VariableFilterExpression? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return VariableFilterParser.Parse(document.RootElement);
    }
}
