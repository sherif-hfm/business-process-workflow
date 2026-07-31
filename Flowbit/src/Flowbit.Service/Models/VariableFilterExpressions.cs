using System.Text.Json;

namespace Flowbit.Service.Models;

/// <summary>
/// A validated, database-translatable predicate over an instance's current
/// variable values. API JSON is converted to this tree before it reaches the
/// persistence boundary.
/// </summary>
public abstract record VariableFilterExpression;

/// <summary>Every term must match.</summary>
public sealed record VariableFilterAllExpression(
    IReadOnlyList<VariableFilterExpression> Terms) : VariableFilterExpression;

/// <summary>At least one term must match.</summary>
public sealed record VariableFilterAnyExpression(
    IReadOnlyList<VariableFilterExpression> Terms) : VariableFilterExpression;

/// <summary>Negates one complete filter expression.</summary>
public sealed record VariableFilterNotExpression(
    VariableFilterExpression Operand) : VariableFilterExpression;

/// <summary>A comparison against an instance variable or an element-relative path.</summary>
public sealed record VariableFilterComparisonExpression(
    VariableFilterFieldReference Field,
    VariableFilterComparisonOperator Operator,
    JsonElement Operand) : VariableFilterExpression;

/// <summary>
/// Matches an array when one element satisfies the complete nested predicate.
/// Nested field references have <see cref="VariableFilterFieldScope.Element"/> scope.
/// </summary>
public sealed record VariableFilterElementMatchExpression(
    VariableFilterFieldReference Field,
    VariableFilterExpression Predicate) : VariableFilterExpression;

public enum VariableFilterFieldScope
{
    InstanceVariable,
    Element
}

/// <summary>
/// Identifies either a top-level Flowbit variable plus an optional JSON path,
/// or a path relative to the current <c>$elemMatch</c> element. VariableName is
/// null only for element-relative references.
/// </summary>
public sealed record VariableFilterFieldReference(
    VariableFilterFieldScope Scope,
    string? VariableName,
    IReadOnlyList<string> Path);

public enum VariableFilterComparisonOperator
{
    Equal,
    EqualIgnoreCase,
    /// <summary>
    /// Compatibility-only text comparison used by legacy GET <c>var=name:value</c>
    /// filters. Unlike <see cref="EqualIgnoreCase"/>, the stored value may be any
    /// JSON scalar and is compared through its text representation.
    /// </summary>
    LegacyEqualIgnoreCase,
    NotEqual,
    In,
    NotIn,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    Exists,
    Contains,
    ContainsAny,
    ContainsAll
}
