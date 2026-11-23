using LogLens.Core.State;
using LogLens.Core.Streaming;
using LogLens.Infrastructure.Loading;
using LogLens.Infrastructure.State;

namespace LogLens.Infrastructure.Tests;

/// <summary>Open folder (FR-O3), merge view (FR-O4) and what state.json keeps of them.</summary>
public sealed class MergeAndFolderTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("loglens-tests-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Write(string name, string text)
    {
        var path = Path.Combine(_folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static string Line(int second, string message) => $"2026-11-13 08:00:{second:00}.000 +00:00 [INF] {message}\n";

    // ------------------------------------------------------------------ folder scan

    [Fact]
    public void Lists_the_matching_files_sorted_by_name()
    {
        Write("log20261114.txt", "x");
        Write("log20261113.txt", "x");
        Write("app.LOG", "x");
        Write("notes.md", "x");

        var files = FolderScanner.Enumerate(_folder);

        Assert.Equal(["app.LOG", "log20261113.txt", "log20261114.txt"], files.Select(f => f.Name));
        Assert.All(files, f => Assert.Equal(1, f.Length));
    }

    [Fact]
    public void Patterns_narrow_the_list_and_subfolders_are_included_on_request()
    {
        Write("a.txt", "x");
        Write("b.log", "x");
        Write(Path.Combine("sub", "c.log"), "x");

        Assert.Equal(["b.log"], FolderScanner.Enumerate(_folder, " *.log ; ").Select(f => f.Name));
        Assert.Equal(["b.log", "c.log"], FolderScanner.Enumerate(_folder, "*.log", includeSubfolders: true).Select(f => f.Name));
    }

    [Fact]
    public void Unusable_patterns_fall_back_to_the_defaults()
    {
        Assert.Equal(FolderScanner.DefaultPatterns.Split(';'), FolderScanner.ParsePatterns("  ;, "));
        Assert.Equal(FolderScanner.DefaultPatterns.Split(';'), FolderScanner.ParsePatterns("a|b"));
        Assert.Equal(["*.log", "app-*.txt"], FolderScanner.ParsePatterns("*.log, app-*.txt;*.LOG"));
    }

    [Fact]
    public void A_missing_folder_is_reported()
        => Assert.Throws<DirectoryNotFoundException>(() => FolderScanner.Enumerate(Path.Combine(_folder, "nope")));

    // ------------------------------------------------------------------ merged stream

    [Fact]
    public async Task A_merged_stream_delivers_every_files_events_in_time_order_in_one_batch()
    {
        var a = Write("a.txt", Line(0, "a1") + Line(10, "a2"));
        var b = Write("b.txt", Line(5, "b1"));
        await using var stream = new FileLogStreamFactory().OpenMerged([a, b, a]);

        var batches = new List<StreamBatch>();
        await foreach (var batch in stream.ReadAsync(CancellationToken.None))
        {
            batches.Add(batch);
        }

        var chunk = Assert.IsType<ChunkBatch>(Assert.Single(batches));
        Assert.True(chunk.InitialLoadComplete);
        Assert.Equal(["a1", "b1", "a2"], chunk.Delta.Events.Select(e => e.Message));
        Assert.Equal([a, b], chunk.MergedFiles!.Select(f => f.Path));
        Assert.Equal("UTF-8", chunk.EncodingName);
        Assert.Equal(new FileInfo(a).Length + new FileInfo(b).Length, chunk.FileLength);
    }

    [Fact]
    public async Task A_missing_file_fails_the_merge_and_names_the_file()
    {
        var a = Write("a.txt", Line(0, "a1"));
        var missing = Path.Combine(_folder, "missing.txt");
        await using var stream = new MergedLogStream([a, missing]);

        var ex = await Assert.ThrowsAsync<MergeSourceException>(async () =>
        {
            await foreach (var _ in stream.ReadAsync(CancellationToken.None))
            {
            }
        });

        Assert.Equal(missing, ex.FilePath);
        Assert.IsType<FileNotFoundException>(ex.InnerException);
    }

    [Fact]
    public async Task A_merge_can_be_cancelled()
    {
        var a = Write("a.txt", Line(0, "a1"));
        await using var stream = new MergedLogStream([a, a + "x"]);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in stream.ReadAsync(cancellation.Token))
            {
            }
        });
    }

    [Fact]
    public void Merging_nothing_is_refused() => Assert.Throws<ArgumentException>(() => new MergedLogStream([" "]));

    // ------------------------------------------------------------------ state.json

    [Fact]
    public void Bookmarks_and_merged_tabs_persist_through_the_real_store()
    {
        var paths = new AppPaths(_folder, _folder);
        var store = new JsonStateStore(paths);
        new BookmarkService(store).Toggle(new Bookmark { File = @"C:\a.txt", Offset = 42, Timestamp = DateTimeOffset.UnixEpoch, Note = "n" });
        new SessionService(store).Save(new SessionState { Tabs = [new SessionTab { MergedFiles = [@"C:\a.txt", @"C:\b.txt"] }] });

        var reloaded = new JsonStateStore(paths).Load();

        var bookmark = Assert.Single(reloaded.Bookmarks);
        Assert.Equal((@"C:\a.txt", 42L, "n"), (bookmark.File, bookmark.Offset, bookmark.Note));
        Assert.Equal(DateTimeOffset.UnixEpoch, bookmark.Timestamp);
        Assert.Equal([@"C:\a.txt", @"C:\b.txt"], Assert.Single(reloaded.Session.Tabs).MergedFiles);
    }

    [Fact]
    public void A_state_file_without_bookmarks_loads_an_empty_list()
    {
        var paths = new AppPaths(_folder, _folder);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.StateFile)!);
        File.WriteAllText(paths.StateFile, """{ "version": 1, "bookmarks": null }""");

        Assert.Empty(new JsonStateStore(paths).Load().Bookmarks);
    }
}
