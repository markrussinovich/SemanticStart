namespace SemanticStart.App;

/// <summary>
/// Decides when a stream of keystrokes has become a question worth answering.
///
/// Ranking is only meaningful for a query the user has finished writing. Every prefix of a word is
/// itself a query, and an unfinished one is not a weaker version of the finished one - it is a
/// different question, matching different words and depressing every cosine at once. Searching on
/// each keystroke put that churn on screen, and watching a list rebuild itself letter by letter
/// reads as broken.
///
/// The interval is measured from the last change, which assumes every change was authored by a
/// person. Holding a key down breaks that assumption, and deleting is the one text operation
/// people perform by holding a key: the first gap it produces is not a pause for thought but the
/// keyboard's repeat delay, which Windows defaults to exactly 500 ms - the same figure as the
/// debounce, so the wait expires at the instant the repeats begin and the list rebuilds itself
/// under a finger that is still down. A key that is physically held is not a user who has
/// stopped, so the wait is extended until it comes up.
/// </summary>
public sealed class SearchDebouncer(TimeSpan interval, Func<CancellationToken, Task<bool>> isEditingKeyHeld)
{
    /// <summary>
    /// How long a held key may postpone a search before it runs regardless. Releasing the key is
    /// what normally ends the wait; this only bounds the case where the keyboard cannot be read,
    /// so that a probe which never reports a release cannot strand the search forever.
    /// </summary>
    public const int MaxHeldKeyExtensions = 20;

    private CancellationTokenSource? _pending;

    /// <summary>
    /// Abandons the pending run and schedules <paramref name="run"/> in its place. An interval of
    /// zero is a request for no debouncing at all, and is honoured literally.
    /// </summary>
    public void Schedule(Func<CancellationToken, Task> run)
    {
        ArgumentNullException.ThrowIfNull(run);

        Cancel();
        var cts = new CancellationTokenSource();
        _pending = cts;

        if (interval <= TimeSpan.Zero)
        {
            _ = run(cts.Token);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(interval, cts.Token).ConfigureAwait(false);

                for (var held = 0; held < MaxHeldKeyExtensions; held++)
                {
                    if (!await isEditingKeyHeld(cts.Token).ConfigureAwait(false))
                        break;

                    await Task.Delay(interval, cts.Token).ConfigureAwait(false);
                }

                cts.Token.ThrowIfCancellationRequested();
                await run(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    public void Cancel()
    {
        _pending?.Cancel();
        _pending = null;
    }
}
