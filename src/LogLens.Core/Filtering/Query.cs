using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LogLens.Core.Models;
using LogLens.Core.Formats;

namespace LogLens.Core.Filtering;

/// <summary>A problem in a query: where it is (<see cref="Position"/>, <see cref="Length"/>) and what is wrong.</summary>
public readonly record struct QueryError(int Position, int Length, string Message);

/// <summary>A successfully compiled query.</summary>
/// <param name="text">The query as typed.</param>
/// <param name="predicate">Tests an event; thread-safe.</param>
/// <param name="expressions">The regular expressions it runs (<c>like</c>, <c>matches</c>), so <see cref="TooSlow"/> can tell when one gave up.</param>
public sealed class CompiledQuery(string text, Func<LogEvent, bool> predicate, IReadOnlyList<UserRegex>? expressions = null)
{
    private readonly IReadOnlyList<UserRegex> _expressions = expressions ?? [];

    public string Text { get; } = text;

    public Func<LogEvent, bool> Predicate { get; } = predicate;

    /// <summary>One of its regular expressions timed out too often and no longer matches anything (NFR-10): the results are not to be trusted.</summary>
    public bool TooSlow => _expressions.Any(e => e.TooSlow);
}

/// <summary>
/// The query bar language (FR-S4). Fields: <c>@l</c> level, <c>@t</c> timestamp, <c>@m</c> message, <c>@x</c> exception, <c>@mt</c>
/// message template, and properties by name (<c>RequestId</c>, a dotted path into a structure such as <c>Http.Status</c>, or
/// <c>@p['Any name']</c>). Operators: <c>= != &lt; &lt;= &gt; &gt;=</c>, <c>in [...]</c>, <c>like</c> (<c>%</c> and <c>_</c> wildcards),
/// <c>contains</c>, <c>startswith</c>, <c>endswith</c>, <c>matches</c> (regex), <c>exists(field)</c>, combined with <c>and</c>, <c>or</c>,
/// <c>not</c> and parentheses. Values: text in quotes, numbers, <c>true</c>, <c>false</c>, <c>null</c>. Text comparisons ignore case.
/// </summary>
/// <example><c>@l in ['Error','Fatal'] and @m like '%payment%' and @t &gt;= '2026-11-13 08:00' and Elapsed &gt; 500</c></example>
public static class QueryParser
{
    public const string FieldsHelp = "@l (level), @t (timestamp), @m (message), @x (exception), @mt (message template) or a property name";

    public static bool TryParse(string? text, out CompiledQuery? query, out QueryError error)
        => TryParse(text, TimestampZone.AsLogged, out query, out error);

    /// <summary>Compiles <paramref name="text"/>; a timestamp without an offset compares with the wall clock in <paramref name="zone"/> (FR-G5).</summary>
    public static bool TryParse(string? text, TimestampZone zone, out CompiledQuery? query, out QueryError error)
    {
        query = null;
        error = default;
        text ??= string.Empty;

        try
        {
            var tokens = Lexer.Tokenize(text);
            var parser = new Parser(tokens, zone);
            var predicate = parser.ParseAll();
            query = new CompiledQuery(text, predicate, parser.Expressions);
            return true;
        }
        catch (QueryException ex)
        {
            error = new QueryError(ex.Position, Math.Max(1, ex.Length), ex.Message);
            return false;
        }
    }

    private sealed class QueryException(int position, int length, string message) : Exception(message)
    {
        public int Position { get; } = position;

        public int Length { get; } = length;
    }

    // ------------------------------------------------------------------ lexer

    private enum T { Word, Field, String, Number, Op, LParen, RParen, LBracket, RBracket, Comma, End }

    private readonly record struct Token(T Kind, string Text, int Pos, int Length, string? Str = null, double Num = 0);

