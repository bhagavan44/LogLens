namespace LogLens.ViewModels.Tests;

public class MainViewModelTests
{
    private const string Path1 = @"C:\logs\app.txt";
    private const string Path2 = @"C:\logs\other.txt";

    [Fact]
    public void Starts_empty()
    {
        var vm = new MainViewModel(new FakeLoader());

        Assert.False(vm.HasLog);
        Assert.True(vm.ShowEmptyState);
        Assert.False(vm.IsLoading);
        Assert.Null(vm.Notice);
        Assert.Equal("LogLens", vm.WindowTitle);
    }

    [Fact]
    public async Task Opening_a_file_shows_it()
    {
        var loader = new FakeLoader();
        var document = TestSupport.Document();
        loader.Returns(Path1, document);
        var vm = new MainViewModel(loader);

        await vm.OpenAsync([Path1]);

        Assert.Same(document, vm.CurrentLog?.Document);
        Assert.False(vm.ShowEmptyState);
        Assert.False(vm.IsLoading);
        Assert.Null(vm.Notice);
        Assert.Equal("app.txt — LogLens", vm.WindowTitle);
    }

    [Fact]
    public async Task While_loading_the_empty_state_is_hidden_and_the_file_is_named()
    {
        var loader = new FakeLoader();
        var pending = loader.Waits(Path1);
        var vm = new MainViewModel(loader);

        var open = vm.OpenAsync([Path1]);

        Assert.True(vm.IsLoading);
        Assert.False(vm.ShowEmptyState);
        Assert.Equal("Opening app.txt…", vm.LoadingText);

        pending.SetResult(TestSupport.Document());
        await open;

        Assert.False(vm.IsLoading);
        Assert.True(vm.HasLog);
    }

    [Fact]
    public async Task Only_the_first_of_several_files_opens_and_a_notice_says_so()
    {
        var loader = new FakeLoader();
        loader.Returns(Path1, TestSupport.Document());
        var vm = new MainViewModel(loader);

        await vm.OpenAsync([Path1, Path2, @"C:\third.txt"]);

        Assert.Equal([Path1], loader.Requested);
        Assert.Equal(NoticeSeverity.Informational, vm.Notice?.Severity);
        Assert.Contains("2 other files were not opened", vm.Notice?.Message);
    }

    [Theory]
    [InlineData(typeof(FileNotFoundException), "File not found")]
    [InlineData(typeof(DirectoryNotFoundException), "File not found")]
    [InlineData(typeof(UnauthorizedAccessException), "Access denied")]
    [InlineData(typeof(IOException), "Could not open app.txt")]
    public async Task Errors_become_a_notice_and_keep_the_current_log(Type exceptionType, string title)
    {
        var loader = new FakeLoader();
        loader.Returns(Path2, TestSupport.Document(path: Path2));
        loader.Throws(Path1, (Exception)Activator.CreateInstance(exceptionType)!);
        var vm = new MainViewModel(loader);
        await vm.OpenAsync([Path2]);

        await vm.OpenAsync([Path1]);

        Assert.Equal(NoticeSeverity.Error, vm.Notice?.Severity);
        Assert.Equal(title, vm.Notice?.Title);
        Assert.Equal(Path2, vm.CurrentLog?.Document.FilePath);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task A_file_where_nothing_parses_gets_a_format_warning()
    {
        var loader = new FakeLoader();
        loader.Returns(Path1, TestSupport.Document("plain text\nnot serilog\n"));
        var vm = new MainViewModel(loader);

        await vm.OpenAsync([Path1]);

        Assert.True(vm.HasLog); // the lines are still shown (FR-F5)
        Assert.Equal(NoticeSeverity.Warning, vm.Notice?.Severity);
        Assert.Equal("0 events parsed — check the format", vm.Notice?.Title);
    }

    [Fact]
    public async Task An_empty_file_says_so()
    {
        var loader = new FakeLoader();
        loader.Returns(Path1, TestSupport.Document(string.Empty));
        var vm = new MainViewModel(loader);

        await vm.OpenAsync([Path1]);

        Assert.True(vm.HasLog);
        Assert.Equal("The file is empty", vm.Notice?.Title);
    }

    [Fact]
    public async Task Cancelling_a_load_keeps_the_previous_log_without_a_notice()
    {
        var loader = new FakeLoader();
        loader.Returns(Path2, TestSupport.Document(path: Path2));
        loader.Waits(Path1);
        var vm = new MainViewModel(loader);
        await vm.OpenAsync([Path2]);

        var open = vm.OpenAsync([Path1]);
        vm.CancelLoad();
        await open;

        Assert.Equal(Path2, vm.CurrentLog?.Document.FilePath);
        Assert.Null(vm.Notice);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task A_newer_open_wins_over_an_older_one()
    {
        var loader = new FakeLoader();
        var slow = loader.Waits(Path1);
        loader.Returns(Path2, TestSupport.Document(path: Path2));
        var vm = new MainViewModel(loader);

        var first = vm.OpenAsync([Path1]);
        await vm.OpenAsync([Path2]);
        slow.TrySetResult(TestSupport.Document(path: Path1));
        await first;

        Assert.Equal(Path2, vm.CurrentLog?.Document.FilePath);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task Closing_returns_to_the_empty_state()
    {
        var loader = new FakeLoader();
        loader.Returns(Path1, TestSupport.Document());
        var vm = new MainViewModel(loader);
        await vm.OpenAsync([Path1]);

        vm.Close();

        Assert.False(vm.HasLog);
        Assert.True(vm.ShowEmptyState);
        Assert.Equal("LogLens", vm.WindowTitle);
    }

    [Fact]
    public async Task Opening_nothing_does_nothing()
    {
        var loader = new FakeLoader();
        var vm = new MainViewModel(loader);

        await vm.OpenAsync([]);

        Assert.Empty(loader.Requested);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public void Unexpected_errors_are_reported_as_a_notice()
    {
        var vm = new MainViewModel(new FakeLoader());

        vm.ReportUnexpectedError(new InvalidOperationException("bug"));

        Assert.Equal(NoticeSeverity.Error, vm.Notice?.Severity);
        Assert.Equal("bug", vm.Notice?.Message);
    }
}
