using System.Windows;

namespace SemanticStart.App;

/// <summary>
/// Setup shown before the first index is built. The first build is the one that matters: whether
/// online documentation was used decides whether an unfamiliar tool can be found by describing it
/// at all, and rebuilding to change that answer costs minutes. Previously the app opened Settings
/// and immediately started building, so those choices were offered only after they had already been
/// made for the user.
/// </summary>
public partial class FirstRunWindow : Window
{
    public FirstRunWindow(AppSettings settings, string? activeHotKey)
    {
        InitializeComponent();
        Result = settings;

        HotKeyText.Text = $"Open with {activeHotKey ?? settings.HotKey}";
        LaunchAtLoginCheck.IsChecked = true;
        OnlineCheck.IsChecked = true;
        EstimateText.Text = "Indexing takes about a minute and runs in the background.";
    }

    /// <summary>The settings to apply, and whether the caller should build the index now.</summary>
    public AppSettings Result { get; private set; }

    public bool BuildRequested { get; private set; }

    private void BuildButton_Click(object sender, RoutedEventArgs e) => Complete(build: true);

    private void SkipButton_Click(object sender, RoutedEventArgs e) => Complete(build: false);

    private void Complete(bool build)
    {
        Result = Result with
        {
            SetupCompleted = true,
            LaunchAtLogin = LaunchAtLoginCheck.IsChecked == true,
            AllowOnlineEnrichment = OnlineCheck.IsChecked == true,
        };

        BuildRequested = build;
        DialogResult = true;
        Close();
    }
}
