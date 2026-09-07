using SemanticStart.Core.Indexing;

namespace SemanticStart.App;

/// <summary>
/// Owns index rebuilds for the lifetime of the process.
///
/// The rebuild used to belong to the settings window, which meant closing that window cancelled it.
/// On a first run that is the whole index: the user opens settings, sees a progress bar, closes the
/// window because there is nothing more to do there, and silently ends up with an empty index and
/// an app that finds nothing. A rebuild is a background job that happens to have a progress display,
/// not a piece of a dialog, so it lives here and outlives any window watching it.
///
/// Only one runs at a time. Asking for a rebuild while one is already going returns the one in
/// flight rather than starting a second pass over the same store.
/// </summary>
public sealed class IndexRebuildCoordinator : IDisposable
{
    private readonly Func<AppSettings, bool, IProgress<IndexProgress>, CancellationToken, Task> _rebuild;
    private readonly Func<int> _entityCount;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _current;

    public IndexRebuildCoordinator(SemanticSearchService searchService)
        : this(searchService.RebuildIndexAsync, () => searchService.Count)
    {
    }

    /// <summary>Takes the rebuild as a delegate so its sequencing can be tested without indexing a machine.</summary>
    internal IndexRebuildCoordinator(
        Func<AppSettings, bool, IProgress<IndexProgress>, CancellationToken, Task> rebuild,
        Func<int> entityCount)
    {
        _rebuild = rebuild;
        _entityCount = entityCount;
    }

    /// <summary>Raised on every progress report, and once more when the run ends.</summary>
    public event EventHandler<IndexRebuildState>? StateChanged;

    /// <summary>The latest state, so a window opened mid-rebuild can show progress immediately.</summary>
    public IndexRebuildState State { get; private set; } = IndexRebuildState.Idle;

    public bool IsRunning => State.IsRunning;

    /// <summary>
    /// Starts a rebuild, or returns the one already running. The returned task completes when the
    /// rebuild does; failures are reported through <see cref="StateChanged"/> rather than thrown,
    /// because the caller is usually a click handler with nowhere to put an exception.
    /// </summary>
    public Task StartAsync(AppSettings settings, bool force)
    {
        lock (_lock)
        {
            if (_current is { IsCompleted: false })
                return _current;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            _current = RunAsync(settings, force, _cts.Token);
            return _current;
        }
    }

    /// <summary>
    /// Stops a running rebuild. Deliberately not called when a window closes: cancelling is
    /// something the user asks for, not something that happens to them.
    /// </summary>
    public void Cancel()
    {
        lock (_lock)
            _cts?.Cancel();
    }

    private async Task RunAsync(AppSettings settings, bool force, CancellationToken cancellationToken)
    {
        Publish(new IndexRebuildState(true, 0, "Starting rebuild...", null));

        var progress = new Progress<IndexProgress>(p => Publish(new IndexRebuildState(
            IsRunning: true,
            Fraction: p.Fraction,
            Message: p.Total > 0 ? $"{p.Phase}: {p.Completed}/{p.Total} {p.CurrentItem}" : $"{p.Phase}: {p.CurrentItem}",
            Outcome: null)));

        try
        {
            await _rebuild(settings, force, progress, cancellationToken);
            Publish(new IndexRebuildState(false, 1, $"Rebuild complete. {_entityCount()} entities loaded.", RebuildOutcome.Completed));
        }
        catch (OperationCanceledException)
        {
            Publish(new IndexRebuildState(false, 0, "Rebuild canceled.", RebuildOutcome.Canceled));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Index rebuild failed");
            Publish(new IndexRebuildState(false, 0, "Rebuild failed; see log for details.", RebuildOutcome.Failed));
        }
    }

    private void Publish(IndexRebuildState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    public void Dispose()
    {
        // Process exit is the one time a rebuild does get cut short, and there is nothing left to
        // write it to.
        _cts?.Cancel();
        _cts?.Dispose();
    }
}

public enum RebuildOutcome
{
    Completed,
    Canceled,
    Failed,
}

/// <param name="IsRunning">Whether a rebuild is in flight right now.</param>
/// <param name="Fraction">Progress from 0 to 1.</param>
/// <param name="Message">The line to show under the index summary.</param>
/// <param name="Outcome">Set only on the final report of a run.</param>
public sealed record IndexRebuildState(bool IsRunning, double Fraction, string Message, RebuildOutcome? Outcome)
{
    public static readonly IndexRebuildState Idle = new(false, 0, "Ready", null);
}
