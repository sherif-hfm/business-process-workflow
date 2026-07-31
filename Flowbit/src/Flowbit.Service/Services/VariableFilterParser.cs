using System.Text;
using System.Text.Json;
using Flowbit.Service.Models;

namespace Flowbit.Service.Services;

/// <summary>
/// Parses the bounded, Mongo-inspired public variable-filter syntax into a
/// small AST that infrastructure can translate using whitelisted SQL shapes.
/// </summary>
public static class VariableFilterParser
{
    private const int PostgreSqlNumericMaxIntegerDigits = 131_072;
    private const int PostgreSqlNumericMaxFractionalDigits = 16_383;

    public const int MaxUtf8Bytes = 64 * 1024;
    public const int MaxLogicalDepth = 5;
    public const int MaxComparisonPredicates = 20;
    public const int MaxMembershipValues = 100;
    public const int MaxPathSegments = 16;
    public const int MaxVariableNameLength = 300;

    /// <summary>
    /// Parses an advanced filter. A missing value, JSON null, or an empty root
    /// object means that no additional variable predicate was requested.
    /// </summary>
    public static VariableFilterExpression? Parse(JsonElement? filter)
    {
        if (filter is null
            || filter.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var element = filter.Value;
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("Variable filter must be a JSON object.");
        }

        if (Encoding.UTF8.GetByteCount(element.GetRawText()) > MaxUtf8Bytes)
        {
            throw Invalid($"Variable filter cannot exceed {MaxUtf8Bytes} UTF-8 bytes.");
        }

        return new Parser().ParseRoot(element);
    }

    /// <summary>
    /// Converts already-validated legacy <c>name:value</c> filters without
    /// changing their case-insensitive scalar-string semantics. A legacy name
    /// is always treated as the complete variable name, even when it has dots.
    /// </summary>
    public static VariableFilterExpression? FromLegacy(
        IReadOnlyList<VariableFilter>? filters)
    {
        if (filters is null || filters.Count == 0)
        {
            return null;
        }

        var terms = new List<VariableFilterExpression>(filters.Count);
        foreach (var filter in filters)
        {
            ValidatePostgreSqlText(filter.Name, "Legacy variable names");
            ValidatePostgreSqlText(filter.Value, "Legacy variable filter values");
            terms.Add(new VariableFilterComparisonExpression(
                new VariableFilterFieldReference(
                    VariableFilterFieldScope.InstanceVariable,
                    filter.Name,
                    []),
                VariableFilterComparisonOperator.LegacyEqualIgnoreCase,
                JsonSerializer.SerializeToElement(filter.Value)));
        }

        return CombineAll(terms);
    }

    private static VariableFilterExpression CombineAll(
        IReadOnlyList<VariableFilterExpression> terms) =>
        terms.Count == 1
            ? terms[0]
            : new VariableFilterAllExpression(terms);

    private static WorkflowDomainException Invalid(string message) =>
        new($"Invalid variable filter: {message}");

