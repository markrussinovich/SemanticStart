using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
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

    private readonly SemanticSearchService _searchService;
    private readonly IconProvider _iconProvider;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _debounceCts;
    private string _query = string.Empty;
    private string _status = IdleStatus;
    private int _selectedIndex = -1;
    private bool _isSearching;

    public OverlayViewModel(SemanticSearchService searchService, IconProvider iconProvider, AppSettings settings)
    {
        _searchService = searchService;
        _iconProvider = iconProvider;
        _settings = settings;
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

            SelectedIndex = Results.Count > 0 ? 0 : -1;
            Status = Results.Count == 0 ? (string.IsNullOrWhiteSpace(query) ? IdleStatus : "No results") : string.Empty;
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
        if (SelectedItem is null)
            return;
        await _searchService.LaunchAsync(SelectedItem.Entity, options, cancellationToken);
    }

    public void Clear()
    {
        _debounceCts?.Cancel();
        _query = string.Empty;
        OnPropertyChanged(nameof(Query));
        Results.Clear();
        SelectedIndex = -1;
        Status = IdleStatus;
    }

    private void DebounceSearch()
    {
        _debounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _debounceCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(70, cts.Token);
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                    () => SearchNowAsync(Query, cts.Token)).Task.Unwrap();
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private async Task LoadIconsAsync(CancellationToken cancellationToken)
    {
        foreach (var item in Results.ToArray())
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
}
