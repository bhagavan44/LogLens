using LogLens.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LogLens.App;

/// <summary>
/// First-run hints (UX-7): two <see cref="TeachingTip"/>s shown one after the other the first time a log is open, once per PC
/// (<see cref="Core.Settings.AppSettings.HintsSeen"/>). Only the LogLens that owns the profile shows them; Help ▸ Show the first-run hints
/// again brings them back.
/// </summary>
public sealed partial class MainWindow
{
    private bool _hintsQueued;

    /// <summary>Called when the log in front changes: shows the hints once its view is laid out, if they are still due.</summary>
    private void QueueFirstRunHints(bool force = false)
    {
        if (_hintsQueued || CurrentView is null || (!force && (!ViewModel.RemembersSession || _settings.Current.HintsSeen)))
        {
            return;
        }

        _hintsQueued = true;
        GuardedSynchronizationContext.Enqueue(DispatcherQueue, ShowFirstRunHints);
    }

    private void ShowFirstRunHints()
    {
        if (CurrentView is not { } view)
        {
            _hintsQueued = false;
            return;
        }

        var target = view.FilterTarget;
        if (!target.IsLoaded)
        {
            RoutedEventHandler? onLoaded = null;
            onLoaded = (_, _) =>
            {
                target.Loaded -= onLoaded;
                GuardedSynchronizationContext.Enqueue(DispatcherQueue, ShowFirstRunHints);
            };
            target.Loaded += onLoaded;
            return;
        }

        _hintsQueued = false;
        FilterTip.Target = target;
        FilterTip.IsOpen = true;

        // Seen from the moment they show, so a crash or a quick close does not bring them back.
        _settings.Update(s => s with { HintsSeen = true });
    }

    private void OnFilterTipNext(TeachingTip sender, object args)
    {
        FilterTip.IsOpen = false;
        TabsTip.Target = Tabs;
        TabsTip.IsOpen = true;
    }

    private void OnShowHints(object sender, RoutedEventArgs e)
    {
        if (CurrentView is null)
        {
            ViewModel.Notice = new ViewModels.Notice(ViewModels.NoticeSeverity.Informational, "Open a log first", "The hints point at parts of an open log.");
            return;
        }

        QueueFirstRunHints(force: true);
    }
}
