using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Launching;
using SemanticStart.Core.Model;

namespace SemanticStart.App;

public class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class SearchResultItem : ObservableObject
{
    private ImageSource? _icon;

    public SearchResultItem(SearchHit hit)
    {
        Hit = hit;
        DisplayName = hit.Entity.DisplayName;
        Summary = string.IsNullOrWhiteSpace(hit.Summary) ? hit.MatchReason ?? hit.Entity.Source : hit.Summary;
        KindBadge = FormatKind(hit.Entity.Kind);
        FallbackGlyph = GlyphFor(hit.Entity.Kind);
    }

    public SearchHit Hit { get; }
    public Entity Entity => Hit.Entity;
    public string DisplayName { get; }
    public string Summary { get; }
    public string KindBadge { get; }
    public string FallbackGlyph { get; }

    /// <summary>
    /// The description shown when a row is expanded, which has to say more than the row already
    /// does or the panel is just a bigger copy of the line above it.
    ///
    /// Prefers the longer prose harvested for the entity. That text usually opens by restating the
    /// one-line summary verbatim - "Simple text editor included with Microsoft Windows. Windows
    /// Notepad is a simple text editor for Windows..." - so the repeated opening is dropped and
    /// only the part that adds something is kept.
    ///
    /// When there is no longer prose, the summary is shown only if it was long enough for the
    /// single-line row to have trimmed it. Repeating a short summary underneath itself is the
    /// redundancy this exists to avoid.
    /// </summary>
    public string DetailSummary => ExtendedDescription(Hit.Details, Summary);

    public bool HasDetailSummary => DetailSummary.Length > 0;

    /// <summary>
    /// Who made it and what kind of thing it is, on one line. Both are dropped when they add
    /// nothing: the category is often just the plural of the badge already on the row, and most
    /// built-in Windows entities have no recorded publisher at all.
    /// </summary>
    public string Provenance
    {
        get
        {
            var parts = new List<string>(2);

            if (Hit.Entity.Publisher is { Length: > 0 } publisher)
                parts.Add(publisher);

            if (Hit.Category is { Length: > 0 } category && !RestatesBadge(category))
                parts.Add(category);

            return string.Join(" · ", parts);
        }
    }

    public bool HasProvenance => Provenance.Length > 0;

    /// <summary>
    /// True when the category says what the badge on the row already says. Categories are plural
    /// ("Applications") and badges singular ("Application"), so they are compared with the plural
    /// removed rather than for equality.
    /// </summary>
    private bool RestatesBadge(string category) =>
        string.Equals(category.TrimEnd('s'), KindBadge.TrimEnd('s'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Drops <paramref name="summary"/> from the front of <paramref name="details"/> when the
    /// prose opens by restating it, and returns what remains.
    /// </summary>
    private static string ExtendedDescription(string? details, string summary)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            return LongSummaryOnly(summary);
        }

        var text = details.Trim();
        if (text.StartsWith(summary, StringComparison.OrdinalIgnoreCase))
        {
            var remainder = text[summary.Length..].TrimStart(' ', '.', '\u2014', '-');

            // Prose that is only the summary again adds nothing, so it is treated as if there were
            // no longer description at all.
            return remainder.Length > 0 ? remainder : LongSummaryOnly(summary);
        }

        return text;
    }

    /// <summary>
    /// The summary, but only when one line could not have held it. Repeating a short summary
    /// directly beneath itself is the redundancy the panel exists to avoid.
    /// </summary>
    private static string LongSummaryOnly(string summary) =>
        summary.Length > SummaryLineLength ? summary : string.Empty;

    /// <summary>
    /// Roughly how much of a summary the single-line row shows before it ellipsizes, at the
    /// overlay's width and font size. Only used to decide whether expanding could reveal more.
    /// </summary>
    private const int SummaryLineLength = 90;

    /// <summary>
    /// Synthesis sometimes pads the task list out to ten near-duplicate phrasings. Showing all of
    /// them makes the panel look like filler, so only the leading few are surfaced.
    /// </summary>
    public IReadOnlyList<string> Tasks => Hit.Tasks.Count > MaxDisplayedTasks
        ? Hit.Tasks.Take(MaxDisplayedTasks).ToList()
        : Hit.Tasks;

    public bool HasTasks => Hit.Tasks.Count > 0;

