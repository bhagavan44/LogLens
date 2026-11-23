using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using LogLens.Core.Filtering;
using LogLens.Core.Models;

namespace LogLens.ViewModels;

/// <summary>
/// The find bar (FR-S3): matching rows of the current list, "n of m", F3 / Shift+F3 and "only matches" with context; plus Go to (FR-G7) and
/// next / previous warning or error (FR-N3).
/// </summary>
public sealed partial class LogViewModel
{
    public const int MaxContextEvents = 100;

    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(250);

    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _searchDebounceCancellation;
    private Task _lastScheduledSearch = Task.CompletedTask;
    private int[] _matches = [];
    private int _activeRow = -1;
    private bool _onlyMatchesApplied;

    [ObservableProperty]
    public partial bool FindBarVisible { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool MatchCase { get; set; }

    [ObservableProperty]
    public partial bool WholeWord { get; set; }

    [ObservableProperty]
    public partial bool UseRegex { get; set; }

    [ObservableProperty]
    public partial bool IncludeException { get; set; } = true;

    /// <summary>Narrow the list to the events that match instead of just highlighting them.</summary>
    [ObservableProperty]
    public partial bool OnlyMatches { get; set; }

    /// <summary>With <see cref="OnlyMatches"/>: also show this many events before and after each match (0 … <see cref="MaxContextEvents"/>).</summary>
    [ObservableProperty]
    public partial int ContextEvents { get; set; }

    /// <summary>Why the find text can't be used (an invalid regular expression); null when it is fine.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchError))]
    public partial string? SearchError { get; private set; }

    public bool HasSearchError => SearchError is not null;

    /// <summary>"3 of 120", "120 matches" or "No matches"; empty while nothing is searched.</summary>
    [ObservableProperty]
    public partial string MatchSummary { get; private set; } = string.Empty;

    /// <summary>The compiled find text, or null when the bar is closed, empty, or the pattern is invalid.</summary>
    public SearchMatcher? CurrentMatcher { get; private set; }

    /// <summary>For the grid: the ranges of a row's text to highlight; null for no highlighting.</summary>
    public Func<string, IReadOnlyList<(int Start, int Length)>>? Highlighter => CurrentMatcher is { } m ? m.FindRanges : null;

    public int MatchCount => _matches.Length;

    /// <summary>Raised when the matches changed, so the visible rows are repainted with new highlights.</summary>
    public event EventHandler? SearchChanged;

    partial void OnSearchTextChanged(string value) => ScheduleSearch(SearchDebounce);

    partial void OnMatchCaseChanged(bool value) => ScheduleSearch(TimeSpan.Zero);

    partial void OnWholeWordChanged(bool value) => ScheduleSearch(TimeSpan.Zero);

    partial void OnUseRegexChanged(bool value) => ScheduleSearch(TimeSpan.Zero);

    partial void OnIncludeExceptionChanged(bool value) => ScheduleSearch(TimeSpan.Zero);

    partial void OnOnlyMatchesChanged(bool value) => ScheduleSearch(TimeSpan.Zero);

    partial void OnContextEventsChanged(int value)
    {
        if (value is < 0 or > MaxContextEvents)
        {
            ContextEvents = Math.Clamp(value, 0, MaxContextEvents);
            return;
        }

        if (OnlyMatches)
        {
            ScheduleSearch(TimeSpan.Zero);
        }
    }

    partial void OnFindBarVisibleChanged(bool value)
    {
        if (!value)
        {
            OnlyMatches = false; // closing the bar ends the search: no highlights, no "only matches"
        }

        ScheduleSearch(TimeSpan.Zero);
    }

    /// <summary>
    /// "Find all events containing" (FR-N4), for following an order number or request id through a log: lists only the events whose message or
    /// exception contains <paramref name="text"/> (plain text, any case), with <paramref name="contextEvents"/> events around each. In a merged
    /// view that covers every file.
    /// </summary>
    /// <param name="clearOtherFilters">Drop the level, time and query filters first, so nothing related stays hidden.</param>
    public void FindAllContaining(string text, int contextEvents = 0, bool clearOtherFilters = true)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (clearOtherFilters)
        {
            ClearFilters();
        }

        MatchCase = false;
        WholeWord = false;
        UseRegex = false;
        IncludeException = true;
        ContextEvents = Math.Clamp(contextEvents, 0, MaxContextEvents);
        SearchText = text;
        FindBarVisible = true;
        OnlyMatches = true;
        ScheduleSearch(TimeSpan.Zero); // now, not after the typing pause the new text started
    }

    private void ScheduleSearch(TimeSpan delay)
    {
        _searchDebounceCancellation?.Cancel();
        var cancellation = _searchDebounceCancellation = new CancellationTokenSource();
        _lastScheduledSearch = RunScheduledSearchAsync(delay, cancellation.Token);
    }

