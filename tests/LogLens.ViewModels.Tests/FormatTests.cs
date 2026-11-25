using LogLens.Core.Formats;
using LogLens.Core.Models;
using LogLens.Core.State;

namespace LogLens.ViewModels.Tests;

/// <summary>The detected format (FR-F1), the format override, and the properties pane with its filters (FR-D5).</summary>
public class FormatTests
{
    private const string ClefPath = @"C:\logs\app.clef";

    private const string Clef = """
        {"@t":"2026-11-13T04:45:32Z","@mt":"Order {Id} shipped","Id":1,"Customer":"Ann","SourceContext":"Shop.Orders"}
        {"@t":"2026-11-13T04:45:33Z","@mt":"Order {Id} shipped","Id":2,"Customer":"Bob","SourceContext":"Shop.Orders"}
        {"@t":"2026-11-13T04:45:34Z","@mt":"Payment failed","@l":"Error","SourceContext":"Shop.Payments"}
        {"@t":"2026-11-13T04:45:35Z","@m":"No properties at all"}

        """;

    private static string ClefText => Clef.ReplaceLineEndings("\n");

    [Fact]
    public Task The_log_takes_the_format_its_stream_detected() => UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        streams.Returns(ClefPath, ClefText, detected: LogFormats.Clef);
        var vm = TestSupport.Main(streams);
        var raised = 0;

        await vm.OpenAsync([ClefPath]);
        var log = vm.CurrentLog!;
        log.FormatChanged += (_, _) => raised++;

