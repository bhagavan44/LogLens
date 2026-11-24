using LogLens.Core.Settings;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace LogLens.App.Services;

/// <summary>
/// Applies the theme the user picked (FR-P1) to a window. The XAML content follows <see cref="FrameworkElement.RequestedTheme"/>, but the
/// caption buttons (minimise, maximise, close) drawn by Windows follow the Windows theme, so they are recoloured to match an override.
/// </summary>
public static class ThemeService
{
    public static ElementTheme ToElementTheme(ThemePreference preference) => preference switch
    {
        ThemePreference.Light => ElementTheme.Light,
        ThemePreference.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default, // follow Windows
    };

    public static void Apply(Window window, FrameworkElement root, ThemePreference preference)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(root);
        root.RequestedTheme = ToElementTheme(preference);

        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var bar = window.AppWindow.TitleBar;
        switch (preference)
        {
            case ThemePreference.Light:
                SetCaptionColors(bar, Color.FromArgb(255, 26, 26, 26), Color.FromArgb(24, 0, 0, 0), Color.FromArgb(255, 150, 150, 150));
                break;
            case ThemePreference.Dark:
                SetCaptionColors(bar, Colors.White, Color.FromArgb(24, 255, 255, 255), Color.FromArgb(255, 130, 130, 130));
                break;
            default:
                bar.ButtonForegroundColor = null;
                bar.ButtonHoverForegroundColor = null;
                bar.ButtonPressedForegroundColor = null;
                bar.ButtonInactiveForegroundColor = null;
                bar.ButtonHoverBackgroundColor = null;
                bar.ButtonPressedBackgroundColor = null;
                break;
        }
    }

    private static void SetCaptionColors(AppWindowTitleBar bar, Color foreground, Color hoverBackground, Color inactive)
    {
        bar.ButtonForegroundColor = foreground;
        bar.ButtonHoverForegroundColor = foreground;
        bar.ButtonPressedForegroundColor = foreground;
        bar.ButtonInactiveForegroundColor = inactive;
        bar.ButtonHoverBackgroundColor = hoverBackground;
        bar.ButtonPressedBackgroundColor = hoverBackground;
    }
}
