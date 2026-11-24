using LogLens.Infrastructure.Diagnostics;

namespace LogLens.Infrastructure.Tests;

/// <summary>LogLens's own log and crash reports (NFR-12).</summary>
public sealed class DiagnosticsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "loglens-tests", Guid.NewGuid().ToString("N"));

    private AppPaths Paths => new(_dir, _dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Diagnostics_live_in_the_local_folder_or_the_data_dir()
    {
        Assert.Equal(Path.Combine(_dir, "LogLens", "logs"), Paths.LogDirectory);

        var dataDir = AppPaths.Resolve(CommandLine.Parse(["--data-dir", "portable"], _dir), _dir);
        Assert.Equal(Path.Combine(_dir, "portable", "LogLens", "logs"), dataDir.LogDirectory);
    }

    [Fact]
    public void The_log_is_written_in_the_default_serilog_template_so_LogLens_can_open_it()
    {
        var log = DiagnosticsLog.Create(Paths);
        log.Information("Started {Version}", "1.0.0");
        log.Error(new InvalidOperationException("boom"), "Failed");
        (log as IDisposable)?.Dispose();

        var file = Assert.Single(Directory.GetFiles(Paths.LogDirectory, "loglens-*.txt"));
        var parsed = new Core.Formats.SerilogText.PlainTextLogParser().Parse(File.ReadAllText(file));

        Assert.Equal(0, parsed.UnparsedCount);
        Assert.Equal([Core.Models.LogLevel.Information, Core.Models.LogLevel.Error], parsed.Events.Select(e => e.Level));
        Assert.Equal("Started 1.0.0", parsed.Events[0].Message);
        Assert.StartsWith("System.InvalidOperationException: boom", parsed.Events[1].Exception);
    }

    [Fact]
    public void A_folder_that_cannot_be_created_gives_a_logger_that_writes_nothing()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "LogLens"), "a file where the folder should be");

        var log = DiagnosticsLog.Create(Paths);

        log.Information("goes nowhere"); // and does not throw
        Assert.Same(Serilog.Core.Logger.None, log);
    }

    [Fact]
    public void A_crash_report_says_what_happened_without_log_content()
    {
        Exception exception;
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (InvalidOperationException ex)
        {
            exception = ex;
        }

        var at = new DateTimeOffset(2026, 11, 24, 10, 15, 30, 123, TimeSpan.FromHours(1));
        var path = CrashReports.Write(Paths, exception, "UI event handler", "1.0.0", survived: true, at);

        Assert.Equal(Path.Combine(Paths.LogDirectory, "crash-20261124-101530-123.txt"), path);
        var text = File.ReadAllText(path!);
        Assert.Contains("LogLens 1.0.0: unhandled exception (LogLens carried on)", text);
        Assert.Contains("Source:  UI event handler", text);
        Assert.Contains("System.InvalidOperationException: boom", text);
        Assert.Contains(nameof(A_crash_report_says_what_happened_without_log_content), text); // the stack trace
    }

    [Fact]
    public void Only_the_newest_reports_are_kept()
    {
        var start = new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < CrashReports.MaxReports + 3; i++)
        {
            CrashReports.Write(Paths, new InvalidOperationException(), "test", "1.0.0", survived: false, start.AddSeconds(i));
        }

        var reports = Directory.GetFiles(Paths.LogDirectory, "crash-*.txt").Select(Path.GetFileName).Order().ToList();
        Assert.Equal(CrashReports.MaxReports, reports.Count);
        Assert.Equal("crash-20261124-100003-000.txt", reports[0]); // the three oldest were deleted
    }

    [Fact]
    public void A_report_that_cannot_be_written_gives_null()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "LogLens"), "a file where the folder should be");

        Assert.Null(CrashReports.Write(Paths, new InvalidOperationException(), "test", "1.0.0", survived: false));
    }
}
