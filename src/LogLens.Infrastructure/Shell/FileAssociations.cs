using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace LogLens.Infrastructure.Shell;

/// <summary>Whether LogLens is in Explorer's Open with menu, and whether that entry still runs this copy of the app.</summary>
public enum AssociationStatus
{
    NotRegistered,

    /// <summary>Registered, and the command runs this exe.</summary>
    Registered,

    /// <summary>Registered for another copy of the app: the exe was moved, renamed by an update, or a second copy was registered.</summary>
    RegisteredElsewhere,
}

/// <summary>
/// Per-user <i>Open with</i> registration for the portable exe (FR-P5): no admin rights, nothing outside <c>HKCU\Software\Classes</c>. It adds
/// LogLens to each extension's <c>OpenWithProgids</c> list and never touches the extension's default program (Windows only lets the user
/// choose that), so other apps keep working exactly as before. <see cref="Unregister"/> removes everything <see cref="Register"/> wrote.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FileAssociations
{
    public const string ProgId = "LogLens.LogFile";

    private const string FriendlyName = "LogLens";

    /// <summary>The types offered in the settings (FR-P5). Only these can be registered.</summary>
    public static readonly IReadOnlyList<string> KnownExtensions = [".log", ".clef", ".txt", ".json"];

    /// <summary>
    /// Ticked until the user chooses (FR-P5): <c>.log</c> and <c>.clef</c>. <c>.txt</c> and <c>.json</c> are too general to claim.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultExtensions = [".log", ".clef"];

    private readonly RegistryKey _root;
    private readonly string _classesPath;
    private readonly bool _notifyShell;

    /// <summary>Registration under <c>HKCU\Software\Classes</c>.</summary>
    public FileAssociations()
        : this(Registry.CurrentUser, @"Software\Classes", notifyShell: true)
    {
    }

    /// <summary>Test seam: registers under <paramref name="root"/>\<paramref name="classesPath"/> instead of the real classes key.</summary>
    public FileAssociations(RegistryKey root, string classesPath, bool notifyShell = false)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        ArgumentException.ThrowIfNullOrWhiteSpace(classesPath);
        _classesPath = classesPath.TrimEnd('\\');
        _notifyShell = notifyShell;
    }

    /// <summary>The command line Explorer runs for a file: the quoted exe and the quoted file name.</summary>
    public static string CommandFor(string exePath) => $"\"{exePath}\" \"%1\"";

    /// <summary>
    /// Registers <paramref name="exePath"/> for <paramref name="extensions"/> (unknown ones are ignored); extensions registered before but not
    /// listed now are removed, and so is the entry of a copy registered before from another place.
    /// </summary>
    public void Register(string exePath, IEnumerable<string> extensions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ArgumentNullException.ThrowIfNull(extensions);
        var wanted = extensions.Select(Normalise).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var exeName = Path.GetFileName(exePath);

        if (RegisteredExePath() is { } previous && !string.Equals(Path.GetFileName(previous), exeName, StringComparison.OrdinalIgnoreCase))
        {
            DeleteApplicationKey(previous); // the exe was renamed (a new version is a new file name)
        }

        using (var progId = _root.CreateSubKey($@"{_classesPath}\{ProgId}"))
        {
            progId.SetValue(string.Empty, "Log file");
            progId.SetValue("FriendlyTypeName", "Log file");
            using (var icon = progId.CreateSubKey("DefaultIcon"))
            {
                icon.SetValue(string.Empty, $"\"{exePath}\",0");
            }

            using var command = progId.CreateSubKey(@"shell\open\command");
            command.SetValue(string.Empty, CommandFor(exePath));
        }

        // "Applications\<exe>" gives the app its name and icon in the Open with list and limits it to the types it handles.
        using (var app = _root.CreateSubKey($@"{_classesPath}\Applications\{exeName}"))
        {
            app.SetValue("FriendlyAppName", FriendlyName);
            using (var command = app.CreateSubKey(@"shell\open\command"))
            {
                command.SetValue(string.Empty, CommandFor(exePath));
            }

            app.DeleteSubKeyTree("SupportedTypes", throwOnMissingSubKey: false);
            using var types = app.CreateSubKey("SupportedTypes");
            foreach (var extension in wanted)
            {
                types.SetValue(extension, string.Empty);
            }
        }

        foreach (var extension in KnownExtensions)
        {
            if (wanted.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                using var openWith = _root.CreateSubKey($@"{_classesPath}\{extension}\OpenWithProgids");
                openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            }
            else
            {
                RemoveOpenWith(extension);
            }
        }

        NotifyShell();
    }

    /// <summary>Removes every key and value <see cref="Register"/> wrote, for this copy and for one registered from another place. Safe to call when nothing is registered.</summary>
    public void Unregister(string exePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        foreach (var extension in KnownExtensions)
        {
            RemoveOpenWith(extension);
        }

        if (RegisteredExePath() is { } previous)
        {
            DeleteApplicationKey(previous);
        }

        DeleteApplicationKey(exePath);
        _root.DeleteSubKeyTree($@"{_classesPath}\{ProgId}", throwOnMissingSubKey: false);
        NotifyShell();
    }

    /// <summary>The extensions whose Open with list includes LogLens.</summary>
    public IReadOnlyList<string> RegisteredExtensions()
    {
        using var classes = _root.OpenSubKey(_classesPath);
        if (classes is null)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var extension in KnownExtensions)
        {
            using var openWith = classes.OpenSubKey($@"{extension}\OpenWithProgids");
            if (openWith?.GetValueNames().Contains(ProgId, StringComparer.OrdinalIgnoreCase) == true)
            {
                result.Add(extension);
            }
        }

        return result;
    }

    public AssociationStatus StatusFor(string exePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        if (RegisteredExePath() is not { } registered || RegisteredExtensions().Count == 0)
        {
            return AssociationStatus.NotRegistered;
        }

        return string.Equals(registered, exePath, StringComparison.OrdinalIgnoreCase) ? AssociationStatus.Registered : AssociationStatus.RegisteredElsewhere;
    }

    /// <summary>The exe the registered command runs, or null when LogLens is not registered.</summary>
    public string? RegisteredExePath()
    {
        using var command = _root.OpenSubKey($@"{_classesPath}\{ProgId}\shell\open\command");
        if (command?.GetValue(string.Empty) is not string text || !text.StartsWith('"'))
        {
            return null;
        }

        var end = text.IndexOf('"', 1);
        return end > 1 ? text[1..end] : null;
    }

    private void DeleteApplicationKey(string exePath)
        => _root.DeleteSubKeyTree($@"{_classesPath}\Applications\{Path.GetFileName(exePath)}", throwOnMissingSubKey: false);

    private void RemoveOpenWith(string extension)
    {
        using (var openWith = _root.OpenSubKey($@"{_classesPath}\{extension}\OpenWithProgids", writable: true))
        {
            if (openWith is null)
            {
                return;
            }

            openWith.DeleteValue(ProgId, throwOnMissingValue: false);
            if (openWith.ValueCount > 0 || openWith.SubKeyCount > 0)
            {
                return; // other apps are listed too
            }
        }

        _root.DeleteSubKey($@"{_classesPath}\{extension}\OpenWithProgids", throwOnMissingSubKey: false);
        bool empty;
        using (var ext = _root.OpenSubKey($@"{_classesPath}\{extension}"))
        {
            empty = ext is { ValueCount: 0, SubKeyCount: 0 };
        }

        if (empty)
        {
            _root.DeleteSubKey($@"{_classesPath}\{extension}", throwOnMissingSubKey: false); // only a key that was left empty
        }
    }

    private static string? Normalise(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        var e = extension.Trim().ToLowerInvariant();
        if (!e.StartsWith('.'))
        {
            e = "." + e;
        }

        return KnownExtensions.Contains(e) ? e : null;
    }

    private void NotifyShell()
    {
        if (_notifyShell)
        {
            SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0 /* SHCNF_IDLIST */, IntPtr.Zero, IntPtr.Zero);
        }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
