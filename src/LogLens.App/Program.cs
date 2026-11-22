using LogLens.App.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace LogLens.App;

/// <summary>Entry point (replaces the XAML-generated <c>Main</c>, see <c>DISABLE_XAML_GENERATED_MAIN</c> in the project file).</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Single-file publish: the self-contained Windows App SDK runtime is unpacked to the .NET extraction folder, so point the SDK at it
        // before anything touches it. Harmless in a normal folder build (BaseDirectory is then the exe's folder).
        Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", AppContext.BaseDirectory);

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(callback =>
        {
            // Like the generated Main's DispatcherQueueSynchronizationContext, but exceptions in continuations reach App.TryContinueAfter.
            var context = new GuardedSynchronizationContext(DispatcherQueue.GetForCurrentThread(), App.TryContinueAfter);
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        return 0;
    }
}