    private static void ValidatePostgreSqlText(string value, string label)
    {
        if (value.Contains('\0'))
        {
            throw Invalid($"{label} cannot contain the Unicode null character.");
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    throw Invalid($"{label} contains invalid Unicode.");
                }

                index++;
            }
            else if (char.IsLowSurrogate(value[index]))
            {
                throw Invalid($"{label} contains invalid Unicode.");
            }
        }
    }

    private sealed class Parser
    {
        private int _comparisonCount;

        public VariableFilterExpression? ParseRoot(JsonElement element)
        {
            var properties = ReadUniqueProperties(element);
            if (properties.Count == 0)
            {
                return null;
            }

            return ParseObject(properties, logicalDepth: 0, elementRelative: false);
        }

        private VariableFilterExpression ParseObject(
            IReadOnlyList<JsonProperty> properties,
            int logicalDepth,
            bool elementRelative)
        {
            if (properties.Count == 0)
            {
                throw Invalid("Nested filter objects cannot be empty.");
            }

            var dollarProperties = properties
                .Where(property => property.Name.StartsWith('$'))
                .ToArray();

            if (dollarProperties.Length == 0)
            {
                return ParseImplicitAnd(properties, logicalDepth, elementRelative);
            }

            var ordinaryProperties = properties
                .Where(property => !property.Name.StartsWith('$'))
                .ToArray();

            if (ordinaryProperties.Length > 0)
            {
                throw Invalid(
                    "Logical or special operators cannot be mixed with field members; use an explicit $and.");
            }

            if (properties.Count == 1 && properties[0].NameEquals("$field"))
            {
                return ParseExplicitField(
                    properties[0].Value,
                    logicalDepth,
                    elementRelative);
            }

            if (properties.Count == 1 && IsLogicalOperator(properties[0].Name))
            {
                return ParseLogical(properties[0], logicalDepth, elementRelative);
            }

            if (elementRelative && properties.All(property => IsComparisonOperator(property.Name)))
            {
                var currentElement = new VariableFilterFieldReference(
                    VariableFilterFieldScope.Element,
                    VariableName: null,
                    Path: []);
                return ParseComparisonObject(
                    currentElement,
                    properties,
                    logicalDepth,
                    elementRelative: true);
            }

            var unknown = properties
                .Where(property =>
                    !IsLogicalOperator(property.Name)
                    && !property.NameEquals("$field"))
                .Select(static property => (JsonProperty?)property)
                .FirstOrDefault();
            if (unknown is JsonProperty unknownProperty)
            {
                throw Invalid($"Unknown operator '{unknownProperty.Name}'.");
            }

            throw Invalid("A logical filter object must contain exactly one logical operator.");
        }

        private VariableFilterExpression ParseImplicitAnd(
            IReadOnlyList<JsonProperty> properties,
            int logicalDepth,
            bool elementRelative)
        {
            var terms = new List<VariableFilterExpression>(properties.Count);
            foreach (var property in properties)
            {
                var field = ParseDottedField(property.Name, elementRelative);
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    throw Invalid(
                        $"Field '{property.Name}' must contain a comparison-operator object.");
                }

                var operators = ReadUniqueProperties(property.Value);
                if (operators.Count == 0)
                {
                    throw Invalid($"Field '{property.Name}' must contain at least one comparison operator.");
                }
                if (operators.Any(item => !item.Name.StartsWith('$')))
                {
                    throw Invalid($"Field '{property.Name}' contains a member that is not an operator.");
                }

                terms.Add(ParseComparisonObject(
                    field,
                    operators,
                    logicalDepth,
                    elementRelative));
            }

            return CombineAll(terms);
        }

        private VariableFilterExpression ParseExplicitField(
            JsonElement element,
            int logicalDepth,
            bool elementRelative)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("$field must contain an object.");
            }

            var properties = ReadUniqueProperties(element);
            var variableProperty = FindProperty(properties, "$var");
            if (elementRelative && variableProperty is not null)
            {
                throw Invalid("Element-relative $field cannot contain $var.");
            }
            if (!elementRelative
                && (variableProperty is null
                    || variableProperty.Value.Value.ValueKind != JsonValueKind.String))
            {
                throw Invalid("$field requires a string $var member.");
            }

            string? variableName = null;
            if (!elementRelative)
            {
                variableName = variableProperty!.Value.Value.GetString()!;
                ValidateVariableName(variableName);
            }

            var pathProperty = FindProperty(properties, "$path");
            if (elementRelative && pathProperty is null)
            {
                throw Invalid("Element-relative $field requires a $path member.");
            }
            IReadOnlyList<string> path = [];
            if (pathProperty is not null)
            {
                path = ParseExplicitPath(pathProperty.Value.Value);
            }

            var operators = properties
                .Where(property => !property.NameEquals("$var") && !property.NameEquals("$path"))
                .ToArray();
            if (operators.Length == 0)
            {
                throw Invalid("$field requires at least one comparison operator.");
            }
            if (operators.Any(property => !property.Name.StartsWith('$')))
            {
                throw Invalid("$field contains an unknown member.");
            }

            var field = new VariableFilterFieldReference(
                elementRelative
                    ? VariableFilterFieldScope.Element
                    : VariableFilterFieldScope.InstanceVariable,
                variableName,
                path);
            return ParseComparisonObject(
                field,
                operators,
                logicalDepth,
                elementRelative);
        }

        private VariableFilterExpression ParseComparisonObject(
            VariableFilterFieldReference field,
            IReadOnlyList<JsonProperty> operators,
            int logicalDepth,
            bool elementRelative)
        {
            var terms = new List<VariableFilterExpression>(operators.Count);
            foreach (var property in operators)
            {
                if (property.NameEquals("$elemMatch"))
                {
                    AddComparison();
                    if (property.Value.ValueKind != JsonValueKind.Object)
                    {
                        throw Invalid("$elemMatch requires an object predicate.");
                    }

                    var nestedProperties = ReadUniqueProperties(property.Value);
                    if (nestedProperties.Count == 0)
                    {
                        throw Invalid("$elemMatch predicate cannot be empty.");
                    }

                    terms.Add(new VariableFilterElementMatchExpression(
                        field,
                        ParseObject(
                            nestedProperties,
                            logicalDepth,
                            elementRelative: true)));
                    continue;
                }

                var comparisonOperator = ParseComparisonOperator(property.Name);
                ValidateOperand(comparisonOperator, property.Value, property.Name);
                AddComparison();
                terms.Add(new VariableFilterComparisonExpression(
                    field,
                    comparisonOperator,
                    property.Value.Clone()));
            }

            return CombineAll(terms);
        }

        private VariableFilterExpression ParseLogical(
            JsonProperty property,
            int logicalDepth,
            bool elementRelative)
        {
            var nextDepth = logicalDepth + 1;
            if (nextDepth > MaxLogicalDepth)
            {
                throw Invalid($"Logical nesting cannot exceed {MaxLogicalDepth} levels.");
            }

            if (property.NameEquals("$not"))
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    throw Invalid("$not requires one object predicate.");
                }

                var nested = ReadUniqueProperties(property.Value);
                if (nested.Count == 0)
                {
                    throw Invalid("$not predicate cannot be empty.");
                }

                return new VariableFilterNotExpression(
                    ParseObject(nested, nextDepth, elementRelative));
            }

            if (property.Value.ValueKind != JsonValueKind.Array)
            {
                throw Invalid($"{property.Name} requires a non-empty array of object predicates.");
            }

            var terms = new List<VariableFilterExpression>();
            foreach (var item in property.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw Invalid($"Every {property.Name} item must be an object predicate.");
                }

                var nested = ReadUniqueProperties(item);
                if (nested.Count == 0)
                {
                    throw Invalid($"{property.Name} items cannot be empty.");
                }

                terms.Add(ParseObject(nested, nextDepth, elementRelative));
            }

            if (terms.Count == 0)
            {
                throw Invalid($"{property.Name} requires at least one predicate.");
            }

            return property.NameEquals("$and")
                ? new VariableFilterAllExpression(terms)
                : new VariableFilterAnyExpression(terms);
        }

        private static VariableFilterFieldReference ParseDottedField(
            string raw,
            bool elementRelative)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw Invalid("Field names cannot be blank.");
            }

            var segments = raw.Split('.', StringSplitOptions.None);
            if (segments.Any(segment => segment.Length == 0))
            {
                throw Invalid($"Field '{raw}' contains an empty path segment.");
            }

            if (elementRelative)
            {
                ValidatePath(segments, raw);
                return new VariableFilterFieldReference(
                    VariableFilterFieldScope.Element,
                    VariableName: null,
                    segments);
            }

            ValidateVariableName(segments[0]);
            var path = segments.Skip(1).ToArray();
            ValidatePath(path, raw);
            return new VariableFilterFieldReference(
                VariableFilterFieldScope.InstanceVariable,
                segments[0],
                path);
        }

        private static IReadOnlyList<string> ParseExplicitPath(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                throw Invalid("$path must be an array of string property names.");
            }

            var segments = new List<string>();
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String
                    || string.IsNullOrEmpty(item.GetString()))
                {
                    throw Invalid("Every $path segment must be a non-empty string.");
                }

                segments.Add(item.GetString()!);
            }

            ValidatePath(segments, "$path");
            return segments.ToArray();
        }

        private static void ValidateVariableName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw Invalid("Variable names cannot be blank.");
            }
            ValidatePostgreSqlText(name, "Variable names");
            if (name.EnumerateRunes().Count() > MaxVariableNameLength)
            {
                throw Invalid(
                    $"Variable names cannot exceed {MaxVariableNameLength} Unicode scalar values.");
            }
        }

        private static void ValidatePath(
            IReadOnlyList<string> path,
            string fieldLabel)
        {
            if (path.Count > MaxPathSegments)
            {
                throw Invalid(
                    $"Field '{fieldLabel}' cannot contain more than {MaxPathSegments} JSON path segments.");
            }

            foreach (var segment in path)
            {
                ValidatePostgreSqlText(segment, "JSON path segments");
                if (IsNumericPathSegment(segment))
                {
                    throw Invalid(
                        $"Field '{fieldLabel}' contains numeric path segment '{segment}'; use array operators instead of indexes.");
                }
            }
        }

        private static bool IsNumericPathSegment(string segment)
        {
            var digits = segment.AsSpan();
            if (digits.Length > 0 && digits[0] is '+' or '-')
            {
                digits = digits[1..];
            }

            return digits.Length > 0
                && digits.IndexOfAnyExceptInRange('0', '9') < 0;
        }

        private static void ValidateOperand(
            VariableFilterComparisonOperator comparisonOperator,
            JsonElement operand,
            string operatorName)
        {
            switch (comparisonOperator)
            {
                case VariableFilterComparisonOperator.EqualIgnoreCase:
                    if (operand.ValueKind != JsonValueKind.String)
                    {
                        throw Invalid("$eqIgnoreCase accepts a string operand only.");
                    }
                    ValidatePostgreSqlText(
                        operand.GetString()!,
                        "$eqIgnoreCase string operands");
                    return;

                case VariableFilterComparisonOperator.LegacyEqualIgnoreCase:
                    throw new ArgumentOutOfRangeException(
                        nameof(comparisonOperator),
                        comparisonOperator,
                        "LegacyEqualIgnoreCase is created only by FromLegacy.");

                case VariableFilterComparisonOperator.In:
                case VariableFilterComparisonOperator.NotIn:
                    ValidateMembershipArray(operand, operatorName, scalarOnly: true);
                    return;

                case VariableFilterComparisonOperator.ContainsAny:
                case VariableFilterComparisonOperator.ContainsAll:
                    ValidateMembershipArray(operand, operatorName, scalarOnly: false);
                    return;

                case VariableFilterComparisonOperator.GreaterThan:
                case VariableFilterComparisonOperator.GreaterThanOrEqual:
                case VariableFilterComparisonOperator.LessThan:
                case VariableFilterComparisonOperator.LessThanOrEqual:
                    if (operand.ValueKind != JsonValueKind.Number)
                    {
                        throw Invalid($"{operatorName} accepts a JSON number operand only.");
                    }
                    ValidateJsonbCompatible(operand, operatorName);
                    return;

                case VariableFilterComparisonOperator.Exists:
                    if (operand.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        throw Invalid("$exists accepts a Boolean operand only.");
                    }
                    return;

                case VariableFilterComparisonOperator.Equal:
                case VariableFilterComparisonOperator.NotEqual:
                case VariableFilterComparisonOperator.Contains:
                    if (operand.ValueKind == JsonValueKind.Undefined)
                    {
                        throw Invalid($"{operatorName} has an invalid operand.");
                    }
                    ValidateJsonbCompatible(operand, operatorName);
                    return;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(comparisonOperator),
                        comparisonOperator,
                        "Unknown comparison operator.");
            }
        }

        private static void ValidateMembershipArray(
            JsonElement operand,
            string operatorName,
            bool scalarOnly)
        {
            if (operand.ValueKind != JsonValueKind.Array)
            {
                throw Invalid($"{operatorName} accepts an array operand only.");
            }

            var count = 0;
            foreach (var item in operand.EnumerateArray())
            {
                count++;
                if (count > MaxMembershipValues)
                {
                    throw Invalid(
                        $"{operatorName} cannot contain more than {MaxMembershipValues} values.");
                }

                if (scalarOnly && item.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                {
                    throw Invalid($"{operatorName} accepts scalar values only.");
                }

                ValidateJsonbCompatible(item, operatorName);
            }
        }

        private static void ValidateJsonbCompatible(
            JsonElement value,
            string operatorName)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    ValidatePostgreSqlText(
                        value.GetString()!,
                        $"{operatorName} string operands");
                    return;

                case JsonValueKind.Number:
                    if (!IsPostgreSqlNumericCompatible(value.GetRawText()))
                    {
                        throw Invalid(
                            $"{operatorName} contains a number outside PostgreSQL jsonb numeric limits.");
                    }
                    return;

                case JsonValueKind.Array:
                    foreach (var item in value.EnumerateArray())
                    {
                        ValidateJsonbCompatible(item, operatorName);
                    }
                    return;

                case JsonValueKind.Object:
                    foreach (var property in value.EnumerateObject())
                    {
                        ValidatePostgreSqlText(
                            property.Name,
                            $"{operatorName} object keys");
                        ValidateJsonbCompatible(property.Value, operatorName);
                    }
                    return;

                case JsonValueKind.Null:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return;

                default:
                    throw Invalid($"{operatorName} has an invalid operand.");
            }
        }

        private static bool IsPostgreSqlNumericCompatible(string raw)
        {
            var number = raw.AsSpan();
            if (number.Length > 0 && number[0] == '-')
            {
                number = number[1..];
            }

            var exponentIndex = number.IndexOfAny('e', 'E');
            var mantissa = exponentIndex >= 0
                ? number[..exponentIndex]
                : number;
            long exponent = 0;
            if (exponentIndex >= 0
                && !long.TryParse(number[(exponentIndex + 1)..], out exponent))
            {
                return false;
            }

            var decimalIndex = mantissa.IndexOf('.');
            var integerDigits = decimalIndex >= 0
                ? decimalIndex
                : mantissa.Length;
            var totalDigits = mantissa.Length - (decimalIndex >= 0 ? 1 : 0);

            // Keep the arithmetic bounded even when an exponent itself is a
            // syntactically valid Int64 at an extreme value. Mantissa zeros can
            // shift the effective decimal position, so retain enough headroom
            // for every digit present in the bounded request body.
            if (exponent > PostgreSqlNumericMaxIntegerDigits + (long)totalDigits
                || exponent < -PostgreSqlNumericMaxFractionalDigits - (long)totalDigits)
            {
                return false;
            }

            var decimalPosition = integerDigits + exponent;
            var leadingZeroDigits = 0;
            foreach (var character in mantissa)
            {
                if (character == '.')
                {
                    continue;
                }
                if (character != '0')
                {
                    break;
                }

                leadingZeroDigits++;
            }

            var integerCount = Math.Max(decimalPosition - leadingZeroDigits, 0);
            var fractionalCount = Math.Max(totalDigits - decimalPosition, 0);
            return integerCount <= PostgreSqlNumericMaxIntegerDigits
                   && fractionalCount <= PostgreSqlNumericMaxFractionalDigits;
        }

        private void AddComparison()
        {
            _comparisonCount++;
            if (_comparisonCount > MaxComparisonPredicates)
            {
                throw Invalid(
                    $"A variable filter cannot contain more than {MaxComparisonPredicates} comparison predicates.");
            }
        }

        private static IReadOnlyList<JsonProperty> ReadUniqueProperties(JsonElement element)
        {
            var properties = element.EnumerateObject().ToArray();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in properties)
            {
                if (!names.Add(property.Name))
                {
                    throw Invalid($"Duplicate member '{property.Name}' is not allowed.");
                }
            }

            return properties;
        }

        private static JsonProperty? FindProperty(
            IReadOnlyList<JsonProperty> properties,
            string name)
        {
            foreach (var property in properties)
            {
                if (property.NameEquals(name))
                {
                    return property;
                }
            }

            return null;
        }

        private static bool IsLogicalOperator(string name) =>
            name is "$and" or "$or" or "$not";

        private static bool IsComparisonOperator(string name) =>
            name is "$eq"
                or "$eqIgnoreCase"
                or "$ne"
                or "$in"
                or "$nin"
                or "$gt"
                or "$gte"
                or "$lt"
                or "$lte"
                or "$exists"
                or "$contains"
                or "$containsAny"
                or "$containsAll"
                or "$elemMatch";

        private static VariableFilterComparisonOperator ParseComparisonOperator(
            string name) => name switch
            {
                "$eq" => VariableFilterComparisonOperator.Equal,
                "$eqIgnoreCase" => VariableFilterComparisonOperator.EqualIgnoreCase,
                "$ne" => VariableFilterComparisonOperator.NotEqual,
                "$in" => VariableFilterComparisonOperator.In,
                "$nin" => VariableFilterComparisonOperator.NotIn,
                "$gt" => VariableFilterComparisonOperator.GreaterThan,
                "$gte" => VariableFilterComparisonOperator.GreaterThanOrEqual,
                "$lt" => VariableFilterComparisonOperator.LessThan,
                "$lte" => VariableFilterComparisonOperator.LessThanOrEqual,
                "$exists" => VariableFilterComparisonOperator.Exists,
                "$contains" => VariableFilterComparisonOperator.Contains,
                "$containsAny" => VariableFilterComparisonOperator.ContainsAny,
                "$containsAll" => VariableFilterComparisonOperator.ContainsAll,
                "$elemMatch" => throw new InvalidOperationException(
                    "$elemMatch is represented by VariableFilterElementMatchExpression."),
                _ => throw Invalid($"Unknown operator '{name}'.")
            };
    }
}