    /// <summary>
    /// Where the thing lives, for the details panel. An AppUserModelId is an opaque package
    /// identifier and says nothing to a user, so packaged apps fall back to the executable
    /// resolved from their manifest at index time; only when even that is unknown is the line
    /// dropped entirely.
    /// </summary>
    public string LaunchTarget => FormatLaunchTarget(Hit.Entity.LaunchTarget) is { Length: > 0 } shown
        ? shown
        : Hit.Entity.RawMetadata.GetValueOrDefault("targetPath") ?? string.Empty;

    public bool HasLaunchTarget => LaunchTarget.Length > 0;

    /// <summary>
    /// What the copy button puts on the clipboard: the command that would start this result. Unlike
    /// <see cref="LaunchTarget"/>, which is a location to read, this is meant to be pasted and run,
    /// so it keeps the host program and the arguments the launcher would have supplied.
    /// </summary>
    public string CommandLine => LaunchCommandLine.For(Hit.Entity);

    public bool HasCommandLine => CommandLine.Length > 0;

    /// <summary>
    /// Set for a moment after a successful copy. Nothing else in the window changes when the
    /// clipboard is written, so without this the button would give no sign it had worked.
    /// </summary>
    public bool JustCopied
    {
        get => _justCopied;
        set
        {
            if (SetProperty(ref _justCopied, value))
            {
                OnPropertyChanged(nameof(CopyGlyph));
                OnPropertyChanged(nameof(CopyToolTip));
            }
        }
    }

    /// <summary>Copy glyph at rest, checkmark just after a copy.</summary>
    public string CopyGlyph => JustCopied ? "\uE73E" : "\uE8C8";

    public string CopyToolTip => JustCopied ? "Copied" : $"Copy command line (Ctrl+C)\n{CommandLine}";

    private bool _justCopied;

    private const int MaxDisplayedTasks = 5;

    private static string FormatLaunchTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return string.Empty;
        }

        if (target.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase) ||
            target.Contains('!', StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return target;
    }

    private bool _isExpanded;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public ImageSource? Icon
    {
        get => _icon;
        set => SetProperty(ref _icon, value);
    }

    private static string FormatKind(EntityKind kind) => kind switch
    {
        EntityKind.Application or EntityKind.PackagedApp => "Application",
        EntityKind.SettingsPage => "Settings",
        EntityKind.OptionalFeature => "Feature",
        EntityKind.SystemTool => "Tool",
        EntityKind.ControlPanelApplet => "Control Panel",
        EntityKind.ManagementConsole => "Console",
        EntityKind.ShellLocation => "Location",
        _ => kind.ToString()
    };

    public static string GlyphFor(EntityKind kind) => kind switch
    {
        EntityKind.SettingsPage => "\uE713",
        EntityKind.OptionalFeature => "\uE7B8",
        EntityKind.SystemTool or EntityKind.ManagementConsole => "\uE756",
        EntityKind.ControlPanelApplet => "\uE770",
        EntityKind.ShellLocation => "\uE8B7",
        _ => "\uECAA"
    };
}

public sealed class OverlayViewModel : ObservableObject
{
    private const string IdleStatus = "Type to search apps, settings, tools, and features";

    /// <summary>
    /// Shown when the query ran successfully but nothing cleared the relevance bar. The engine
    /// deliberately returns nothing rather than padding the list with weak matches, so this is a
    /// normal outcome and must not read like an error.
    /// </summary>
    private const string NoMatchStatus = "No good matches found";

    /// <summary>
    /// Shown instead of <see cref="NoMatchStatus"/> when there is nothing to search. "No good
    /// matches" for every query reads as a broken search rather than a missing index.
    /// </summary>
    internal const string EmptyIndexStatus = "The index hasn't been built yet. Open Settings from the tray icon and choose Build index.";

    /// <summary>Shown while the microphone is open, so an idle recogniser is never mistaken for a dead one.</summary>
    internal const string ListeningStatus = "Listening\u2026";

    /// <summary>
    /// Shown between the dictation hotkey being pressed and the microphone actually opening.
    /// Normally that gap is nothing - the recogniser is warm from startup - but on the very first
    /// run it spans a model download, and a press that produces no visible change at all is
    /// indistinguishable from a hotkey that is not registered.
    /// </summary>
    internal const string PreparingStatus = "Getting dictation ready\u2026";

