namespace LogLens.Infrastructure;

/// <summary>Where LogLens keeps its files (SPEC §6.5). The roots are injectable so tests and <c>--data-dir</c> never touch the real profile.</summary>
public sealed class AppPaths
{
    private const string AppFolder = "LogLens";

    public AppPaths(string roamingRoot, string localRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roamingRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(localRoot);
        RoamingRoot = Path.Combine(roamingRoot, AppFolder);
        LocalRoot = Path.Combine(localRoot, AppFolder);
    }

    /// <summary>
    /// The paths for this start: everything under <c>&lt;folder&gt;\LogLens</c> with <c>--data-dir &lt;folder&gt;</c> (FR-P6; a relative folder is
    /// read against <paramref name="workingDirectory"/>), otherwise <c>%APPDATA%\LogLens</c> and <c>%LOCALAPPDATA%\LogLens</c>.
    /// </summary>
    /// <exception cref="ArgumentException"><c>--data-dir</c> has no usable folder. Callers must not fall back to the real profile.</exception>
    public static AppPaths Resolve(CommandLine commandLine, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        if (commandLine.DataDir is not { } dataDir)
        {
            return new AppPaths(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        }

        if (string.IsNullOrWhiteSpace(dataDir))
        {
            throw new ArgumentException($"{CommandLine.DataDirOption} needs a folder: {CommandLine.DataDirOption} <folder>");
        }

        try
        {
            var folder = Path.GetFullPath(dataDir, workingDirectory);
            return new AppPaths(folder, folder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException($"{CommandLine.DataDirOption}: \"{dataDir}\" is not a usable folder name.", ex);
        }
    }

    /// <summary>Settings and state (<c>%APPDATA%\LogLens</c>).</summary>
    public string RoamingRoot { get; }

    /// <summary>Diagnostics (<c>%LOCALAPPDATA%\LogLens</c>).</summary>
    public string LocalRoot { get; }

    /// <summary>Recent files and the open logs.</summary>
    public string StateFile => Path.Combine(RoamingRoot, "state.json");

    /// <summary>Identifies this profile for single instance: another <c>--data-dir</c> is another LogLens.</summary>
    public string InstanceKey => RoamingRoot;
}
