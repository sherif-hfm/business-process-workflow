using System.Text;
using System.Text.Json;
using Flowbit.Service.Models;

namespace Flowbit.Infrastructure.Repositories;

/// <summary>
/// Compiles the validated variable-filter AST into parameterized PostgreSQL.
/// Only static SQL templates are emitted; variable names, JSON paths, and
/// comparison operands are always parameters.
/// </summary>
internal static class VariableFilterSqlCompiler
{
    public static void Append(
        StringBuilder where,
        List<(string Name, object Value)> arguments,
        VariableFilterExpression? expression,
        string instanceIdSql)
    {
        if (expression is null)
        {
            return;
        }

        var compiler = new Compiler(arguments, instanceIdSql);
        var sql = compiler.Compile(expression, elementSql: null);

        // Comparisons deliberately produce SQL NULL for a missing variable or
        // path. PostgreSQL's three-valued logic then keeps missing values from
        // becoming matches through $ne, $nin, or $not. $exists:false is the
        // explicit exception and produces a normal Boolean result.
        where.Append(" AND (")
            .Append(sql)
            .Append(") IS TRUE");
    }

    private sealed class Compiler(
        List<(string Name, object Value)> arguments,
        string instanceIdSql)
    {
        private int _parameterIndex;
        private int _aliasIndex;

        public string Compile(
            VariableFilterExpression expression,
            string? elementSql) => expression switch
            {
                VariableFilterAllExpression all => CompileLogical(
                    all.Terms,
                    " AND ",
                    elementSql),
                VariableFilterAnyExpression any => CompileLogical(
                    any.Terms,
                    " OR ",
                    elementSql),
                VariableFilterNotExpression not =>
                    $"NOT ({Compile(not.Operand, elementSql)})",
                VariableFilterComparisonExpression comparison =>
                    CompileComparison(comparison, elementSql),
                VariableFilterElementMatchExpression elementMatch =>
                    CompileElementMatch(elementMatch, elementSql),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(expression),
                    expression,
                    "Unsupported variable-filter expression.")
            };

        private string CompileLogical(
            IReadOnlyList<VariableFilterExpression> terms,
            string separator,
            string? elementSql)
        {
            if (terms.Count == 0)
            {
                throw new InvalidOperationException(
                    "A logical variable-filter expression cannot be empty.");
            }

            return "(" + string.Join(
                separator,
                terms.Select(term => $"({Compile(term, elementSql)})")) + ")";
        }

        private string CompileComparison(
            VariableFilterComparisonExpression comparison,
            string? elementSql)
        {
            if (comparison.Field.Scope == VariableFilterFieldScope.Element)
            {
                if (elementSql is null)
                {
                    throw new InvalidOperationException(
                        "An element-relative comparison must be inside $elemMatch.");
                }

                var target = BuildTargetSql(elementSql, comparison.Field.Path);
                if (comparison.Operator == VariableFilterComparisonOperator.Exists)
                {
                    return comparison.Operand.GetBoolean()
                        ? $"{target} IS NOT NULL"
                        : $"{target} IS NULL";
                }

                return $"CASE WHEN {target} IS NULL THEN NULL " +
                       $"ELSE {CompileValuePredicate(target, comparison.Operator, comparison.Operand)} END";
            }

            EnsureInstanceField(comparison.Field);
            var variableAlias = NextAlias("current_variable");
            var variableNameParameter = AddParameter(comparison.Field.VariableName!);
            var rootTarget = BuildTargetSql(
                $"{variableAlias}.\"ValueJson\"",
                comparison.Field.Path);
            var rowPredicate =
                $"{variableAlias}.\"InstanceId\" = {instanceIdSql} " +
                $"AND {variableAlias}.\"VariableName\" = @{variableNameParameter}";

            if (comparison.Operator == VariableFilterComparisonOperator.Exists)
            {
                var existsSql =
                    $"EXISTS (SELECT 1 FROM flowbit.instance_variable_current_values {variableAlias} " +
                    $"WHERE {rowPredicate} AND {rootTarget} IS NOT NULL)";
                return comparison.Operand.GetBoolean()
                    ? existsSql
                    : $"NOT ({existsSql})";
            }

            var pathExistsSql =
                $"EXISTS (SELECT 1 FROM flowbit.instance_variable_current_values {variableAlias} " +
                $"WHERE {rowPredicate} AND {rootTarget} IS NOT NULL)";
            var valuePredicate = CompileValuePredicate(
                rootTarget,
                comparison.Operator,
                comparison.Operand);
            var matchesSql =
                $"EXISTS (SELECT 1 FROM flowbit.instance_variable_current_values {variableAlias} " +
                $"WHERE {rowPredicate} AND {rootTarget} IS NOT NULL AND ({valuePredicate}))";

            // Keep the value predicate inside the correlated EXISTS so PostgreSQL
            // can use the projection's JSON/string/numeric indexes. The outer
            // CASE still returns UNKNOWN when the variable or nested path is
            // missing, which is required for safe $not semantics.
            return $"CASE WHEN {pathExistsSql} THEN {matchesSql} ELSE NULL END";
        }