    /// <summary>
    /// The first-run case, where the wait is a download rather than a model load and is long
    /// enough that the user deserves to know how far along it is.
    /// </summary>
    internal const string DownloadingModelStatus = "Downloading the speech model\u2026";

    private readonly SemanticSearchService _searchService;
    private readonly IconProvider _iconProvider;
    private readonly AppSettings _settings;
    private readonly SearchDebouncer _debouncer;

    /// <summary>
    /// The query the visible results were produced from. Compared against the current text before
    /// launching, so a keystroke that is still inside the debounce window cannot be acted on with
    /// the previous query's selection.
    /// </summary>
    private string _resultsQuery = string.Empty;
    private string _query = string.Empty;
    private string _status = IdleStatus;
    private int _selectedIndex = -1;
    private bool _isSearching;
    private bool _isResultsActive;
    private bool _isListening;
    private bool _isPreparingDictation;
    private double _dictationPreparationProgress;
    private double _microphoneLevel;

    /// <summary>
    /// Text that was in the box when dictation started. Partial transcripts replace each other,
    /// but they must not eat what the user typed first.
    /// </summary>
    private string _dictationPrefix = string.Empty;
    private string _dictationBaseQuery = string.Empty;
    private string? _lastDictatedQuery;
    private bool _applyingTranscript;
    private bool _dictationEdited;

    public OverlayViewModel(SemanticSearchService searchService, IconProvider iconProvider, AppSettings settings)
    {
        _searchService = searchService;
        _iconProvider = iconProvider;
        _settings = settings;
        _debouncer = new SearchDebouncer(
            TimeSpan.FromMilliseconds(settings.SearchDebounceMilliseconds),
            IsEditingKeyHeldAsync);
    }

    public ObservableCollection<SearchResultItem> Results { get; } = [];

    public string Query
    {
        get => _query;
        set
        {
            if (!SetProperty(ref _query, value))
                return;
            if (!_applyingTranscript)
            {
                _lastDictatedQuery = null;
                if (IsListening)
                    _dictationEdited = true;
            }
            DebounceSearch();
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>
    /// Shows a notice that did not come from the search, such as a clipboard write being refused.
    /// A method rather than a public setter so the status line keeps one owner: whatever is written
    /// here is replaced by the next search, which is the right lifetime for a transient message.
    /// </summary>
    public void ReportStatus(string message) => Status = message;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (SetProperty(ref _selectedIndex, value))
                OnPropertyChanged(nameof(SelectedItem));
        }
    }

    public SearchResultItem? SelectedItem => SelectedIndex >= 0 && SelectedIndex < Results.Count ? Results[SelectedIndex] : null;

    /// <summary>
    /// Whether the arrow keys are steering the result list rather than the caret in the query.
    /// <para>
    /// The keyboard focus never leaves the search box - the query has to stay typeable at every
    /// moment - so this is what tells the two modes apart, and what the list uses to show that the
    /// highlighted row is the one the arrows are moving.
    /// </para>
    /// </summary>
    public bool IsResultsActive
    {
        get => _isResultsActive;
        set => SetProperty(ref _isResultsActive, value);
    }

    public bool IsSearching
    {
        get => _isSearching;
        private set => SetProperty(ref _isSearching, value);
    }

    /// <summary>Whether the microphone is open and speech is being transcribed right now.</summary>
    public bool IsListening
    {
        get => _isListening;
        private set => SetProperty(ref _isListening, value);
    }

    /// <summary>
    /// Whether a dictation turn has been asked for but the recogniser is not ready yet. Drives the
    /// footer's preparation bar; never true at the same time as <see cref="IsListening"/>.
    /// </summary>
    public bool IsPreparingDictation
    {
        get => _isPreparingDictation;
        private set => SetProperty(ref _isPreparingDictation, value);
    }

    /// <summary>
    /// How far the first-run model download has got, 0 to 1. Negative means "working, but with no
    /// measurable progress", which the bar shows as indeterminate rather than as a bar stuck at
    /// zero - the difference between "something is happening" and "something has hung".
    /// </summary>
    public double DictationPreparationProgress
    {
        get => _dictationPreparationProgress;
        private set
        {
            if (SetProperty(ref _dictationPreparationProgress, value))
                OnPropertyChanged(nameof(IsDictationPreparationIndeterminate));
        }
    }

    /// <summary>Whether the preparation bar has a figure to show or only the fact that it is busy.</summary>
    public bool IsDictationPreparationIndeterminate => _dictationPreparationProgress < 0;

