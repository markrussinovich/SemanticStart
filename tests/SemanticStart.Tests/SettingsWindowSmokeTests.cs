using System.Windows.Threading;
using SemanticStart.App;
using Xunit;

namespace SemanticStart.Tests;

/// <summary>
/// Constructs the settings window for real. Compiling XAML proves element and property names
/// resolve, but not that the window loads: a missing x:Name, a handler signature that does not
/// match its event, or code-behind touching a control before it exists all fail only when the
/// window is actually opened, which on this app means after a rebuild and a trip to the tray icon.
/// </summary>
public class SettingsWindowSmokeTests
{
    [Fact]
    public void SettingsWindow_Constructs()
    {
        Exception? failure = null;

        // WPF windows require an STA thread with a dispatcher.
        var thread = new Thread(() =>
        {
            try
            {
                // The window's styles live in App.xaml. Constructing the Application and loading
                // its component registers those resources without running OnStartup, so the test
                // exercises the real resource lookups rather than a stripped-down window.
                var app = new SemanticStart.App.App();
                app.InitializeComponent();

                var settings = new AppSettings();
                var settingsService = new AppSettingsService();
                var searchService = new SemanticSearchService(settings);
                var activation = new ActivationManager(Dispatcher.CurrentDispatcher, () => { }, settings);

                var window = new SettingsWindow(settingsService, searchService, activation);
                window.Close();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Constructing the settings window timed out.");
        Assert.Null(failure);
    }
}
