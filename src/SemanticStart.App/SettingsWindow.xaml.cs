using System.Diagnostics;
using System.Windows;
using SemanticStart.Core.Indexing;
using SemanticStart.Core.Synthesis;

namespace SemanticStart.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettingsService _settingsService;
    private readonly SemanticSearchService _searchService;
    private readonly ActivationManager _activationManager;
    private CancellationTokenSource? _rebuildCts;
    private CancellationTokenSource? _catalogCts;
    private IReadOnlyList<LocalLlmCatalogItem> _modelCatalog = [];
    private bool _hasDetectedRuntime;
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
        _ = RefreshLocalLlmCatalogAsync();
        _ = RefreshGeneratorStatusAsync();
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
            await RefreshGeneratorStatusAsync();
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
        _catalogCts?.Cancel();
        base.OnClosed(e);
    }

    private void LoadControls()
    {
        HotKeyBox.Text = _settings.HotKey;
        TakeStartBox.IsChecked = _settings.TakeOverStartKey;
        OnlineBox.IsChecked = _settings.AllowOnlineEnrichment;
        LoginBox.IsChecked = _settings.LaunchAtLogin;
        LimitSlider.Value = _settings.ResultLimit;
        LocalLlmModeBox.SelectedValue = _settings.LocalLlmMode.ToString();
        LocalLlmEndpointBox.Text = _settings.LocalLlmEndpointBaseUrl;
        LocalLlmCustomModelBox.Text = _settings.LocalLlmModelName;
        LocalLlmStatusText.Text = "Not tested.";
        UpdateLocalLlmEnabledState();
        UpdateHotKeyStatus();
    }

    /// <summary>
    /// Shows the hotkey that is genuinely in force. These can differ for two reasons now: the text
    /// may not be a valid chord at all, or it may be valid but already owned by another app, in
    /// which case registration falls back to a free one. Both need saying, because in each case
    /// pressing what was typed does nothing.
    /// </summary>
    private void UpdateHotKeyStatus()
    {
        if (!HotKeySpec.TryParse(HotKeyBox.Text, out _, out var error))
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
    /// Validates as the user types, so a rejected chord is reported at the keystroke rather than
    /// discovered later when the shortcut does not work.
    /// </summary>
    private void HotKeyBox_TextChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;

        UpdateHotKeyStatus();
    }

    private void SaveFromControls()
    {
        // An unparseable chord keeps the previous one rather than falling back to the default:
        // silently replacing what the user typed with something else is how the old parser turned
        // a typo into a different working shortcut with no indication anything had happened.
        var hotKey = HotKeySpec.TryParse(HotKeyBox.Text, out var spec, out _) && spec is not null
            ? spec.Normalized
            : _settings.HotKey;

        _settings = _settings with
        {
            HotKey = hotKey,
            TakeOverStartKey = TakeStartBox.IsChecked == true,
            AllowOnlineEnrichment = OnlineBox.IsChecked == true,
            LaunchAtLogin = LoginBox.IsChecked == true,
            ResultLimit = (int)Math.Round(LimitSlider.Value),
            LocalLlmMode = ParseLocalLlmMode(LocalLlmModeBox.SelectedValue?.ToString()),
            LocalLlmEndpointBaseUrl = LocalLlmEndpointBox.Text.Trim(),
            LocalLlmModelName = SelectedModelName(),
        };
        _settingsService.Save(_settings);
        _activationManager.ApplySettings(_settings);
        HotKeyBox.Text = _settings.HotKey;
        UpdateHotKeyStatus();
    }

    private string SelectedModelName()
    {
        if (CustomModelBox.IsChecked == true)
            return string.IsNullOrWhiteSpace(LocalLlmCustomModelBox.Text)
                ? LocalLlmOptions.DefaultModelName
                : LocalLlmCustomModelBox.Text.Trim();

        return LocalLlmModelBox.SelectedItem is LocalLlmCatalogItem item
            ? item.ModelName
            : LocalLlmOptions.DefaultModelName;
    }

    private async Task RefreshLocalLlmCatalogAsync()
    {
        _catalogCts?.Cancel();
        _catalogCts = new CancellationTokenSource();
        RefreshModelsButton.IsEnabled = false;
        DownloadModelButton.IsEnabled = false;
        InstallFoundryButton.IsEnabled = false;
        LocalLlmProgressBar.Visibility = Visibility.Collapsed;
        LocalLlmRuntimeStatusText.Text = "Checking local runtimes...";

        try
        {
            var catalog = await LocalLlmProfileSynthesizer.DiscoverCatalogAsync(cancellationToken: _catalogCts.Token);
            _modelCatalog = catalog.Models;
            _hasDetectedRuntime = catalog.DetectedRuntime is not null;
            LocalLlmModelBox.ItemsSource = _modelCatalog;
            LocalLlmRuntimeStatusText.Text = catalog.StatusMessage;

            var configured = _modelCatalog.FirstOrDefault(m => m.ModelName.Equals(_settings.LocalLlmModelName, StringComparison.OrdinalIgnoreCase));
            if (configured is null
                && !string.IsNullOrWhiteSpace(_settings.LocalLlmModelName)
                && !_settings.LocalLlmModelName.Equals(LocalLlmOptions.DefaultModelName, StringComparison.OrdinalIgnoreCase))
            {
                CustomModelBox.IsChecked = true;
            }
            else
            {
                var selected = configured
                    ?? _modelCatalog.FirstOrDefault(m => m.IsReady)
                    ?? _modelCatalog.FirstOrDefault(m => m.ModelName.Equals(LocalLlmOptions.DefaultModelName, StringComparison.OrdinalIgnoreCase))
                    ?? _modelCatalog.FirstOrDefault();
                LocalLlmModelBox.SelectedItem = selected;
            }
            UpdateLocalLlmEnabledState();
        }
        catch (OperationCanceledException)
        {
            LocalLlmRuntimeStatusText.Text = "Model refresh canceled.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Local LLM catalog refresh failed");
            LocalLlmRuntimeStatusText.Text = "Could not refresh model catalog; see log for details.";
        }
        finally
        {
            UpdateLocalLlmEnabledState();
        }
    }

    private async Task RefreshIndexStatsAsync()
    {
        try
        {
            var stats = await _searchService.GetIndexStatsAsync(CancellationToken.None);
            if (stats.Total == 0)
            {
                IndexStatsText.Text = "Index is empty. Rebuild to populate it.";
                return;
            }

            var parts = new List<string>
            {
                $"{stats.Apps} apps",
                $"{stats.SystemTools} system utilities",
                $"{stats.WindowsSettings} Windows settings",
            };

            if (stats.Other > 0)
                parts.Add($"{stats.Other} other");

            IndexStatsText.Text = $"{stats.Total} entries — {string.Join(", ", parts)} — {stats.SizeDisplay} on disk";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to read index stats");
            IndexStatsText.Text = "Index statistics unavailable.";
        }
    }

    private async Task RefreshGeneratorStatusAsync()
    {
        try
        {
            var counts = await _searchService.GetGeneratorBreakdownAsync(CancellationToken.None);
            if (counts.Count == 0)
            {
                LastGeneratorStatusText.Text = "Last index generator: no profiles yet.";
                return;
            }

            var summary = string.Join(", ", counts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"));
            LastGeneratorStatusText.Text = $"Last index generator: {summary}.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to read generator breakdown");
            LastGeneratorStatusText.Text = "Last index generator: unavailable.";
        }
    }

    /// <summary>
    /// Enables each local-LLM control only once the thing it depends on is actually present, so
    /// the dialog never offers a choice that cannot work yet. The chain is: synthesis must be
    /// turned on, then a runtime must be detected, then a model must be selected. Without this a
    /// user could pick a model and press Test with nothing installed to serve it.
    /// </summary>
    private void UpdateLocalLlmEnabledState()
    {
        var mode = ParseLocalLlmMode(LocalLlmModeBox.SelectedValue?.ToString());
        var enabled = mode != LocalLlmMode.Off;
        var custom = CustomModelBox.IsChecked == true;
        var item = LocalLlmModelBox.SelectedItem as LocalLlmCatalogItem;

        // A custom endpoint is only meaningful in Custom mode; Auto discovers it.
        LocalLlmEndpointBox.IsEnabled = enabled && mode == LocalLlmMode.Custom;

        // Models cannot be listed or chosen until a runtime exists to host them.
        LocalLlmModelBox.IsEnabled = enabled && _hasDetectedRuntime && !custom;
        RefreshModelsButton.IsEnabled = enabled && !custom;
        CustomModelBox.IsEnabled = enabled;
        LocalLlmCustomModelBox.IsEnabled = enabled && custom;
        LocalLlmCustomModelBox.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;

        InstallFoundryButton.IsEnabled = enabled && !_hasDetectedRuntime;
        DownloadModelButton.IsEnabled = enabled && _hasDetectedRuntime && !custom && item is { IsReady: false, CanDownload: true };
        TestLocalLlmButton.IsEnabled = enabled && (_hasDetectedRuntime || mode == LocalLlmMode.Custom);

        UpdateSelectedModelDetails();
    }

    private void UpdateSelectedModelDetails()
    {
        if (!_hasDetectedRuntime)
        {
            LocalLlmStatusText.Text = ParseLocalLlmMode(LocalLlmModeBox.SelectedValue?.ToString()) == LocalLlmMode.Off
                ? "Local LLM synthesis is off; descriptions come from built-in heuristics."
                : "No runtime detected yet. Install Foundry Local, then choose Refresh to list models.";
            return;
        }

        if (CustomModelBox.IsChecked == true)
        {
            LocalLlmStatusText.Text = "Using a custom model identifier.";
            return;
        }

        if (LocalLlmModelBox.SelectedItem is not LocalLlmCatalogItem item)
        {
            LocalLlmStatusText.Text = "Select a model.";
            return;
        }

        if (!string.IsNullOrWhiteSpace(item.EndpointBaseUrl))
            LocalLlmEndpointBox.Text = item.EndpointBaseUrl;

        LocalLlmStatusText.Text = $"{item.DisplayName}: {(item.IsReady ? "ready" : "download required")}. {item.BestFor}";
    }

    private static LocalLlmMode ParseLocalLlmMode(string? value) =>
        Enum.TryParse<LocalLlmMode>(value, ignoreCase: true, out var mode) ? mode : LocalLlmMode.Auto;

    private async void RebuildButton_Click(object sender, RoutedEventArgs e) => await RebuildIndexAsync(force: true);

    private async void RefreshModelsButton_Click(object sender, RoutedEventArgs e) => await RefreshLocalLlmCatalogAsync();

    private void LocalLlmModelBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateLocalLlmEnabledState();

    private void LocalLlmModeBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsInitialized)
            return;

        UpdateLocalLlmEnabledState();
    }

    private void CustomModelBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
            return;

        UpdateLocalLlmEnabledState();
    }

    private async void TestLocalLlmButton_Click(object sender, RoutedEventArgs e)
    {
        SaveFromControls();
        TestLocalLlmButton.IsEnabled = false;
        LocalLlmStatusText.Text = "Testing...";

        try
        {
            var result = await LocalLlmProfileSynthesizer.TestConnectionAsync(_settings.ToLocalLlmOptions());
            LocalLlmStatusText.Text = result.Success
                ? $"Success: {result.ModelName} generated a response at {result.EndpointBaseUrl}"
                : $"Failed: {result.Message}";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Local LLM connection test failed");
            LocalLlmStatusText.Text = "Failed: see log for details.";
        }
        finally
        {
            UpdateLocalLlmEnabledState();
        }
    }

    private async void DownloadModelButton_Click(object sender, RoutedEventArgs e)
    {
        if (LocalLlmModelBox.SelectedItem is not LocalLlmCatalogItem item)
            return;

        DownloadModelButton.IsEnabled = false;
        LocalLlmProgressBar.Visibility = Visibility.Visible;
        LocalLlmProgressBar.IsIndeterminate = item.Runtime == LocalLlmRuntime.FoundryLocal;
        LocalLlmProgressBar.Value = 0;
        LocalLlmStatusText.Text = $"Downloading {item.DisplayName}...";

        try
        {
            var progress = new Progress<double>(p =>
            {
                LocalLlmProgressBar.IsIndeterminate = false;
                LocalLlmProgressBar.Value = Math.Clamp(p, 0, 1);
            });
            var result = await LocalLlmProfileSynthesizer.DownloadModelAsync(item, progress);
            LocalLlmStatusText.Text = result.Success ? result.Message : $"Failed: {result.Message}";
            await RefreshLocalLlmCatalogAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Local LLM model download failed");
            LocalLlmStatusText.Text = "Download failed; see log for details.";
        }
        finally
        {
            LocalLlmProgressBar.IsIndeterminate = false;
            UpdateLocalLlmEnabledState();
        }
    }

    private async void InstallFoundryButton_Click(object sender, RoutedEventArgs e)
    {
        InstallFoundryButton.IsEnabled = false;
        LocalLlmStatusText.Text = "Installing Foundry Local with winget...";

        try
        {
            var exitCode = await RunProcessAsync(
                "winget",
                ["install", "-e", "--id", "Microsoft.FoundryLocal", "--accept-package-agreements", "--accept-source-agreements"]);
            LocalLlmStatusText.Text = exitCode == 0
                ? "Foundry Local install completed. Refreshing model catalog..."
                : $"Foundry Local installer exited with code {exitCode}.";
            await RefreshLocalLlmCatalogAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Foundry Local installation failed");
            LocalLlmStatusText.Text = "Install failed. Install manually with: winget install Microsoft.FoundryLocal";
        }
        finally
        {
            UpdateLocalLlmEnabledState();
        }
    }

    internal static async Task<int> RunProcessAsync(string fileName, IReadOnlyList<string> args)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo { FileName = fileName, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        process.Start();
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

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