    private static class Lexer
    {
        public static List<Token> Tokenize(string s)
        {
            var tokens = new List<Token>();
            var i = 0;
            while (i < s.Length)
            {
                var c = s[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                }
                else if (c == '(')
                {
                    tokens.Add(new Token(T.LParen, "(", i++, 1));
                }
                else if (c == ')')
                {
                    tokens.Add(new Token(T.RParen, ")", i++, 1));
                }
                else if (c == '[')
                {
                    tokens.Add(new Token(T.LBracket, "[", i++, 1));
                }
                else if (c == ']')
                {
                    tokens.Add(new Token(T.RBracket, "]", i++, 1));
                }
                else if (c == ',')
                {
                    tokens.Add(new Token(T.Comma, ",", i++, 1));
                }
                else if (c is '\'' or '"')
                {
                    tokens.Add(ReadString(s, ref i));
                }
                else if (c == '@' || char.IsLetter(c) || c == '_')
                {
                    var start = i++;
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '.'))
                    {
                        i++;
                    }

                    var word = s[start..i];
                    tokens.Add(new Token(c == '@' ? T.Field : T.Word, word, start, i - start));
                }
                else if (char.IsDigit(c) || (c == '-' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
                {
                    var start = i++;
                    while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.'))
                    {
                        i++;
                    }

                    var raw = s[start..i];
                    if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    {
                        throw new QueryException(start, raw.Length, $"'{raw}' isn't a valid number.");
                    }

                    tokens.Add(new Token(T.Number, raw, start, raw.Length, null, number));
                }
                else if (c is '=' or '!' or '<' or '>')
                {
                    var start = i++;
                    if (i < s.Length && (s[i] == '=' || (c == '<' && s[i] == '>')))
                    {
                        i++;
                    }

                    var op = s[start..i];
                    if (op == "!")
                    {
                        throw new QueryException(start, 1, "Unexpected '!'. Use != for \"not equal\" or the word 'not'.");
                    }

                    tokens.Add(new Token(T.Op, op == "==" ? "=" : op == "<>" ? "!=" : op, start, i - start));
                }
                else
                {
                    throw new QueryException(i, 1, $"Unexpected character '{c}'.");
                }
            }

            tokens.Add(new Token(T.End, string.Empty, s.Length, 0));
            return tokens;
        }

        private static Token ReadString(string s, ref int i)
        {
            var quote = s[i];
            var start = i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                if (s[i] == quote)
                {
                    if (i + 1 < s.Length && s[i + 1] == quote)
                    {
                        sb.Append(quote); // doubled quote = literal quote
                        i += 2;
                        continue;
                    }

                    i++;
                    return new Token(T.String, s[start..i], start, i - start, sb.ToString());
                }

                sb.Append(s[i++]);
            }

