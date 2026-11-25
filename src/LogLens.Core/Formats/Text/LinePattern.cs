using System.Globalization;
using System.Text.RegularExpressions;
using LogLens.Core.Models;

namespace LogLens.Core.Formats.Text;

/// <summary>One timestamp field of a <see cref="LinePattern"/>: the regex group that captures it and the .NET format it was written with.</summary>
/// <param name="Group">Name of the capture group.</param>
/// <param name="Format">.NET date/time format; null to accept any common date/time text (<see cref="TimestampFormat.TryParseAny"/>).</param>
/// <param name="Universal">True when text without an offset is UTC (NLog <c>universalTime=true</c>, log4net <c>%utcdate</c>); otherwise local time.</param>
public sealed record TimestampField(string Group, string? Format, bool Universal = false);

/// <summary>
/// What the first line of an event looks like in a plain-text log, whatever wrote it: a regular expression plus which of its groups hold the
/// timestamp, level, message, an exception and properties. Serilog output templates, NLog layouts, log4net conversion patterns and user regexes
/// all compile to one, so <see cref="PlainTextLogParser"/> reads every plain-text format the same way (FR-F2, FR-F3).
/// </summary>
public sealed class LinePattern
{
    private readonly int[] _timestampGroups;
    private readonly int _levelGroup;
    private readonly int _messageGroup;
    private readonly int _exceptionGroup;
    private readonly int[] _propertyGroups;
    private readonly TimestampFormat.TimestampParser _parseTimestamp;

    /// <param name="startOfEvent">Matches the first line of an event (the line without its newline is the whole input, so <c>^</c> / <c>$</c> anchor to it).</param>
    /// <param name="timestamp">The group(s) holding the timestamp; several (e.g. NLog <c>${shortdate} ${time}</c>) are read as one, joined by a space.</param>
    /// <param name="levelGroup">The group holding the level; null when the format writes none (every event is then <see cref="LogLevel.Unknown"/>).</param>
    /// <param name="messageGroup">The group holding the message.</param>
    /// <param name="exceptionGroup">The group holding the first line of an exception written on the event's own line, or null.</param>
    /// <param name="properties">Property names and the groups holding them, in the order written.</param>
    /// <param name="startsWithDigit">True when every event line begins with a digit (a cheap pre-check before the regex).</param>
    /// <exception cref="ArgumentException">A group named here doesn't exist in <paramref name="startOfEvent"/>.</exception>
    public LinePattern(
        Regex startOfEvent,
        IReadOnlyList<TimestampField> timestamp,
        string? levelGroup,
        string messageGroup,
        string? exceptionGroup,
        IReadOnlyList<(string Name, string Group)> properties,
        bool startsWithDigit)
    {
        ArgumentNullException.ThrowIfNull(startOfEvent);
        ArgumentNullException.ThrowIfNull(timestamp);
        ArgumentNullException.ThrowIfNull(messageGroup);
        ArgumentNullException.ThrowIfNull(properties);
        if (timestamp.Count == 0)
        {
            throw new ArgumentException("A line pattern needs a timestamp.", nameof(timestamp));
        }

        StartOfEvent = startOfEvent;
        Timestamp = timestamp;
        StartsWithDigit = startsWithDigit;
        PropertyNames = [.. properties.Select(p => p.Name)];
        HasLevel = levelGroup is not null;
        HasException = exceptionGroup is not null;

        _timestampGroups = [.. timestamp.Select(t => Group(t.Group))];
        _levelGroup = levelGroup is null ? -1 : Group(levelGroup);
        _messageGroup = Group(messageGroup);
        _exceptionGroup = exceptionGroup is null ? -1 : Group(exceptionGroup);
        _propertyGroups = [.. properties.Select(p => Group(p.Group))];
        _parseTimestamp = CreateTimestampParser(timestamp);

        int Group(string name)
        {
            var number = startOfEvent.GroupNumberFromName(name);
            return number >= 0 ? number : throw new ArgumentException($"The pattern has no group '{name}'.", nameof(startOfEvent));
        }
    }

    public Regex StartOfEvent { get; }

    public IReadOnlyList<TimestampField> Timestamp { get; }

    /// <summary>True when an event line must begin with a digit, which lets the parser reject most continuation lines without running the regex.</summary>
    public bool StartsWithDigit { get; }

