using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LogLens.Core.Models;

/// <summary>
/// The value of a structured property (SPEC §6.4): a scalar (<see cref="string"/>, <see cref="long"/>, <see cref="double"/>,
/// <see cref="decimal"/>, <see cref="bool"/> or null), a sequence (<see cref="IReadOnlyList{T}"/> of values) or a structure
/// (<see cref="IReadOnlyDictionary{TKey, TValue}"/> of named values). Equal values compare equal, whatever their shape.
/// </summary>
public sealed class LogValue : IEquatable<LogValue>
{
    public static LogValue Null { get; } = new(null);

    /// <param name="value">A scalar, a <see cref="IReadOnlyList{T}"/> of <see cref="LogValue"/> or a <see cref="IReadOnlyDictionary{TKey, TValue}"/> of them.</param>
    public LogValue(object? value)
    {
        Value = value switch
        {
            null or string or bool or long or double or decimal or IReadOnlyList<LogValue> or IReadOnlyDictionary<string, LogValue> => value,
            int i => (long)i,
            float f => (double)f,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        };
    }

    /// <summary>The value as read from the log.</summary>
    public object? Value { get; }

    public bool IsNull => Value is null;

    /// <summary>A number (integer or not).</summary>
    public bool IsNumber => Value is long or double or decimal;

    public bool IsScalar => Value is not (IReadOnlyList<LogValue> or IReadOnlyDictionary<string, LogValue>);

    public IReadOnlyList<LogValue>? AsSequence => Value as IReadOnlyList<LogValue>;

    public IReadOnlyDictionary<string, LogValue>? AsStructure => Value as IReadOnlyDictionary<string, LogValue>;

    /// <summary>The value as a number, when it is one.</summary>
    public bool TryGetNumber(out double number)
    {
        switch (Value)
        {
            case long l:
                number = l;
                return true;
            case double d:
                number = d;
                return true;
            case decimal m:
                number = (double)m;
                return true;
            default:
                number = 0;
                return false;
        }
    }

    /// <summary>
    /// The value as a person reads it: text without quotes, numbers in the invariant culture, <c>true</c> / <c>false</c>, <c>null</c>, and
    /// sequences and structures as compact JSON.
    /// </summary>
    public override string ToString() => Value switch
    {
        null => "null",
        string s => s,
        _ when !IsScalar => ToJson(),
        _ => ScalarText(Value),
    };

    /// <summary>The value as JSON (text quoted and escaped).</summary>
    public string ToJson()
    {
        var sb = new StringBuilder();
        WriteJson(sb, this);
        return sb.ToString();
    }

    /// <summary>Converts a JSON value; nesting deeper than <paramref name="maxDepth"/> is kept as its JSON text (NFR-10).</summary>
    public static LogValue FromJson(JsonElement element, int maxDepth = 32)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return new LogValue(element.GetString());
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var l))
                {
                    return new LogValue(l);
                }

                if (element.TryGetDecimal(out var m) && !element.GetRawText().Contains('e', StringComparison.OrdinalIgnoreCase))
                {
                    return new LogValue(m);
                }

                return new LogValue(element.GetDouble());
            case JsonValueKind.True:
                return new LogValue(true);
            case JsonValueKind.False:
                return new LogValue(false);
            case JsonValueKind.Array when maxDepth > 0:
                var items = new List<LogValue>(element.GetArrayLength());
                foreach (var item in element.EnumerateArray())
                {
                    items.Add(FromJson(item, maxDepth - 1));
                }

                return new LogValue(items);
            case JsonValueKind.Object when maxDepth > 0:
                var fields = new Dictionary<string, LogValue>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    fields[property.Name] = FromJson(property.Value, maxDepth - 1);
                }

                return new LogValue(fields);
            case JsonValueKind.Array or JsonValueKind.Object:
                return new LogValue(element.GetRawText());
            default:
                return Null;
        }
    }

    public bool Equals(LogValue? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return (Value, other.Value) switch
        {
            (IReadOnlyList<LogValue> a, IReadOnlyList<LogValue> b) => a.SequenceEqual(b),
            (IReadOnlyDictionary<string, LogValue> a, IReadOnlyDictionary<string, LogValue> b) => PropertiesEqual(a, b),
            (var a, var b) => Equals(a, b),
        };
    }

    public override bool Equals(object? obj) => obj is LogValue other && Equals(other);

    public override int GetHashCode() => Value switch
    {
        null => 0,
        IReadOnlyList<LogValue> list => list.Count,
        IReadOnlyDictionary<string, LogValue> map => map.Count,
        var v => v.GetHashCode(),
    };

    /// <summary>True when both sets of properties hold the same names with equal values (either may be null).</summary>
    public static bool PropertiesEqual(IReadOnlyDictionary<string, LogValue>? a, IReadOnlyDictionary<string, LogValue>? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null || a.Count != b.Count)
        {
            return false;
        }

        foreach (var (key, value) in a)
        {
            if (!b.TryGetValue(key, out var other) || !value.Equals(other))
            {
                return false;
            }
        }

        return true;
    }

    private static string ScalarText(object? value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static void WriteJson(StringBuilder sb, LogValue value)
    {
        switch (value.Value)
        {
            case null:
                sb.Append("null");
                break;
            case string s:
                WriteString(sb, s);
                break;
            case IReadOnlyList<LogValue> list:
                sb.Append('[');
                for (var i = 0; i < list.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    WriteJson(sb, list[i]);
                }

                sb.Append(']');
                break;
            case IReadOnlyDictionary<string, LogValue> map:
                sb.Append('{');
                var first = true;
                foreach (var (key, item) in map)
                {
                    sb.Append(first ? " " : ", ");
                    first = false;
                    WriteString(sb, key);
                    sb.Append(": ");
                    WriteJson(sb, item);
                }

                sb.Append(first ? "}" : " }");
                break;
            case double d when !double.IsFinite(d):
                WriteString(sb, ScalarText(d));
                break;
            default:
                sb.Append(ScalarText(value.Value));
                break;
        }
    }

    internal static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case < ' ':
                    sb.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        sb.Append('"');
    }
}
