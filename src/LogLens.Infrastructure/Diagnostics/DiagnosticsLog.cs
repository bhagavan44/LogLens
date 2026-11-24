using Serilog;

namespace LogLens.Infrastructure.Diagnostics;

/// <summary>
/// LogLens's own log (NFR-12): one file a day in <see cref="AppPaths.LogDirectory"/>, the last <see cref="RetainedDays"/> kept. Written with
/// Serilog's default text template, so LogLens can open its own log. It stays on this PC (NFR-9) and never holds log content, only what
/// LogLens did and what went wrong.
/// </summary>
public static class DiagnosticsLog
{
    public const int RetainedDays = 14;

    /// <summary>File name pattern; Serilog puts the date before the extension (<c>loglens-20261124.txt</c>).</summary>
    public const string FilePattern = "loglens-.txt";

    /// <summary>A logger writing to the diagnostics folder, or one that writes nothing when the folder can't be created: diagnostics never stop LogLens from starting.</summary>
    public static ILogger Create(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        try
        {
            Directory.CreateDirectory(paths.LogDirectory);
            return new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(
                    Path.Combine(paths.LogDirectory, FilePattern),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: RetainedDays,
                    shared: true) // a second LogLens (--new-window) writes to the same file
                .CreateLogger();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return Serilog.Core.Logger.None;
        }
    }
}