    private async Task RunScheduledSearchAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, token);
            }

            RebuildMatcher();

            // "Only matches" changes which events are listed; otherwise only the matches within the current list are recounted.
            if (OnlyMatches || _onlyMatchesApplied)
            {
                await ApplyFiltersAsync();
            }
            else
            {
                await RecomputeMatchesAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer change
        }
    }

    private void RebuildMatcher()
    {
        if (!FindBarVisible || string.IsNullOrEmpty(SearchText))
        {
            CurrentMatcher = null;
            SearchError = null;
            return;
        }

        CurrentMatcher = new SearchSpec(SearchText, MatchCase, WholeWord, UseRegex, IncludeException).TryCompile(out var error);
        SearchError = error;
    }

    /// <summary>Finds the matching rows of the current list on a worker thread, then refreshes "n of m" and the highlights.</summary>
    private async Task RecomputeMatchesAsync()
    {
        _searchCancellation?.Cancel();
        var cancellation = _searchCancellation = new CancellationTokenSource();
        var matcher = CurrentMatcher;
        _onlyMatchesApplied = OnlyMatches && matcher is not null;

        if (matcher is null)
        {
            _matches = [];
        }
        else
        {
            BeginPass(); // the list must not grow under the worker
            try
            {
                var view = View;
                var matches = await Task.Run(() => view.FindRows(matcher.IsMatch, cancellation.Token), cancellation.Token);
                if (cancellation.IsCancellationRequested)
                {
                    return;
                }

                _matches = matches;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return; // a newer search (started after a newer filter pass) replaced this one
            }
            finally
            {
                EndPass();
            }
        }

        UpdateSummary();
        SearchChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Rows changed at the end of the list (new events, or the last event extended): drop the matches from <paramref name="firstRow"/> on and
    /// test the rows there now, so "n of m" and F3 stay right without searching the whole list again.
    /// </summary>
    private void UpdateMatchesAfterSync(int firstRow, int rowsRemoved, int rowsAdded)
    {
        if (CurrentMatcher is not { } matcher || (rowsRemoved == 0 && rowsAdded == 0))
        {
            return;
        }

        var keep = _matches.Length;
        while (keep > 0 && _matches[keep - 1] >= firstRow)
        {
            keep--;
        }

        List<int>? extra = null;
        for (var row = firstRow; row < firstRow + rowsAdded && row < View.Count; row++)
        {
            if (matcher.IsMatch(View[row]))
            {
                (extra ??= []).Add(row);
            }
        }

        if (keep == _matches.Length && extra is null)
        {
            return;
        }

        _matches = [.. _matches.AsSpan(0, keep), .. (extra ?? [])];
        UpdateSummary();
    }

    /// <summary>The row to move to for F3 (<paramref name="forward"/>) or Shift+F3, wrapping around; -1 when there are no matches.</summary>
    public int StepMatch(int fromRow, bool forward) => MatchNavigator.Step(_matches, fromRow, forward);

    /// <summary>Tells the search which row is active, so the summary can say "3 of 120".</summary>
    public void UpdateActiveRow(int row)
    {
        _activeRow = row;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (CurrentMatcher is null)
        {
            MatchSummary = string.Empty;
            return;
        }

        if (_matches.Length == 0)
        {
            MatchSummary = "No matches";
            return;
        }

        var position = MatchNavigator.PositionOf(_matches, _activeRow);
        MatchSummary = position > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{position:N0} of {_matches.Length:N0}")
            : string.Create(CultureInfo.InvariantCulture, $"{_matches.Length:N0} {(_matches.Length == 1 ? "match" : "matches")}");
    }

    // ------------------------------------------------------------------ navigation

    /// <summary>The next (or previous) warning, error or fatal event after row <paramref name="fromRow"/>, wrapping around; -1 when there is none (FR-N3).</summary>
    public int NextProblemRow(int fromRow, bool forward) => RowFinder.Step(View, fromRow, forward, RowFinder.IsProblem);

    /// <summary>Resolves the Go to box (FR-G7): the row to reveal in the current list, or a message saying why there is none.</summary>
    public (int Row, string? Error) ResolveGoTo(string text)
    {
        if (!GoTo.TryParse(text, FirstTimestamp(), out var target, out var error))
        {
            return (-1, error);
        }

        switch (target)
        {
            case GoToTarget.EventNumber n:
                if (n.Number > Document.Events.Count)
                {
                    return (-1, string.Create(CultureInfo.InvariantCulture, $"The file only has {Document.Events.Count:N0} events."));
                }

                var row = View.IndexOf(Document.Events[n.Number - 1]);
                return row >= 0 ? (row, null) : (-1, string.Create(CultureInfo.InvariantCulture, $"Event {n.Number:N0} is hidden by the current filters."));

            case GoToTarget.Time t:
                var at = GoTo.IndexAtOrAfter(View, t.Value);
                return at >= 0 ? (at, null) : (-1, "No event with a timestamp is shown.");

            default:
                return (-1, "Type an event number or a time.");
        }
    }
}
