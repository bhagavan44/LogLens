using LogLens.App.Services;
using LogLens.Infrastructure;
using LogLens.Infrastructure.Instances;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace LogLens.App;

/// <summary>
/// Entry point (replaces the XAML-generated <c>Main</c>, see <c>DISABLE_XAML_GENERATED_MAIN</c> in the project file). Before any window is
/// created it looks for a running LogLens with the same profile: if there is one, the command line is handed over (the file opens there)
/// and this process exits at once.
/// </summary>
public static class Program
{
    /// <summary>Starts a separate LogLens on purpose, without handing over to a running one.</summary>
    public const string NewWindowOption = "--new-window";

    /// <summary>The command line this process was started with.</summary>
    internal static CommandLine CommandLine { get; private set; } = new([], null);

    /// <summary>Where settings and state live for this run (FR-P6).</summary>
    internal static AppPaths Paths { get; private set; } = null!;

    /// <summary>The single-instance channel; null with <see cref="NewWindowOption"/>.</summary>
    internal static SingleInstance? Instance { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // Single-file publish: the self-contained Windows App SDK runtime is unpacked to the .NET extraction folder, so point the SDK at it
        // before anything touches it. Harmless in a normal folder build (BaseDirectory is then the exe's folder).
        Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", AppContext.BaseDirectory);

        WinRT.ComWrappersSupport.InitializeComWrappers();

        CommandLine = CommandLine.Parse(args, Environment.CurrentDirectory);
        try
        {
            Paths = AppPaths.Resolve(CommandLine, Environment.CurrentDirectory);
        }
        catch (ArgumentException ex)
        {
            // --data-dir without a usable folder: stop rather than quietly use the real profile, which the option is there to avoid.
            NativeMethods.ShowError(AppInfo.Name, ex.Message);
            return 2;
        }

        if (!args.Contains(NewWindowOption, StringComparer.OrdinalIgnoreCase))
        {
            Instance = SingleInstance.Claim(Paths.InstanceKey, new LaunchRequest(Environment.CurrentDirectory, args), NativeMethods.AllowForeground);
            if (Instance is null)
            {
                return 0; // handed over to the running LogLens
            }
        }

        Application.Start(callback =>
        {
            // Like the generated Main's DispatcherQueueSynchronizationContext, but exceptions in continuations reach App.TryContinueAfter.
            var context = new GuardedSynchronizationContext(DispatcherQueue.GetForCurrentThread(), App.TryContinueAfter);
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        Instance?.Dispose();
        return 0;
    }
}
