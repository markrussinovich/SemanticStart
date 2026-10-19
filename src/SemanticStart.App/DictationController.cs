using System.Diagnostics;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using SemanticStart.Core;
using SemanticStart.Core.Speech;

namespace SemanticStart.App;

/// <summary>
/// Connects the dictation hotkey to the overlay: keeps a recogniser warm while dictation is enabled,
/// runs a listening turn on demand, and puts transcripts into the query box.
///
/// <para>
/// The engine is created once at startup rather than on the first press. Loading the model is
/// hundreds of milliseconds of work that would otherwise land in front of a user who has already
/// started talking, and a first utterance that loses its opening words is worse than one that never
/// started. Everything on the hotkey path here is bookkeeping.
/// </para>
///
/// <para>
/// Speech never leaves the machine: the transcriber is an ONNX model reading the local microphone.
/// See <see cref="ISpeechTranscriber"/> for why the platform's own recognisers are not used.
/// </para>
/// </summary>
public sealed class DictationController : IDisposable
{
    /// <summary>
    /// How often the dictation chord is checked for release in push-to-talk mode. Short enough to
    /// feel immediate and long enough to be free; release is noticed within one interval.
    /// </summary>
    private static readonly TimeSpan PushToTalkPollInterval = TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// How long a press may wait for the recogniser before the microphone is no longer opened for
    /// it.
    ///
    /// <para>
    /// A warm recogniser is instant and a cold one is a few hundred milliseconds, so in the
    /// ordinary case the press flows straight into listening. The case this guards is the first
    /// run, where the wait is a ~665 MB download: opening the microphone at the end of that would
    /// mean recording started minutes after the keypress, quite possibly after the user had
    /// stopped paying attention, which is the one thing a microphone must never do. Past this
    /// threshold the overlay reports that dictation is ready and waits to be asked again.
    /// </para>
    /// </summary>
    private static readonly TimeSpan ListenAfterPreparationLimit = TimeSpan.FromSeconds(5);

    private readonly Dispatcher _dispatcher;
    private readonly OverlayViewModel _viewModel;

    /// <summary>
    /// Injected rather than taken from the static <see cref="Log"/>, so the category on these
    /// lines is this type and a test can be handed a logger that records instead of writing to
    /// <c>%LOCALAPPDATA%</c>.
    /// </summary>
    private readonly ILogger _logger;

    private readonly Func<bool> _isHotKeyHeld;
    private readonly Action _showOverlay;
    /// <summary>
    /// Admits one start at a time, and only one.
    ///
    /// <para>
    /// A semaphore rather than a lock because starting is asynchronous: it may await the first
    /// model load, and a <c>lock</c> cannot be held across an await. It is used with
    /// <c>WaitAsync(0)</c>, which returns false rather than waiting, so this is a non-blocking
    /// mutex, not a queue - a second hotkey press while a start is still in flight is dropped on
    /// the spot and nothing is buffered to run afterwards.
    /// </para>
    /// <para>
    /// That is deliberate for the first press of a session, when the warm start may still be
    /// loading the engine or downloading the model: queueing would mean a press, a long silence,
    /// and then the microphone opening by itself once the download finished, which is the one
    /// behaviour a microphone must never have. Toggling off is not gated by it - the stop path
    /// cancels the session directly - so a press during startup is a no-op, not a lost toggle, and
    /// the user is never left out of sync with the indicator.
    /// </para>
    /// </summary>
    private readonly SemaphoreSlim _startGate = new(1, 1);

    /// <summary>
    /// Cancels the warm start. Loading the engine can involve a model download, so without this a
    /// shutdown or opt-out during first run would leave an HTTP read running against a temp file
    /// until HttpClient's own timeout, and would finish by constructing a recognizer nobody wants.
    /// </summary>
    private CancellationTokenSource _startup = new();

    /// <summary>
    /// How many presses are currently waiting on the warm start. Model-download progress is only
    /// pushed to the overlay while this is non-zero: during the silent background warm start there
    /// is nobody waiting, and overwriting the status line of an overlay the user opened to type in
    /// would be the app talking over them.
    /// </summary>
    private int _awaitingPreparation;

    /// <summary>Last percentage shown, so an identical status line is not re-published hundreds of times.</summary>
    private int _reportedPreparationPercent = -1;
    private int _stopVersion;

    private AppSettings _settings;
    private DictationEngine? _engine;
    private Task<DictationEngine?>? _warmStart;
    private CancellationTokenSource? _session;
    private DispatcherTimer? _pushToTalkTimer;
    private string? _unavailableReason;
    private bool _disposed;

