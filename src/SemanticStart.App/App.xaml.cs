using System.Windows;
using SemanticStart.Core;

namespace SemanticStart.App;

public partial class App : System.Windows.Application
{
    private AppSettingsService? _settingsService;
    private SemanticSearchService? _searchService;
    private OverlayViewModel? _overlayViewModel;
    private OverlayWindow? _overlayWindow;
    private ActivationManager? _activationManager;
    private DictationController? _dictation;
    private SpeechTelemetry? _speechTelemetry;
    private TrayIconService? _trayIconService;
    private IndexRebuildCoordinator? _rebuilds;
    private IndexRefreshScheduler? _refreshScheduler;
    private SettingsWindow? _settingsWindow;
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateSignal;
    private CancellationTokenSource? _mcpCancellation;
    private Task? _mcpTask;
    private volatile bool _shuttingDown;

    private const string ActivateSignalName = @"Local\SemanticStart.Activate";

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppPaths.EnsureCreated();
        Log.Initialize();
        InstallCrashLogging();

        // Before anything speech-related exists, so the model load and the first dictation turn
        // are traced like everything else rather than being the one part of the app that is quiet.
        _speechTelemetry = SpeechTelemetry.Install();
        if (e.Args.Any(a => string.Equals(a, "--startup", StringComparison.OrdinalIgnoreCase)))
            Log.Info("Started from the Windows login registration.");
        ThemeService.Initialize(this);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var mcpMode = e.Args.Any(a => string.Equals(a, "--mcp", StringComparison.OrdinalIgnoreCase));
        if (mcpMode)
        {
            _mcpCancellation = new CancellationTokenSource();
            _mcpTask = RunMcpServerAsync(e.Args, _mcpCancellation.Token);
            _ = ShutdownWhenMcpEndsAsync(_mcpTask);
        }

        if (e.Args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            var code = await RunSelfTestAsync();
            Shutdown(code);
            return;
        }

        if (!TryAcquireSingleInstance(activateExisting: !mcpMode))
        {
            if (mcpMode)
                return;

            // Another copy owns the hotkey; hand the activation over to it and get out of the way.
            Shutdown();
            return;
        }

        _settingsService = new AppSettingsService();
        var settings = _settingsService.Load();
        _searchService = new SemanticSearchService(settings);
        _rebuilds = new IndexRebuildCoordinator(_searchService);
        _rebuilds.StateChanged += OnRebuildStateChanged;
        var iconProvider = new IconProvider();
        _overlayViewModel = new OverlayViewModel(_searchService, iconProvider, settings);
        _overlayWindow = new OverlayWindow(_overlayViewModel);
        _overlayWindow.Hide();
        _overlayWindow.SetDictationEnabled(settings.DictationEnabled);
        _overlayWindow.SettingsRequested = () => ShowSettingsWindow();

        _dictation = new DictationController(
            _overlayWindow.Dispatcher,
            _overlayViewModel,
            settings,
            () => _activationManager?.IsDictationHotKeyHeld() ?? false,
            () => _overlayWindow.ShowOverlay(),
            Log.CreateLogger<DictationController>());
        _overlayWindow.DictationRequested = () => _dictation.Toggle();
        _overlayWindow.DictationStopRequested = () => _dictation.Stop();

        _activationManager = new ActivationManager(
            _overlayWindow.Dispatcher,
            () => _overlayWindow.ShowOverlay(),
            settings,
            () => _dictation.Toggle());
        _activationManager.HotKeyRegistered += OnHotKeyRegistered;
        _activationManager.DictationHotKeyRegistered += OnDictationHotKeyRegistered;
        _activationManager.Start();
        _trayIconService = new TrayIconService(_overlayWindow, () => ShowSettingsWindow(), () => RebuildIndexFromTrayAsync(), () => Shutdown(), settings);

        // Started before the index is loaded on purpose: the scheduler re-checks every minute and
        // declines to do anything while the index is empty, so there is nothing to sequence here.
        _refreshScheduler = new IndexRefreshScheduler(
            _settingsService,
            _searchService,
            _rebuilds,
            () => _overlayWindow?.IsOpen ?? false);
        _refreshScheduler.Start();

        // The recogniser is loaded now rather than on the first press of the dictation hotkey:
        // model load is the expensive part and it must not be paid by someone mid-sentence.
        if (settings.DictationEnabled)
            _ = _dictation.WarmStartAsync();

