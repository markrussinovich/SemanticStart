using System.Windows;
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
        base.OnClosed(e);
    }

    private void LoadControls()
    {
        HotKeyBox.SelectedValue = _settings.HotKey;
        TakeStartBox.IsChecked = _settings.TakeOverStartKey;
        OnlineBox.IsChecked = _settings.AllowOnlineEnrichment;
        LoginBox.IsChecked = _settings.LaunchAtLogin;
        LimitSlider.Value = _settings.ResultLimit;
        UpdateHotKeyStatus();
    }

    /// <summary>
    /// Shows the hotkey that is genuinely in force. These can differ: if another app already owns
    /// the chosen combination, registration falls back to a free one, and the user needs to see
    /// that rather than wonder why nothing happens.
    /// </summary>
    private void UpdateHotKeyStatus()
    {
        var active = _activationManager.ActiveHotKey;

        HotKeyStatus.Text = active switch
        {
            null => "No hotkey is active. Every candidate is already claimed by another app; pick a different one, or open SemanticStart from the tray icon.",
            _ when !string.Equals(active, _settings.HotKey, StringComparison.OrdinalIgnoreCase) =>
                $"{_settings.HotKey} is already used by another app, so {active} is active instead.",
            _ => $"{active} is active.",
        };
    }

    private void SaveFromControls()
    {
        _settings = _settings with
        {
            HotKey = HotKeyBox.SelectedValue?.ToString() ?? AppSettings.DefaultHotKey,
            TakeOverStartKey = TakeStartBox.IsChecked == true,
            AllowOnlineEnrichment = OnlineBox.IsChecked == true,
            LaunchAtLogin = LoginBox.IsChecked == true,
            ResultLimit = (int)Math.Round(LimitSlider.Value),
        };
        _settingsService.Save(_settings);
        _activationManager.ApplySettings(_settings);
        UpdateHotKeyStatus();
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
