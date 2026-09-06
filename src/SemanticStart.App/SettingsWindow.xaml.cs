using System.Windows;
using System.Windows.Documents;
using SemanticStart.Core.Indexing;

namespace SemanticStart.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettingsService _settingsService;
    private readonly SemanticSearchService _searchService;
    private readonly ActivationManager _activationManager;
    private CancellationTokenSource? _rebuildCts;
    private AppSettings _settings;

    public SettingsWindow(AppSettingsService settingsService, SemanticSearchService searchService, ActivationManager activationManager)
    {
        InitializeComponent();
        ThemeService.Refresh();
        ThemeService.ApplyWindowChrome(this);
        _settingsService = settingsService;
        _searchService = searchService;
        _activationManager = activationManager;
        _settings = settingsService.Load();
        VersionText.Text = $"SemanticStart {ProductVersion}";
        LoadControls();
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

    public async Task RebuildIndexAsync(bool force)
    {
        _rebuildCts?.Cancel();
        _rebuildCts = new CancellationTokenSource();
        RebuildButton.IsEnabled = false;
        ProgressText.Text = "Starting rebuild...";
        ProgressBar.Value = 0;

        var progress = new Progress<IndexProgress>(p =>
        {
            ProgressBar.Value = p.Fraction;
            ProgressText.Text = p.Total > 0
                ? $"{p.Phase}: {p.Completed}/{p.Total} {p.CurrentItem}"
                : $"{p.Phase}: {p.CurrentItem}";
        });

        try
        {
            SaveFromControls();
            await _searchService.RebuildIndexAsync(_settings, force, progress, _rebuildCts.Token);
            ProgressBar.Value = 1;
            ProgressText.Text = $"Rebuild complete. {_searchService.Count} entities loaded.";
            await RefreshIndexStatsAsync();
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "Rebuild canceled.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Index rebuild failed");
            ProgressText.Text = "Rebuild failed; see log for details.";
        }
        finally
        {
            RebuildButton.IsEnabled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _rebuildCts?.Cancel();

        // Closing while the recorder still has focus would otherwise leave the hotkey suspended,
        // so the shortcut would stop working until the app was restarted. Re-applying is harmless
        // when nothing was suspended.
        _activationManager.ResumeAfterCapture();

        base.OnClosed(e);
    }

    private void LoadControls()
    {
        HotKeyBox.HotKey = _settings.HotKey;
        TakeStartBox.IsChecked = _settings.TakeOverStartKey;
        OnlineBox.IsChecked = _settings.AllowOnlineEnrichment;
        LoginBox.IsChecked = _settings.LaunchAtLogin;
        LimitSlider.Value = _settings.ResultLimit;
        UpdateHotKeyStatus();
    }

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
            TakeOverStartKey = TakeStartBox.IsChecked == true,
            AllowOnlineEnrichment = OnlineBox.IsChecked == true,
            LaunchAtLogin = LoginBox.IsChecked == true,
            ResultLimit = (int)Math.Round(LimitSlider.Value),
        };
        _settingsService.Save(_settings);
        _activationManager.ApplySettings(_settings);
        HotKeyBox.HotKey = _settings.HotKey;
        UpdateHotKeyStatus();
    }

    /// <summary>
    /// What the stats block ended up showing, as (line count, bolded value count). Exposed so a
    /// test can confirm the counts really are on separate lines and really are bold, which is the
    /// whole point of building this as inlines instead of a formatted string.
    /// </summary>
    internal (int Lines, int BoldValues) IndexStatsShape
    {
        get
        {
            var inlines = IndexStatsText.Inlines.ToList();
            if (inlines.Count == 0)
                return (IndexStatsText.Text.Length > 0 ? 1 : 0, 0);

            var breaks = inlines.Count(i => i is LineBreak);
            var bold = inlines.Count(i => i is Run run && run.FontWeight == FontWeights.SemiBold);
            return (breaks + 1, bold);
        }
    }

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
            IndexStatsText.Text = "Index statistics unavailable.";
        }
    }

    /// <summary>
    /// Lists what the index holds, one category per line with the count in bold.
    ///
    /// Built as inlines rather than a formatted string because the counts are the part worth
    /// scanning for, and a single run-on line hid them: the whole point of this block is to answer
    /// "how much of my machine did it actually find" at a glance.
    ///
    /// Separated from the read above so it can be exercised with known numbers.
    /// </summary>
    internal void RenderIndexStats(IndexStats stats)
    {
        IndexStatsText.Inlines.Clear();

        if (stats.Total == 0)
        {
            IndexStatsText.Text = "Index is empty. Rebuild to populate it.";
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

        for (var i = 0; i < rows.Count; i++)
        {
            if (i > 0)
                IndexStatsText.Inlines.Add(new LineBreak());

            IndexStatsText.Inlines.Add(new Run($"{rows[i].Label}: "));
            IndexStatsText.Inlines.Add(new Run(rows[i].Value) { FontWeight = FontWeights.SemiBold });
        }
    }

    private async void RebuildButton_Click(object sender, RoutedEventArgs e) => await RebuildIndexAsync(force: true);

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveFromControls();
        ProgressText.Text = "Settings saved.";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        SaveFromControls();
        Close();
    }
}
