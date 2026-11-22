using System.Runtime.InteropServices;

namespace LogLens.Core.Diagnostics;

/// <summary>What to do after an exception nobody caught on the UI thread.</summary>
public enum CrashDecision
{
    /// <summary>Mark it handled, tell the user, keep running: the viewer is read-only, so the worst case is a wrong screen, not lost data.</summary>
    Continue,

    /// <summary>Let the process end: the runtime is in a state that can't be trusted, or the same failure keeps repeating.</summary>
    Terminate,
}

/// <summary>
/// Decides whether the app may carry on after an unhandled UI exception. A viewer never writes the logs it shows, so most failures (a
/// bug in one handler, a file that vanished mid-read) are safer to report and survive than to crash on — but a corrupted runtime, or an
/// error that fires again and again, must still end the process instead of leaving a window that only shows error messages.
/// </summary>
public sealed class CrashPolicy
{
    /// <summary>More unhandled exceptions than this within <see cref="Window"/> means the app is stuck in a loop.</summary>
    public const int MaxInWindow = 3;

    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private readonly Queue<DateTimeOffset> _recent = new();
    private readonly Func<DateTimeOffset> _clock;

    public CrashPolicy(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>How many exceptions have been survived so far.</summary>
    public int Survived { get; private set; }

    public CrashDecision Decide(Exception? exception)
    {
        if (exception is null || IsCorrupting(exception))
        {
            return CrashDecision.Terminate;
        }

        var now = _clock();
        while (_recent.Count > 0 && now - _recent.Peek() > Window)
        {
            _recent.Dequeue();
        }

        _recent.Enqueue(now);
        if (_recent.Count > MaxInWindow)
        {
            return CrashDecision.Terminate;
        }

        Survived++;
        return CrashDecision.Continue;
    }

    /// <summary>Exceptions after which no managed state can be trusted (also when wrapped, e.g. inside an <see cref="AggregateException"/>).</summary>
    public static bool IsCorrupting(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is OutOfMemoryException or InsufficientExecutionStackException or AccessViolationException or SEHException
                or InvalidProgramException or BadImageFormatException or TypeInitializationException or AppDomainUnloadedException)
            {
                return true;
            }

            if (e is AggregateException aggregate && aggregate.InnerExceptions.Any(IsCorrupting))
            {
                return true;
            }
        }

        return false;
    }
}
