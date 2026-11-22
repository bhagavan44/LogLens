using LogLens.Core.Diagnostics;
using LogLens.Core.Loading;
using LogLens.Infrastructure;
using LogLens.Infrastructure.Loading;
using LogLens.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace LogLens.App;

public partial class App : Application
{
    private readonly CrashPolicy _crashPolicy = new();
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    /// <summary>Application-wide service provider; the composition root is <see cref="ConfigureServices"/>.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Services = ConfigureServices();

        // Compile the template regexes and JIT the parser on a pool thread while the window starts, so the first file opens sooner (NFR-2).
        _ = Task.Run(WarmUpParser);

        var window = Services.GetRequiredService<MainWindow>();
        _window = window;
        window.Activate();

        // "Open with" / command line: LogLens.exe [--data-dir <folder>] [<log file> ...]
        var commandLine = CommandLine.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray(), Environment.CurrentDirectory);
        if (commandLine.Files.Count > 0)
        {
            window.Open(commandLine.Files);
        }
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILogDocumentLoader>(_ => new LogFileLoader());
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
            return false;
        }

        window.ReportUnexpectedError(exception);
        return true;
    }
}
