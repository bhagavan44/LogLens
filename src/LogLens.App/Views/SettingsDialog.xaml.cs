using System.Diagnostics;
using System.Globalization;
using LogLens.Core.Filtering;
using LogLens.Core.Formats;
using LogLens.Core.Formats.SerilogText;
using LogLens.Core.Models;
using LogLens.Core.Settings;
using LogLens.Infrastructure;
using LogLens.Infrastructure.Shell;
using LogLens.Infrastructure.State;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace LogLens.App.Views;

/// <summary>
/// Settings (FR-P1 … FR-P6, FR-S6, FR-F2): appearance, timestamps, start-up, custom output templates, hide rules, <i>Open with</i> and settings
/// export / import. Nothing is
/// applied until Save, except <i>Open with</i>, which changes the registry at once (it has its own Register / Remove buttons).
/// </summary>
public sealed partial class SettingsDialog : ContentDialog
{
    private static readonly string[] SuggestedFonts = [AppSettings.DefaultFontFamily, "Segoe UI", "Cascadia Mono", "Cascadia Code", "Consolas", "Lucida Console", "Courier New"];

    private readonly AppPaths _paths;
    private readonly IntPtr _hwnd;
    private readonly List<RuleRow> _rules = [];
    private readonly List<TemplateRow> _templates = [];
    private readonly FileAssociations _associations = new();
    private AppSettings _base;

    /// <param name="current">The settings in force.</param>
    /// <param name="paths">Where settings and diagnostics live (shown and opened from the dialog).</param>
    /// <param name="hwnd">The window, for the file pickers.</param>
    public SettingsDialog(AppSettings current, AppPaths paths, IntPtr hwnd)
    {
        ArgumentNullException.ThrowIfNull(current);
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _hwnd = hwnd;
        _base = current;
        InitializeComponent();

        foreach (var font in SuggestedFonts)
        {
            FontBox.Items.Add(font);
        }

        FontSizeBox.Minimum = AppSettings.MinFontSize;
        FontSizeBox.Maximum = AppSettings.MaxFontSize;
        DataDirText.Text = $"Settings: {_paths.SettingsFile}\nDiagnostics: {_paths.LogDirectory}";
        Load(current);
        RefreshAssociations();
        PrimaryButtonClick += OnSaveClick;
    }

    /// <summary>The settings as edited; valid once the dialog closed with Save.</summary>
    public AppSettings Result { get; private set; } = new();

    // ------------------------------------------------------------------ load / build

    private void Load(AppSettings s)
    {
        ThemeBox.SelectedIndex = (int)s.Theme;
        DensityButtons.SelectedIndex = s.RowDensity == RowDensity.Compact ? 1 : 0;
        FontBox.SelectedItem = SuggestedFonts.FirstOrDefault(f => string.Equals(f, s.FontFamily, StringComparison.OrdinalIgnoreCase));
        FontBox.Text = s.FontFamily;
        FontSizeBox.Value = s.FontSize;
        ZoneBox.SelectedIndex = (int)s.TimestampZone;
        FormatBox.Text = s.TimestampFormat;
        RestoreSessionSwitch.IsOn = s.RestoreSession;
        TailSwitch.IsOn = s.TailByDefault;

        RulesPanel.Children.Clear();
        _rules.Clear();
        foreach (var rule in s.HideRules)
        {
            AddRuleRow(rule);
        }

        UpdateRulesHint();

        TemplatesPanel.Children.Clear();
        _templates.Clear();
        foreach (var template in s.CustomTemplates)
        {
            AddTemplateRow(template);
        }

        UpdateTemplatesHint();
        ValidateTemplates();
        UpdateFormatPreview();
        SavedFiltersText.Text = s.SavedFilters.Count == 0
            ? "None yet. Save the query and levels of a log from its Saved menu, then pick them again in any log."
            : $"{string.Join(", ", s.SavedFilters.Select(f => f.Name))}. Pick or delete them from the Saved menu of a log.";
    }

