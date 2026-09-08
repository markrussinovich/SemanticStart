using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SemanticStart.Core.Abstractions;
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
            DebounceSearch();
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

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
            Status = Results.Count == 0 ? (string.IsNullOrWhiteSpace(query) ? IdleStatus : NoMatchStatus) : string.Empty;
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