    /// <summary>
    /// Loudness of the captured audio, 0 to 1, for the level meter. Without it a silent failure -
    /// a muted device, the wrong default endpoint - looks exactly like a user who has not spoken.
    /// </summary>
    public double MicrophoneLevel
    {
        get => _microphoneLevel;
        private set => SetProperty(ref _microphoneLevel, value);
    }

    /// <summary>
    /// Opens a dictation turn, replacing unedited speech from the previous turn while keeping
    /// the text the user typed. A keyboard edit makes the whole query user-owned.
    /// </summary>
    public void BeginDictation()
    {
        if (_lastDictatedQuery is not null)
            Query = _dictationBaseQuery;
        _dictationBaseQuery = Query;
        _dictationPrefix = string.IsNullOrWhiteSpace(Query) ? string.Empty : Query.TrimEnd() + " ";
        _dictationEdited = false;
        MicrophoneLevel = 0;
        ClearPreparation();
        IsListening = true;
        Status = ListeningStatus;
    }

    /// <summary>
    /// Acknowledges a dictation request that cannot be served yet: the recogniser is still loading
    /// or its model is still downloading. Called for every press in that window, including ones
    /// that are otherwise dropped, so the answer to "did it hear me?" is always yes.
    /// </summary>
    /// <param name="downloadProgress">
    /// Fraction of the model download completed, or null while there is no figure to report.
    /// </param>
    public void ReportDictationPreparing(double? downloadProgress = null)
    {
        if (IsListening)
            return;

        IsPreparingDictation = true;

        if (downloadProgress is { } fraction)
        {
            var clamped = Math.Clamp(fraction, 0, 1);
            DictationPreparationProgress = clamped;
            Status = $"{DownloadingModelStatus} {clamped:P0}";
        }
        else
        {
            DictationPreparationProgress = -1;
            Status = PreparingStatus;
        }
    }

    /// <summary>
    /// A transcript that is still being revised. It goes through the ordinary Query setter, so the
    /// debouncer decides when to search exactly as it does for typing - the engine emits partials
    /// every few hundred milliseconds and searching each one would spend the machine on text the
    /// recogniser is about to change.
    ///
    /// <para>
    /// Concatenation rather than a <c>StringBuilder</c>, deliberately. Nothing accumulates here:
    /// each partial <em>replaces</em> the whole query, because the recogniser revises its own text
    /// rather than appending to it, so there is no loop for a builder to amortise. The destination
    /// is a <see cref="string"/> property bound to a <c>TextBox</c>, so the single result string
    /// has to be materialised either way - a builder would allocate itself and an internal buffer
    /// on top of it, and be slower. At two operands the compiler emits one
    /// <see cref="string.Concat(string, string)"/>, which allocates exactly once and copies both
    /// operands with no intermediate.
    /// </para>
    /// </summary>
    public void ApplyPartialTranscript(string text)
    {
        // A keyboard edit takes precedence over the remaining cumulative transcripts this turn.
        if (_dictationEdited)
            return;

        _applyingTranscript = true;
        try
        {
            Query = _dictationPrefix + text;
            _lastDictatedQuery = Query;
        }
        finally
        {
            _applyingTranscript = false;
        }
    }