    /// <summary>The properties the pattern reads from an event's first line, in order.</summary>
    public IReadOnlyList<string> PropertyNames { get; }

    public bool HasLevel { get; }

    /// <summary>True when an exception can start on the event's own line (after the message).</summary>
    public bool HasException { get; }

    public bool HasSourceContext => PropertyNames.Contains(LogEvent.SourceContextProperty);

    /// <summary>The optional event fields this pattern fills.</summary>
    public LogFields Fields => LogFields.Exception
                               | (PropertyNames.Count > 0 ? LogFields.Properties : LogFields.None)
                               | (HasSourceContext ? LogFields.SourceContext : LogFields.None);

    /// <summary>
    /// Reads the line <c>text[start..end]</c> as the first line of an event. False when it isn't one: no match, a timestamp that doesn't parse, or a
    /// line so pathological that the regex timed out (NFR-10).
    /// </summary>
    internal bool TryRead(string text, int start, int end, out LineFields fields)
    {
        fields = default;
        var length = end - start;
        if (length == 0 || (StartsWithDigit && (uint)(text[start] - '0') > 9))
        {
            return false;
        }

        Match match;
        try
        {
            match = StartOfEvent.Match(text, start, length);
        }
        catch (RegexMatchTimeoutException)
        {
            return false; // a pathological line is a continuation line, never a crash
        }

        if (!match.Success || !TryTimestamp(text, match, out var timestamp))
        {
            return false;
        }

        var level = LogLevel.Unknown;
        if (_levelGroup >= 0 && match.Groups[_levelGroup] is { Success: true } lvl)
        {
            level = LogLevelParser.Parse(text.AsSpan(lvl.Index, lvl.Length));
        }

        var msg = match.Groups[_messageGroup];
        var message = msg.Success ? text.Substring(msg.Index, msg.Length) : string.Empty;

        string? exception = null;
        if (_exceptionGroup >= 0 && match.Groups[_exceptionGroup] is { Success: true, Length: > 0 } exc)
        {
            exception = text.Substring(exc.Index, exc.Length).TrimEnd();
            message = message.TrimEnd();
        }

        Dictionary<string, LogValue>? properties = null;
        if (_propertyGroups.Length > 0)
        {
            properties = new Dictionary<string, LogValue>(_propertyGroups.Length, StringComparer.Ordinal);
            for (var i = 0; i < _propertyGroups.Length; i++)
            {
                var group = match.Groups[_propertyGroups[i]];
                if (group.Success)
                {
                    properties[PropertyNames[i]] = PropertyValue(text.AsSpan(group.Index, group.Length).Trim());
                }
            }
        }

        fields = new LineFields(timestamp, level, message, exception, properties);
        return true;
    }

    private bool TryTimestamp(string text, Match match, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (_timestampGroups.Length == 1)
        {
            var group = match.Groups[_timestampGroups[0]];
            return group.Success && _parseTimestamp(text.AsSpan(group.Index, group.Length), out timestamp);
        }

        var parts = new string[_timestampGroups.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var group = match.Groups[_timestampGroups[i]];
            if (!group.Success)
            {
                return false;
            }

            parts[i] = group.Value;
        }

        return _parseTimestamp(string.Join(' ', parts), out timestamp);
    }

    private static TimestampFormat.TimestampParser CreateTimestampParser(IReadOnlyList<TimestampField> fields)
    {
        var universal = fields.Any(f => f.Universal);
        if (fields.Any(f => f.Format is null))
        {
            return universal ? TimestampFormat.TryParseAnyUniversal : TimestampFormat.TryParseAny;
        }

        return TimestampFormat.CreateParser(string.Join(' ', fields.Select(f => f.Format)), universal);
    }

    /// <summary>A property read from a line: quoted text loses its quotes, a whole number becomes a number, anything else stays text.</summary>
    private static LogValue PropertyValue(ReadOnlySpan<char> raw)
    {
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            return new LogValue(raw[1..^1].ToString().Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal));
        }

        if (raw.Length is > 0 and < 19 && long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
        {
            return new LogValue(number);
        }

        return new LogValue(raw.ToString());
    }
}

/// <summary>What <see cref="LinePattern"/> reads from the first line of an event.</summary>
internal readonly record struct LineFields(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Message,
    string? Exception,
    Dictionary<string, LogValue>? Properties);