        private string CompileElementMatch(
            VariableFilterElementMatchExpression elementMatch,
            string? elementSql)
        {
            if (elementMatch.Field.Scope == VariableFilterFieldScope.Element)
            {
                if (elementSql is null)
                {
                    throw new InvalidOperationException(
                        "An element-relative $elemMatch must be nested inside $elemMatch.");
                }

                var target = BuildTargetSql(elementSql, elementMatch.Field.Path);
                return CompileElementMatchValue(target, elementMatch.Predicate);
            }

            EnsureInstanceField(elementMatch.Field);
            var variableAlias = NextAlias("current_variable");
            var variableNameParameter = AddParameter(elementMatch.Field.VariableName!);
            var rootTarget = BuildTargetSql(
                $"{variableAlias}.\"ValueJson\"",
                elementMatch.Field.Path);
            var rowPredicate =
                $"{variableAlias}.\"InstanceId\" = {instanceIdSql} " +
                $"AND {variableAlias}.\"VariableName\" = @{variableNameParameter}";

            return
                $"(SELECT {CompileElementMatchValue(rootTarget, elementMatch.Predicate)} " +
                $"FROM flowbit.instance_variable_current_values {variableAlias} " +
                $"WHERE {rowPredicate})";
        }

        private string CompileElementMatchValue(
            string target,
            VariableFilterExpression predicate)
        {
            var elementAlias = NextAlias("array_element");
            var valueSql = $"{elementAlias}.\"Value\"";
            var predicateSql = Compile(predicate, valueSql);
            return
                $"CASE WHEN {target} IS NULL THEN NULL " +
                $"WHEN jsonb_typeof({target}) <> 'array' THEN FALSE " +
                $"ELSE EXISTS (SELECT 1 FROM jsonb_array_elements({target}) " +
                $"AS {elementAlias}(\"Value\") WHERE ({predicateSql}) IS TRUE) END";
        }

