using System.Windows.Controls;
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
        RunOnUiThread(_ => { });
    }

    [Fact]
    public void PickingAPresetPutsTheChordInTheBox_NotTheControlsTypeName()
    {
        string? text = null;

        // An editable ComboBox displays the selected item's ToString(). ComboBoxItem does not
        // override it, so choosing a preset would otherwise fill the box with
        // "System.Windows.Controls.ComboBoxItem: Win+Alt+S" and store that as the hotkey.
        RunOnUiThread(window =>
        {
            var box = (ComboBox)window.FindName("HotKeyBox");
            box.SelectedIndex = 1;
            text = box.Text;
        });

        Assert.True(
            HotKeySpec.TryParse(text, out _, out var error),
            $"Selecting a preset produced '{text}', which is not a usable chord: {error}");
    }

    private static readonly object AppGate = new();

    private static void RunOnUiThread(Action<SettingsWindow> body)
    {
        Exception? failure = null;

        // WPF windows require an STA thread with a dispatcher.
        var thread = new Thread(() =>
        {
            try
            {
                // The window's styles live in App.xaml, and only an Application registers them.
                // WPF permits exactly one per process, so this is created once and shared: without
                // the guard the second test in a run dies on "Cannot create more than one
                // System.Windows.Application instance", which looks exactly like a product fault.
                lock (AppGate)
                {
                    if (System.Windows.Application.Current is null)
                    {
                        var app = new SemanticStart.App.App();
                        app.InitializeComponent();
                    }
                }

                var settings = new AppSettings();
                var settingsService = new AppSettingsService();
                var searchService = new SemanticSearchService(settings);
                var activation = new ActivationManager(Dispatcher.CurrentDispatcher, () => { }, settings);

                var window = new SettingsWindow(settingsService, searchService, activation);
                body(window);
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
    }
}
