using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SemanticStart.App;
using SemanticStart.Core.Model;
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
        var setupRendered = false;

        var thread = new Thread(() =>
        {
            try
            {
                // The window's styles live in Theme.xaml, and only an Application registers them
                // process-wide. Loading the dictionary into a bare Application exercises the real
                // resource lookups without constructing SemanticStart's own App: WPF runs
                // OnStartup on the first Show even though nobody called Run, and that sequence
                // registers a global hotkey and a tray icon and - because the app is
                // single-instance - hands over and shuts down whenever a copy is already running.
                // This test used to pass or fail depending on whether the developer had
                // SemanticStart open, because that shutdown closed the window under test and took
                // every application resource with it.
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/SemanticStart.App;component/Theme.xaml"),
                });

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

                // First run uses this same window in setup mode; it has to render with the welcome
                // banner and the Build index action rather than failing on a style lookup.
                var setupWindow = new SettingsWindow(settingsService, searchService, activation, rebuilds, setupMode: true)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = -32000,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                };
                setupWindow.Show();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
                setupRendered = setupWindow.IsSetupMode && setupWindow.Title.Contains("Set up")
                    && setupWindow.PrimaryActionText == "Build index" && setupWindow.IsSaveEnabled;
                setupWindow.Close();

                VerifyOverlayCards(searchService, settings);
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
        Assert.True(setupRendered, "First-run setup did not render with Build index as the footer's primary action.");
    }

    private static void VerifyOverlayCards(SemanticSearchService searchService, AppSettings settings)
    {
        using var icons = new IconProvider();
        var viewModel = new OverlayViewModel(searchService, icons, settings);
        for (var i = 0; i < 2; i++)
        {
            viewModel.Results.Add(new SearchResultItem(new SearchHit
            {
                Entity = new Entity
                {
                    Id = $"theme-preview-{i}",
                    DisplayName = $"Example application {i}",
                    Kind = EntityKind.Application,
                    LaunchKind = LaunchKind.Executable,
                    LaunchTarget = "example.exe",
                    Source = "test",
                },
                Score = 1,
                Summary = "An application with an expandable description.",
                Details = "Additional information about the application and its capabilities.",
            }));
        }
        viewModel.SelectedIndex = 0;

        var overlay = new OverlayWindow(viewModel)
        {
            Left = -32000,
            Top = -32000,
            ShowActivated = false,
        };
        try
        {
            overlay.Show();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
            overlay.UpdateLayout();

            var search = (TextBox)overlay.FindName("SearchBox");
            var placeholder = (TextBlock)search.Template.FindName("Placeholder", search);
            var placeholderLeft = placeholder.TransformToAncestor(search).Transform(new Point()).X
                + placeholder.Padding.Left;
            var caretLeft = search.GetRectFromCharacterIndex(0).Left;
            Assert.InRange(Math.Abs(placeholderLeft - caretLeft), 0, 1);

            var results = (ListBox)overlay.FindName("ResultsList");
            var first = (ListBoxItem)results.ItemContainerGenerator.ContainerFromIndex(0);
            var second = (ListBoxItem)results.ItemContainerGenerator.ContainerFromIndex(1);
            var card = (Border)first.Template.FindName("Bd", first);
            var separator = (Border)first.Template.FindName("Rule", first);
            Assert.NotNull(card.Effect);
            Assert.Equal(Visibility.Hidden, separator.Visibility);

            // The new soft card still needs a distinct keyboard-navigation state.
            viewModel.IsResultsActive = true;
            Assert.Same(overlay.FindResource("FocusStrokeBrush"), card.BorderBrush);
            viewModel.SelectedIndex = 1;
            Assert.Null(card.Effect);
            Assert.Equal(Visibility.Visible, separator.Visibility);
            Assert.NotNull(((Border)second.Template.FindName("Bd", second)).Effect);

            var collapsedHeight = second.ActualHeight;
            viewModel.Results[1].IsExpanded = true;
            overlay.UpdateLayout();
            Assert.True(second.ActualHeight > collapsedHeight, "Expanded details were clipped by the card layout.");

            // Exercise the shadow and all dynamic resources through WPF's renderer.
            var image = new RenderTargetBitmap((int)overlay.ActualWidth, (int)overlay.ActualHeight,
                96, 96, PixelFormats.Pbgra32);
            image.Render(overlay);
        }
        finally
        {
            overlay.Close();
        }
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
        Assert.Contains("Build index", SettingsWindow.EmptyIndexMessage(rebuilding: false, setup: true));
    }
}