    public DictationController(
        Dispatcher dispatcher,
        OverlayViewModel viewModel,
        AppSettings settings,
        Func<bool> isHotKeyHeld,
        Action showOverlay,
        ILogger? logger = null)
    {
        _logger = logger ?? Log.CreateLogger<DictationController>();
        _dispatcher = dispatcher;
        _viewModel = viewModel;
        _settings = settings;
        _isHotKeyHeld = isHotKeyHeld;
        _showOverlay = showOverlay;
    }

    /// <summary>Whether a listening turn is in progress.</summary>
    public bool IsListening => _session is not null;

    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;

        if (!settings.DictationEnabled)
        {
            Stop();
            _startup.Cancel();
        }
    }

    /// <summary>
    /// Loads the recogniser, once, in the background. Failure is remembered rather than thrown:
    /// the rest of the app works without dictation, and the reason is what the user needs when
    /// they press the hotkey and nothing happens.
    /// </summary>
    public async Task WarmStartAsync()
    {
        if (_disposed || !_settings.DictationEnabled)
            return;

        if (_startup.IsCancellationRequested)
        {
            if (_warmStart is { } cancelledWarmStart)
                await cancelledWarmStart.ConfigureAwait(true);

            if (_disposed || !_settings.DictationEnabled)
                return;

            if (_startup.IsCancellationRequested)
            {
                _engine?.Dispose();
                _engine = null;
                _warmStart = null;
                _unavailableReason = null;
                _startup.Dispose();
                _startup = new CancellationTokenSource();
            }
        }

        // Task.Run rather than TaskFactory.StartNew: this is called on the UI thread, and
        // StartNew queues to TaskScheduler.Current - which is the UI scheduler whenever the caller
        // is itself inside a dispatcher-marshalled continuation - so the model load would run on
        // the STA thread it exists to stay off. Task.Run always means TaskScheduler.Default, and
        // unwraps the async delegate instead of handing back a Task<Task>. LongRunning is wrong
        // here for the same reason: the load is mostly awaited I/O, and the CPU-bound stretch is
        // short enough not to be worth a dedicated thread.
        _warmStart ??= Task.Run(CreateEngineAsync);
        await _warmStart.ConfigureAwait(true);
    }

    /// <summary>
    /// The dictation hotkey. Opens the overlay and starts listening; in toggle mode a second press
    /// stops. Push-to-talk stops on release instead, polled rather than hooked - see
    /// <see cref="ActivationManager.IsDictationHotKeyHeld"/>.
    /// </summary>
    public void Toggle()
    {
        if (_disposed || !_settings.DictationEnabled)
            return;

        _showOverlay();

        if (IsListening)
        {
            if (!_settings.DictationPushToTalk)
                Stop();
            return;
        }

        _ = StartAsync();
    }

    public void Stop()
    {
        _stopVersion++;
        StopPushToTalkPolling();

        // Cancelled but not disposed: the listening task is still holding this token, and
        // disposing it underneath ONNX and the capture wrapper is how a cancellation turns into an
        // ObjectDisposedException. RunSessionAsync's finally owns the disposal.
        _session?.Cancel();
        _viewModel.EndDictation();
    }

    public async Task UnloadAsync()
    {
        Stop();
        _startup.Cancel();

        if (_warmStart is { } warmStart)
            await warmStart.ConfigureAwait(true);

        await _startGate.WaitAsync().ConfigureAwait(true);
        try
        {
            while (_session is not null)
                await Task.Delay(20).ConfigureAwait(true);

            _engine?.Dispose();
            _engine = null;
        }
        finally
        {
            _startGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        Stop();
        _startup.Cancel();

        // The start gate is deliberately not disposed: a start that is still between its wait and
        // its release would then throw on a background task during shutdown, which is a crash log
        // for no benefit. Letting it be collected costs nothing.

        // The warm-start task owns the engine until it completes, so disposing here would race it.
        // Hand the disposal to whichever finishes last.
        if (_warmStart is { } warmStart)
        {
            _ = warmStart.ContinueWith(
                task =>
                {
                    // Nothing awaits this continuation, so an exception here would be an
                    // unobserved task exception raised long after shutdown, attributed to nothing.
                    // Releasing native ONNX sessions is exactly where that would happen.
                    try
                    {
                        task.Result?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Disposing the dictation engine failed.");
                    }
                },
                // The load itself is cancelled above, by _startup. This token is the
                // continuation's own, and must stay None: a cancelled continuation would skip the
                // disposal and leak the native ONNX sessions it exists to release.
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion,
                TaskScheduler.Default).ContinueWith(_ => _startup.Dispose(), TaskScheduler.Default);
        }
        else
        {
            try
            {
                _engine?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Disposing the dictation engine failed.");
            }

            _startup.Dispose();
        }
    }

    private async Task<DictationEngine?> CreateEngineAsync()
    {
        var started = Stopwatch.StartNew();
        var startupToken = _startup.Token;
        try
        {
            var models = new SpeechModelBootstrapper();
            var selector = new SpeechEngineSelector([new SherpaOnnxSpeechProvider(models)]);
            var startup = await selector
                .StartAsync(new Progress<double>(OnPreparationProgress), startupToken)
                .ConfigureAwait(false);

            // Every provider that said no, with its reason, rather than only the first one that
            // ends up in the status line: with a second engine this is the only record of why the
            // one that was expected to win did not.
            foreach (var rejection in startup.Rejected)
            {
                if (rejection.Exception is { } failure)
                    _logger.LogError(failure, "Speech provider {ProviderId} was rejected: {Reason}", rejection.Metadata.Id, rejection.Reason);
                else
                    _logger.LogInformation("Speech provider {ProviderId} was rejected: {Reason}", rejection.Metadata.Id, rejection.Reason);
            }

            if (!startup.IsAvailable || startup.Transcriber is null)
            {
                _unavailableReason = startup.FailureReason;
                _logger.LogInformation("Dictation is unavailable: {Reason}", _unavailableReason);
                return null;
            }

            var engine = new DictationEngine(
                startup.Transcriber,
                new SileroVoiceActivityDetector(models.VadPath),
                () => new WasapiMicrophoneCapture());

            _engine = engine;
            _logger.LogInformation(
                "Dictation ready: {Engine} ({ModelId}) in {ElapsedMs} ms.",
                engine.Metadata.DisplayName,
                engine.Metadata.ModelId,
                started.ElapsedMilliseconds);
            return engine;
        }
        catch (OperationCanceledException) when (startupToken.IsCancellationRequested)
        {
            // Cancellation, not a failure. Recorded rather than dropped so a log that ends here is
            // distinguishable from one where the load simply never finished.
            _logger.LogInformation(
                "The dictation engine load was cancelled after {ElapsedMs} ms.",
                started.ElapsedMilliseconds);
            return null;
        }
        catch (Exception ex)
        {
            _unavailableReason = "The speech model could not be loaded; see the log for details.";
            _logger.LogError(ex, "Failed to start the dictation engine after {ElapsedMs} ms.", started.ElapsedMilliseconds);
            return null;
        }
    }

    private async Task StartAsync()
    {
        if (_disposed || !_settings.DictationEnabled)
            return;

        if (!await _startGate.WaitAsync(0).ConfigureAwait(true))
        {
            // A start is already in flight. The press is still answered: on the first run that
            // start is a model download, and silence here is indistinguishable from a hotkey that
            // never registered. Only when the engine is not ready yet - once it is, losing this
            // race is a sub-millisecond window and saying anything would just flicker.
            if (_engine is null)
                _viewModel.ReportDictationPreparing();

            return;
        }

        CancellationTokenSource? session = null;
        try
        {
            var stopVersion = _stopVersion;
            var waited = Stopwatch.StartNew();
            var engine = await WarmStartEngineAsync().ConfigureAwait(true);
            waited.Stop();

            if (_disposed)
                return;
            if (stopVersion != _stopVersion || !_settings.DictationEnabled)
            {
                _viewModel.EndDictation();
                return;
            }

            if (engine is null)
            {
                _viewModel.EndDictation(_unavailableReason ?? "Dictation is unavailable on this machine.");
                return;
            }

            if (_disposed || IsListening)
                return;

            if (waited.Elapsed > ListenAfterPreparationLimit)
            {
                _logger.LogInformation(
                    "Dictation became ready after {ElapsedMs} ms; waiting for a fresh press rather than opening the microphone.",
                    waited.ElapsedMilliseconds);
                _viewModel.EndDictation($"Dictation is ready. Press {_settings.DictationHotKey} to talk.");
                return;
            }

            session = new CancellationTokenSource();
            _session = session;
            _viewModel.BeginDictation();
            StartPushToTalkPolling();
        }
        finally
        {
            _startGate.Release();
        }

        if (session is null)
            return;

        await RunSessionAsync(session).ConfigureAwait(true);
    }

    private async Task<DictationEngine?> WarmStartEngineAsync()
    {
        if (_engine is { } ready)
            return ready;

        // Counted rather than set, because two presses can be waiting on the same load and the
        // first to finish must not switch progress reporting off for the second.
        Interlocked.Increment(ref _awaitingPreparation);
        try
        {
            _viewModel.ReportDictationPreparing();
            await WarmStartAsync().ConfigureAwait(true);
            return _engine;
        }
        finally
        {
            Interlocked.Decrement(ref _awaitingPreparation);
        }
    }

    /// <summary>
    /// Model-download progress on its way to the overlay. Called from whichever thread is doing
    /// the download, so it hops to the UI thread; throttled to whole percent, since the only
    /// consumer is a status line and a 4 px bar.
    /// </summary>
    private void OnPreparationProgress(double fraction)
    {
        if (_disposed || Volatile.Read(ref _awaitingPreparation) == 0)
            return;

        var percent = (int)Math.Round(Math.Clamp(fraction, 0, 1) * 100);
        if (Interlocked.Exchange(ref _reportedPreparationPercent, percent) == percent)
            return;

        _dispatcher.InvokeAsync(() =>
        {
            if (_viewModel.IsPreparingDictation)
                _viewModel.ReportDictationPreparing(percent / 100.0);
        });
    }

    private async Task RunSessionAsync(CancellationTokenSource session)
    {
        var options = new DictationOptions
        {
            TrailingSilence = TimeSpan.FromMilliseconds(_settings.DictationTrailingSilenceMilliseconds),
        };

        try
        {
            await _engine!.ListenAsync(
                    options,
                    transcript => _dispatcher.InvokeAsync(() =>
                    {
                        if (!session.IsCancellationRequested)
                            OnTranscript(transcript);
                    }),
                    level => _dispatcher.InvokeAsync(() => _viewModel.ReportMicrophoneLevel(level)),
                    session.Token)
                .ConfigureAwait(true);

            _viewModel.EndDictation();
        }
        catch (OperationCanceledException)
        {
            // Expected - it is how Stop, push-to-talk release and hiding the overlay all end a
            // session - but a session that ends with no trace at all is indistinguishable from one
            // that never started, so it is still recorded.
            _logger.LogTrace("Dictation session was cancelled.");
            _viewModel.EndDictation();
        }
        catch (MicrophoneUnavailableException ex)
        {
            // Never fail silently here: a microphone that cannot be opened looks identical to a
            // recogniser that heard nothing, and only one of the two is fixable by the user.
            _logger.LogError(ex, "The microphone could not be opened for dictation.");
            _viewModel.EndDictation(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dictation failed.");
            _viewModel.EndDictation("Dictation failed; see the log for details.");
        }
        finally
        {
            StopPushToTalkPolling();
            if (ReferenceEquals(_session, session))
            {
                _session = null;
                session.Dispose();
            }
        }
    }

    private void OnTranscript(SpeechTranscript transcript)
    {
        if (transcript is FinalTranscript)
            _ = _viewModel.ApplyFinalTranscriptAsync(transcript.Text);
        else
            _viewModel.ApplyPartialTranscript(transcript.Text);
    }

    private void StartPushToTalkPolling()
    {
        if (!_settings.DictationPushToTalk)
            return;

        _pushToTalkTimer = new DispatcherTimer(DispatcherPriority.Input, _dispatcher)
        {
            Interval = PushToTalkPollInterval,
        };

        // The chord is still down at the moment the hotkey message arrives, but a press short
        // enough to be released before the first tick would otherwise stop instantly. Waiting for
        // one observed "held" reading makes a tap behave like a very short hold rather than a
        // no-op.
        var everHeld = false;
        var idleTicks = 0;
        _pushToTalkTimer.Tick += (_, _) =>
        {
            if (_isHotKeyHeld())
            {
                everHeld = true;
                return;
            }

            // A press released before the first tick is never observed as held. Stopping after a
            // few idle ticks anyway means the worst case is a tap that listens for a moment, not a
            // session that never ends.
            if (everHeld || ++idleTicks >= 3)
                Stop();
        };
        _pushToTalkTimer.Start();
    }

    private void StopPushToTalkPolling()
    {
        _pushToTalkTimer?.Stop();
        _pushToTalkTimer = null;
    }
}
