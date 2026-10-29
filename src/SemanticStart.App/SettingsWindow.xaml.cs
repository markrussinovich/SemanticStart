using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using SemanticStart.Core.Indexing;
using SemanticStart.Core.Speech;

namespace SemanticStart.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettingsService _settingsService;
    private readonly SemanticSearchService _searchService;
    private readonly ActivationManager _activationManager;
    private readonly IndexRebuildCoordinator _rebuilds;
    private readonly DictationController? _dictation;
    private readonly Action<bool>? _dictationEnabledChanged;

    /// <summary>
    /// One bootstrapper for the window's lifetime. Each instance owns an <see cref="HttpClient"/>,
    /// and the status line asks about the model often enough that allocating one per question is a
    /// socket leak in slow motion.
    /// </summary>
    private readonly SpeechModelBootstrapper _speechModels = new();
    private CancellationTokenSource? _modelDownload;
    private AppSettings _settings;
    private bool _dirty;
    private bool _loading;
    private bool _setupMode;

    public SettingsWindow(
        AppSettingsService settingsService,
        SemanticSearchService searchService,
        ActivationManager activationManager,
        IndexRebuildCoordinator rebuilds,
        bool setupMode = false,
        DictationController? dictation = null,
        Action<bool>? dictationEnabledChanged = null)
    {
        InitializeComponent();
        ThemeService.Refresh();
        ThemeService.ApplyWindowChrome(this);
        _settingsService = settingsService;
        _searchService = searchService;
        _activationManager = activationManager;
        _rebuilds = rebuilds;
        _dictation = dictation;
        _dictationEnabledChanged = dictationEnabledChanged;
        _settings = settingsService.Load();
        VersionText.Text = $"SemanticStart {ProductVersion}";
        LoadControls();
        if (setupMode)
            EnterSetupMode();

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
        if (_setupMode)
        {
            _settings = _settings with { SetupCompleted = true };
            ExitSetupMode();
        }

        SaveFromControls();
        return _rebuilds.StartAsync(_settings, force);
    }

    /// <summary>
    /// First run shows the whole settings page rather than a separate setup dialog, because the
    /// build that follows opens this page for its progress anyway. Nothing is built until the user
    /// chooses Build index, and closing first keeps setup pending for the next launch.
    ///
    /// Build index takes Save's place in the footer rather than sitting in the Index card: a user
    /// reviewing settings finishes with the footer's primary button, and when that button was Save
    /// it confirmed the settings, built nothing, and left an app that finds nothing.
    /// </summary>
    private void EnterSetupMode()
    {
        _setupMode = true;
        Title = "Set up SemanticStart";
        SetupBanner.Visibility = Visibility.Visible;
        RebuildButton.Visibility = Visibility.Collapsed;
        SaveButton.Content = "Build index";

        // Recommended defaults for a new install. Online lookup already defaults on; starting at
        // sign-in is what makes the hotkey work without remembering to launch anything.
        _loading = true;
        LoginBox.IsChecked = true;
        _loading = false;
        UpdateSaveState();
    }

    private void ExitSetupMode()
    {
        _setupMode = false;
        Title = "SemanticStart Settings";
        SetupBanner.Visibility = Visibility.Collapsed;
        RebuildButton.Visibility = Visibility.Visible;
        SaveButton.Content = "Save";
        UpdateSaveState();
    }

    /// <summary>Whether the window is showing first-run setup. Exposed for tests.</summary>
    internal bool IsSetupMode => _setupMode;

    /// <summary>The footer's primary button label. Exposed for tests.</summary>
    internal string PrimaryActionText => SaveButton.Content as string ?? string.Empty;

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
        CancelSpeechModelDownload();

        base.OnClosed(e);
    }

    private void LoadControls()
    {
        _loading = true;
        HotKeyBox.HotKey = _settings.HotKey;
        BackgroundRefreshBox.IsChecked = _settings.BackgroundRefresh;
        OnlineBox.IsChecked = _settings.AllowOnlineEnrichment;
        LoginBox.IsChecked = _settings.LaunchAtLogin;
        LimitSlider.Value = _settings.ResultLimit;
        DebounceSlider.Value = _settings.SearchDebounceMilliseconds;
        DictationBox.IsChecked = _settings.DictationEnabled;
        DictationHotKeyBox.HotKey = _settings.DictationHotKey;
        TrailingSilenceSlider.Value = _settings.DictationTrailingSilenceMilliseconds;
        PushToTalkBox.IsChecked = _settings.DictationPushToTalk;
        _loading = false;

        UpdateHotKeyStatus();
        UpdateDictationStatus();
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
    internal void UpdateSaveState() => SaveButton.IsEnabled = _setupMode || _dirty || !_settingsService.HasSavedSettings;

    /// <summary>
    /// What to say when the index holds nothing. Telling the user to rebuild while a rebuild is
    /// running contradicts the progress bar directly below and points at a disabled button.
    /// </summary>
    internal static string EmptyIndexMessage(bool rebuilding, bool setup = false) => rebuilding
        ? "Index is empty. The build below is populating it."
        : setup
            ? "Index is empty. Choose Build index to create it."
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

        var dictationHotKey = HotKeySpec.TryParse(DictationHotKeyBox.HotKey, out var dictationSpec, out _) && dictationSpec is not null
            ? dictationSpec.Normalized
            : _settings.DictationHotKey;

        _settings = _settings with
        {
            HotKey = hotKey,
            BackgroundRefresh = BackgroundRefreshBox.IsChecked == true,
            AllowOnlineEnrichment = OnlineBox.IsChecked == true,
            LaunchAtLogin = LoginBox.IsChecked == true,
            ResultLimit = (int)Math.Round(LimitSlider.Value),
            SearchDebounceMilliseconds = (int)Math.Round(DebounceSlider.Value),
            DictationEnabled = DictationBox.IsChecked == true,
            DictationHotKey = dictationHotKey,
            DictationTrailingSilenceMilliseconds = (int)Math.Round(TrailingSilenceSlider.Value),
            DictationPushToTalk = PushToTalkBox.IsChecked == true,
        };
        _settingsService.Save(_settings);
        _activationManager.ApplySettings(_settings);
        _dictation?.ApplySettings(_settings);
        _dictationEnabledChanged?.Invoke(_settings.DictationEnabled);
        HotKeyBox.HotKey = _settings.HotKey;
        DictationHotKeyBox.HotKey = _settings.DictationHotKey;
        _dirty = false;
        UpdateSaveState();
        UpdateHotKeyStatus();
        UpdateDictationStatus();
    }

    /// <summary>
    /// Turning dictation on is what authorises the model download, so the download starts here
    /// rather than at the next press of the hotkey, with its progress on screen. Anything else
    /// leaves a user who has just enabled a feature with ~665 MB of silent background traffic.
    /// </summary>
    private void Dictation_Changed(object sender, RoutedEventArgs e)
    {
        MarkDirty();
        if (_loading || !IsLoaded)
            return;

        SaveFromControls();

        if (DictationBox.IsChecked == true)
            _ = DownloadSpeechModelAsync();
        else
            CancelSpeechModelDownload();
    }

    private async Task DownloadSpeechModelAsync()
    {
        var models = _speechModels;
        if (models.IsDownloaded)
        {
            UpdateDictationStatus();
            _ = _dictation?.WarmStartAsync();
            return;
        }

        CancelSpeechModelDownload();
        var download = new CancellationTokenSource();
        _modelDownload = download;

        DictationProgress.Visibility = Visibility.Visible;
        DictationProgress.Value = 0;
        DictationStatus.Text = "Downloading the speech model\u2026";

        try
        {
            var progress = new Progress<double>(value => DictationProgress.Value = Math.Clamp(value, 0, 1));
            await models.EnsureAsync(progress, download.Token);
            DictationStatus.Text = "Speech model ready.";
            _dictation?.ApplySettings(_settings);
            _ = _dictation?.WarmStartAsync();
        }
        catch (OperationCanceledException)
        {
            Log.Info("The speech model download was cancelled.");
            DictationStatus.Text = "Speech model download cancelled.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Speech model download failed");
            DictationStatus.Text = "The speech model could not be downloaded. Check your connection and try again.";
        }
        finally
        {
            DictationProgress.Visibility = Visibility.Collapsed;
            if (ReferenceEquals(_modelDownload, download))
            {
                _modelDownload = null;
                download.Dispose();
            }
            UpdateSpeechModelDetails();
        }
    }

    private void CancelSpeechModelDownload()
    {
        // Left in place for the download's own finally to clear and dispose: clearing it here
        // would make that cleanup skip the source it was asked to cancel.
        _modelDownload?.Cancel();
        DictationProgress.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Says which dictation chord is in force, on the same grounds as
    /// <see cref="UpdateHotKeyStatus"/>, and whether the model is on the machine yet.
    /// </summary>
    private void UpdateDictationStatus()
    {
        UpdateSpeechModelDetails();

        if (DictationBox.IsChecked != true)
        {
            DictationHotKeyStatus.Text = "Dictation is off.";
            DictationStatus.Text = "Dictation is disabled.";
            return;
        }

        var active = _activationManager.ActiveDictationHotKey;
        DictationHotKeyStatus.Text = active switch
        {
            null => "No dictation hotkey is active. Every candidate is already claimed by another app; pick a different one.",
            _ when !string.Equals(active, _settings.DictationHotKey, StringComparison.OrdinalIgnoreCase) =>
                $"{_settings.DictationHotKey} is already used by another app, so {active} is active instead.",
            _ => $"{active} is active.",
        };

        if (_modelDownload is null)
            DictationStatus.Text = _speechModels.IsDownloaded
                ? "Speech model ready."
                : "The speech model will be downloaded when dictation is first used.";
    }

    private void UpdateSpeechModelDetails()
    {
        SpeechModelName.Text = $"{_speechModels.ModelId} (int8)";
        SpeechModelLocation.Text = $"Location: {_speechModels.ModelsDirectory}";
        SpeechModelReadiness.Text = _speechModels.IsDownloaded
            ? "Readiness: model files are ready."
            : "Readiness: model files are missing or incomplete.";
        SpeechModelSize.Text =
            _speechModels.IsDownloaded
            ? $"Size: {FormatBytes(_speechModels.InstalledBytes)} installed."
            : $"Size: about {FormatBytes(_speechModels.Options.ApproximateDownloadBytes)} to download.";
        RemoveSpeechModelButton.IsEnabled = _speechModels.HasModelFiles && _modelDownload is null;
    }

    private static string FormatBytes(long bytes)
        => $"{bytes / (1024.0 * 1024.0):N1} MB";

    private async void RemoveSpeechModelButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_speechModels.HasModelFiles)
        {
            UpdateSpeechModelDetails();
            return;
        }

        var result = System.Windows.MessageBox.Show(
            this,
            "Remove the downloaded dictation model files? Dictation will be disabled and the model must be downloaded again before it can be used.",
            "Remove dictation model",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
            return;

        DictationBox.IsChecked = false;
        _dictation?.Stop();
        RemoveSpeechModelButton.IsEnabled = false;

        try
        {
            if (_dictation is not null)
                await _dictation.UnloadAsync();
            _speechModels.DeleteModelFiles();
            DictationStatus.Text = "Dictation model files removed.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to remove dictation model files");
            DictationStatus.Text = "The dictation model files could not be removed.";
        }
        finally
        {
            UpdateSpeechModelDetails();
        }
    }

    private void DictationHotKeyBox_HotKeyChanged(object? sender, EventArgs e)
    {
        if (!IsLoaded)
            return;

        SaveFromControls();
    }

    private void DictationHotKeyBox_RecordingStopped(object? sender, EventArgs e)
    {
        _activationManager.ResumeAfterCapture();
        UpdateDictationStatus();
    }

    /// <summary>Whether Save is currently offered. Exposed so a test can check it tracks edits.</summary>
    internal bool IsSaveEnabled => SaveButton.IsEnabled;

    internal string DictationSettingsTabHeader => ((TabItem)SettingsTabs.Items[1]).Header as string ?? string.Empty;

    internal string SpeechModelNameText => SpeechModelName.Text;

    internal string SpeechModelLocationText => SpeechModelLocation.Text;

    internal string SpeechModelReadinessText => SpeechModelReadiness.Text;

    internal string SpeechModelSizeText => SpeechModelSize.Text;

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

            // Rows, not RowDefinitions: the grid also holds a rule row separating the totals from
            // the categories above them, and that is not a statistic.
            return (values.Count, bold);
        }
    }

    /// <summary>
    /// Whether a rule separates the summary rows from the category rows above them.
    /// </summary>
    internal bool IndexStatsHasSummaryRule =>
        IndexStatsGrid.Children.OfType<Border>().Any(b => Grid.GetColumnSpan(b) == 2);

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
                : EmptyIndexMessage(_rebuilds.IsRunning, _setupMode);

            // The idle status reads "Ready", which next to an empty index looks like a finished build.
            if (stats is not null && ReferenceEquals(_rebuilds.State, IndexRebuildState.Idle))
                ProgressText.Text = "Not built yet";
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

        // Total entries and Size on disk summarise the rows above rather than sitting alongside
        // them, so a rule separates the two groups instead of letting the total read as one more
        // category that happens to be much larger.
        var summaryStart = rows.Count;
        rows.Add(("Total entries", stats.Total.ToString("N0")));
        rows.Add(("Size on disk", stats.SizeDisplay));

        IndexStatsText.Visibility = Visibility.Collapsed;
        IndexStatsGrid.Visibility = Visibility.Visible;

        var caption = (Style)FindResource("Caption");
        var row = 0;

        for (var i = 0; i < rows.Count; i++)
        {
            if (i == summaryStart)
            {
                IndexStatsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var rule = new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 5, 0, 5),
                    Background = (System.Windows.Media.Brush)FindResource("DividerBrush"),
                };

                Grid.SetRow(rule, row);
                Grid.SetColumn(rule, 0);
                Grid.SetColumnSpan(rule, 2);
                IndexStatsGrid.Children.Add(rule);
                row++;
            }

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

            Grid.SetRow(label, row);
            Grid.SetColumn(label, 0);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);

            IndexStatsGrid.Children.Add(label);
            IndexStatsGrid.Children.Add(value);
            row++;
        }
    }

    private async void RebuildButton_Click(object sender, RoutedEventArgs e) => await RebuildIndexAsync(force: true);

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_setupMode)
        {
            await RebuildIndexAsync(force: true);
            return;
        }

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
