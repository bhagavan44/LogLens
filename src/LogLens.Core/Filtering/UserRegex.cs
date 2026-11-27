using System.Text.RegularExpressions;

namespace LogLens.Core.Filtering;

/// <summary>
/// A regular expression the user typed (find bar, query bar, custom format) that can never stall a pass over a log (NFR-10). It runs on .NET's
/// linear-time engine (<see cref="RegexOptions.NonBacktracking"/>) when the pattern allows, so nested repeats such as <c>(\w+\s?)+!</c> cannot
/// backtrack catastrophically. Patterns that engine can't run (lookarounds, backreferences, atomic groups) use the backtracking engine with a short
/// timeout per match; after <see cref="MaxTimeouts"/> timeouts the expression is <see cref="TooSlow"/> and stops matching anything, so a pass
/// over 250 k events ends in well under a second instead of taking hours.
/// </summary>
/// <remarks>Thread-safe: a filter pass may run it on a worker while the UI thread matches new events.</remarks>
public sealed class UserRegex
{
    /// <summary>How many matches may time out before the expression gives up.</summary>
    public const int MaxTimeouts = 3;

    /// <summary>The longest one match may take.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>What to tell the user when an expression gave up.</summary>
    public const string TooSlowMessage = "The regular expression is too slow on this log (it timed out), so it isn't applied. Simplify it, for example avoid lookarounds or backreferences.";

    private int _timeouts;

    private UserRegex(Regex regex) => Regex = regex;

    /// <summary>The compiled expression.</summary>
    public Regex Regex { get; }

    /// <summary>True when the expression runs on the linear-time engine and so can't backtrack catastrophically.</summary>
    public bool IsLinear => (Regex.Options & RegexOptions.NonBacktracking) != 0;

    /// <summary>The expression timed out too often and no longer matches anything.</summary>
    public bool TooSlow => Volatile.Read(ref _timeouts) >= MaxTimeouts;

    /// <summary>Compiles <paramref name="pattern"/>, on the linear-time engine when it can.</summary>
    /// <exception cref="ArgumentException">The pattern is not a valid regular expression.</exception>
    public static UserRegex Create(string pattern, RegexOptions options)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        try
        {
            return new UserRegex(new Regex(pattern, (options & ~RegexOptions.Compiled) | RegexOptions.NonBacktracking, MatchTimeout));
        }
        catch (NotSupportedException)
        {
            // A construct only the backtracking engine has (lookaround, backreference, atomic group …), or a pattern too large to run linearly.
            return new UserRegex(new Regex(pattern, options, MatchTimeout));
        }
    }

    /// <summary>True when <paramref name="text"/> contains a match; false once the expression is <see cref="TooSlow"/>.</summary>
    public bool IsMatch(string text)
    {
        if (TooSlow)
        {
            return false;
        }

        try
        {
            return Regex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            NoteTimeout();
            return false;
        }
    }

    /// <summary>The first match within <c>text[start..start + length]</c>; null when there is none, the match timed out, or the expression is <see cref="TooSlow"/>.</summary>
    public Match? Match(string text, int start, int length)
    {
        if (TooSlow)
        {
            return null;
        }

        try
        {
            var match = Regex.Match(text, start, length);
            return match.Success ? match : null;
        }
        catch (RegexMatchTimeoutException)
        {
            NoteTimeout();
            return null;
        }
    }

    /// <summary>Records a timeout that happened outside <see cref="IsMatch"/> / <see cref="Match"/> (e.g. while enumerating matches).</summary>
    public void NoteTimeout() => Interlocked.Increment(ref _timeouts);
}
