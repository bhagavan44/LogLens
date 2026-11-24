using LogLens.App.Services;
using LogLens.Core.Diagnostics;
using LogLens.Core.Settings;
using LogLens.Core.State;
using LogLens.Core.Streaming;
using LogLens.Infrastructure;
using LogLens.Infrastructure.Diagnostics;
using LogLens.Infrastructure.Loading;
using LogLens.Infrastructure.State;
using LogLens.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace LogLens.App;

public partial class App : Application
{
    private static int _closingReported;
    private readonly CrashPolicy _crashPolicy = new();
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;

        // NFR-12: an exception that ends the process (a worker thread, or one the policy refuses to survive) still leaves a report.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                ReportCrash(ex, "unhandled exception", survived: false);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Warning(e.Exception, "A background task failed and nobody waited for it");
            e.SetObserved();
        };
    }

    /// <summary>LogLens's own diagnostics log (NFR-12); writes nothing until the paths are known.</summary>
    internal static Serilog.ILogger Log { get; private set; } = Serilog.Core.Logger.None;

    /// <summary>Application-wide service provider; the composition root is <see cref="ConfigureServices"/>.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log = DiagnosticsLog.Create(Program.Paths);
        Log.Information("LogLens {Version} started; settings in {Folder}", AppInfo.Version, Program.Paths.RoamingRoot);
        Services = ConfigureServices();

        // Compile the template regexes and JIT the parser on a pool thread while the window starts, so the first file opens sooner (NFR-2).
        _ = Task.Run(WarmUpParser);

        var window = Services.GetRequiredService<MainWindow>();
        _window = window;
        window.Closed += (_, _) =>
        {
            Log.Information("LogLens closed");
            (Log as IDisposable)?.Dispose(); // flushes the file
        };
        window.Activate();

        // Only the LogLens that owns the profile remembers the open log; a --new-window one leaves the session alone.
        var primary = Program.Instance is { IsPrimary: true };
        var viewModel = window.ViewModel;
        viewModel.RemembersSession = primary;

        // "Open with" / command line: LogLens.exe [--data-dir <folder>] [<log file> ...]; otherwise what was open last time (FR-P3).
        if (Program.CommandLine.Files.Count > 0)
        {
            window.Open(Program.CommandLine.Files);
        }
        else if (primary && Services.GetRequiredService<SettingsService>().Current.RestoreSession)
        {
            _ = viewModel.RestoreSessionAsync();
        }

        // Later starts ("Open with", a second LogLens.exe) hand their command line to this window.
        if (Program.Instance is { IsPrimary: true } instance)
        {
            instance.Listen(request => GuardedSynchronizationContext.Enqueue(window.DispatcherQueue, () => window.HandleLaunch(request)));
        }
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Program.Paths);
        services.AddSingleton(Log);
        services.AddSingleton<IStateStore>(sp => new JsonStateStore(sp.GetRequiredService<AppPaths>()));
        services.AddSingleton<ISettingsStore>(sp => new JsonSettingsStore(sp.GetRequiredService<AppPaths>()));
        services.AddSingleton(sp => new SettingsService(sp.GetRequiredService<ISettingsStore>()));
        services.AddSingleton(sp => new RecentFilesService(sp.GetRequiredService<IStateStore>()));
        services.AddSingleton(sp => new SessionService(sp.GetRequiredService<IStateStore>()));
        services.AddSingleton(sp => new BookmarkService(sp.GetRequiredService<IStateStore>()));
        services.AddSingleton<ILogStreamFactory>(_ => new FileLogStreamFactory());
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        return services.BuildServiceProvider();
    }

    private static void WarmUpParser()
    {
        try
        {
            new Core.Formats.SerilogText.PlainTextLogParser().Parse("2026-11-13 00:00:00.000 +00:00 [INF] warm up\nSystem.Exception: x\n   at A.B()\n");
        }
        catch (Exception)
        {
            // purely an optimisation; never let it affect start-up
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        => e.Handled = TryContinueAfter(e.Exception ?? new InvalidOperationException(e.Message), "UI event handler");

    /// <summary>
    /// Judges an exception that escaped UI code. The viewer never writes the logs it shows, so a failure in one command is reported in the
    /// message bar and survived; a corrupted runtime or a failure that keeps repeating still ends the process (<see cref="CrashPolicy"/>).
    /// Also called by <see cref="Services.GuardedSynchronizationContext"/> for dispatcher callbacks, which WinUI would otherwise fail fast on.
    /// </summary>
    internal static bool TryContinueAfter(Exception exception, string source)
    {
        if (Current is not App { _window: { } window } app)
        {
            return false;
        }

        if (app._crashPolicy.Decide(exception) != CrashDecision.Continue)
        {
            ReportCrash(exception, source, survived: false);
            return false;
        }

        ReportCrash(exception, source, survived: true);
        window.ReportUnexpectedError(exception);
        return true;
    }

    /// <summary>Logs the exception and writes a crash report next to the diagnostics log (NFR-12). Never throws.</summary>
    private static void ReportCrash(Exception exception, string source, bool survived)
    {
        if (!survived && Interlocked.Exchange(ref _closingReported, 1) == 1)
        {
            return; // already reported: the process is on its way out
        }

        try
        {
            Log.Error(exception, "Unhandled exception in {Source}; {Outcome}", source, survived ? "carried on" : "closing");
            if (Program.Paths is { } paths)
            {
                CrashReports.Write(paths, exception, source, AppInfo.Version, survived);
            }

            if (!survived)
            {
                (Log as IDisposable)?.Dispose();
            }
        }
        catch (Exception)
        {
            // reporting must never cause a second failure
        }
    }
}