    private AppSettings Build() => _base with
    {
        Theme = (ThemePreference)Math.Max(0, ThemeBox.SelectedIndex),
        RowDensity = DensityButtons.SelectedIndex == 1 ? RowDensity.Compact : RowDensity.Comfortable,
        FontFamily = string.IsNullOrWhiteSpace(FontText) ? AppSettings.DefaultFontFamily : FontText.Trim(),
        FontSize = double.IsNaN(FontSizeBox.Value) ? AppSettings.DefaultFontSize : FontSizeBox.Value,
        TimestampZone = (TimestampZone)Math.Max(0, ZoneBox.SelectedIndex),
        TimestampFormat = string.IsNullOrWhiteSpace(FormatBox.Text) ? TimeDisplay.DefaultFormat : FormatBox.Text,
        RestoreSession = RestoreSessionSwitch.IsOn,
        TailByDefault = TailSwitch.IsOn,
        HideRules = [.. _rules
            .Where(r => !string.IsNullOrWhiteSpace(r.Query.Text))
            .Select(r => new HideRule { Name = r.Name.Text.Trim(), Query = r.Query.Text.Trim(), Enabled = r.Enabled.IsChecked == true })],
        CustomTemplates = [.. _templates
            .Where(t => !t.IsBlank)
            .Select(t => new CustomTemplate(t.Name.Text.Trim(), t.Template.Text.Trim()))],
    };

    /// <summary>An editable ComboBox updates SelectedItem / Text only when the text is committed: read whichever is current.</summary>
    private string FontText => string.IsNullOrWhiteSpace(FontBox.Text) ? FontBox.SelectedItem as string ?? string.Empty : FontBox.Text;

    private void OnSaveClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var problems = _rules.Count(r => !Validate(r)) + (TimestampFormatError() is null ? 0 : 1) + ValidateTemplates();
        if (problems > 0)
        {
            args.Cancel = true; // the messages next to the fields say what to fix
            FileStatusText.Text = "Fix the marked fields first (or switch the rule off, or delete the rule or template).";
            return;
        }

