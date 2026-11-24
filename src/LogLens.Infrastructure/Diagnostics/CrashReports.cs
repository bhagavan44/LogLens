using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace LogLens.Infrastructure.Diagnostics;

/// <summary>
/// Writes a plain-text report for an exception that reached the top of the app, next to the diagnostics log (NFR-12). The diagnostics log may
/// not be flushed when the process dies, so the report is written directly and synchronously. Never throws. Reports stay on this PC (NFR-9);
/// only the newest <see cref="MaxReports"/> are kept.
/// </summary>
public static class CrashReports
{
    public const int MaxReports = 10;

    public const string Prefix = "crash-";

    /// <summary>Writes the report and returns its path, or null when it could not be written.</summary>
    /// <param name="source">Where the exception was caught, e.g. "UI event handler".</param>
    /// <param name="survived">LogLens carried on after it (the report then says so).</param>
    public static string? Write(AppPaths paths, Exception exception, string source, string version, bool survived, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(exception);
        try
        {
            Directory.CreateDirectory(paths.LogDirectory);
            var at = now ?? DateTimeOffset.Now;
            var path = Path.Combine(paths.LogDirectory, $"{Prefix}{at.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}.txt");
            File.WriteAllText(path, Format(exception, source, version, survived, at), Encoding.UTF8);
            Prune(paths.LogDirectory);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The report's text: what, where and when, the versions involved, and the exception with its stack trace. No log content.</summary>
    public static string Format(Exception exception, string source, string version, bool survived, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"LogLens {version}: unhandled exception ({(survived ? "LogLens carried on" : "LogLens closed")})")
            .AppendLine(CultureInfo.InvariantCulture, $"Time:    {at:yyyy-MM-dd HH:mm:ss.fff zzz}")
            .AppendLine(CultureInfo.InvariantCulture, $"Source:  {source}")
            .AppendLine(CultureInfo.InvariantCulture, $"OS:      {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})")
            .AppendLine(CultureInfo.InvariantCulture, $"Runtime: {RuntimeInformation.FrameworkDescription}")
            .AppendLine()
            .AppendLine(exception.ToString())
            .ToString();
    }

    /// <summary>Deletes all but the newest <see cref="MaxReports"/> reports.</summary>
    public static void Prune(string directory)
    {
        try
        {
            var old = new DirectoryInfo(directory).GetFiles(Prefix + "*.txt")
                .OrderByDescending(f => f.Name, StringComparer.Ordinal) // the names sort by time
                .Skip(MaxReports);
            foreach (var file in old)
            {
                file.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }
}