        private string CompileValuePredicate(
            string target,
            VariableFilterComparisonOperator comparisonOperator,
            JsonElement operand) => comparisonOperator switch
            {
                VariableFilterComparisonOperator.Equal =>
                    $"{target} = CAST(@{AddJsonParameter(operand)} AS jsonb)",
                VariableFilterComparisonOperator.EqualIgnoreCase =>
                    $"jsonb_typeof({target}) = 'string' AND " +
                    $"lower({RootText(target)}) = lower(@{AddParameter(operand.GetString()!)})",
                VariableFilterComparisonOperator.LegacyEqualIgnoreCase =>
                    $"jsonb_typeof({target}) NOT IN ('array', 'object') AND " +
                    $"lower({RootText(target)}) = lower(@{AddParameter(operand.GetString()!)})",
                VariableFilterComparisonOperator.NotEqual =>
                    $"{target} <> CAST(@{AddJsonParameter(operand)} AS jsonb)",
                VariableFilterComparisonOperator.In =>
                    CompileMembership(target, operand, negate: false),
                VariableFilterComparisonOperator.NotIn =>
                    CompileMembership(target, operand, negate: true),
                VariableFilterComparisonOperator.GreaterThan =>
                    CompileNumericRange(target, operand, ">"),
                VariableFilterComparisonOperator.GreaterThanOrEqual =>
                    CompileNumericRange(target, operand, ">="),
                VariableFilterComparisonOperator.LessThan =>
                    CompileNumericRange(target, operand, "<"),
                VariableFilterComparisonOperator.LessThanOrEqual =>
                    CompileNumericRange(target, operand, "<="),
                VariableFilterComparisonOperator.Contains =>
                    CompileContains(target, operand),
                VariableFilterComparisonOperator.ContainsAny =>
                    CompileContainmentGroup(target, operand, requireAll: false),
                VariableFilterComparisonOperator.ContainsAll =>
                    CompileContainmentGroup(target, operand, requireAll: true),
                VariableFilterComparisonOperator.Exists =>
                    throw new InvalidOperationException(
                        "$exists is compiled with path-presence semantics."),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(comparisonOperator),
                    comparisonOperator,
                    "Unsupported variable-filter comparison operator.")
            };

        private string CompileMembership(
            string target,
            JsonElement operand,
            bool negate)
        {
            var comparisons = operand
                .EnumerateArray()
                .Select(item =>
                    $"{target} {(negate ? "<>" : "=")} " +
                    $"CAST(@{AddJsonParameter(item)} AS jsonb)")
                .ToArray();

            if (comparisons.Length == 0)
            {
                return negate
                    ? $"jsonb_typeof({target}) NOT IN ('array', 'object')"
                    : "FALSE";
            }

            return $"(jsonb_typeof({target}) NOT IN ('array', 'object') AND (" +
                   string.Join(negate ? " AND " : " OR ", comparisons) + "))";
        }

        private string CompileContainmentGroup(
            string target,
            JsonElement operand,
            bool requireAll)
        {
            var comparisons = operand
                .EnumerateArray()
                .Select(item => CompileContainedElement(target, item))
                .ToArray();

            if (comparisons.Length == 0)
            {
                return requireAll
                    ? $"jsonb_typeof({target}) IN ('array', 'object')"
                    : "FALSE";
            }

            return $"(jsonb_typeof({target}) IN ('array', 'object') AND (" +
                   string.Join(requireAll ? " AND " : " OR ", comparisons) + "))";
        }

        private string CompileContains(string target, JsonElement operand)
        {
            var operandSql = $"CAST(@{AddJsonParameter(operand)} AS jsonb)";
            return
                $"((jsonb_typeof({target}) = 'array' AND " +
                $"{target} @> CASE WHEN jsonb_typeof({operandSql}) = 'array' " +
                $"THEN {operandSql} ELSE jsonb_build_array({operandSql}) END) " +
                $"OR (jsonb_typeof({target}) = 'object' AND {target} @> {operandSql}))";
        }

        private string CompileContainedElement(string target, JsonElement operand)
        {
            var operandSql = $"CAST(@{AddJsonParameter(operand)} AS jsonb)";
            return
                $"((jsonb_typeof({target}) = 'array' AND " +
                $"{target} @> jsonb_build_array({operandSql})) " +
                $"OR (jsonb_typeof({target}) = 'object' AND {target} @> {operandSql}))";
        }

        private string CompileNumericRange(
            string target,
            JsonElement operand,
            string comparisonOperator)
        {
            var operandParameter = AddParameter(operand.GetRawText());
            return
                $"jsonb_typeof({target}) = 'number' AND " +
                $"(CASE WHEN jsonb_typeof({target}) = 'number' THEN " +
                $"CAST({RootText(target)} AS numeric) ELSE NULL END) " +
                $"{comparisonOperator} CAST(@{operandParameter} AS numeric)";
        }

        private string BuildTargetSql(
            string rootSql,
            IReadOnlyList<string> path)
        {
            if (path.Count == 0)
            {
                return rootSql;
            }

            var pathParameter = AddParameter(path.ToArray());
            return $"({rootSql} #> @{pathParameter})";
        }

        private static string RootText(string jsonSql) =>
            $"({jsonSql} #>> ARRAY[]::text[])";

        private string AddJsonParameter(JsonElement value) =>
            AddParameter(value.GetRawText());

        private string AddParameter(object value)
        {
            var name = $"advancedVariableFilter{_parameterIndex++}";
            arguments.Add((name, value));
            return name;
        }

        private string NextAlias(string prefix) =>
            $"{prefix}_{_aliasIndex++}";

        private static void EnsureInstanceField(VariableFilterFieldReference field)
        {
            if (field.Scope != VariableFilterFieldScope.InstanceVariable
                || string.IsNullOrEmpty(field.VariableName))
            {
                throw new InvalidOperationException(
                    "A root variable-filter field must identify an instance variable.");
            }
        }
    }
}