        Result = Build().Normalised();
    }

    // ------------------------------------------------------------------ appearance and timestamps

    private void OnFontSizeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(args.NewValue))
        {
            sender.Value = AppSettings.DefaultFontSize;
        }
    }

    private void OnFormatChanged(object sender, TextChangedEventArgs e) => UpdateFormatPreview();

    private void OnZoneChanged(object sender, SelectionChangedEventArgs e) => UpdateFormatPreview();

    private string? TimestampFormatError()
    {
        var format = FormatBox.Text;
        if (string.IsNullOrWhiteSpace(format))
        {
            return null; // the default is used
        }

        if (format.Length > TimeDisplay.MaxFormatLength)
        {
            return "That format is too long.";
        }

        return TimeDisplay.IsUsableFormat(format) ? null : "That isn't a date and time format. Example: yyyy-MM-dd HH:mm:ss.fff";
    }

    private void UpdateFormatPreview()
    {
        if (FormatPreview is null || ZoneBox is null)
        {
            return; // still initialising
        }

        if (TimestampFormatError() is { } error)
        {
            FormatPreview.Text = error;
            FormatPreview.Style = (Style)Resources["ErrorHint"];
            return;
        }

        var display = new TimeDisplay((TimestampZone)Math.Max(0, ZoneBox.SelectedIndex), FormatBox.Text);
        FormatPreview.Text = string.Create(CultureInfo.InvariantCulture, $"Now: {display.Time(DateTimeOffset.Now)}");
        FormatPreview.Style = (Style)Resources["Hint"];
    }

    // ------------------------------------------------------------------ hide rules (FR-S6)

    /// <summary>The controls of one rule in the editor.</summary>
    private sealed record RuleRow(StackPanel Panel, CheckBox Enabled, TextBox Name, TextBox Query, TextBlock Error);

    private void OnAddRuleClick(object sender, RoutedEventArgs e)
    {
        var row = AddRuleRow(new HideRule());
        UpdateRulesHint();
        row.Query.Focus(FocusState.Programmatic);
    }

    private RuleRow AddRuleRow(HideRule rule)
    {
        var enabled = new CheckBox { IsChecked = rule.Enabled, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBox { Text = rule.Name, PlaceholderText = "Name (optional)", Width = 160 };
        var query = new TextBox
        {
            Text = rule.Query,
            PlaceholderText = "e.g. @m startswith 'Health check'",
            FontFamily = LogFormat.CodeFont,
        };
        var remove = new Button { Content = new SymbolIcon(Symbol.Delete), Padding = new Thickness(8, 4, 8, 4) };
        var error = new TextBlock { Visibility = Visibility.Collapsed, Style = (Style)Resources["ErrorHint"] };

        AutomationProperties.SetName(enabled, "Rule switched on");
        AutomationProperties.SetName(name, "Rule name");
        AutomationProperties.SetName(query, "Rule query");
        AutomationProperties.SetName(remove, "Delete rule");
        AutomationProperties.SetLiveSetting(error, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        ToolTipService.SetToolTip(remove, "Delete this rule");

        var line = new Grid { ColumnSpacing = 8 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(name, 1);
        Grid.SetColumn(query, 2);
        Grid.SetColumn(remove, 3);
        line.Children.Add(enabled);
        line.Children.Add(name);
        line.Children.Add(query);
        line.Children.Add(remove);

        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(line);
        panel.Children.Add(error);

        var row = new RuleRow(panel, enabled, name, query, error);
        query.TextChanged += (_, _) => Validate(row);
        enabled.Click += (_, _) => Validate(row);
        remove.Click += (_, _) =>
        {
            _rules.Remove(row);
            RulesPanel.Children.Remove(panel);
            UpdateRulesHint();
        };

        _rules.Add(row);
        RulesPanel.Children.Add(panel);
        Validate(row);
        return row;
    }

    /// <summary>Shows why a switched-on rule can't be read; empty rules are dropped on Save and switched-off ones are kept as they are.</summary>
    private static bool Validate(RuleRow row)
    {
        var text = row.Query.Text;
        if (string.IsNullOrWhiteSpace(text) || row.Enabled.IsChecked != true || QueryParser.TryParse(text, out _, out var error))
        {
            row.Error.Visibility = Visibility.Collapsed;
            return true;
        }

        row.Error.Text = string.Create(CultureInfo.InvariantCulture, $"{error.Message}  (at position {error.Position + 1})");
        row.Error.Visibility = Visibility.Visible;
        return false;
    }

    private void UpdateRulesHint() => NoRulesText.Visibility = _rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ------------------------------------------------------------------ custom output templates (FR-F2)

    /// <summary>The controls of one custom template in the editor.</summary>
    private sealed record TemplateRow(StackPanel Panel, TextBox Name, TextBox Template, TextBox Test, TextBlock Status)
    {
        public bool IsBlank => string.IsNullOrWhiteSpace(Name.Text) && string.IsNullOrWhiteSpace(Template.Text);
    }

    private void OnAddTemplateClick(object sender, RoutedEventArgs e)
    {
        var row = AddTemplateRow(new CustomTemplate(string.Empty, OutputTemplate.DefaultText));
        UpdateTemplatesHint();
        ValidateTemplates();
        row.Name.Focus(FocusState.Programmatic);
    }

    private TemplateRow AddTemplateRow(CustomTemplate template)
    {
        var name = new TextBox { Text = template.Name, PlaceholderText = "Name, e.g. Shop API", Width = 160 };
        var text = new TextBox { Text = template.Template, PlaceholderText = OutputTemplate.DefaultText, FontFamily = LogFormat.CodeFont };
        var test = new TextBox { PlaceholderText = "Paste a line from the log to test the template", FontFamily = LogFormat.CodeFont };
        var remove = new Button { Content = new SymbolIcon(Symbol.Delete), Padding = new Thickness(8, 4, 8, 4) };
        var status = new TextBlock { Style = (Style)Resources["Hint"] };

        AutomationProperties.SetName(name, "Template name");
        AutomationProperties.SetName(text, "Output template");
        AutomationProperties.SetName(test, "Test line");
        AutomationProperties.SetName(remove, "Delete template");
        AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        ToolTipService.SetToolTip(remove, "Delete this template");

        var line = new Grid { ColumnSpacing = 8 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(remove, 2);
        line.Children.Add(name);
        line.Children.Add(text);
        line.Children.Add(remove);

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(line);
        panel.Children.Add(test);
        panel.Children.Add(status);

        var row = new TemplateRow(panel, name, text, test, status);
        name.TextChanged += (_, _) => ValidateTemplates(); // a name can clash with another row's
        text.TextChanged += (_, _) => ValidateTemplate(row);
        test.TextChanged += (_, _) => ValidateTemplate(row);
        remove.Click += (_, _) =>
        {
            _templates.Remove(row);
            TemplatesPanel.Children.Remove(panel);
            UpdateTemplatesHint();
            ValidateTemplates();
        };

        _templates.Add(row);
        TemplatesPanel.Children.Add(panel);
        return row;
    }

    /// <summary>Checks every template row; returns how many have a problem (blank rows are dropped on Save, not a problem).</summary>
    private int ValidateTemplates() => _templates.Count(row => !ValidateTemplate(row));

    /// <summary>Shows what is wrong with a template, or what it reads from the test line.</summary>
    private bool ValidateTemplate(TemplateRow row)
    {
        if (row.IsBlank)
        {
            row.Status.Style = (Style)Resources["Hint"];
            row.Status.Text = "Empty: dropped when the settings are saved.";
            return true;
        }

        var others = _templates.Where(t => !ReferenceEquals(t, row) && !t.IsBlank).Select(t => t.Name.Text);
        var error = FormatCatalog.CheckCustomTemplate(row.Name.Text, row.Template.Text, others);
        row.Status.Style = (Style)Resources[error is null ? "Hint" : "ErrorHint"];
        row.Status.Text = error ?? FormatCatalog.TestLine(row.Template.Text, row.Test.Text);
        return error is null;
    }

    private void UpdateTemplatesHint() => NoTemplatesText.Visibility = _templates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // ------------------------------------------------------------------ Open with (FR-P5)

    /// <summary>The exe itself (not the single-file extraction folder, see the project file).</summary>
    private static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, AppInfo.Name + ".exe");

    private CheckBox[] ExtensionChecks => [LogCheck, ClefCheck, TxtCheck, JsonCheck];

    private void RefreshAssociations()
    {
        IReadOnlyList<string> registered;
        AssociationStatus status;
        try
        {
            registered = _associations.RegisteredExtensions();
            status = _associations.StatusFor(ExePath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            AssociationStatusText.Text = "Couldn't read the registration: " + ex.Message;
            return;
        }

        foreach (var check in ExtensionChecks)
        {
            var extension = (string)check.Tag;
            check.IsChecked = status == AssociationStatus.NotRegistered ? FileAssociations.DefaultExtensions.Contains(extension) : registered.Contains(extension);
        }

        UnregisterButton.IsEnabled = status != AssociationStatus.NotRegistered;
        AssociationStatusText.Text = status switch
        {
            AssociationStatus.Registered => $"Registered for {string.Join(", ", registered)}.",
            AssociationStatus.RegisteredElsewhere => $"Registered for {string.Join(", ", registered)}, but for a copy of LogLens in another place ({_associations.RegisteredExePath()}). Press Register to use this one ({ExePath}).",
            _ => "Not registered. .clef and .json files are read from a later version on; until then they show as unparsed lines.",
        };
    }

    private void OnRegisterClick(object sender, RoutedEventArgs e)
    {
        var chosen = ExtensionChecks.Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
        if (chosen.Count == 0)
        {
            AssociationStatusText.Text = "Tick at least one file type, or use Remove.";
            return;
        }

        RunRegistryAction(() => _associations.Register(ExePath, chosen));
    }

    private void OnUnregisterClick(object sender, RoutedEventArgs e) => RunRegistryAction(() => _associations.Unregister(ExePath));

    private void RunRegistryAction(Action action)
    {
        try
        {
            action();
            RefreshAssociations();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            AssociationStatusText.Text = "Couldn't change the registration: " + ex.Message;
        }
    }

    // ------------------------------------------------------------------ settings file (FR-P4) and folders

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = "LogLens-settings" };
        picker.FileTypeChoices.Add("Settings (JSON)", [".json"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
        if (await picker.PickSaveFileAsync() is not { } file)
        {
            return;
        }

        try
        {
            JsonSettingsStore.Export(Build(), file.Path);
            FileStatusText.Text = $"Saved to {file.Path}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileStatusText.Text = "Couldn't save: " + ex.Message;
        }
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".json");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
        if (await picker.PickSingleFileAsync() is not { } file)
        {
            return;
        }

        try
        {
            _base = JsonSettingsStore.Import(file.Path, _base);
            Load(_base);
            FileStatusText.Text = $"Loaded {file.Name}. Press Save to use these settings.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            FileStatusText.Text = "Couldn't import: " + ex.Message;
        }
    }

    private void OnOpenSettingsFolderClick(object sender, RoutedEventArgs e) => OpenFolder(_paths.RoamingRoot);

    private void OnOpenLogsFolderClick(object sender, RoutedEventArgs e) => OpenFolder(_paths.LogDirectory);

    private void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            FileStatusText.Text = $"Couldn't open {folder}: {ex.Message}";
        }
    }
}
