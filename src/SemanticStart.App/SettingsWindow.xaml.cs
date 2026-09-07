using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using SemanticStart.Core.Indexing;

namespace SemanticStart.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettingsService _settingsService;
    private readonly SemanticSearchService _searchService;
    private readonly ActivationManager _activationManager;
    private readonly IndexRebuildCoordinator _rebuilds;
    private AppSettings _settings;
    private bool _dirty;
    private bool _loading;

    public SettingsWindow(
        AppSettingsService settingsService,
        SemanticSearchService searchService,
        ActivationManager activationManager,
        IndexRebuildCoordinator rebuilds)
    {
        InitializeComponent();
        ThemeService.Refresh();
        ThemeService.ApplyWindowChrome(this);
        _settingsService = settingsService;
        _searchService = searchService;
        _activationManager = activationManager;
        _rebuilds = rebuilds;
        _settings = settingsService.Load();
        VersionText.Text = $"SemanticStart {ProductVersion}";
        LoadControls();

        // The window can open while a rebuild is already running - the tray starts one, and so does
        // first run - so it adopts the current state rather than assuming it is idle.
        _rebuilds.StateChanged += OnRebuildStateChanged;
        ApplyRebuildState(_rebuilds.State);

        // A window that never fits its content is a window with a permanent scrollbar. Growing to
        // fit and capping at the working area keeps the scrollbar for the screens that need it and
        // removes it everywhere else.
        MaxHeight = SystemParameters.WorkArea.Height - 40;

        _ = RefreshIndexStatsAsync();
    }

    /// <summary>
    /// Informational version stamped by the build, with any source-control suffix
    /// (for example "1.0.0+abc1234") trimmed off.
    /// </summary>
    private static string ProductVersion
    {
        get
        {
            var assembly = typeof(SettingsWindow).Assembly;
            var informational = assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion;

            var version = informational ?? assembly.GetName().Version?.ToString() ?? "unknown";
            var plus = version.IndexOf('+');
            return plus >= 0 ? version[..plus] : version;
        }
    }

    /// <summary>
    /// Starts a rebuild and leaves it running whether or not this window survives.
    /// </summary>
    public Task RebuildIndexAsync(bool force)
    {
        SaveFromControls();
        return _rebuilds.StartAsync(_settings, force);
    }

    private void OnRebuildStateChanged(object? sender, IndexRebuildState state) =>
        Dispatcher.BeginInvoke(() => ApplyRebuildState(state));

    private void ApplyRebuildState(IndexRebuildState state)
    {
        ProgressBar.Value = state.Fraction;
        ProgressText.Text = state.Message;
        RebuildButton.IsEnabled = !state.IsRunning;

        // Says the one thing the user cannot find out by looking: that the work is not tied to this
        // window. Without it the only safe-looking option is to sit and wait for a job that takes
        // minutes.
        BackgroundNote.Visibility = state.IsRunning ? Visibility.Visible : Visibility.Collapsed;

        // The stats block claims the index is empty while a build is filling it, and tells the user
        // to press a button that is disabled. Re-render so it describes what is actually happening.
        RenderIndexStats(_lastStats);

        if (state.Outcome == RebuildOutcome.Completed)
            _ = RefreshIndexStatsAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        // Deliberately does not cancel the rebuild. Closing a window is not a request to throw away
        // several minutes of indexing.
        _rebuilds.StateChanged -= OnRebuildStateChanged;

        // Closing while the recorder still has focus would otherwise leave the hotkey suspended,
        // so the shortcut would stop working until the app was restarted. Re-applying is harmless
        // when nothing was suspended.
        _activationManager.ResumeAfterCapture();

        base.OnClosed(e);
    }

    private void LoadControls()
    {
        _loading = true;
        HotKeyBox.HotKey = _settings.HotKey;
        OnlineBox.IsChecked = _settings.AllowOnlineEnrichment;
        LoginBox.IsChecked = _settings.LaunchAtLogin;
        LimitSlider.Value = _settings.ResultLimit;
        DebounceSlider.Value = _settings.SearchDebounceMilliseconds;
        _loading = false;

        UpdateHotKeyStatus();
        UpdateSaveState();
    }

    /// <summary>
    /// Enables Save only when pressing it would do something.
    ///
    /// A permanently-enabled Save on a page that also writes on close cannot be told apart from one
    /// with pending changes, so it says nothing about whether the user has edited anything. The
    /// exception is a first run, where nothing has been written yet and confirming the defaults is
    /// a real action.
    /// </summary>
    internal void UpdateSaveState() => SaveButton.IsEnabled = _dirty || !_settingsService.HasSavedSettings;

    /// <summary>
    /// What to say when the index holds nothing. Telling the user to rebuild while a rebuild is
    /// running contradicts the progress bar directly below and points at a disabled button.
    /// </summary>
    internal static string EmptyIndexMessage(bool rebuilding) => rebuilding
        ? "Index is empty. The build below is populating it."
        : "Index is empty. Rebuild to populate it.";

    /// <summary>Marks the form edited. Wired to every control that Save would persist.</summary>
    internal void MarkDirty(object? sender = null, EventArgs? e = null)
    {
        if (_loading || !IsInitialized)
            return;

        _dirty = true;
        UpdateSaveState();
    }

    private void Setting_Changed(object sender, RoutedEventArgs e) => MarkDirty();

    private void Setting_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => MarkDirty();

    /// <summary>The chord currently shown in the hotkey field. Exposed so a test can read what the user sees.</summary>
    internal string HotKeyDisplayText => HotKeyBox.HotKey;

    /// <summary>
    /// Shows the hotkey that is genuinely in force. These can differ for two reasons now: the
    /// captured chord may not be usable at all, or it may be valid but already owned by another
    /// app, in which case registration falls back to a free one. Both need saying, because in each
    /// case pressing what was captured does nothing.
    /// </summary>
    private void UpdateHotKeyStatus()
    {
        if (!HotKeySpec.TryParse(HotKeyBox.HotKey, out _, out var error))
        {
            HotKeyStatus.Text = error;
            return;
        }

        var active = _activationManager.ActiveHotKey;

        HotKeyStatus.Text = active switch
        {
            null => "No hotkey is active. Every candidate is already claimed by another app; pick a different one, or open SemanticStart from the tray icon.",
            _ when !string.Equals(active, _settings.HotKey, StringComparison.OrdinalIgnoreCase) =>
                $"{_settings.HotKey} is already used by another app, so {active} is active instead.",
            _ => $"{active} is active.",
        };
    }

    /// <summary>
    /// Applies a captured chord immediately. The recorder only raises this once a press has become
    /// a usable chord, so there is no partially-typed state to guard against.
    /// </summary>
    private void HotKeyBox_HotKeyChanged(object? sender, EventArgs e)
    {
        if (!IsLoaded)
            return;

        SaveFromControls();
    }

    /// <summary>
    /// Reports a press that cannot be a hotkey, at the keystroke rather than later when the
    /// shortcut silently does not work.
    /// </summary>
    private void HotKeyBox_HotKeyRejected(object? sender, string reason) => HotKeyStatus.Text = reason;

    /// <summary>
    /// Hands the keyboard to the recorder. The currently assigned chord is the one a user is most
    /// likely to press while editing, and the OS delivers a registered hotkey to us as an
    /// activation rather than as key input, so without releasing it the overlay would pop up over
    /// this window and the field would never see the press.
    /// </summary>
    private void HotKeyBox_RecordingStarted(object? sender, EventArgs e)
    {
        _activationManager.SuspendForCapture();
        HotKeyStatus.Text = "Press the shortcut you want. Esc keeps the current one.";
    }

    private void HotKeyBox_RecordingStopped(object? sender, EventArgs e)
    {
        _activationManager.ResumeAfterCapture();
        UpdateHotKeyStatus();
    }

    private void SaveFromControls()
    {
        // An unusable chord keeps the previous one rather than falling back to the default:
        // silently replacing what the user asked for with something else is how the old parser
        // turned a typo into a different working shortcut with no indication anything had happened.
        var hotKey = HotKeySpec.TryParse(HotKeyBox.HotKey, out var spec, out _) && spec is not null
            ? spec.Normalized
            : _settings.HotKey;

        _settings = _settings with
        {
            HotKey = hotKey,
            AllowOnlineEnrichment = OnlineBox.IsChecked == true,
            LaunchAtLogin = LoginBox.IsChecked == true,
            ResultLimit = (int)Math.Round(LimitSlider.Value),
            SearchDebounceMilliseconds = (int)Math.Round(DebounceSlider.Value),
        };
        _settingsService.Save(_settings);
        _activationManager.ApplySettings(_settings);
        HotKeyBox.HotKey = _settings.HotKey;
        _dirty = false;
        UpdateSaveState();
        UpdateHotKeyStatus();
    }

    /// <summary>Whether Save is currently offered. Exposed so a test can check it tracks edits.</summary>
    internal bool IsSaveEnabled => SaveButton.IsEnabled;

    /// <summary>Whether the "indexing runs in the background" note is showing.</summary>
    internal bool IsBackgroundNoteVisible => BackgroundNote.Visibility == Visibility.Visible;

    /// <summary>
    /// What the stats block ended up showing, as (line count, bolded value count). Exposed so a
    /// test can confirm the counts really are on separate lines, really are bold, and really are
    /// right-aligned in a column of their own, which is the whole point of building this as a grid
    /// instead of a formatted string.
    /// </summary>
    internal (int Lines, int BoldValues) IndexStatsShape
    {
        get
        {
            if (IndexStatsGrid.Visibility != Visibility.Visible)
                return (IndexStatsText.Text.Length > 0 ? 1 : 0, 0);

            var values = IndexStatsGrid.Children
                .OfType<TextBlock>()
                .Where(t => Grid.GetColumn(t) == 1)
                .ToList();

            var bold = values.Count(t =>
                t.FontWeight == FontWeights.SemiBold
                && t.HorizontalAlignment == System.Windows.HorizontalAlignment.Right);

            return (IndexStatsGrid.RowDefinitions.Count, bold);
        }
    }

    private IndexStats? _lastStats;

    private async Task RefreshIndexStatsAsync()
    {
        try
        {
            var stats = await _searchService.GetIndexStatsAsync(CancellationToken.None);
            RenderIndexStats(stats);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to read index stats");
            _lastStats = null;
            IndexStatsGrid.Children.Clear();
            IndexStatsGrid.RowDefinitions.Clear();
            IndexStatsGrid.Visibility = Visibility.Collapsed;
            IndexStatsText.Visibility = Visibility.Visible;
            IndexStatsText.Inlines.Clear();
            IndexStatsText.Text = "Index statistics unavailable.";
        }
    }

    /// <summary>
    /// Lists what the index holds, one category per line with the count in bold.
    ///
    /// Laid out as a two-column grid rather than as lines of text because the counts are the part
    /// worth scanning for. Labels vary in width, so counts set as "Label: 238" start at a
    /// different horizontal position on every row and cannot be compared without reading each one;
    /// giving them their own right-aligned column lines the digits up, which is what makes the
    /// block answer "how much of my machine did it actually find" at a glance.
    ///
    /// The single-line states - empty index, read failure - stay in the TextBlock above, since
    /// they are a sentence rather than a table.
    ///
    /// Separated from the read above so it can be exercised with known numbers.
    /// </summary>
    internal void RenderIndexStats(IndexStats? stats)
    {
        _lastStats = stats;
        IndexStatsText.Inlines.Clear();
        IndexStatsGrid.Children.Clear();
        IndexStatsGrid.RowDefinitions.Clear();

        if (stats is null || stats.Total == 0)
        {
            IndexStatsGrid.Visibility = Visibility.Collapsed;
            IndexStatsText.Visibility = Visibility.Visible;

            // "Rebuild to populate it" while a rebuild is running contradicts the progress bar
            // directly below it and points at a button that is disabled for the duration.
            IndexStatsText.Text = stats is null
                ? "Reading index..."
                : EmptyIndexMessage(_rebuilds.IsRunning);
            return;
        }

        var rows = new List<(string Label, string Value)>
        {
            ("Applications", stats.Apps.ToString("N0")),
            ("System utilities", stats.SystemTools.ToString("N0")),
            ("Windows settings", stats.WindowsSettings.ToString("N0")),
        };

        if (stats.Other > 0)
            rows.Add(("Other", stats.Other.ToString("N0")));

        rows.Add(("Total entries", stats.Total.ToString("N0")));
        rows.Add(("Size on disk", stats.SizeDisplay));

        IndexStatsText.Visibility = Visibility.Collapsed;
        IndexStatsGrid.Visibility = Visibility.Visible;

        var caption = (Style)FindResource("Caption");

        for (var i = 0; i < rows.Count; i++)
        {
            IndexStatsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Style = caption,
                Text = rows[i].Label,
                Margin = new Thickness(0, 0, 12, 0),
            };

            var value = new TextBlock
            {
                Style = caption,
                Text = rows[i].Value,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            };

            Grid.SetRow(label, i);
            Grid.SetColumn(label, 0);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);

            IndexStatsGrid.Children.Add(label);
            IndexStatsGrid.Children.Add(value);
        }
    }

    private async void RebuildButton_Click(object sender, RoutedEventArgs e) => await RebuildIndexAsync(force: true);

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveFromControls();
        if (!_rebuilds.IsRunning)
            ProgressText.Text = "Settings saved.";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        SaveFromControls();
        Close();
    }
}
