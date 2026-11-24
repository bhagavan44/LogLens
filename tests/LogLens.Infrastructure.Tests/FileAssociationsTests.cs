using System.Runtime.Versioning;
using LogLens.Infrastructure.Shell;
using Microsoft.Win32;

namespace LogLens.Infrastructure.Tests;

/// <summary>
/// Per-user Open with registration (FR-P5). Each test registers under its own key in <c>HKCU\Software\LogLensTests</c> instead of the real
/// <c>Software\Classes</c>, and deletes it afterwards.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FileAssociationsTests : IDisposable
{
    private const string Exe = @"C:\Tools\LogLens-1.0.0-win-x64.exe";

    private readonly string _testRoot = $@"Software\LogLensTests\{Guid.NewGuid():N}";

    private FileAssociations NewAssociations() => new(Registry.CurrentUser, _testRoot + @"\Classes");

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_testRoot, throwOnMissingSubKey: false);

    private RegistryKey? Open(string relative) => Registry.CurrentUser.OpenSubKey($@"{_testRoot}\Classes\{relative}");

    [Fact]
    public void Nothing_is_registered_at_first()
    {
        var associations = NewAssociations();

        Assert.Empty(associations.RegisteredExtensions());
        Assert.Equal(AssociationStatus.NotRegistered, associations.StatusFor(Exe));
        Assert.Null(associations.RegisteredExePath());
    }

    [Fact]
    public void Register_adds_an_open_with_entry_and_a_command_that_runs_this_exe()
    {
        var associations = NewAssociations();

        associations.Register(Exe, [".log", "clef"]);

        Assert.Equal([".log", ".clef"], associations.RegisteredExtensions());
        Assert.Equal(AssociationStatus.Registered, associations.StatusFor(Exe));
        Assert.Equal(Exe, associations.RegisteredExePath());
        using var command = Open($@"{FileAssociations.ProgId}\shell\open\command");
        Assert.Equal($"\"{Exe}\" \"%1\"", command!.GetValue(string.Empty));
        using var types = Open(@"Applications\LogLens-1.0.0-win-x64.exe\SupportedTypes");
        Assert.Equal([".clef", ".log"], types!.GetValueNames().Order());
    }

    [Fact]
    public void The_extensions_default_program_is_never_changed()
    {
        using (var ext = Registry.CurrentUser.CreateSubKey($@"{_testRoot}\Classes\.log"))
        {
            ext.SetValue(string.Empty, "SomeEditor.log");
        }

        var associations = NewAssociations();
        associations.Register(Exe, [".log"]);
        associations.Unregister(Exe);

        using var after = Open(".log");
        Assert.Equal("SomeEditor.log", after!.GetValue(string.Empty));
    }

    [Fact]
    public void Registering_fewer_extensions_removes_the_others()
    {
        var associations = NewAssociations();
        associations.Register(Exe, [".log", ".clef", ".txt"]);

        associations.Register(Exe, [".log"]);

        Assert.Equal([".log"], associations.RegisteredExtensions());
        Assert.Null(Open(".txt")); // a key LogLens created and left empty is removed
    }

    [Fact]
    public void Unregister_removes_everything_but_other_apps_entries()
    {
        using (var other = Registry.CurrentUser.CreateSubKey($@"{_testRoot}\Classes\.log\OpenWithProgids"))
        {
            other.SetValue("Notepad++.log", Array.Empty<byte>(), RegistryValueKind.None);
        }

        var associations = NewAssociations();
        associations.Register(Exe, [".log", ".clef"]);
        associations.Unregister(Exe);

        Assert.Empty(associations.RegisteredExtensions());
        Assert.Null(Open(FileAssociations.ProgId));
        Assert.Null(Open(@"Applications\LogLens-1.0.0-win-x64.exe"));
        Assert.Null(Open(".clef"));
        using var log = Open(@".log\OpenWithProgids");
        Assert.Equal(["Notepad++.log"], log!.GetValueNames());
    }

    [Fact]
    public void A_moved_copy_is_reported_as_registered_elsewhere()
    {
        var associations = NewAssociations();
        associations.Register(@"D:\Old\LogLens.exe", [".log"]);

        Assert.Equal(AssociationStatus.RegisteredElsewhere, associations.StatusFor(Exe));
        Assert.Equal(@"D:\Old\LogLens.exe", associations.RegisteredExePath());
    }

    [Fact]
    public void Registering_a_new_version_removes_the_old_exes_entry()
    {
        var associations = NewAssociations();
        associations.Register(@"C:\Tools\LogLens-0.9.0-win-x64.exe", [".log"]);

        associations.Register(Exe, [".log"]);

        Assert.Null(Open(@"Applications\LogLens-0.9.0-win-x64.exe"));
        Assert.NotNull(Open(@"Applications\LogLens-1.0.0-win-x64.exe"));
        Assert.Equal(AssociationStatus.Registered, associations.StatusFor(Exe));
    }

    [Fact]
    public void Unregister_also_removes_a_copy_registered_from_elsewhere()
    {
        var associations = NewAssociations();
        associations.Register(@"D:\Old\LogLens-0.9.0-win-x64.exe", [".log"]);

        associations.Unregister(Exe);

        Assert.Null(Open(@"Applications\LogLens-0.9.0-win-x64.exe"));
        Assert.Equal(AssociationStatus.NotRegistered, associations.StatusFor(Exe));
    }

    [Fact]
    public void Unknown_extensions_are_ignored() // only the types LogLens reads may be claimed
    {
        var associations = NewAssociations();

        associations.Register(Exe, [".exe", ".log", " "]);

        Assert.Equal([".log"], associations.RegisteredExtensions());
        Assert.Null(Open(".exe"));
    }

    [Fact]
    public void Unregister_is_safe_when_nothing_is_registered() => NewAssociations().Unregister(Exe);

    [Fact]
    public void Only_log_is_ticked_until_the_user_chooses()
        => Assert.Equal([".log"], FileAssociations.DefaultExtensions);
}
