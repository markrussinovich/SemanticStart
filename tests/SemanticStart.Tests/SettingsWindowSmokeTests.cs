using System.Windows;
using System.Windows.Threading;
using SemanticStart.App;
using Xunit;

namespace SemanticStart.Tests;

/// <summary>
/// Constructs and renders the settings window for real. Compiling XAML proves element and property
/// names resolve, but not that the window loads: a missing x:Name, a handler signature that does
/// not match its event, or code-behind touching a control before it exists all fail only when the
/// window is actually opened, which on this app means after a rebuild and a trip to the tray icon.
///
/// Everything runs in a single test on a single STA thread with a single Application, because WPF
/// allows exactly one Application per process and will not let it be replaced once it has shut
/// down. Splitting these assertions across xunit tests produced failures that looked like product
/// faults ("Cannot create more than one System.Windows.Application instance", "The Application
/// object is being shut down") but were purely artifacts of the harness.
/// </summary>
public class SettingsWindowSmokeTests
{
    [Fact]
    public void SettingsWindowRendersWithTheSavedHotKeyAndReadableIndexStats()
    {
        Exception? failure = null;
        string? hotKeyText = null;
        var saveEnabledAfterEdit = false;
        var backgroundNoteVisibleWhenIdle = true;
        (int Lines, int BoldValues) statsShape = default;
        var statsHasSummaryRule = false;

        var thread = new Thread(() =>
        {
            try
            {
                // The window's styles live in App.xaml, and only an Application registers them.
                // InitializeComponent loads them without running OnStartup, so this exercises the
                // real resource lookups rather than a stripped-down window.
                var app = new SemanticStart.App.App();
                app.InitializeComponent();

                var settings = new AppSettings();
                var settingsService = new AppSettingsService();
                var searchService = new SemanticSearchService(settings);
                var activation = new ActivationManager(Dispatcher.CurrentDispatcher, () => { }, settings);

                var rebuilds = new IndexRebuildCoordinator(searchService);

                var window = new SettingsWindow(settingsService, searchService, activation, rebuilds);

                // Control templates are applied on show, not on construct, so anything a template
                // does to a value set in the constructor stays invisible until the window renders.
                // Kept off-screen and unactivated so the suite does not steal focus.
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -32000;
                window.Top = -32000;
                window.ShowActivated = false;
                window.ShowInTaskbar = false;
                window.Show();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

                hotKeyText = window.HotKeyDisplayText;

                // Known numbers rather than the machine's real index: this asserts the block's
                // shape, and a test that depends on how many apps happen to be installed asserts
                // nothing repeatable.
                window.RenderIndexStats(new IndexStats(
                    Total: 553, Apps: 300, SystemTools: 150, WindowsSettings: 100, Other: 3,
                    SizeBytes: 12_345_678));
                statsShape = window.IndexStatsShape;
                statsHasSummaryRule = window.IndexStatsHasSummaryRule;

                // Save tracks edits. It starts disabled only once settings have been written at
                // least once, so the meaningful assertion is that editing turns it on.
                window.MarkDirty();
                saveEnabledAfterEdit = window.IsSaveEnabled;

                // Nothing is rebuilding, so the note about closing the window must stay hidden.
                backgroundNoteVisibleWhenIdle = window.IsBackgroundNoteVisible;

                window.Close();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The settings window timed out.");
        Assert.Null(failure);

        Assert.False(
            string.IsNullOrWhiteSpace(hotKeyText),
            "The hotkey field was blank after rendering, so the active chord is invisible to the user.");
        Assert.Equal(new AppSettings().HotKey, hotKeyText);

        // Six categories, six rows, and on every one a bold value in its own right-aligned column.
        // The counts are the reason to read this block, so a run-on line, an unbolded number, or a
        // count that starts wherever its label happened to end is a regression.
        Assert.Equal((6, 6), statsShape);

        // Total entries and Size on disk summarise the categories above them; without a rule the
        // total reads as one more category that happens to be far larger than the rest.
        Assert.True(statsHasSummaryRule, "Nothing separated the totals from the per-category counts.");

        Assert.True(saveEnabledAfterEdit, "Save stayed disabled after a setting was changed, so the change cannot be committed.");
        Assert.False(backgroundNoteVisibleWhenIdle, "The 'indexing runs in the background' note showed with no rebuild running.");
    }

    /// <summary>
    /// The empty-index line used to tell the user to rebuild while a rebuild was already running,
    /// next to a live progress bar and a disabled Rebuild button.
    /// </summary>
    [Fact]
    public void TheEmptyIndexLineDoesNotAskForARebuildWhileOneIsRunning()
    {
        Assert.Equal("Index is empty. Rebuild to populate it.", SettingsWindow.EmptyIndexMessage(rebuilding: false));

        var running = SettingsWindow.EmptyIndexMessage(rebuilding: true);
        Assert.DoesNotContain("Rebuild to populate", running, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("populating", running, StringComparison.OrdinalIgnoreCase);
    }
}
