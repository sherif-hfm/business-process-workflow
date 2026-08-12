using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

/// <summary>
/// Compiles the deliberately small user-task inbox visibility language into a
/// canonical, typed postfix program. The program is data, never executable SQL,
/// and is interpreted by the matching bounded PostgreSQL evaluator.
/// </summary>
public static class InboxVisibilityConditionCompiler
{
    public const int CurrentProgramVersion = 1;
    public const int MaxUtf8Bytes = 4 * 1024;
    public const int MaxExpressionDepth = 8;
    public const int MaxInstructions = 64;
    public const int MaxComparisons = 16;
    public const int MaxVariableReferences = 8;
    public const int MaxExternalReferences = 16;
    public const int MaxLiterals = 16;
    public const int MaxStringLiteralUtf8Bytes = 512;
    public const int MaxNumericLiteralCharacters = 128;
    public const int MaxNumericExponentMagnitude = 100;

    /// <summary>
    /// Compiles one optional condition. Missing or blank text means that the
    /// task has no additional visibility restriction.
    /// </summary>
    public static InboxVisibilityConditionCompilation? Compile(
        string? condition,
        WorkflowModel definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(condition))
        {
            return null;
        }

        ValidateSource(condition);

        var symbols = VariableSymbolTable.Create(definition);
        return new Compiler(condition, symbols).Compile();
    }

    /// <summary>
    /// Compiles every nonblank user-task condition, keyed by flow-node ID.
    /// </summary>
    public static IReadOnlyDictionary<int, InboxVisibilityConditionCompilation> CompileAll(
        WorkflowModel definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var authored = definition.FlowNodes
            .Where(node => !string.IsNullOrWhiteSpace(node.InboxVisibilityCondition))
            .OrderBy(node => node.Id)
            .ToArray();
        if (authored.Length == 0)
        {
            return new Dictionary<int, InboxVisibilityConditionCompilation>();
        }

        var invalidOwner = authored.FirstOrDefault(node => !BpmnFlowNodeTypes.IsUserTask(node.Type));
        if (invalidOwner is not null)
        {
            throw new WorkflowDomainException(
                $"Flow node #{invalidOwner.Id} defines inboxVisibilityCondition but is not a user task.");
        }

        var symbols = VariableSymbolTable.Create(definition);
        var result = new Dictionary<int, InboxVisibilityConditionCompilation>();
        foreach (var node in authored)
        {
            try
            {
                ValidateSource(node.InboxVisibilityCondition!);
                result.Add(
                    node.Id,
                    new Compiler(node.InboxVisibilityCondition!, symbols).Compile());
            }
            catch (WorkflowDomainException exception)
            {
                throw new WorkflowDomainException(
                    $"User task #{node.Id} has an invalid inboxVisibilityCondition: {exception.Message}");
            }
        }

        return result;
    }

    /// <summary>
    /// Validates bounds that belong to the authored source itself before model
    /// normalization can trim it. Blank text still means no additional rule.
    /// </summary>
    internal static void ValidateAuthoredSource(string? condition)
    {
        if (!string.IsNullOrWhiteSpace(condition))
        {
            ValidateSource(condition);
        }
    }

    private static void ValidateSource(string condition)
    {
        ValidateUnicode(condition, "The expression");
        if (Encoding.UTF8.GetByteCount(condition) > MaxUtf8Bytes)
        {
            throw Invalid($"The expression cannot exceed {MaxUtf8Bytes} UTF-8 bytes.");
        }
    }

    private static WorkflowDomainException Invalid(string message) =>
        new($"Invalid inbox visibility condition: {message}");

    private static void ValidateUnicode(string value, string label)
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

    private enum ValueType
    {
        String,
        Number,
        Boolean,
        Date,
        DateTime,
        Dynamic
    }

    private static string TypeName(ValueType type) => type switch
    {
        ValueType.String => WorkflowVariableTypes.String,
        ValueType.Number => WorkflowVariableTypes.Number,
        ValueType.Boolean => WorkflowVariableTypes.Boolean,
        ValueType.Date => WorkflowVariableTypes.Date,
        ValueType.DateTime => WorkflowVariableTypes.DateTime,
        ValueType.Dynamic => "dynamic",
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private abstract record Node;

    private sealed record LiteralNode(ValueType Type, object Value) : Node;

    private sealed record ReferenceNode(
        string Name,
        ValueType Type,
        bool IsExternal) : Node;

    private sealed record UnaryNode(TokenKind Operator, Node Operand) : Node;

    private sealed record BinaryNode(TokenKind Operator, Node Left, Node Right) : Node;

    private sealed record NumberNode(Node Operand) : Node;

    private enum TokenKind
    {
        End,
        Reference,
        String,
        Number,
        Identifier,
        True,
        False,
        LeftParenthesis,
        RightParenthesis,
        Comma,
        Plus,
        Minus,
        Star,
        Slash,
        Percent,
        Equal,
        NotEqual,
        Greater,
        GreaterOrEqual,
        Less,
        LessOrEqual,
        And,
        Or,
        Not
    }

    private readonly record struct Token(TokenKind Kind, string Text, int Position);

    private sealed class Tokenizer(string source)
    {
        private int _position;

        public Token Next()
        {
            SkipWhitespace();
            if (_position == source.Length)
            {
                return new Token(TokenKind.End, string.Empty, _position);
            }

            var start = _position;
            var value = source[_position++];
            switch (value)
            {
                case '[':
                    return ReadReference(start);
                case '\'':
                case '"':
                    return ReadString(start, value);
                case '(':
                    return new Token(TokenKind.LeftParenthesis, "(", start);
                case ')':
                    return new Token(TokenKind.RightParenthesis, ")", start);
                case ',':
                    return new Token(TokenKind.Comma, ",", start);
                case '+':
                    return new Token(TokenKind.Plus, "+", start);
                case '-':
                    return new Token(TokenKind.Minus, "-", start);
                case '*':
                    return new Token(TokenKind.Star, "*", start);
                case '/':
                    return new Token(TokenKind.Slash, "/", start);
                case '%':
                    return new Token(TokenKind.Percent, "%", start);
                case '=' when Match('='):
                    return new Token(TokenKind.Equal, "==", start);
                case '!':
                    return Match('=')
                        ? new Token(TokenKind.NotEqual, "!=", start)
                        : new Token(TokenKind.Not, "!", start);
                case '>':
                    return Match('=')
                        ? new Token(TokenKind.GreaterOrEqual, ">=", start)
                        : new Token(TokenKind.Greater, ">", start);
                case '<':
                    return Match('=')
                        ? new Token(TokenKind.LessOrEqual, "<=", start)
                        : new Token(TokenKind.Less, "<", start);
                case '&' when Match('&'):
                    return new Token(TokenKind.And, "&&", start);
                case '|' when Match('|'):
                    return new Token(TokenKind.Or, "||", start);
            }

            if (IsAsciiDigit(value)
                || (value == '.' && _position < source.Length && IsAsciiDigit(source[_position])))
            {
                _position--;
                return ReadNumber(start);
            }

            if (char.IsLetter(value) || value == '_')
            {
                while (_position < source.Length
                       && (char.IsLetterOrDigit(source[_position]) || source[_position] == '_'))
                {
                    _position++;
                }

                var text = source[start.._position];
                return text.ToLowerInvariant() switch
                {
                    "and" => new Token(TokenKind.And, text, start),
                    "or" => new Token(TokenKind.Or, text, start),
                    "not" => new Token(TokenKind.Not, text, start),
                    "true" => new Token(TokenKind.True, text, start),
                    "false" => new Token(TokenKind.False, text, start),
                    _ => new Token(TokenKind.Identifier, text, start)
                };
            }

            throw Invalid($"Unexpected character '{value}' at position {start + 1}.");
        }

        private Token ReadReference(int start)
        {
            var builder = new StringBuilder();
            while (_position < source.Length)
            {
                var value = source[_position++];
                if (value == ']')
                {
                    if (builder.Length == 0 || string.IsNullOrWhiteSpace(builder.ToString()))
                    {
                        throw Invalid($"A reference at position {start + 1} cannot be blank.");
                    }

                    var name = builder.ToString();
                    ValidateUnicode(name, "A reference");
                    return new Token(TokenKind.Reference, name, start);
                }

                if (value == '\\')
                {
                    if (_position >= source.Length || source[_position] is not (']' or '\\'))
                    {
                        throw Invalid(
                            $"A reference at position {start + 1} contains an unsupported escape; only \\] and \\\\ are allowed.");
                    }

                    value = source[_position++];
                }

                if (value is '\r' or '\n')
                {
                    throw Invalid(
                        $"A reference at position {start + 1} cannot contain a line break.");
                }

                builder.Append(value);
            }

            throw Invalid($"Reference beginning at position {start + 1} is missing ']'.");
        }

        private Token ReadString(int start, char quote)
        {
            var builder = new StringBuilder();
            while (_position < source.Length)
            {
                var value = source[_position++];
                if (value == quote)
                {
                    if (_position < source.Length && source[_position] == quote)
                    {
                        _position++;
                        builder.Append(quote);
                        continue;
                    }

                    var text = builder.ToString();
                    ValidateUnicode(text, "A string literal");
                    if (Encoding.UTF8.GetByteCount(text) > MaxStringLiteralUtf8Bytes)
                    {
                        throw Invalid(
                            $"A string literal cannot exceed {MaxStringLiteralUtf8Bytes} UTF-8 bytes.");
                    }

                    return new Token(TokenKind.String, text, start);
                }

                if (value == '\\')
                {
                    if (_position >= source.Length)
                    {
                        throw Invalid($"String literal beginning at position {start + 1} has an incomplete escape.");
                    }

                    var escaped = source[_position++];
                    value = escaped switch
                    {
                        '\\' => '\\',
                        '/' => '/',
                        '\'' => '\'',
                        '"' => '"',
                        'b' => '\b',
                        'f' => '\f',
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        _ => throw Invalid(
                            $"String literal beginning at position {start + 1} contains an unsupported escape.")
                    };
                }

                else if (value is '\r' or '\n')
                {
                    throw Invalid(
                        $"String literal beginning at position {start + 1} cannot contain a line break.");
                }

                builder.Append(value);
            }

            throw Invalid($"String literal beginning at position {start + 1} is not terminated.");
        }

        private Token ReadNumber(int start)
        {
            var hasIntegerDigits = false;
            while (_position < source.Length && IsAsciiDigit(source[_position]))
            {
                hasIntegerDigits = true;
                _position++;
            }

            var hasFractionDigits = false;
            if (_position < source.Length && source[_position] == '.')
            {
                _position++;
                while (_position < source.Length && IsAsciiDigit(source[_position]))
                {
                    hasFractionDigits = true;
                    _position++;
                }
            }

            if (!hasIntegerDigits && !hasFractionDigits)
            {
                throw Invalid($"Invalid numeric literal at position {start + 1}.");
            }

            if (_position < source.Length && source[_position] is 'e' or 'E')
            {
                _position++;
                if (_position < source.Length && source[_position] is '+' or '-')
                {
                    _position++;
                }

                var exponentStart = _position;
                while (_position < source.Length && IsAsciiDigit(source[_position]))
                {
                    _position++;
                }

                if (_position == exponentStart)
                {
                    throw Invalid($"Numeric literal at position {start + 1} has an invalid exponent.");
                }
            }

            var text = source[start.._position];
            if (text.Length > MaxNumericLiteralCharacters)
            {
                throw Invalid(
                    $"A numeric literal cannot exceed {MaxNumericLiteralCharacters} characters.");
            }

            return new Token(TokenKind.Number, CanonicalizeNumber(text), start);
        }

        private static string CanonicalizeNumber(string sourceNumber)
        {
            var exponentIndex = sourceNumber.IndexOfAny(['e', 'E']);
            var mantissa = exponentIndex < 0 ? sourceNumber : sourceNumber[..exponentIndex];
            var exponentText = exponentIndex < 0 ? null : sourceNumber[(exponentIndex + 1)..];
            if (exponentText is not null
                && !int.TryParse(
                    exponentText,
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out _))
            {
                throw Invalid("A numeric literal exponent exceeds the supported bound.");
            }

            var exponent = exponentText is null
                ? 0
                : int.Parse(exponentText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            if (Math.Abs((long)exponent) > MaxNumericExponentMagnitude)
            {
                throw Invalid(
                    $"A numeric literal exponent must be between -{MaxNumericExponentMagnitude} and {MaxNumericExponentMagnitude}.");
            }

            var decimalIndex = mantissa.IndexOf('.');
            var fractionalDigits = decimalIndex < 0 ? 0 : mantissa.Length - decimalIndex - 1;
            var digits = mantissa.Replace(".", string.Empty, StringComparison.Ordinal).TrimStart('0');
            if (digits.Length == 0)
            {
                return "0";
            }

            var scale = fractionalDigits - exponent;
            while (scale > 0 && digits.EndsWith('0'))
            {
                digits = digits[..^1];
                scale--;
            }

            string canonical;
            if (scale <= 0)
            {
                canonical = digits + new string('0', -scale);
            }
            else if (scale >= digits.Length)
            {
                canonical = "0." + new string('0', scale - digits.Length) + digits;
            }
            else
            {
                canonical = digits.Insert(digits.Length - scale, ".");
            }

            if (canonical.Length > MaxNumericLiteralCharacters)
            {
                throw Invalid("A numeric literal expands beyond the supported precision bound.");
            }

            return canonical;
        }

        private bool Match(char expected)
        {
            if (_position >= source.Length || source[_position] != expected)
            {
                return false;
            }

            _position++;
            return true;
        }

        private static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';

        private void SkipWhitespace()
        {
            while (_position < source.Length && char.IsWhiteSpace(source[_position]))
            {
                _position++;
            }
        }
    }

    private sealed class Parser
    {
        private readonly Tokenizer _tokenizer;
        private Token _current;
        private int _nodeCount;
        private int _groupDepth;
        private int _unaryDepth;

        public Parser(string source)
        {
            _tokenizer = new Tokenizer(source);
            _current = _tokenizer.Next();
        }

        public Node Parse()
        {
            var result = ParseExpression(0);
            if (_current.Kind != TokenKind.End)
            {
                throw Invalid(
                    $"Unexpected token '{_current.Text}' at position {_current.Position + 1}.");
            }

            return result;
        }

        private Node ParseExpression(int minimumPrecedence)
        {
            var left = ParseUnary();
            while (TryGetPrecedence(_current.Kind, out var precedence)
                   && precedence >= minimumPrecedence)
            {
                var operation = _current.Kind;
                Advance();
                var right = ParseExpression(precedence + 1);
                left = AddNode(new BinaryNode(operation, left, right));
            }

            return left;
        }

        private Node ParseUnary()
        {
            if (_current.Kind is TokenKind.Not or TokenKind.Plus or TokenKind.Minus)
            {
                var operation = _current.Kind;
                Advance();
                EnterUnary();
                try
                {
                    return AddNode(new UnaryNode(operation, ParseUnary()));
                }
                finally
                {
                    _unaryDepth--;
                }
            }

            return ParsePrimary();
        }

        private Node ParsePrimary()
        {
            var token = _current;
            switch (token.Kind)
            {
                case TokenKind.Reference:
                    Advance();
                    return AddNode(new UnresolvedReferenceNode(token.Text));
                case TokenKind.String:
                    Advance();
                    return AddNode(new LiteralNode(ValueType.String, token.Text));
                case TokenKind.Number:
                    Advance();
                    return AddNode(new LiteralNode(ValueType.Number, token.Text));
                case TokenKind.True:
                case TokenKind.False:
                    Advance();
                    return AddNode(new LiteralNode(ValueType.Boolean, token.Kind == TokenKind.True));
                case TokenKind.LeftParenthesis:
                    Advance();
                    EnterGroup();
                    try
                    {
                        var nested = ParseExpression(0);
                        Expect(TokenKind.RightParenthesis, "Expected ')' to close the expression.");
                        return nested;
                    }
                    finally
                    {
                        _groupDepth--;
                    }
                case TokenKind.Identifier:
                    Advance();
                    if (!string.Equals(token.Text, "Number", StringComparison.OrdinalIgnoreCase))
                    {
                        throw Invalid($"Unknown function or identifier '{token.Text}'.");
                    }

                    Expect(TokenKind.LeftParenthesis, "Number must be followed by '('.");
                    EnterGroup();
                    try
                    {
                        var argument = ParseExpression(0);
                        if (_current.Kind == TokenKind.Comma)
                        {
                            throw Invalid("Number accepts exactly one argument.");
                        }
                        Expect(TokenKind.RightParenthesis, "Expected ')' after the Number argument.");
                        return AddNode(new NumberNode(argument));
                    }
                    finally
                    {
                        _groupDepth--;
                    }
                default:
                    throw Invalid(
                        $"Expected a literal, reference, Number call, or parenthesized expression at position {token.Position + 1}.");
            }
        }

        private T AddNode<T>(T node) where T : Node
        {
            _nodeCount++;
            if (_nodeCount > MaxInstructions)
            {
                throw Invalid($"A compiled expression cannot exceed {MaxInstructions} instructions.");
            }

            return node;
        }

        private void EnterGroup()
        {
            _groupDepth++;
            if (_groupDepth > MaxExpressionDepth)
            {
                throw Invalid($"Expression depth cannot exceed {MaxExpressionDepth}.");
            }
        }

        private void EnterUnary()
        {
            _unaryDepth++;
            if (_unaryDepth > MaxExpressionDepth)
            {
                throw Invalid($"Expression depth cannot exceed {MaxExpressionDepth}.");
            }
        }

        private void Expect(TokenKind kind, string message)
        {
            if (_current.Kind != kind)
            {
                throw Invalid(message);
            }

            Advance();
        }

        private void Advance() => _current = _tokenizer.Next();

        private static bool TryGetPrecedence(TokenKind kind, out int precedence)
        {
            precedence = kind switch
            {
                TokenKind.Or => 1,
                TokenKind.And => 2,
                TokenKind.Equal or TokenKind.NotEqual
                    or TokenKind.Greater or TokenKind.GreaterOrEqual
                    or TokenKind.Less or TokenKind.LessOrEqual => 3,
                TokenKind.Plus or TokenKind.Minus => 4,
                TokenKind.Star or TokenKind.Slash or TokenKind.Percent => 5,
                _ => 0
            };
            return precedence != 0;
        }
    }

    private sealed record UnresolvedReferenceNode(string Name) : Node;

    private sealed class Compiler(string source, VariableSymbolTable symbols)
    {
        private readonly List<LiteralNode> _literals = [];
        private readonly Dictionary<string, ReferenceNode> _variables =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ReferenceNode> _externals =
            new(StringComparer.OrdinalIgnoreCase);
        private int _comparisons;

        public InboxVisibilityConditionCompilation Compile()
        {
            var parsed = new Parser(source).Parse();
            var resolved = Resolve(parsed);
            var resultType = ValidateAndGetType(resolved);
            if (resultType is not (ValueType.Boolean or ValueType.Dynamic))
            {
                throw Invalid("The root expression must produce a boolean value; truthiness is not supported.");
            }

            var depth = GetDepth(resolved);
            if (depth > MaxExpressionDepth)
            {
                throw Invalid($"Expression depth cannot exceed {MaxExpressionDepth}.");
            }
            if (_comparisons > MaxComparisons)
            {
                throw Invalid($"An expression cannot contain more than {MaxComparisons} comparisons.");
            }
            if (_literals.Count > MaxLiterals)
            {
                throw Invalid($"An expression cannot contain more than {MaxLiterals} literals.");
            }
            if (_variables.Count > MaxVariableReferences)
            {
                throw Invalid(
                    $"An expression cannot reference more than {MaxVariableReferences} distinct instance variables.");
            }
            if (_externals.Count > MaxExternalReferences)
            {
                throw Invalid(
                    $"An expression cannot reference more than {MaxExternalReferences} distinct external values.");
            }

            var variables = _variables.Values
                .OrderBy(reference => reference.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(reference => reference.Name, StringComparer.Ordinal)
                .ToArray();
            var externals = _externals.Values
                .OrderBy(reference => reference.Name, StringComparer.Ordinal)
                .ToArray();
            var variableIndexes = variables
                .Select((reference, index) => (reference.Name, index))
                .ToDictionary(pair => pair.Name, pair => pair.index, StringComparer.OrdinalIgnoreCase);
            var externalIndexes = externals
                .Select((reference, index) => (reference.Name, index))
                .ToDictionary(pair => pair.Name, pair => pair.index, StringComparer.OrdinalIgnoreCase);

            var instructions = new List<Instruction>();
            Emit(resolved, variableIndexes, externalIndexes, instructions);
            if (instructions.Count > MaxInstructions)
            {
                throw Invalid($"A compiled expression cannot exceed {MaxInstructions} instructions.");
            }

            var bytes = WriteCanonicalProgram(variables, externals, instructions);
            using var document = JsonDocument.Parse(bytes);
            var program = document.RootElement.Clone();
            var fingerprint = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            return new InboxVisibilityConditionCompilation(
                CurrentProgramVersion,
                program,
                variables.Select(reference => reference.Name).ToArray(),
                externals.Select(reference => reference.Name).ToArray(),
                fingerprint);
        }

        private Node Resolve(Node node) => node switch
        {
            UnresolvedReferenceNode unresolved => ResolveReference(unresolved.Name),
            UnaryNode unary => unary with { Operand = Resolve(unary.Operand) },
            BinaryNode binary => binary with
            {
                Left = Resolve(binary.Left),
                Right = Resolve(binary.Right)
            },
            NumberNode number => number with { Operand = Resolve(number.Operand) },
            LiteralNode literal => RegisterLiteral(literal),
            _ => throw new InvalidOperationException("Unknown inbox visibility AST node.")
        };

        private LiteralNode RegisterLiteral(LiteralNode literal)
        {
            _literals.Add(literal);
            return literal;
        }

        private ReferenceNode ResolveReference(string authoredName)
        {
            if (TryResolveExternal(authoredName, out var external))
            {
                _externals.TryAdd(external.Name, external);
                return external;
            }

            var variable = symbols.Resolve(authoredName);
            _variables.TryAdd(variable.Name, variable);
            return variable;
        }

        private static bool TryResolveExternal(string name, out ReferenceNode reference)
        {
            var canonical = name.ToLowerInvariant();
            var type = canonical switch
            {
                "sys.user" or "sys.actingfor" or "sys.workflowname" or "sys.nodename" =>
                    ValueType.String,
                "sys.now" => ValueType.DateTime,
                "sys.today" => ValueType.Date,
                "sys.instanceid" or "sys.workflowid" or "sys.nodeid" => ValueType.Number,
                _ when canonical.StartsWith("sys.claim.", StringComparison.Ordinal)
                    && canonical.Length > "sys.claim.".Length => ValueType.String,
                _ when canonical.StartsWith("config.", StringComparison.Ordinal)
                    && canonical.Length > "config.".Length => ValueType.String,
                _ when canonical.StartsWith("setting.", StringComparison.Ordinal)
                    && canonical.Length > "setting.".Length => ValueType.Dynamic,
                _ => (ValueType)(-1)
            };

            if ((int)type >= 0)
            {
                reference = new ReferenceNode(canonical, type, IsExternal: true);
                return true;
            }

            if (canonical.StartsWith("sys.", StringComparison.Ordinal)
                || canonical.StartsWith("config.", StringComparison.Ordinal)
                || canonical.StartsWith("setting.", StringComparison.Ordinal)
                || canonical.StartsWith("mi.", StringComparison.Ordinal))
            {
                throw Invalid($"Reference '[{name}]' is not a supported scalar context value.");
            }

            reference = null!;
            return false;
        }

        private ValueType ValidateAndGetType(Node node) => node switch
        {
            LiteralNode literal => literal.Type,
            ReferenceNode reference => reference.Type,
            NumberNode number => ValidateNumber(number),
            UnaryNode unary => ValidateUnary(unary),
            BinaryNode binary => ValidateBinary(binary),
            _ => throw new InvalidOperationException("Inbox visibility references must be resolved before validation.")
        };

        private ValueType ValidateNumber(NumberNode number)
        {
            var operand = ValidateAndGetType(number.Operand);
            if (operand is not (ValueType.Number or ValueType.String or ValueType.Dynamic))
            {
                throw Invalid($"Number cannot convert a value of type {TypeName(operand)}.");
            }

            return ValueType.Number;
        }

        private ValueType ValidateUnary(UnaryNode unary)
        {
            var operand = ValidateAndGetType(unary.Operand);
            if (unary.Operator == TokenKind.Not)
            {
                RequireBoolean(operand, "not");
                return ValueType.Boolean;
            }

            RequireNumeric(operand, unary.Operator == TokenKind.Plus ? "unary +" : "unary -");
            return ValueType.Number;
        }

        private ValueType ValidateBinary(BinaryNode binary)
        {
            var left = ValidateAndGetType(binary.Left);
            var right = ValidateAndGetType(binary.Right);
            switch (binary.Operator)
            {
                case TokenKind.And:
                case TokenKind.Or:
                    RequireBoolean(left, binary.Operator == TokenKind.And ? "and" : "or");
                    RequireBoolean(right, binary.Operator == TokenKind.And ? "and" : "or");
                    return ValueType.Boolean;
                case TokenKind.Plus:
                case TokenKind.Minus:
                case TokenKind.Star:
                case TokenKind.Slash:
                case TokenKind.Percent:
                    RequireNumeric(left, OperatorText(binary.Operator));
                    RequireNumeric(right, OperatorText(binary.Operator));
                    return ValueType.Number;
                case TokenKind.Equal:
                case TokenKind.NotEqual:
                    _comparisons++;
                    _ = ResolveComparisonType(left, right, ordering: false);
                    return ValueType.Boolean;
                case TokenKind.Greater:
                case TokenKind.GreaterOrEqual:
                case TokenKind.Less:
                case TokenKind.LessOrEqual:
                    _comparisons++;
                    _ = ResolveComparisonType(left, right, ordering: true);
                    return ValueType.Boolean;
                default:
                    throw new InvalidOperationException("Unknown inbox visibility binary operator.");
            }
        }

        private static void RequireBoolean(ValueType type, string operation)
        {
            if (type is not (ValueType.Boolean or ValueType.Dynamic))
            {
                throw Invalid($"Operator {operation} requires boolean operands, not {TypeName(type)}.");
            }
        }

        private static void RequireNumeric(ValueType type, string operation)
        {
            if (type is not (ValueType.Number or ValueType.Dynamic))
            {
                throw Invalid(
                    $"Operator {operation} requires numeric operands; use Number(...) for numeric strings.");
            }
        }

        private static ValueType ResolveComparisonType(
            ValueType left,
            ValueType right,
            bool ordering)
        {
            if (left == right)
            {
                if (ordering && left is not (ValueType.Number or ValueType.Date or ValueType.DateTime))
                {
                    throw Invalid(
                        $"Ordering is supported only for number, date, and datetime values, not {TypeName(left)}.");
                }

                if (ordering && left == ValueType.Dynamic)
                {
                    throw Invalid(
                        "Ordering two dynamic setting values requires Number(...) or a typed variable/date operand.");
                }

                return left;
            }

            if (left == ValueType.Dynamic || right == ValueType.Dynamic)
            {
                var known = left == ValueType.Dynamic ? right : left;
                if (ordering && known is not (ValueType.Number or ValueType.Date or ValueType.DateTime))
                {
                    throw Invalid(
                        "Ordering a dynamic value requires a number, date, or datetime operand to establish its type.");
                }

                return known;
            }

            if ((left is ValueType.Date or ValueType.DateTime) && right == ValueType.String)
            {
                return left;
            }
            if ((right is ValueType.Date or ValueType.DateTime) && left == ValueType.String)
            {
                return right;
            }

            throw Invalid(
                $"Cannot compare incompatible {TypeName(left)} and {TypeName(right)} values without an explicit conversion.");
        }

        private static int GetDepth(Node node) => node switch
        {
            LiteralNode or ReferenceNode => 1,
            NumberNode number => 1 + GetDepth(number.Operand),
            UnaryNode unary => 1 + GetDepth(unary.Operand),
            BinaryNode binary => 1 + Math.Max(GetDepth(binary.Left), GetDepth(binary.Right)),
            _ => throw new InvalidOperationException("Unknown inbox visibility AST node.")
        };

        private static void Emit(
            Node node,
            IReadOnlyDictionary<string, int> variableIndexes,
            IReadOnlyDictionary<string, int> externalIndexes,
            ICollection<Instruction> instructions)
        {
            switch (node)
            {
                case LiteralNode literal:
                    instructions.Add(new Instruction("literal", TypeName(literal.Type), literal.Value));
                    return;
                case ReferenceNode reference when reference.IsExternal:
                    instructions.Add(new Instruction("external", Index: externalIndexes[reference.Name]));
                    return;
                case ReferenceNode reference:
                    instructions.Add(new Instruction("variable", Index: variableIndexes[reference.Name]));
                    return;
                case NumberNode number:
                    Emit(number.Operand, variableIndexes, externalIndexes, instructions);
                    instructions.Add(new Instruction("number"));
                    return;
                case UnaryNode unary:
                    Emit(unary.Operand, variableIndexes, externalIndexes, instructions);
                    instructions.Add(new Instruction(unary.Operator switch
                    {
                        TokenKind.Not => "not",
                        TokenKind.Plus => "positive",
                        TokenKind.Minus => "negate",
                        _ => throw new InvalidOperationException("Unknown inbox visibility unary operator.")
                    }));
                    return;
                case BinaryNode binary:
                    Emit(binary.Left, variableIndexes, externalIndexes, instructions);
                    Emit(binary.Right, variableIndexes, externalIndexes, instructions);
                    var comparisonType = IsComparison(binary.Operator)
                        ? ResolveComparisonType(GetStaticType(binary.Left), GetStaticType(binary.Right), IsOrdering(binary.Operator))
                        : (ValueType?)null;
                    instructions.Add(new Instruction(
                        Opcode(binary.Operator),
                        comparisonType is null ? null : TypeName(comparisonType.Value)));
                    return;
                default:
                    throw new InvalidOperationException("Unknown inbox visibility AST node.");
            }
        }

        private static ValueType GetStaticType(Node node) => node switch
        {
            LiteralNode literal => literal.Type,
            ReferenceNode reference => reference.Type,
            NumberNode => ValueType.Number,
            UnaryNode unary when unary.Operator == TokenKind.Not => ValueType.Boolean,
            UnaryNode => ValueType.Number,
            BinaryNode binary when IsComparison(binary.Operator) => ValueType.Boolean,
            BinaryNode binary when binary.Operator is TokenKind.And or TokenKind.Or => ValueType.Boolean,
            BinaryNode => ValueType.Number,
            _ => throw new InvalidOperationException("Unknown inbox visibility AST node.")
        };

        private static bool IsComparison(TokenKind kind) => kind is
            TokenKind.Equal or TokenKind.NotEqual
            or TokenKind.Greater or TokenKind.GreaterOrEqual
            or TokenKind.Less or TokenKind.LessOrEqual;

        private static bool IsOrdering(TokenKind kind) => kind is
            TokenKind.Greater or TokenKind.GreaterOrEqual
            or TokenKind.Less or TokenKind.LessOrEqual;

        private static string Opcode(TokenKind kind) => kind switch
        {
            TokenKind.And => "and",
            TokenKind.Or => "or",
            TokenKind.Plus => "add",
            TokenKind.Minus => "subtract",
            TokenKind.Star => "multiply",
            TokenKind.Slash => "divide",
            TokenKind.Percent => "modulo",
            TokenKind.Equal => "equal",
            TokenKind.NotEqual => "notEqual",
            TokenKind.Greater => "greater",
            TokenKind.GreaterOrEqual => "greaterOrEqual",
            TokenKind.Less => "less",
            TokenKind.LessOrEqual => "lessOrEqual",
            _ => throw new InvalidOperationException("Unknown inbox visibility binary operator.")
        };

        private static string OperatorText(TokenKind kind) => kind switch
        {
            TokenKind.Plus => "+",
            TokenKind.Minus => "-",
            TokenKind.Star => "*",
            TokenKind.Slash => "/",
            TokenKind.Percent => "%",
            _ => kind.ToString()
        };
    }

    private sealed record Instruction(
        string Op,
        string? Type = null,
        object? Value = null,
        int? Index = null);

    private static byte[] WriteCanonicalProgram(
        IReadOnlyList<ReferenceNode> variables,
        IReadOnlyList<ReferenceNode> externals,
        IReadOnlyList<Instruction> instructions)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
               {
                   Encoder = JavaScriptEncoder.Default,
                   Indented = false
               }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", CurrentProgramVersion);
            writer.WriteStartArray("variables");
            foreach (var variable in variables)
            {
                writer.WriteStartObject();
                writer.WriteString("name", variable.Name);
                writer.WriteString("type", TypeName(variable.Type));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("externalReferences");
            foreach (var external in externals)
            {
                writer.WriteStartObject();
                writer.WriteString("name", external.Name);
                writer.WriteString("type", TypeName(external.Type));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("instructions");
            foreach (var instruction in instructions)
            {
                writer.WriteStartObject();
                writer.WriteString("op", instruction.Op);
                if (instruction.Index is int index)
                {
                    writer.WriteNumber("index", index);
                }
                if (instruction.Type is not null)
                {
                    writer.WriteString("type", instruction.Type);
                }
                if (instruction.Value is not null)
                {
                    writer.WritePropertyName("value");
                    if (instruction.Value is bool boolean)
                    {
                        writer.WriteBooleanValue(boolean);
                    }
                    else
                    {
                        writer.WriteStringValue((string)instruction.Value);
                    }
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private sealed class VariableSymbolTable(
        IReadOnlyDictionary<string, IReadOnlyList<VariableProducer>> producers)
    {
        public static VariableSymbolTable Create(WorkflowModel definition)
        {
            var collected = new List<VariableProducer>();

            AddVariables(definition.Variables, "process variables");
            foreach (var node in definition.FlowNodes)
            {
                AddVariables(node.Variables, $"flow node #{node.Id}");

                if (node.Service is not null)
                {
                    foreach (var mapping in node.Service.OutputMappings ?? [])
                    {
                        if (mapping is not null)
                        {
                            Add(mapping.Variable, mapping.DataType, mapping.IsArray, $"service task #{node.Id} output");
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(node.Service.StatusVariable))
                    {
                        Add(
                            node.Service.StatusVariable,
                            WorkflowVariableTypes.Number,
                            false,
                            $"service task #{node.Id} statusVariable");
                    }
                }

                if (node.Message is not null)
                {
                    foreach (var mapping in node.Message.OutputMappings ?? [])
                    {
                        if (mapping is not null)
                        {
                            Add(mapping.Variable, mapping.DataType, mapping.IsArray, $"message node #{node.Id} output");
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(node.ErrorVariable))
                {
                    Add(
                        node.ErrorVariable,
                        WorkflowVariableTypes.String,
                        false,
                        $"error boundary #{node.Id} errorVariable");
                }

                // Entry-event idempotency values are persisted as scalar string
                // instance variables, but definition validation deliberately
                // forbids also declaring them in the normal variable collections.
                if (!string.IsNullOrWhiteSpace(node.Idempotency?.Variable))
                {
                    Add(
                        node.Idempotency.Variable,
                        WorkflowVariableTypes.String,
                        false,
                        $"entry event #{node.Id} idempotency variable");
                }
            }

            foreach (var flow in definition.SequenceFlows)
            {
                AddVariables(flow.Variables ?? [], $"sequence flow #{flow.Id}");
            }

            return new VariableSymbolTable(
                collected
                    .GroupBy(producer => producer.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => (IReadOnlyList<VariableProducer>)group.ToArray(),
                        StringComparer.OrdinalIgnoreCase));

            void AddVariables(IEnumerable<VariableModel>? variables, string owner)
            {
                foreach (var variable in variables ?? [])
                {
                    if (variable is not null)
                    {
                        Add(variable.Name, variable.DataType, variable.IsArray, owner);
                    }
                }
            }

            void Add(string? name, string? type, bool? isArray, string owner)
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    collected.Add(new VariableProducer(name, type, isArray, owner));
                }
            }
        }

        public ReferenceNode Resolve(string authoredName)
        {
            if (!producers.TryGetValue(authoredName, out var matches))
            {
                throw Invalid($"Reference '[{authoredName}]' is not a declared instance variable.");
            }

            var spellings = matches
                .Select(match => match.Name)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (spellings.Length != 1)
            {
                throw Invalid(
                    $"Variable '[{authoredName}]' has producers with inconsistent canonical spelling: {string.Join(", ", spellings.Select(value => $"'{value}'"))}.");
            }

            var contracts = matches
                .Select(match => (match.Type, match.IsArray))
                .Distinct()
                .ToArray();
            if (contracts.Length != 1 || contracts[0].Type is null || contracts[0].IsArray is null)
            {
                throw Invalid(
                    $"Variable '[{authoredName}]' has producers with inconsistent or unknown types.");
            }

            var (dataType, isArray) = contracts[0];
            if (isArray.GetValueOrDefault()
                || string.Equals(dataType, WorkflowVariableTypes.Json, StringComparison.Ordinal))
            {
                throw Invalid(
                    $"Variable '[{authoredName}]' must be a scalar string, number, boolean, date, or datetime value.");
            }

            var type = dataType switch
            {
                WorkflowVariableTypes.String => ValueType.String,
                WorkflowVariableTypes.Number => ValueType.Number,
                WorkflowVariableTypes.Boolean => ValueType.Boolean,
                WorkflowVariableTypes.Date => ValueType.Date,
                WorkflowVariableTypes.DateTime => ValueType.DateTime,
                _ => throw Invalid(
                    $"Variable '[{authoredName}]' has unsupported type '{dataType}'.")
            };
            return new ReferenceNode(spellings[0], type, IsExternal: false);
        }
    }

    private sealed record VariableProducer(
        string Name,
        string? Type,
        bool? IsArray,
        string Owner);
}