        _ = WarmStartAsync(settings);
    }

    /// <summary>
    /// A background tray app that dies silently is undiagnosable: the user sees only that the
    /// hotkey stopped working. Record anything fatal, and keep the app alive through UI-thread
    /// faults so a bug in one keystroke path cannot take down the whole process.
    /// </summary>
    private void InstallCrashLogging()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Unhandled exception on the UI thread");
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Log.Error(ex, "Unhandled exception; the process is terminating");
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };
    }

    /// <summary>
    /// Tells the user which hotkey actually works. A silent failure here is the worst outcome
    /// available: the app is running correctly but appears completely dead.
    /// </summary>
    private void OnHotKeyRegistered(string? hotKey, bool differsFromRequested)
    {
        if (!differsFromRequested)
            return;

        var message = hotKey is null
            ? "No hotkey could be registered because every candidate is already in use. Open SemanticStart from the tray icon, then pick a free hotkey in Settings."
            : $"Your preferred hotkey was already taken by another app, so SemanticStart is using {hotKey} instead. You can change this in Settings.";

        Dispatcher.BeginInvoke(new Action(() => _trayIconService?.ShowMessage("SemanticStart", message)));
    }

    /// <summary>
    /// The same report for the dictation chord. Quieter than the activation one: dictation is an
    /// alternative way to type, so losing its hotkey is an inconvenience rather than a dead app.
    /// </summary>
    private void OnDictationHotKeyRegistered(string? hotKey, bool differsFromRequested)
    {
        if (!differsFromRequested)
            return;

        var message = hotKey is null
            ? "No dictation hotkey could be registered because every candidate is already in use. You can pick a free one in Settings."
            : $"Your preferred dictation hotkey was already taken by another app, so SemanticStart is using {hotKey} instead.";

        Dispatcher.BeginInvoke(new Action(() => _trayIconService?.ShowMessage("SemanticStart", message)));
    }

    /// <summary>
    /// Ensures exactly one copy runs. Global hotkeys are owned by whichever process registers
    /// first, so a second instance would start, fail to register, and sit there doing nothing
    /// while appearing to be running. Instead the second instance asks the first to show itself
    /// and then exits, which also gives "run it again" the behaviour users expect from a launcher.
    /// </summary>
    private bool TryAcquireSingleInstance(bool activateExisting)
    {
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, @"Local\SemanticStart.Instance", out var createdNew);

            if (!createdNew)
            {
                if (activateExisting && EventWaitHandle.TryOpenExisting(ActivateSignalName, out var existing))
                {
                    existing.Set();
                    existing.Dispose();
                }

                return false;
            }

            _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateSignalName);

            var listener = new Thread(WaitForActivationRequests)
            {
                IsBackground = true,
                Name = "SemanticStart.InstanceListener",
            };
            listener.Start();

            return true;
        }
        catch (Exception ex)
        {
            // Never let instance coordination stop the app from starting.
            Log.Error(ex, "Single-instance check failed");
            return true;
        }
    }

    private void WaitForActivationRequests()
    {
        while (_activateSignal is not null && !_shuttingDown)
        {
            try
            {
                if (!_activateSignal.WaitOne(TimeSpan.FromSeconds(1)))
                    continue;

                Dispatcher.BeginInvoke(new Action(() => _overlayWindow?.ShowOverlay()));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Instance activation listener failed");
                return;
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mcpCancellation?.Cancel();
        _trayIconService?.Dispose();
        _refreshScheduler?.Dispose();
        _rebuilds?.Dispose();
        _activationManager?.Dispose();
        _dictation?.Dispose();
        _speechTelemetry?.Dispose();
        _searchService?.Dispose();
        _shuttingDown = true;
        _activateSignal?.Dispose();
        _instanceMutex?.Dispose();
        _mcpCancellation?.Dispose();
        ThemeService.Shutdown();

        // Last, so anything disposed above still has somewhere to report a failure.
        Log.Shutdown();
        base.OnExit(e);
    }

    private static async Task RunMcpServerAsync(string[] args, CancellationToken cancellationToken)
    {
        try
        {
            await McpServerHost.RunAsync(args, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "MCP server failed");
            Console.Error.WriteLine($"SemanticStart MCP server failed: {ex.Message}");
        }
    }

    private async Task ShutdownWhenMcpEndsAsync(Task mcpTask)
    {
        await mcpTask;
        await Dispatcher.InvokeAsync(Shutdown);
    }

    private async Task WarmStartAsync(AppSettings settings)
    {
        if (_searchService is null)
            return;

        try
        {
            await _searchService.InitializeAsync(CancellationToken.None);
            if (_searchService.Count > 0)
                return;

            if (!settings.SetupCompleted)
            {
                // First run opens settings in setup mode and builds only when the user asks, so
                // every choice that shapes the first index is visible before it is made.
                Dispatcher.Invoke(() => ShowSettingsWindow());
                return;
            }

            await RebuildIndexFromTrayAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Warm start failed");
        }
    }

    /// <summary>
    /// Shows the settings window, reusing the one already open.
    ///
    /// There are three ways in - the tray menu, the overlay's gear, and a tray rebuild - and each
    /// used to construct its own window. Two copies of a page that writes the same settings file
    /// means whichever is closed last wins, and the progress of a rebuild appears in only one of
    /// them.
    /// </summary>
    private SettingsWindow ShowSettingsWindow()
    {
        if (_settingsService is null || _searchService is null || _activationManager is null || _rebuilds is null)
            throw new InvalidOperationException("Application services are not ready.");

        if (_settingsWindow is null)
        {
            // Until the first build, every way into settings is a way into setup, so the page leads
            // with Build index wherever the user opened it from.
            var setupMode = !_settingsService.Load().SetupCompleted && _searchService.Count == 0 && !_rebuilds.IsRunning;
            _settingsWindow = new SettingsWindow(
                _settingsService,
                _searchService,
                _activationManager,
                _rebuilds,
                setupMode,
                _dictation,
                enabled => _overlayWindow?.SetDictationEnabled(enabled));
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        else if (_settingsWindow.WindowState == WindowState.Minimized)
        {
            _settingsWindow.WindowState = WindowState.Normal;
        }

        _settingsWindow.Activate();
        return _settingsWindow;
    }

    /// <summary>
    /// Tells the user a background rebuild finished. Now that closing the settings window no longer
    /// stops the build, the window that was showing progress is often gone by the time it ends.
    ///
    /// Only for rebuilds the user asked for. A scheduled refresh that announced itself every hour
    /// would be a notification about something nobody requested and nobody needs to act on, which
    /// is how a useful notification channel gets muted.
    /// </summary>
    private void OnRebuildStateChanged(object? sender, IndexRebuildState state)
    {
        if (state.Outcome is not (RebuildOutcome.Completed or RebuildOutcome.Failed))
            return;

        if (state.Trigger == RebuildTrigger.Automatic)
        {
            Log.Info($"Background index refresh {state.Outcome.ToString()!.ToLowerInvariant()}; {_searchService?.Count ?? 0} entities loaded.");
            return;
        }

        var message = state.Outcome == RebuildOutcome.Completed
            ? $"Indexing finished. {_searchService?.Count ?? 0} apps, tools, and settings are searchable."
            : "Indexing failed; see the log for details.";

        Dispatcher.BeginInvoke(new Action(() => _trayIconService?.ShowMessage("SemanticStart", message)));
    }

    private Task RebuildIndexFromTrayAsync()
    {
        try
        {
            var window = ShowSettingsWindow();
            return window.RebuildIndexAsync(force: true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Tray rebuild failed");
            return Task.CompletedTask;
        }
    }

    private static async Task<int> RunSelfTestAsync()
    {
        try
        {
            var settings = new AppSettingsService().Load();
            using var searchService = new SemanticSearchService(settings);
            var viewModel = new OverlayViewModel(searchService, new IconProvider(), settings);
            await searchService.InitializeAsync(CancellationToken.None);
            await viewModel.SearchNowAsync("free up disk space", CancellationToken.None);

            Console.WriteLine($"\"free up disk space\" -> {viewModel.Results.Count} results (index: {searchService.Count})");
            for (var i = 0; i < Math.Min(3, viewModel.Results.Count); i++)
            {
                var hit = viewModel.Results[i];
                Console.WriteLine($"{i + 1}. {hit.DisplayName} [{hit.KindBadge}] {hit.Summary}");
            }

            return viewModel.Results.Count > 0 ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
