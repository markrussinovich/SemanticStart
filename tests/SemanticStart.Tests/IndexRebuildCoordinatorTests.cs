using SemanticStart.App;
using SemanticStart.Core.Indexing;
using Xunit;

namespace SemanticStart.Tests;

/// <summary>
/// The rebuild used to be owned by the settings window, so closing that window cancelled it. On a
/// first run that is the entire index: the user closes a window that has nothing left to offer and
/// silently ends up with an app that finds nothing. These cover the sequencing that makes closing
/// safe, using a stand-in for the rebuild itself so no test indexes the machine.
/// </summary>
public sealed class IndexRebuildCoordinatorTests
{
    [Fact]
    public void NothingIsRunningUntilARebuildIsStarted()
    {
        using var coordinator = Coordinator(_ => Task.CompletedTask);

        Assert.False(coordinator.IsRunning);
        Assert.Equal(IndexRebuildState.Idle, coordinator.State);
    }

    /// <summary>
    /// The point of the whole class: a rebuild survives everything except an explicit cancel. A
    /// window that opened it, showed progress, and closed leaves it running.
    /// </summary>
    [Fact]
    public async Task ARebuildKeepsRunningAfterItsListenerGoesAway()
    {
        var release = new TaskCompletionSource();
        using var coordinator = Coordinator(_ => release.Task);

        void Listener(object? sender, IndexRebuildState state) { }
        coordinator.StateChanged += Listener;

        var run = coordinator.StartAsync(new AppSettings(), force: true);
        Assert.True(coordinator.IsRunning);

        // What SettingsWindow.OnClosed now does, and all it does.
        coordinator.StateChanged -= Listener;

        Assert.True(coordinator.IsRunning);
        release.SetResult();
        await run;

        Assert.False(coordinator.IsRunning);
        Assert.Equal(RebuildOutcome.Completed, coordinator.State.Outcome);
    }

    /// <summary>
    /// Two clicks on Rebuild, or a tray rebuild while one is already going, must not start a second
    /// pass over the same store.
    /// </summary>
    [Fact]
    public async Task StartingWhileARebuildIsRunningJoinsTheOneInFlight()
    {
        var release = new TaskCompletionSource();
        var starts = 0;
        using var coordinator = Coordinator(_ =>
        {
            Interlocked.Increment(ref starts);
            return release.Task;
        });

        var first = coordinator.StartAsync(new AppSettings(), force: true);
        var second = coordinator.StartAsync(new AppSettings(), force: true);

        Assert.Same(first, second);

        release.SetResult();
        await first;

        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task CancellingEndsTheRunAndSaysSo()
    {
        using var coordinator = Coordinator(async token => await Task.Delay(Timeout.Infinite, token));

        var run = coordinator.StartAsync(new AppSettings(), force: true);
        coordinator.Cancel();
        await run;

        Assert.False(coordinator.IsRunning);
        Assert.Equal(RebuildOutcome.Canceled, coordinator.State.Outcome);
    }

    /// <summary>
    /// Failure is reported, not thrown. The caller is a click handler with nowhere to put an
    /// exception, and letting it escape would take a tray app down with no window to show it in.
    /// </summary>
    [Fact]
    public async Task AFailedRebuildReportsItsOutcomeInsteadOfThrowing()
    {
        using var coordinator = Coordinator(_ => Task.FromException(new InvalidOperationException("indexing blew up")));

        await coordinator.StartAsync(new AppSettings(), force: true);

        Assert.Equal(RebuildOutcome.Failed, coordinator.State.Outcome);
        Assert.False(coordinator.State.IsRunning);
    }

    [Fact]
    public async Task ProgressIsReportedAsAFractionAndALine()
    {
        // Progress<T> marshals through a synchronisation context, so a report raised inside the
        // rebuild can arrive after it returns. Wait for the one this test cares about instead of
        // reading the list while it is still being written to.
        var reported = new TaskCompletionSource<IndexRebuildState>();
        var final = new TaskCompletionSource<IndexRebuildState>();

        using var coordinator = WithProgress((progress, _) =>
        {
            progress.Report(new IndexProgress
            {
                Phase = "Enriching",
                Completed = 22,
                Total = 506,
                CurrentItem = "Developer Command Prompt",
            });
            return Task.CompletedTask;
        });

        coordinator.StateChanged += (_, state) =>
        {
            if (state.Message.Contains("Enriching: 22/506", StringComparison.Ordinal))
                reported.TrySetResult(state);
            if (state.Outcome is not null)
                final.TrySetResult(state);
        };

        await coordinator.StartAsync(new AppSettings(), force: true);

        var progressState = await reported.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("Developer Command Prompt", progressState.Message, StringComparison.Ordinal);

        var completed = await final.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RebuildOutcome.Completed, completed.Outcome);
        Assert.Equal(1, completed.Fraction);
    }

    private static IndexRebuildCoordinator Coordinator(Func<CancellationToken, Task> rebuild) =>
        new((_, _, _, token) => rebuild(token), () => 0);

    private static IndexRebuildCoordinator WithProgress(Func<IProgress<IndexProgress>, CancellationToken, Task> rebuild) =>
        new((_, _, progress, token) => rebuild(progress, token), () => 0);
}
