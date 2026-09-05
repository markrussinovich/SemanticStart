using System.Windows;
using System.Windows.Controls;
using SemanticStart.Core.Synthesis;

namespace SemanticStart.App;

/// <summary>
/// Setup shown before the first index is built. The first build is the one that matters: whether
/// online documentation and a local model were used decides whether an unfamiliar tool can be found
/// by describing it at all, and rebuilding to change that answer costs minutes. Previously the app
/// opened Settings and immediately started building, so those choices were offered only after they
/// had already been made for the user.
/// </summary>
public partial class FirstRunWindow : Window
{
    private IReadOnlyList<LocalLlmCatalogItem> _catalog = [];
    private CancellationTokenSource? _catalogCts;
    private bool _isBusy;

    public FirstRunWindow(AppSettings settings, string? activeHotKey)
    {
        InitializeComponent();
        Result = settings;

        HotKeyText.Text = $"Open with {activeHotKey ?? settings.HotKey}";
        LaunchAtLoginCheck.IsChecked = true;
        OnlineCheck.IsChecked = true;
        LlmAutoRadio.IsChecked = settings.LocalLlmMode != LocalLlmMode.Off;
        LlmOffRadio.IsChecked = settings.LocalLlmMode == LocalLlmMode.Off;

        ModelCombo.ItemsSource = _catalog;

        Loaded += async (_, _) => await RefreshCatalogAsync();
    }

    /// <summary>The settings to apply, and whether the caller should build the index now.</summary>
    public AppSettings Result { get; private set; }

    public bool BuildRequested { get; private set; }

    private async Task RefreshCatalogAsync()
    {
        _catalogCts?.Cancel();
        _catalogCts = new CancellationTokenSource();
        LlmStatusText.Text = "Checking for a local model runtime...";
        ModelCombo.IsEnabled = false;
        ModelActionButton.IsEnabled = false;

        try
        {
            var catalog = await LocalLlmProfileSynthesizer.DiscoverCatalogAsync(cancellationToken: _catalogCts.Token);
            _catalog = catalog.Models;
            ModelCombo.ItemsSource = _catalog;
            ModelCombo.SelectedItem = _catalog.FirstOrDefault(m => m.IsReady)
                ?? _catalog.FirstOrDefault(m => m.ModelName.Equals(LocalLlmOptions.DefaultModelName, StringComparison.OrdinalIgnoreCase))
                ?? _catalog.FirstOrDefault();
            LlmStatusText.Text = catalog.StatusMessage;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "First-run model discovery failed");
            LlmStatusText.Text = "Could not check for a local model runtime.";
        }

        UpdateEnabledState();
    }

    /// <summary>
    /// Keeps the model controls honest about what is actually possible right now: no runtime means
    /// the only useful action is installing one, and an already-downloaded model must not offer a
    /// download that would do nothing.
    /// </summary>
    private void UpdateEnabledState()
    {
        var useLlm = LlmAutoRadio.IsChecked == true;
        var selected = ModelCombo.SelectedItem as LocalLlmCatalogItem;
        var hasRuntime = _catalog.Any(m => !string.IsNullOrWhiteSpace(m.EndpointBaseUrl)) || _catalog.Count > 0;

        ModelCombo.IsEnabled = useLlm && !_isBusy && _catalog.Count > 0;
        ModelActionButton.IsEnabled = useLlm && !_isBusy;
        ModelActionButton.Content = hasRuntime && selected is not null && !selected.IsReady
            ? "Download"
            : hasRuntime && selected is { IsReady: true }
                ? "Ready"
                : "Install runtime";

        if (selected is { IsReady: true })
            ModelActionButton.IsEnabled = false;

        EstimateText.Text = useLlm
            ? "Indexing takes several minutes and runs in the background."
            : "Indexing takes about a minute.";
        BuildButton.IsEnabled = !_isBusy;
        SkipButton.IsEnabled = !_isBusy;
    }

    private void LlmMode_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
            UpdateEnabledState();
    }

    private async void ModelActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (ModelCombo.SelectedItem is not LocalLlmCatalogItem item)
        {
            await InstallRuntimeAsync();
            return;
        }

        _isBusy = true;
        UpdateEnabledState();
        ModelProgress.Visibility = Visibility.Visible;
        ModelProgress.IsIndeterminate = item.Runtime == LocalLlmRuntime.FoundryLocal;
        LlmStatusText.Text = $"Downloading {item.DisplayName}...";

        try
        {
            var progress = new Progress<double>(p =>
            {
                ModelProgress.IsIndeterminate = false;
                ModelProgress.Value = Math.Clamp(p, 0, 1);
            });
            var result = await LocalLlmProfileSynthesizer.DownloadModelAsync(item, progress);
            LlmStatusText.Text = result.Success ? result.Message : $"Failed: {result.Message}";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "First-run model download failed");
            LlmStatusText.Text = "Download failed; see log for details.";
        }
        finally
        {
            ModelProgress.Visibility = Visibility.Collapsed;
            _isBusy = false;
            await RefreshCatalogAsync();
        }
    }

    private async Task InstallRuntimeAsync()
    {
        _isBusy = true;
        UpdateEnabledState();
        ModelProgress.Visibility = Visibility.Visible;
        ModelProgress.IsIndeterminate = true;
        LlmStatusText.Text = "Installing Foundry Local with winget...";

        try
        {
            var exitCode = await SettingsWindow.RunProcessAsync(
                "winget",
                ["install", "-e", "--id", "Microsoft.FoundryLocal", "--accept-package-agreements", "--accept-source-agreements"]);
            LlmStatusText.Text = exitCode == 0 ? "Foundry Local installed." : "Install did not complete.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "First-run runtime install failed");
            LlmStatusText.Text = "Install failed; see log for details.";
        }
        finally
        {
            ModelProgress.Visibility = Visibility.Collapsed;
            _isBusy = false;
            await RefreshCatalogAsync();
        }
    }

    private void BuildButton_Click(object sender, RoutedEventArgs e) => Complete(build: true);

    private void SkipButton_Click(object sender, RoutedEventArgs e) => Complete(build: false);

    private void Complete(bool build)
    {
        var useLlm = LlmAutoRadio.IsChecked == true;
        Result = Result with
        {
            SetupCompleted = true,
            LaunchAtLogin = LaunchAtLoginCheck.IsChecked == true,
            AllowOnlineEnrichment = OnlineCheck.IsChecked == true,
            LocalLlmMode = useLlm ? LocalLlmMode.Auto : LocalLlmMode.Off,
            LocalLlmModelName = useLlm && ModelCombo.SelectedItem is LocalLlmCatalogItem item
                ? item.ModelName
                : Result.LocalLlmModelName,
        };

        BuildRequested = build;
        DialogResult = true;
        Close();
    }
}