    /// <summary>
    /// The recogniser's settled text for an utterance. This one is worth searching immediately:
    /// the user has stopped speaking and is waiting for the answer, so the debounce interval would
    /// be pure dead time.
    /// </summary>
    public async Task ApplyFinalTranscriptAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_dictationEdited)
            return;

        ApplyPartialTranscript(text);
        _dictationPrefix = Query.Length == 0 ? string.Empty : Query.TrimEnd() + " ";
        await FlushPendingSearchAsync(cancellationToken);
    }

    /// <summary>Closes a dictation turn, optionally with a message explaining why it ended.</summary>
    public void EndDictation(string? message = null)
    {
        var wasPreparing = IsPreparingDictation;
        IsListening = false;
        MicrophoneLevel = 0;
        ClearPreparation();

        if (message is not null)
            Status = message;
        else if (wasPreparing || string.Equals(Status, ListeningStatus, StringComparison.Ordinal))
            Status = Results.Count == 0 && Query.Length == 0 ? IdleStatus : string.Empty;
    }

    private void ClearPreparation()
    {
        IsPreparingDictation = false;
        DictationPreparationProgress = 0;
    }

    public void ReportMicrophoneLevel(double level) => MicrophoneLevel = Math.Clamp(level, 0, 1);

    public async Task SearchNowAsync(string query, CancellationToken cancellationToken)
    {
        IsSearching = true;
        try
        {
            var hits = string.IsNullOrWhiteSpace(query)
                ? Array.Empty<SearchHit>()
                : await _searchService.SearchAsync(query, _settings.ResultLimit, cancellationToken);

            Results.Clear();
            foreach (var hit in hits)
                Results.Add(new SearchResultItem(hit));

            _resultsQuery = query;
            SelectedIndex = Results.Count > 0 ? 0 : -1;
            IsResultsActive = false;
            Status = Results.Count == 0
                ? string.IsNullOrWhiteSpace(query) ? IdleStatus : _searchService.Count == 0 ? EmptyIndexStatus : NoMatchStatus
                : string.Empty;
            _ = LoadIconsAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Search failed");
            Status = "Search failed; see log for details.";
        }
        finally
        {
            IsSearching = false;
        }
    }

    public void MoveSelection(int delta)
    {
        if (Results.Count == 0)
            return;
        SelectedIndex = Math.Clamp(SelectedIndex + delta, 0, Results.Count - 1);
    }

    public async Task LaunchSelectedAsync(LaunchOptions options, CancellationToken cancellationToken = default)
    {
        // Waiting for the query to settle means the visible list can lag the text box by up to the
        // debounce interval, and pressing Enter in that window would launch the previous query's
        // answer. Settle first, then act on what the user can actually see.
        await FlushPendingSearchAsync(cancellationToken);

        if (SelectedItem is null)
            return;
        await _searchService.LaunchAsync(SelectedItem.Entity, options, cancellationToken);
    }

    /// <summary>
    /// Runs the pending debounced search immediately, if the visible results are out of date.
    /// </summary>
    public async Task FlushPendingSearchAsync(CancellationToken cancellationToken = default)
    {
        if (string.Equals(_resultsQuery, Query, StringComparison.Ordinal))
            return;

        _debouncer.Cancel();
        await SearchNowAsync(Query, cancellationToken);
    }

    public void Clear()
    {
        _debouncer.Cancel();
        _query = string.Empty;
        _dictationPrefix = string.Empty;
        _dictationBaseQuery = string.Empty;
        _lastDictatedQuery = null;
        _dictationEdited = IsListening;
        OnPropertyChanged(nameof(Query));
        Results.Clear();
        _resultsQuery = string.Empty;
        SelectedIndex = -1;
        IsResultsActive = false;
        Status = IdleStatus;
    }

    /// <summary>
    /// Waiting for a pause spends latency the engine does not need - a query costs about 2.5 ms -
    /// to buy the appearance of a settled answer, which is what the user is actually reading. The
    /// wait is dead time only while the user is still typing, and Enter flushes it, so the cost is
    /// never paid by someone who has finished. <see cref="SearchDebouncer"/> holds the reasoning
    /// about when that pause has arrived.
    /// </summary>
    private void DebounceSearch() =>
        _debouncer.Schedule(token => System.Windows.Application.Current.Dispatcher
            .InvokeAsync(() => SearchNowAsync(Query, token)).Task.Unwrap());

    /// <summary>
    /// Whether a key that edits text by repeating is down. Backspace and Delete are the whole set
    /// in practice - a held character key produces "aaaaaa", which nobody types on purpose. Read
    /// from the keyboard rather than tracked from key events, so that a key-up lost to a focus
    /// change cannot leave the search waiting for a release that has already happened.
    /// </summary>
    private static async Task<bool> IsEditingKeyHeldAsync(CancellationToken cancellationToken) =>
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(
            () => Keyboard.IsKeyDown(Key.Back) || Keyboard.IsKeyDown(Key.Delete),
            DispatcherPriority.Input,
            cancellationToken).Task;

    private async Task LoadIconsAsync(CancellationToken cancellationToken)
    {
        var loads = Results.ToArray().Select(item => LoadIconAsync(item, cancellationToken));
        await Task.WhenAll(loads);
    }

    private async Task LoadIconAsync(SearchResultItem item, CancellationToken cancellationToken)
    {
        try
        {
            var icon = await _iconProvider.GetIconAsync(item.Entity, cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
                item.Icon = icon;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, $"Icon load failed for {item.Entity.Id}");
        }
    }
}
