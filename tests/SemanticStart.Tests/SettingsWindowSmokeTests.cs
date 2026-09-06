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
    public void SettingsWindowRendersWithTheSavedHotKeyVisible()
    {
        Exception? failure = null;
        string? hotKeyText = null;

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

                var window = new SettingsWindow(settingsService, searchService, activation);

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
    }
}
