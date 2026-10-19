using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SemanticStart.App;
using SemanticStart.Core;
using SemanticStart.Core.Speech;
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
        string? dictationTabHeader = null;
        string? speechModelName = null;
        string? speechModelLocation = null;
        string? speechModelReadiness = null;
        string? speechModelSize = null;
        var disabledDictationButtonVisible = true;
        var disabledDictationStartedOverlay = true;

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
                var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
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
                dictationTabHeader = window.DictationSettingsTabHeader;
                speechModelName = window.SpeechModelNameText;
                speechModelLocation = window.SpeechModelLocationText;
                speechModelReadiness = window.SpeechModelReadinessText;
                speechModelSize = window.SpeechModelSizeText;
                var viewModel = new OverlayViewModel(searchService, new IconProvider(), settings);
                var overlay = new OverlayWindow(viewModel);
                overlay.SetDictationEnabled(settings.DictationEnabled);
                disabledDictationButtonVisible = overlay.IsDictationButtonVisible;
                var dictationOverlayShown = false;
                using (var controller = new DictationController(
                           Dispatcher.CurrentDispatcher,
                           viewModel,
                           settings,
                           () => false,
                           () => dictationOverlayShown = true))
                {
                    controller.Toggle();
                    disabledDictationStartedOverlay = dictationOverlayShown;
                }

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
        Assert.Equal("Dictation", dictationTabHeader);
        Assert.Contains(SpeechModelOptions.Default.ModelId, speechModelName);
        Assert.Contains(AppPaths.ModelsDirectory, speechModelLocation);
        Assert.Contains("Readiness:", speechModelReadiness);
        Assert.Contains("Size:", speechModelSize);
        Assert.False(disabledDictationButtonVisible);
        Assert.False(disabledDictationStartedOverlay);

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

    /// <summary>
    /// A dictation press that cannot be served yet must still be visibly acknowledged. On the
    /// first run the gap between the press and the microphone opening is a ~665 MB download, and an
    /// overlay that shows nothing in that window is indistinguishable from a hotkey that never
    /// registered - which is the bug report that would follow.
    ///
    /// <para>
    /// Same class as the test below for the same reason: one <see cref="Application"/> per process.
    /// </para>
    /// </summary>
    [Fact]
    public void ADictationPressIsAcknowledgedWhileTheRecogniserIsStillBeingPrepared()
    {
        Exception? failure = null;
        var preparingWithoutProgress = false;
        var indeterminateWithoutProgress = false;
        string? statusWhilePreparing = null;
        var indeterminateWithProgress = true;
        var progressValue = -1.0;
        string? statusWhileDownloading = null;
        var preparingAfterListeningStarted = true;
        var preparingAfterFailure = true;
        string? statusAfterFailure = null;

        var thread = new Thread(() =>
        {
            try
            {
                _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

                var settings = new AppSettings();
                using var searchService = new SemanticSearchService(settings);
                var viewModel = new OverlayViewModel(searchService, new IconProvider(), settings);

                viewModel.ReportDictationPreparing();
                preparingWithoutProgress = viewModel.IsPreparingDictation;
                indeterminateWithoutProgress = viewModel.IsDictationPreparationIndeterminate;
                statusWhilePreparing = viewModel.Status;

                viewModel.ReportDictationPreparing(0.42);
                indeterminateWithProgress = viewModel.IsDictationPreparationIndeterminate;
                progressValue = viewModel.DictationPreparationProgress;
                statusWhileDownloading = viewModel.Status;

                // The microphone opening replaces the preparation state rather than sitting
                // alongside it, or the footer would show a download bar during the turn.
                viewModel.BeginDictation();
                preparingAfterListeningStarted = viewModel.IsPreparingDictation;
                viewModel.EndDictation();

                // A preparation that ends in failure must clear the bar and say why, not leave a
                // bar turning forever.
                viewModel.ReportDictationPreparing();
                viewModel.EndDictation("Dictation is unavailable on this machine.");
                preparingAfterFailure = viewModel.IsPreparingDictation;
                statusAfterFailure = viewModel.Status;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The dictation preparation test timed out.");
        Assert.Null(failure);

        Assert.True(preparingWithoutProgress, "A press during warm start left the overlay showing nothing.");
        Assert.True(indeterminateWithoutProgress, "A load with no measurable progress showed a bar stuck at zero.");
        Assert.Equal(OverlayViewModel.PreparingStatus, statusWhilePreparing);

        Assert.False(indeterminateWithProgress, "A download with a known fraction was still shown as indeterminate.");
        Assert.Equal(0.42, progressValue, 3);
        Assert.StartsWith(OverlayViewModel.DownloadingModelStatus, statusWhileDownloading, StringComparison.Ordinal);
        Assert.Contains("42", statusWhileDownloading, StringComparison.Ordinal);

        Assert.False(preparingAfterListeningStarted, "The preparation bar stayed up once the microphone opened.");
        Assert.False(preparingAfterFailure, "The preparation bar stayed up after dictation failed to start.");
        Assert.Equal("Dictation is unavailable on this machine.", statusAfterFailure);
    }

    /// <summary>
    /// Dictation fills the same query box as typing, so it has to obey the same rule: a transcript
    /// the recogniser is still revising waits for the debouncer, and a finished one searches at
    /// once. Getting this backwards would either search every few hundred milliseconds against
    /// text about to change, or leave the user who has stopped talking staring at stale results.
    ///
    /// <para>
    /// Runs in this class rather than its own because xunit does not parallelise inside a class,
    /// and the view model's debouncer marshals through <see cref="Application.Current"/> - which
    /// WPF permits exactly one of per process.
    /// </para>
    /// </summary>
    [Fact]
    public void DictationPartialsWaitForTheDebouncerAndFinalTranscriptsSearchImmediately()
    {
        Exception? failure = null;
        string? queryAfterPartial = null;
        string? statusAfterPartial = null;
        string? queryAfterFinal = null;
        string? statusAfterFinal = null;
        var listeningDuringPartial = false;
        var listeningAfterEnd = true;

        var thread = new Thread(() =>
        {
            try
            {
                _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

                var settings = new AppSettings();
                using var searchService = new SemanticSearchService(settings);
                var viewModel = new OverlayViewModel(searchService, new IconProvider(), settings);

                viewModel.Query = "open";
                viewModel.BeginDictation();

                viewModel.ApplyPartialTranscript("note");
                queryAfterPartial = viewModel.Query;
                statusAfterPartial = viewModel.Status;
                listeningDuringPartial = viewModel.IsListening;

                var flush = viewModel.ApplyFinalTranscriptAsync("notepad");
                while (!flush.IsCompleted)
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

                queryAfterFinal = viewModel.Query;
                statusAfterFinal = viewModel.Status;

                viewModel.EndDictation();
                listeningAfterEnd = viewModel.IsListening;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The dictation view model test timed out.");
        Assert.Null(failure);

        // What was already typed survives; the spoken words are added to it rather than over it.
        Assert.Equal("open note", queryAfterPartial);
        Assert.Equal("open notepad", queryAfterFinal);
        Assert.True(listeningDuringPartial);
        Assert.False(listeningAfterEnd);

        // The status line is the visible proof of which path ran: still "Listening..." while the
        // partial waits out the debounce interval, and replaced by the search's own answer once
        // the final transcript has been flushed.
        Assert.Equal(OverlayViewModel.ListeningStatus, statusAfterPartial);
        Assert.NotEqual(OverlayViewModel.ListeningStatus, statusAfterFinal);
    }

    [Theory]
    [InlineData("", false, false)]
    [InlineData("", true, false)]
    [InlineData("open ", false, false)]
    [InlineData("open ", true, false)]
    [InlineData("", false, true)]
    [InlineData("", true, true)]
    [InlineData("open ", false, true)]
    [InlineData("open ", true, true)]
    public void DictationRetryReplacesOnlyUneditedSpeech(string typed, bool final, bool edit)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var settings = new AppSettings();
                using var searchService = new SemanticSearchService(settings);
                var viewModel = new OverlayViewModel(searchService, new IconProvider(), settings);
                viewModel.Query = typed;
                viewModel.BeginDictation();
                viewModel.ApplyPartialTranscript("note");
                if (final)
                {
                    var flush = viewModel.ApplyFinalTranscriptAsync("notepad");
                    while (!flush.IsCompleted)
                        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
                    flush.GetAwaiter().GetResult();
                }
                viewModel.EndDictation();

                if (edit)
                    viewModel.Query = "edited query";
                var expectedPrefix = edit ? "edited query" : typed;
                viewModel.BeginDictation();
                Assert.Equal(expectedPrefix, viewModel.Query);
                viewModel.ApplyPartialTranscript("calculator");
                Assert.Equal((string.IsNullOrWhiteSpace(expectedPrefix) ? "" : expectedPrefix.TrimEnd() + " ")
                    + "calculator", viewModel.Query);
                viewModel.EndDictation();

                viewModel.BeginDictation();
                Assert.Equal(expectedPrefix, viewModel.Query);
                viewModel.EndDictation();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The dictation retry test timed out.");
        Assert.Null(failure);
    }

    [Fact]
    public void ClearSearchButtonStopsDictationAndErasesTheQuery()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/SemanticStart.App;component/Theme.xaml"),
                });
                var settings = new AppSettings();
                using var searchService = new SemanticSearchService(settings);
                var viewModel = new OverlayViewModel(searchService, new IconProvider(), settings);
                var window = new OverlayWindow(viewModel);
                var stopped = false;
                window.DictationStopRequested = () =>
                {
                    stopped = true;
                    viewModel.EndDictation();
                };
                viewModel.Query = "typed";
                viewModel.BeginDictation();
                viewModel.ApplyPartialTranscript("words");
                var button = (System.Windows.Controls.Button)window.FindName("ClearQueryButton");
                button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                Assert.True(stopped);
                Assert.False(viewModel.IsListening);
                Assert.Equal("", viewModel.Query);
                viewModel.BeginDictation();
                viewModel.ApplyPartialTranscript("new");
                Assert.Equal("new", viewModel.Query);
                viewModel.EndDictation();

                viewModel.Query = "typed prefix";
                viewModel.BeginDictation();
                viewModel.ApplyPartialTranscript("old speech");
                viewModel.EndDictation();
                viewModel.Clear();
                viewModel.BeginDictation();
                Assert.Equal("", viewModel.Query);
                viewModel.EndDictation();
                window.Close();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The clear search test timed out.");
        Assert.Null(failure);
    }

    [Fact]
    public void KeyboardEditWhileListeningIsNotOverwrittenByLaterTranscripts()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var settings = new AppSettings();
                using var searchService = new SemanticSearchService(settings);
                var viewModel = new OverlayViewModel(searchService, new IconProvider(), settings);
                viewModel.BeginDictation();
                viewModel.ApplyPartialTranscript("note");
                viewModel.Query = "open note";
                viewModel.ApplyPartialTranscript("notep");
                viewModel.ApplyFinalTranscriptAsync("notepad").GetAwaiter().GetResult();
                Assert.Equal("open note", viewModel.Query);
                viewModel.EndDictation();
                viewModel.BeginDictation();
                Assert.Equal("open note", viewModel.Query);
                viewModel.ApplyPartialTranscript("settings");
                Assert.Equal("open note settings", viewModel.Query);
                viewModel.EndDictation();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The keyboard edit test timed out.");
        Assert.Null(failure);
    }

    [Fact]
    public void StoppingDuringPreparationInvalidatesThePendingStart()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                var settings = new AppSettings { DictationEnabled = true };
                using var searchService = new SemanticSearchService(settings);
                var viewModel = new OverlayViewModel(searchService, new IconProvider(), settings);
                using var controller = new DictationController(
                    Dispatcher.CurrentDispatcher, viewModel, settings, () => false, () => { });
                var preparation = new TaskCompletionSource<SemanticStart.Core.Speech.DictationEngine?>();
                typeof(DictationController).GetField("_warmStart",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .SetValue(controller, preparation.Task);
                var start = (Task)typeof(DictationController).GetMethod("StartAsync",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(controller, null)!;
                Assert.True(viewModel.IsPreparingDictation);
                controller.Stop();
                var stoppedStatus = viewModel.Status;
                controller.Toggle();
                Assert.True(viewModel.IsPreparingDictation);
                preparation.SetResult(null);
                while (!start.IsCompleted)
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
                start.GetAwaiter().GetResult();
                Assert.False(viewModel.IsPreparingDictation);
                Assert.False(controller.IsListening);
                Assert.Equal(stoppedStatus, viewModel.Status);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "The preparation cancellation test timed out.");
        Assert.Null(failure);
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
