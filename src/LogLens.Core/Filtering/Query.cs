using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LogLens.Core.Models;
using LogLens.Core.Formats;

namespace LogLens.Core.Filtering;

/// <summary>A problem in a query: where it is (<see cref="Position"/>, <see cref="Length"/>) and what is wrong.</summary>
public readonly record struct QueryError(int Position, int Length, string Message);

/// <summary>A successfully compiled query.</summary>
public sealed class CompiledQuery(string text, Func<LogEvent, bool> predicate)
{
    public string Text { get; } = text;

    public Func<LogEvent, bool> Predicate { get; } = predicate;
}

/// <summary>
/// The query bar language (FR-S4). Fields: <c>@l</c> level, <c>@t</c> timestamp, <c>@m</c> message, <c>@x</c> exception.
/// Operators: <c>= != &lt; &lt;= &gt; &gt;=</c>, <c>in [...]</c>, <c>like</c> (<c>%</c> and <c>_</c> wildcards), <c>contains</c>,
/// <c>startswith</c>, <c>endswith</c>, <c>matches</c> (regex), <c>exists(field)</c>, combined with <c>and</c>, <c>or</c>, <c>not</c> and parentheses.
/// Text comparisons ignore case. Structured properties arrive with the structured formats (SPEC §8); plain-text logs only have the four fields.
/// </summary>
/// <example><c>@l in ['Error','Fatal'] and @m like '%payment%' and @t &gt;= '2026-11-13 08:00'</c></example>
public static class QueryParser
{
    public const string FieldsHelp = "@l (level), @t (timestamp), @m (message), @x (exception)";

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
            query = new CompiledQuery(text, predicate);
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

    private enum Field { Level, Time, Message, Exception }

    private abstract record Literal(Token Token);

    private sealed record StringLiteral(Token Token, string Value) : Literal(Token);

    private sealed record NumberLiteral(Token Token, double Value) : Literal(Token);

    private sealed record NullLiteral(Token Token) : Literal(Token);

    private sealed class Parser(List<Token> tokens, TimestampZone zone)
    {
        private int _p;

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
                Expect(T.LParen, "Write exists(@x).");
                var field = ParseField();
                Expect(T.RParen, "Missing ')' after the field.");
                return Exists(field);
            }

            if (t.Kind != T.Field)
            {
                if (t.Kind == T.Word)
                {
                    throw Error(t, $"Unknown field '{t.Text}'. Fields start with @: {FieldsHelp}. Structured properties need JSON logs.");
                }

                throw Error(t, "Expected a field (@l, @t, @m or @x), 'exists(...)', 'not' or '('.");
            }