        Assert.Same(LogFormats.Clef, log.Format);
        Assert.Same(LogFormats.Clef, log.Document.Format);
        Assert.True(log.HasStructuredFields);
        Assert.True(log.HasSourceContext);
        Assert.Null(log.ChosenFormat);
        Assert.Contains("Serilog CLEF", log.StatusText);
        Assert.Null(streams.RequestedFormats.Single()); // detect
        Assert.Equal(0, raised); // already known when the tab showed
    });

    [Fact]
    public void A_batch_naming_another_format_updates_the_log_and_says_so()
    {
        var stream = new FakeStream(ClefPath, LogFormats.Clef);
        var log = new LogViewModel(new LogDocument(ClefPath, new AutoDetectFormat(FormatCatalog.BuiltIn)));
        var changed = new List<string?>();
        var raised = 0;
        log.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        log.FormatChanged += (_, _) => raised++;

        Assert.False(log.HasStructuredFields);
        stream.Load(ClefText);
        _ = log.RunAsync(stream);

        Assert.Same(LogFormats.Clef, log.Format);
        Assert.Equal(1, raised);
        Assert.Contains(nameof(LogViewModel.HasStructuredFields), changed);
        Assert.Contains(nameof(LogViewModel.HasSourceContext), changed);
        log.Dispose();
    }

    [Fact]
    public void Plain_text_logs_have_no_structured_fields()
    {
        var log = new LogViewModel(TestSupport.Document());
        log.SelectedEvent = log.Events[0];

        Assert.False(log.HasStructuredFields);
        Assert.False(log.HasSourceContext);
        Assert.False(log.HasProperties);
        Assert.False(log.HasMessageTemplate);
        Assert.Empty(log.SelectedProperties);
        Assert.Equal("This event has no properties.", log.NoPropertiesText);
    }

    [Fact]
    public Task The_detail_pane_shows_the_template_and_properties() => UiThread.Run(async () =>
    {
        var log = await OpenClefAsync();
        var changed = new List<string?>();
        log.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        log.SelectedEvent = log.Events[0];

        Assert.True(log.HasMessageTemplate);
        Assert.Equal("Order {Id} shipped", log.MessageTemplateText);
        Assert.True(log.HasProperties);
        Assert.Equal(["Id", "Customer", "SourceContext"], log.SelectedProperties.Select(p => p.Name));
        Assert.Equal(["1", "Ann", "Shop.Orders"], log.SelectedProperties.Select(p => p.ValueText));
        Assert.Contains(nameof(LogViewModel.SelectedProperties), changed);
        Assert.Contains(nameof(LogViewModel.MessageTemplateText), changed);

        log.SelectedEvent = log.Events[3];
        Assert.False(log.HasProperties);
        Assert.False(log.HasMessageTemplate);
    });

    [Fact]
    public Task Filter_to_and_exclude_a_property_value() => UiThread.Run(async () =>
    {
        var log = await OpenClefAsync();
        var customer = new PropertyRow("Customer", new LogValue("Ann"));

        log.FilterToProperty(customer, exclude: false);
        await log.WhenSettledAsync();

        Assert.Equal("Customer = 'Ann'", log.QueryText);
        Assert.Equal([0], log.Events.Select(e => e.Id));

        log.QueryText = string.Empty;
        log.FilterToProperty(customer, exclude: true);
        await log.WhenSettledAsync();

        Assert.Equal("not (Customer = 'Ann')", log.QueryText);
        Assert.Equal([1, 2, 3], log.Events.Select(e => e.Id)); // events without the property stay

        log.FilterToProperty(new PropertyRow("SourceContext", new LogValue("Shop.Payments")), exclude: false);
        await log.WhenSettledAsync();

        Assert.Equal("not (Customer = 'Ann') and SourceContext = 'Shop.Payments'", log.QueryText);
        Assert.Equal([2], log.Events.Select(e => e.Id));
        Assert.Null(log.QueryError);
    });

    [Fact]
    public Task Reopening_with_another_format_replaces_the_tab_in_place_and_is_remembered() => UiThread.Run(async () =>
    {
        var store = new MemoryStateStore();
        var streams = new FakeStreams();
        streams.Returns(@"C:\logs\a.txt");
        streams.Returns(ClefPath, ClefText, detected: LogFormats.Clef);
        streams.Returns(@"C:\logs\c.txt");
        var vm = TestSupport.Main(streams, store);
        await vm.OpenAsync([@"C:\logs\a.txt", ClefPath, @"C:\logs\c.txt"]);
        var old = vm.Tabs[1];

        await vm.ReopenWithFormatAsync(old, LogFormats.SerilogText);

        Assert.Equal(3, vm.Tabs.Count);
        var reopened = vm.Tabs[1];
        Assert.NotSame(old, reopened);
        Assert.Same(reopened, vm.CurrentLog);
        Assert.Same(LogFormats.SerilogText, reopened.ChosenFormat);
        Assert.Same(LogFormats.SerilogText, reopened.Format);
        Assert.Equal(ClefPath, reopened.Document.FilePath);
        Assert.Same(LogFormats.SerilogText, streams.RequestedFormats[^1]);
        Assert.Equal(reopened.Document.Events.Count, reopened.Document.UnparsedCount); // JSON read as text
        Assert.Equal([null, "serilog-text", null], store.State.Session.Tabs.Select(t => t.Format));

        await vm.ReopenWithFormatAsync(reopened, null); // back to detection
        Assert.Null(vm.Tabs[1].ChosenFormat);
        Assert.Same(LogFormats.Clef, vm.Tabs[1].Format);
        Assert.Equal([null, null, null], store.State.Session.Tabs.Select(t => t.Format));
    });

    [Fact]
    public Task Reopening_with_the_same_choice_does_nothing() => UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        streams.Returns(ClefPath, ClefText, detected: LogFormats.Clef);
        var vm = TestSupport.Main(streams);
        await vm.OpenAsync([ClefPath]);
        var log = vm.CurrentLog!;

        await vm.ReopenWithFormatAsync(log, null);

        Assert.Same(log, vm.CurrentLog);
        Assert.Single(streams.Requested);
    });

    [Fact]
    public Task A_restored_tab_is_read_with_the_format_chosen_for_it() => UiThread.Run(async () =>
    {
        var store = new MemoryStateStore
        {
            State = new AppState
            {
                Session = new SessionState
                {
                    Tabs =
                    [
                        new SessionTab { Path = ClefPath, Format = "clef" },
                        new SessionTab { Path = @"C:\logs\a.txt", Format = "template:Deleted" },
                    ],
                },
            },
        };
        var streams = new FakeStreams();
        streams.Returns(ClefPath, ClefText);
        streams.Returns(@"C:\logs\a.txt");
        var vm = TestSupport.Main(streams, store);

        await vm.RestoreSessionAsync();

        Assert.Equal([LogFormats.Clef, null], streams.RequestedFormats);
        Assert.Same(LogFormats.Clef, vm.Tabs[0].ChosenFormat);
        Assert.Null(vm.Tabs[1].ChosenFormat); // a custom template that is gone is detected instead
    });

    [Fact]
    public void The_window_offers_every_format_of_the_catalog()
    {
        var store = new MemoryStateStore();
        var catalog = new FormatCatalog([new CustomTemplate("Shop", "{Timestamp:HH:mm:ss} {Level:u3} {Message}")]);
        var vm = new MainViewModel(new FakeStreams(), new RecentFilesService(store), new SessionService(store), new BookmarkService(store), () => catalog);

        Assert.Equal(["clef", "serilog-json", "template:Shop", "nlog", "log4net", "serilog-text"], vm.AvailableFormats.Select(f => f.Id));
        Assert.Equal(["clef", "serilog-json", "nlog", "log4net", "serilog-text"], TestSupport.Main(new FakeStreams()).AvailableFormats.Select(f => f.Id));
    }

    [Fact]
    public Task The_zero_events_notice_points_at_the_format_choice() => UiThread.Run(async () =>
    {
        var streams = new FakeStreams();
        streams.Returns(@"C:\logs\odd.txt", "just some text\nand more\n");
        var vm = TestSupport.Main(streams);

        await vm.OpenAsync([@"C:\logs\odd.txt"]);

        Assert.Equal("0 events parsed — check the format", vm.Notice?.Title);
        Assert.Contains("View ▸ Format", vm.Notice?.Message);
    });

    private static async Task<LogViewModel> OpenClefAsync()
    {
        var streams = new FakeStreams();
        streams.Returns(ClefPath, ClefText, detected: LogFormats.Clef);
        var vm = TestSupport.Main(streams);
        await vm.OpenAsync([ClefPath]);
        return vm.CurrentLog!;
    }
}