            throw new QueryException(start, s.Length - start, $"This text is missing its closing {quote}.");
        }
    }

    // ------------------------------------------------------------------ parser / compiler

    private enum FieldKind { Level, Time, Message, Exception, Template, Property }

    /// <summary>What a term compares: a built-in field, or a property (by name or dotted path).</summary>
    private sealed record FieldRef(FieldKind Kind, Token Token, string? Name = null)
    {
        public bool IsText => Kind is FieldKind.Message or FieldKind.Exception or FieldKind.Template;

        public string Display => Kind == FieldKind.Property ? Name! : Token.Text;
    }

    private abstract record Literal(Token Token);

    private sealed record StringLiteral(Token Token, string Value) : Literal(Token);

    private sealed record NumberLiteral(Token Token, double Value) : Literal(Token);

    private sealed record BoolLiteral(Token Token, bool Value) : Literal(Token);

    private sealed record NullLiteral(Token Token) : Literal(Token);

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "or", "not", "in", "like", "contains", "startswith", "endswith", "matches", "exists", "null", "true", "false",
    };

    private sealed class Parser(List<Token> tokens, TimestampZone zone)
    {
        private readonly List<UserRegex> _expressions = [];
        private int _p;

        /// <summary>The regular expressions compiled so far.</summary>
        public IReadOnlyList<UserRegex> Expressions => _expressions;

        private Token Peek => tokens[_p];

        private Token Next() => tokens[_p++];

        public Func<LogEvent, bool> ParseAll()
        {
            if (Peek.Kind == T.End)
            {
                throw new QueryException(0, 1, "The query is empty.");
            }

            var result = ParseOr();
            if (Peek.Kind != T.End)
            {
                throw Error(Peek, $"Unexpected '{Peek.Text}'. Did you forget 'and' / 'or'?");
            }

            return result;
        }

        private Func<LogEvent, bool> ParseOr()
        {
            var left = ParseAnd();
            while (IsWord("or"))
            {
                Next();
                var l = left;
                var r = ParseAnd();
                left = e => l(e) || r(e);
            }

            return left;
        }

        private Func<LogEvent, bool> ParseAnd()
        {
            var left = ParseNot();
            while (IsWord("and"))
            {
                Next();
                var l = left;
                var r = ParseNot();
                left = e => l(e) && r(e);
            }

            return left;
        }

        private Func<LogEvent, bool> ParseNot()
        {
            if (IsWord("not"))
            {
                Next();
                var inner = ParseNot();
                return e => !inner(e);
            }

            return ParseTerm();
        }

        private Func<LogEvent, bool> ParseTerm()
        {
            var t = Peek;
            if (t.Kind == T.LParen)
            {
                Next();
                var inner = ParseOr();
                Expect(T.RParen, "Missing ')'.");
                return inner;
            }

            if (IsWord("exists"))
            {
                Next();
                Expect(T.LParen, "Write exists(@x) or exists(PropertyName).");
                var field = ParseField();
                Expect(T.RParen, "Missing ')' after the field.");
                return Exists(field);
            }

            if (t.Kind != T.Field && !(t.Kind == T.Word && !Keywords.Contains(t.Text)))
            {
                throw Error(t, "Expected a field (@l, @t, @m, @x, @mt or a property name), 'exists(...)', 'not' or '('.");
            }

            var f = ParseField();
            var negate = false;
            if (IsWord("not") && IsOperatorWord(tokens[_p + 1]))
            {
                Next();
                negate = true;
            }

            var op = Peek;
            Func<LogEvent, bool> result;
            if (op.Kind == T.Op)
            {
                Next();
                var literal = ParseLiteral();
                result = Compare(f, op, literal);
            }
            else if (IsWord("in"))
            {
                Next();
                result = In(f, ParseList());
            }
            else if (IsOperatorWord(op))
            {
                Next();
                var literal = ParseLiteral();
                result = TextOperator(f, op, literal);
            }
            else
            {
                throw Error(op.Kind == T.End ? f.Token : op, $"After {f.Display} expected a comparison such as =, >=, in, like, contains, startswith, endswith or matches.");
            }

            if (negate)
            {
                var inner = result;
                return e => !inner(e);
            }

            return result;
        }

        private FieldRef ParseField()
        {
            var t = Next();
            if (t.Kind == T.Word && !Keywords.Contains(t.Text))
            {
                return new FieldRef(FieldKind.Property, t, t.Text);
            }

            if (t.Kind != T.Field)
            {
                throw Error(t, $"Expected a field: {FieldsHelp}.");
            }

            switch (t.Text.ToLowerInvariant())
            {
                case "@l" or "@level":
                    return new FieldRef(FieldKind.Level, t);
                case "@t" or "@timestamp":
                    return new FieldRef(FieldKind.Time, t);
                case "@m" or "@message":
                    return new FieldRef(FieldKind.Message, t);
                case "@x" or "@exception":
                    return new FieldRef(FieldKind.Exception, t);
                case "@mt" or "@messagetemplate":
                    return new FieldRef(FieldKind.Template, t);
                case "@p" or "@properties":
                    // @p['Any name'] — a property whose name is not a plain word.
                    Expect(T.LBracket, "Write @p['Property name'].");
                    var name = Next();
                    if (name.Kind != T.String || name.Str!.Length == 0)
                    {
                        throw Error(name, "Write the property name in quotes, e.g. @p['Request Id'].");
                    }

                    Expect(T.RBracket, "Missing ']' after the property name.");
                    return new FieldRef(FieldKind.Property, name, name.Str);
                default:
                    throw Error(t, $"Unknown field '{t.Text}'. Available: {FieldsHelp}.");
            }
        }

        private Literal ParseLiteral()
        {
            var t = Next();
            return t.Kind switch
            {
                T.String => new StringLiteral(t, t.Str!),
                T.Number => new NumberLiteral(t, t.Num),
                T.Word when t.Text.Equals("null", StringComparison.OrdinalIgnoreCase) => new NullLiteral(t),
                T.Word when t.Text.Equals("true", StringComparison.OrdinalIgnoreCase) => new BoolLiteral(t, true),
                T.Word when t.Text.Equals("false", StringComparison.OrdinalIgnoreCase) => new BoolLiteral(t, false),
                T.Word => throw Error(t, $"'{t.Text}' isn't a value. Put text in quotes, e.g. '{t.Text}'."),
                _ => throw Error(t, "Expected a value: text in quotes, a number, true, false or null."),
            };
        }

        private List<Literal> ParseList()
        {
            Expect(T.LBracket, "After 'in' write a list like ['Error', 'Fatal'].");
            var items = new List<Literal>();
            if (Peek.Kind == T.RBracket)
            {
                throw Error(Peek, "The list is empty.");
            }

            while (true)
            {
                items.Add(ParseLiteral());
                if (Peek.Kind == T.Comma)
                {
                    Next();
                    continue;
                }

                Expect(T.RBracket, "Missing ']'.");
                return items;
            }
        }

        // ---- semantics ----

        private static Func<LogEvent, string?> TextOf(FieldRef field)
        {
            switch (field.Kind)
            {
                case FieldKind.Message:
                    return e => e.Message;
                case FieldKind.Exception:
                    return e => e.Exception;
                case FieldKind.Template:
                    return e => e.MessageTemplate;
                default:
                    var get = PropertyOf(field.Name!);
                    return e => get(e) is { IsNull: false } v ? v.ToString() : null;
            }
        }

        /// <summary>
        /// Reads a property: by its full name first, then — for a dotted name such as <c>Http.Status</c> — by walking into structures, so both a
        /// property literally called "Http.Status" and the Status field of an Http structure are found.
        /// </summary>
        private static Func<LogEvent, LogValue?> PropertyOf(string name)
        {
            var path = name.Contains('.') ? name.Split('.') : null;
            return e =>
            {
                if (e.Properties is not { } properties)
                {
                    return null;
                }

                if (properties.TryGetValue(name, out var value))
                {
                    return value;
                }

                if (path is null || !properties.TryGetValue(path[0], out value))
                {
                    return null;
                }

                for (var i = 1; i < path.Length; i++)
                {
                    if (value.AsStructure is not { } structure || !structure.TryGetValue(path[i], out value))
                    {
                        return null;
                    }
                }

                return value;
            };
        }

        private static Func<LogEvent, bool> Exists(FieldRef field)
        {
            switch (field.Kind)
            {
                case FieldKind.Exception:
                    return e => e.Exception is not null;
                case FieldKind.Template:
                    return e => e.MessageTemplate is not null;
                case FieldKind.Time:
                    return e => !e.IsUnparsed;
                case FieldKind.Level:
                    return e => LevelMask.HasSeverity(e.Level);
                case FieldKind.Property:
                    var get = PropertyOf(field.Name!);
                    return e => get(e) is { IsNull: false };
                default:
                    return _ => true;
            }
        }

        private Func<LogEvent, bool> Compare(FieldRef field, Token op, Literal literal)
        {
            var kind = op.Text;
            switch (field.Kind)
            {
                case FieldKind.Level:
                    var level = LevelOf(literal);
                    // Unparsed lines and unrecognised levels have no severity, so they never match a level comparison.
                    return Ordered(kind, e => LevelMask.HasSeverity(e.Level) ? (long)e.Level : null, (long)level, op);

                case FieldKind.Time:
                    return CompareTime(kind, literal, op);

                case FieldKind.Property:
                    return CompareProperty(field, kind, literal, op);

                default:
                    return CompareText(field, kind, literal, op);
            }
        }

        private Func<LogEvent, bool> CompareTime(string kind, Literal literal, Token op)
        {
            if (literal is not StringLiteral s)
            {
                throw Error(literal.Token, "Write the timestamp in quotes, e.g. '2026-11-13 08:00:05'.");
            }

            if (!TimeLiteral.TryParse(s.Value, null, out var value))
            {
                throw Error(s.Token, $"'{s.Value}' isn't a timestamp. Use e.g. '2026-11-13', '2026-11-13 08:00' or '2026-11-13 08:00:05.301'.");
            }

            if (!value.HasDate)
            {
                throw Error(s.Token, "Include the date, e.g. '2026-11-13 08:00:05'.");
            }

            if (value.Instant is { } instant)
            {
                return Ordered(kind, e => e.IsUnparsed ? null : e.Timestamp.UtcTicks, instant.UtcTicks, op);
            }

            return Ordered(kind, e => e.IsUnparsed ? null : TimeRange.WallClock(e.Timestamp, zone).Ticks, value.Wall.Ticks, op);
        }

        private Func<LogEvent, bool> CompareText(FieldRef field, string kind, Literal literal, Token op)
        {
            var get = TextOf(field);

            if (literal is NullLiteral)
            {
                return kind switch
                {
                    "=" => e => get(e) is null,
                    "!=" => e => get(e) is not null,
                    _ => throw Error(op, "Only = and != can be used with null."),
                };
            }

            if (literal is not StringLiteral s)
            {
                throw Error(literal.Token, $"Compare text with text in quotes, e.g. {field.Display} = 'started'.");
            }

            return kind switch
            {
                "=" => e => string.Equals(get(e), s.Value, StringComparison.OrdinalIgnoreCase),
                "!=" => e => !string.Equals(get(e), s.Value, StringComparison.OrdinalIgnoreCase),
                _ => throw Error(op, $"'{kind}' can't be used on text. Use =, !=, like, contains, startswith, endswith, matches or in."),
            };
        }

        /// <summary>
        /// A property against a value. Numbers compare as numbers (text that reads as a number counts), text ignores case, true / false match
        /// booleans. An event without the property — or with a null value — matches only <c>= null</c>; every other comparison is false for it.
        /// </summary>
        private Func<LogEvent, bool> CompareProperty(FieldRef field, string kind, Literal literal, Token op)
        {
            var get = PropertyOf(field.Name!);
            switch (literal)
            {
                case NullLiteral:
                    return kind switch
                    {
                        "=" => e => get(e) is null or { IsNull: true },
                        "!=" => e => get(e) is { IsNull: false },
                        _ => throw Error(op, "Only = and != can be used with null."),
                    };

                case BoolLiteral b:
                    if (kind is not ("=" or "!="))
                    {
                        throw Error(op, "Only = and != can be used with true and false.");
                    }

                    var wanted = b.Value;
                    return kind == "="
                        ? e => AsBool(get(e)) == wanted
                        : e => AsBool(get(e)) is { } v && v != wanted;

                case NumberLiteral n:
                    var target = n.Value;
                    return OrderedDouble(kind, e => AsNumber(get(e)), target, op);

                case StringLiteral s:
                    var text = s.Value;
                    var textIsNumber = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var textNumber);
                    return kind switch
                    {
                        "=" => e => ValueEquals(get(e), text),
                        "!=" => e => get(e) is { IsNull: false } v && !ValueEquals(v, text),
                        _ when textIsNumber => OrderedDouble(kind, e => AsNumber(get(e)), textNumber, op),
                        _ => OrderedText(kind, e => get(e) is { IsNull: false, IsScalar: true } v ? v.ToString() : null, text, op),
                    };

                default:
                    throw Error(literal.Token, "Expected a value.");
            }
        }

        private Func<LogEvent, bool> In(FieldRef field, List<Literal> items)
        {
            switch (field.Kind)
            {
                case FieldKind.Level:
                    var mask = 0;
                    foreach (var item in items)
                    {
                        mask |= LevelMask.Bit(LevelOf(item));
                    }

                    return e => LevelMask.Allows(mask, e.Level);

                case FieldKind.Time:
                    throw Error(field.Token, "'in' can't be used with @t. Use >= and <= for a time range.");

                case FieldKind.Property:
                    var alternatives = items.Select(item => Compare(field, new Token(T.Op, "=", item.Token.Pos, 0), item)).ToArray();
                    return e =>
                    {
                        foreach (var alternative in alternatives)
                        {
                            if (alternative(e))
                            {
                                return true;
                            }
                        }

                        return false;
                    };

                default:
                    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in items)
                    {
                        if (item is not StringLiteral s)
                        {
                            throw Error(item.Token, "Use text in quotes inside the list.");
                        }

                        set.Add(s.Value);
                    }

                    var get = TextOf(field);
                    return e => get(e) is { } v && set.Contains(v);
            }
        }

        private Func<LogEvent, bool> TextOperator(FieldRef field, Token op, Literal literal)
        {
            if (field.Kind is FieldKind.Level or FieldKind.Time)
            {
                throw Error(op, $"'{op.Text}' works on text fields (@m, @x, @mt) and properties, not {field.Token.Text}.");
            }

            if (literal is not StringLiteral s)
            {
                throw Error(literal.Token, $"'{op.Text}' needs text in quotes.");
            }

            var get = TextOf(field);
            var value = s.Value;

            switch (op.Text.ToLowerInvariant())
            {
                case "contains":
                    return e => get(e) is { } v && v.Contains(value, StringComparison.OrdinalIgnoreCase);
                case "startswith":
                    return e => get(e) is { } v && v.StartsWith(value, StringComparison.OrdinalIgnoreCase);
                case "endswith":
                    return e => get(e) is { } v && v.EndsWith(value, StringComparison.OrdinalIgnoreCase);
                case "like":
                    var like = BuildRegex(LikeToRegex(value), s.Token, ignoreCase: true);
                    return e => get(e) is { } v && like.IsMatch(v);
                default: // matches
                    var regex = BuildRegex(value, s.Token, ignoreCase: true);
                    return e => get(e) is { } v && regex.IsMatch(v);
            }
        }

        private static bool ValueEquals(LogValue? value, string text)
        {
            if (value is null || value.IsNull)
            {
                return false;
            }

            if (value.IsNumber && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                return AsNumber(value) == number;
            }

            return string.Equals(value.ToString(), text, StringComparison.OrdinalIgnoreCase);
        }

        private static double? AsNumber(LogValue? value)
        {
            if (value is null)
            {
                return null;
            }

            if (value.TryGetNumber(out var number))
            {
                return number;
            }

            return value.Value is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? number : null;
        }

        private static bool? AsBool(LogValue? value) => value?.Value switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var b) => b,
            _ => null,
        };

        private UserRegex BuildRegex(string pattern, Token token, bool ignoreCase)
        {
            try
            {
                var options = RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled;
                if (ignoreCase)
                {
                    options |= RegexOptions.IgnoreCase;
                }

                var regex = UserRegex.Create(pattern, options);
                _expressions.Add(regex);
                return regex;
            }
            catch (ArgumentException ex)
            {
                throw Error(token, "Invalid regular expression: " + ex.Message.Split('\n')[0]);
            }
        }

        private static string LikeToRegex(string like)
        {
            var sb = new StringBuilder("^");
            foreach (var c in like)
            {
                sb.Append(c switch { '%' => ".*", '_' => ".", _ => Regex.Escape(c.ToString()) });
            }

            return sb.Append('$').ToString();
        }

        private LogLevel LevelOf(Literal literal)
        {
            switch (literal)
            {
                case StringLiteral s:
                    var level = LogLevelParser.Parse(s.Value);
                    if (level == LogLevel.Unknown)
                    {
                        throw Error(s.Token, $"'{s.Value}' isn't a level. Use Verbose, Debug, Information, Warning, Error or Fatal.");
                    }

                    return level;
                case NumberLiteral n when n.Value is >= 1 and <= 6 && n.Value == Math.Floor(n.Value):
                    return (LogLevel)(int)n.Value;
                case NumberLiteral n:
                    throw Error(n.Token, "Levels as numbers run from 1 (Verbose) to 6 (Fatal).");
                default:
                    throw Error(literal.Token, "Compare @l with a level name in quotes, e.g. @l >= 'Warning'.");
            }
        }

        /// <summary>Builds an ordered comparison on a numeric key; a null key (e.g. an unparsed line's timestamp) never matches.</summary>
        private Func<LogEvent, bool> Ordered(string kind, Func<LogEvent, long?> key, long target, Token op) => kind switch
        {
            "=" => e => key(e) == target,
            "!=" => e => key(e) is { } k && k != target,
            "<" => e => key(e) is { } k && k < target,
            "<=" => e => key(e) is { } k && k <= target,
            ">" => e => key(e) is { } k && k > target,
            ">=" => e => key(e) is { } k && k >= target,
            _ => throw Error(op, $"Unknown operator '{kind}'."),
        };

        private Func<LogEvent, bool> OrderedDouble(string kind, Func<LogEvent, double?> key, double target, Token op) => kind switch
        {
            "=" => e => key(e) == target,
            "!=" => e => key(e) is { } k && k != target,
            "<" => e => key(e) is { } k && k < target,
            "<=" => e => key(e) is { } k && k <= target,
            ">" => e => key(e) is { } k && k > target,
            ">=" => e => key(e) is { } k && k >= target,
            _ => throw Error(op, $"Unknown operator '{kind}'."),
        };

        private Func<LogEvent, bool> OrderedText(string kind, Func<LogEvent, string?> key, string target, Token op)
        {
            int? Cmp(LogEvent e) => key(e) is { } k ? string.Compare(k, target, StringComparison.OrdinalIgnoreCase) : null;
            return kind switch
            {
                "<" => e => Cmp(e) < 0,
                "<=" => e => Cmp(e) <= 0,
                ">" => e => Cmp(e) > 0,
                ">=" => e => Cmp(e) >= 0,
                _ => throw Error(op, $"Unknown operator '{kind}'."),
            };
        }

        private bool IsWord(string word) => Peek.Kind == T.Word && Peek.Text.Equals(word, StringComparison.OrdinalIgnoreCase);

        private static bool IsOperatorWord(Token t)
            => t.Kind == T.Word && t.Text.ToLowerInvariant() is "in" or "like" or "contains" or "startswith" or "endswith" or "matches";

        private void Expect(T kind, string message)
        {
            if (Peek.Kind != kind)
            {
                throw Error(Peek.Kind == T.End ? tokens[Math.Max(0, _p - 1)] : Peek, message);
            }

            Next();
        }

        private static QueryException Error(Token t, string message) => new(t.Pos, t.Length, message);
    }
}