            var fieldToken = t;
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
                result = Compare(f, fieldToken, op, literal);
            }
            else if (IsWord("in"))
            {
                Next();
                result = In(f, fieldToken, ParseList());
            }
            else if (IsOperatorWord(op))
            {
                Next();
                var literal = ParseLiteral();
                result = TextOperator(f, fieldToken, op, literal);
            }
            else
            {
                throw Error(op.Kind == T.End ? fieldToken : op, $"After {fieldToken.Text} expected a comparison such as =, >=, in, like, contains, startswith, endswith or matches.");
            }

            if (negate)
            {
                var inner = result;
                return e => !inner(e);
            }

            return result;
        }

        private Field ParseField()
        {
            var t = Next();
            if (t.Kind != T.Field)
            {
                throw Error(t, $"Expected a field: {FieldsHelp}.");
            }

            return t.Text.ToLowerInvariant() switch
            {
                "@l" or "@level" => Field.Level,
                "@t" or "@timestamp" => Field.Time,
                "@m" or "@message" => Field.Message,
                "@x" or "@exception" => Field.Exception,
                "@mt" => throw Error(t, "@mt (message template) isn't available in plain-text logs; use @m for the rendered message."),
                _ => throw Error(t, $"Unknown field '{t.Text}'. Available: {FieldsHelp}."),
            };
        }

        private Literal ParseLiteral()
        {
            var t = Next();
            return t.Kind switch
            {
                T.String => new StringLiteral(t, t.Str!),
                T.Number => new NumberLiteral(t, t.Num),
                T.Word when t.Text.Equals("null", StringComparison.OrdinalIgnoreCase) => new NullLiteral(t),
                T.Word => throw Error(t, $"'{t.Text}' isn't a value. Put text in quotes, e.g. '{t.Text}'."),
                _ => throw Error(t, "Expected a value: text in quotes, a number, or null."),
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

        private Func<LogEvent, bool> Exists(Field field) => field switch
        {
            Field.Exception => e => e.Exception is not null,
            Field.Time => e => !e.IsUnparsed,
            Field.Level => e => LevelMask.HasSeverity(e.Level),
            _ => _ => true,
        };

        private Func<LogEvent, bool> Compare(Field field, Token fieldToken, Token op, Literal literal)
        {
            var kind = op.Text;
            switch (field)
            {
                case Field.Level:
                    var level = LevelOf(literal);
                    // Unparsed lines and unrecognised levels have no severity, so they never match a level comparison.
                    return Ordered(kind, e => LevelMask.HasSeverity(e.Level) ? (long)e.Level : null, (long)level, op);

                case Field.Time:
                    return CompareTime(kind, literal, op);

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

        private Func<LogEvent, bool> CompareText(Field field, string kind, Literal literal, Token op)
        {
            Func<LogEvent, string?> get = field == Field.Message ? e => e.Message : e => e.Exception;

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
                throw Error(literal.Token, "Compare text with text in quotes, e.g. @m = 'started'.");
            }

            return kind switch
            {
                "=" => e => string.Equals(get(e), s.Value, StringComparison.OrdinalIgnoreCase),
                "!=" => e => !string.Equals(get(e), s.Value, StringComparison.OrdinalIgnoreCase),
                _ => throw Error(op, $"'{kind}' can't be used on text. Use =, !=, like, contains, startswith, endswith, matches or in."),
            };
        }

        private Func<LogEvent, bool> In(Field field, Token fieldToken, List<Literal> items)
        {
            switch (field)
            {
                case Field.Level:
                    var mask = 0;
                    foreach (var item in items)
                    {
                        mask |= LevelMask.Bit(LevelOf(item));
                    }

                    return e => LevelMask.Allows(mask, e.Level);

                case Field.Time:
                    throw Error(fieldToken, "'in' can't be used with @t. Use >= and <= for a time range.");

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

                    Func<LogEvent, string?> get = field == Field.Message ? e => e.Message : e => e.Exception;
                    return e => get(e) is { } v && set.Contains(v);
            }
        }

        private Func<LogEvent, bool> TextOperator(Field field, Token fieldToken, Token op, Literal literal)
        {
            if (field is Field.Level or Field.Time)
            {
                throw Error(op, $"'{op.Text}' works on text fields (@m, @x), not {fieldToken.Text}.");
            }

            if (literal is not StringLiteral s)
            {
                throw Error(literal.Token, $"'{op.Text}' needs text in quotes.");
            }

            Func<LogEvent, string?> get = field == Field.Message ? e => e.Message : e => e.Exception;
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
                    return e => get(e) is { } v && IsMatch(like, v);
                default: // matches
                    var regex = BuildRegex(value, s.Token, ignoreCase: true);
                    return e => get(e) is { } v && IsMatch(regex, v);
            }
        }

        private static bool IsMatch(Regex regex, string text)
        {
            try
            {
                return regex.IsMatch(text);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        private Regex BuildRegex(string pattern, Token token, bool ignoreCase)
        {
            try
            {
                var options = RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled;
                if (ignoreCase)
                {
                    options |= RegexOptions.IgnoreCase;
                }

                return new Regex(pattern, options, TimeSpan.FromSeconds(2));
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
