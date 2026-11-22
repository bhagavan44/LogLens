namespace LogLens.Infrastructure;

/// <summary>
/// What LogLens was started with (FR-O1): <c>LogLens.exe [options] [&lt;log file&gt; …]</c>. Options start with <c>--</c>; the ones listed in
/// <see cref="OptionsWithValue"/> take the next argument as their value. Everything else is a file to open.
/// </summary>
/// <param name="Files">The files to open, as full paths.</param>
/// <param name="DataDir">The folder after <c>--data-dir</c>; null without the option, empty when the option has no folder after it.</param>
public sealed record CommandLine(IReadOnlyList<string> Files, string? DataDir)
{
    /// <summary>Keeps settings and state in another folder (FR-P6).</summary>
    public const string DataDirOption = "--data-dir";

    private static readonly string[] OptionsWithValue = [DataDirOption];

    /// <summary>Parses the arguments after the program name; relative file paths are read against <paramref name="workingDirectory"/>.</summary>
    public static CommandLine Parse(IReadOnlyList<string> args, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(args);
        var files = new List<string>();
        string? dataDir = null;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (OptionsWithValue.Contains(arg, StringComparer.OrdinalIgnoreCase))
            {
                var value = i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : string.Empty;
                if (string.Equals(arg, DataDirOption, StringComparison.OrdinalIgnoreCase))
                {
                    dataDir = value;
                }

                continue;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(arg))
            {
                continue; // unknown switches are ignored rather than opened as files
            }

            files.Add(ToFullPath(arg, workingDirectory));
        }

        return new CommandLine(files, dataDir);
    }

    private static string ToFullPath(string path, string workingDirectory)
    {
        try
        {
            return Path.GetFullPath(path, workingDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path; // reported as "file not found" when it is opened
        }
    }
}
