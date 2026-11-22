using LogLens.Core.Models;

namespace LogLens.ViewModels.Tests;

public class LogViewModelTests
{
    [Fact]
    public void Without_a_selection_the_detail_pane_asks_for_one()
    {
        var vm = new LogViewModel(TestSupport.Document());

        Assert.False(vm.HasSelection);
        Assert.Equal("Select an event to see its details", vm.DetailHeader);
        Assert.Equal(string.Empty, vm.MessageText);
        Assert.Equal(string.Empty, vm.ExceptionText);
        Assert.Equal(string.Empty, vm.RawText);
        Assert.Equal(string.Empty, vm.MetadataText);
        Assert.Equal(3, vm.Events.Count);
    }

    [Fact]
    public void Selecting_an_event_fills_message_exception_raw_and_metadata()
    {
        var vm = new LogViewModel(TestSupport.Document());

        vm.SelectedEvent = vm.Events[1];

        Assert.Equal("Error  ·  2026-11-13 08:00:05.301 +05:30", vm.DetailHeader);
        Assert.Equal("Payment failed", vm.MessageText);
        Assert.True(vm.HasException);
        Assert.Equal("System.InvalidOperationException: boom\n   at A.B()", vm.ExceptionText);
        Assert.Equal("2026-11-13 08:00:05.301 +05:30 [ERR] Payment failed\nSystem.InvalidOperationException: boom\n   at A.B()", vm.RawText);
        Assert.Equal("app.txt  ·  line 2  ·  event 2 of 3", vm.MetadataText);
    }

    [Fact]
    public void An_event_without_an_exception_says_so()
    {
        var vm = new LogViewModel(TestSupport.Document()) { SelectedEvent = null };

        vm.SelectedEvent = vm.Events[2];

        Assert.False(vm.HasException);
        Assert.Equal("No exception was logged with this event.", vm.ExceptionText);
        Assert.Equal("Message with an embedded newline:\nsecond line", vm.MessageText);
    }

    [Fact]
    public void Changing_the_selection_notifies_every_detail_property()
    {
        var vm = new LogViewModel(TestSupport.Document());
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SelectedEvent = vm.Events[0];

        foreach (var name in new[] { nameof(vm.SelectedEvent), nameof(vm.HasSelection), nameof(vm.DetailHeader), nameof(vm.MessageText), nameof(vm.HasException), nameof(vm.ExceptionText), nameof(vm.RawText), nameof(vm.MetadataText) })
        {
            Assert.Contains(name, changed);
        }
    }

    [Fact]
    public void Status_bar_counts_events_and_names_format_and_encoding()
    {
        var vm = new LogViewModel(TestSupport.Document());

        Assert.Equal("3 events  ·  1 error  ·  1 warning  ·  Serilog text  ·  UTF-8  ·  2.0 KB", vm.StatusText);
    }

    [Fact]
    public void Status_bar_mentions_unparsed_lines()
    {
        var vm = new LogViewModel(TestSupport.Document("stray\n" + TestSupport.Text));

        Assert.Contains("1 unparsed line", vm.StatusText);
        Assert.StartsWith("4 events", vm.StatusText);
    }

    [Fact]
    public void Copy_as_text_is_tab_separated_with_the_exception()
    {
        var vm = new LogViewModel(TestSupport.Document());

        var text = vm.BuildCopyText(vm.Events.Take(2), raw: false);

        Assert.Equal(
            "2026-11-13 08:00:00.012\tINF\tApplication starting up\r\n2026-11-13 08:00:05.301\tERR\tPayment failed\r\nSystem.InvalidOperationException: boom\n   at A.B()",
            text);
    }

    [Fact]
    public void Copy_raw_gives_the_original_lines()
    {
        var vm = new LogViewModel(TestSupport.Document());

        var text = vm.BuildCopyText([vm.Events[2]], raw: true);

        Assert.Equal("2026-11-13 08:00:06.177 +05:30 [WRN] Message with an embedded newline:\nsecond line", text);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(30L * 1024 * 1024, "30.0 MB")]
    public void Sizes_are_human_readable(long bytes, string expected) => Assert.Equal(expected, LogViewModel.FormatSize(bytes));

    [Fact]
    public void Unparsed_events_show_without_a_timestamp()
    {
        var vm = new LogViewModel(TestSupport.Document("not a log line\n"));

        vm.SelectedEvent = vm.Events[0];

        Assert.Equal(LogLevel.Unparsed, vm.SelectedEvent.Level);
        Assert.Equal("Unparsed  ·  no timestamp", vm.DetailHeader);
        Assert.Equal("not a log line", vm.RawText);
    }
}
