using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Luma.App.Services;
using Luma.Core.Enums;
using Luma.Core.Exceptions;
using Luma.Core.Interfaces;
using Luma.Core.Models;
using Microsoft.Extensions.Logging;

namespace Luma.App.ViewModels;

public sealed record SourceOption(string Id, string Label, string IconKey);

public sealed partial class CategoryChip(string label, string query) : ObservableObject
{
    public string Label { get; } = label;
    public string Query { get; } = query;

    [ObservableProperty]
    private bool _isActive;
}

/// <summary>
/// The Discover experience: debounced search across one or all enabled providers, provider-aware filters, incremental
/// loading and honest error handling (a failing provider never hides results from the others).
/// </summary>
public sealed partial class DiscoverViewModel : PageViewModel
{
    private sealed class Cursor(IWallpaperProvider provider)
    {
        public IWallpaperProvider Provider { get; } = provider;
        public int NextPage { get; set; } = 1;
        public bool HasMore { get; set; } = true;
        public string? Seed { get; set; }
        public string? Error { get; set; }
    }

    public static readonly IReadOnlyList<CategoryChip> CategoryChips =
    [
        new("Anime", "anime"), new("Nature", "nature"), new("Architecture", "architecture"), new("Minimal", "minimal"),
        new("Space", "space"), new("Gaming", "gaming"), new("Abstract", "abstract"), new("Photography", "photography"),
        new("Cyberpunk", "cyberpunk"), new("Landscapes", "landscape")
    ];

    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan FilterDebounce = TimeSpan.FromMilliseconds(250);

    private readonly IProviderRegistry _providers;
    private readonly ISettingsService _settings;
    private readonly WallpaperItemFactory _factory;
    private readonly ShellStartup _startup;
    private readonly ILogger<DiscoverViewModel> _logger;
    private readonly List<WallpaperItemViewModel> _all = [];
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    private List<Cursor> _cursors = [];
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _debounceCts;
    private bool _suppressRefresh;
    private bool _initialised;
    private bool _autoLoadBlocked;
    private string? _seed;

    public DiscoverViewModel(IProviderRegistry providers, ISettingsService settings, WallpaperItemFactory factory, ShellStartup startup,
        ILogger<DiscoverViewModel> logger)
    {
        _providers = providers;
        _settings = settings;
        _factory = factory;
        _startup = startup;
        _logger = logger;
        Filters = new DiscoverFilters();
        Filters.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(FilterBadgeText));
            RefreshCapabilityFlags();
            ScheduleRefresh(FilterDebounce);
        };
        _settings.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(OnSettingsChanged);
        RebuildSources();
        _selectedSource = Sources[0];
        RefreshCapabilityFlags();
    }

    public override string Title => "Discover";

    public DiscoverFilters Filters { get; }

    public ObservableCollection<SourceOption> Sources { get; } = [];

    /// <summary>Wallpapers shown in the grid (the featured one is held separately).</summary>
    public ObservableCollection<WallpaperItemViewModel> Items { get; } = [];

    public IReadOnlyList<CategoryChip> Categories => CategoryChips;

    public IReadOnlyList<Option<WallpaperSort>> SortOptions { get; private set; } = DiscoverFilters.AllSorts;

    [ObservableProperty]
    private SourceOption _selectedSource;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private CategoryChip? _activeCategory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsEmpty), nameof(IsError), nameof(HasContent))]
    private GalleryState _state = GalleryState.Idle;

    [ObservableProperty]
    private string _errorTitle = string.Empty;

    [ObservableProperty]
    private string _errorDetail = string.Empty;

    [ObservableProperty]
    private bool _errorIsOffline;

    [ObservableProperty]
    private string? _partialWarning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFeatured), nameof(FeaturedImageSource), nameof(FeaturedHeading), nameof(FeaturedDetail))]
    private WallpaperItemViewModel? _featured;

    [ObservableProperty]
    private bool _hasMore;

    [ObservableProperty]
    private bool _isLoadingMore;

    [ObservableProperty]
    private string _summary = string.Empty;

    // Which filter groups apply to the selected source(s).
    [ObservableProperty] private bool _showCategories;
    [ObservableProperty] private bool _showPurity;
    [ObservableProperty] private bool _showExplicit;
    [ObservableProperty] private bool _showColors;
    [ObservableProperty] private bool _showContentKind;
    [ObservableProperty] private bool _showLicense;
    [ObservableProperty] private bool _showMinResolution;
    [ObservableProperty] private bool _showAspect;
    [ObservableProperty] private bool _showTopRange;
    [ObservableProperty] private string? _filterScopeNote;

    public bool HasFeatured => Featured is not null;
    public string? FeaturedImageSource => Featured?.Wallpaper.PreviewUrl ?? Featured?.ThumbnailSource;
    public string FeaturedHeading => Featured is null ? string.Empty : Featured.Wallpaper.Title is { Length: > 0 } title ? title : DescribeFeatured(Featured);
    public string FeaturedDetail => Featured is null ? string.Empty
        : string.Join("  ·  ", new[] { Featured.ProviderName, Featured.ResolutionText, Featured.CreatorText is { Length: > 0 } c ? "by " + c : null }.Where(s => !string.IsNullOrEmpty(s)));

    public bool IsLoading => State == GalleryState.Loading;
    public bool IsEmpty => State == GalleryState.Empty;
    public bool IsError => State == GalleryState.Error;
    public bool HasContent => State == GalleryState.Loaded;
    public bool HasPartialWarning => !string.IsNullOrEmpty(PartialWarning);
    public string FilterBadgeText => Filters.ActiveCount > 0 ? Filters.ActiveCount.ToString() : string.Empty;
    public bool HasFilters => Filters.ActiveCount > 0;
    public string Subtitle => "Find something worth looking at.";
    public bool IsBrowse => string.IsNullOrWhiteSpace(EffectiveText) && Filters.ActiveCount == 0;

    /// <summary>Everything currently loaded (featured first); the preview uses it for previous/next navigation.</summary>
    public IReadOnlyList<WallpaperItemViewModel> AllItems => _all;

    private string EffectiveText => string.Join(' ', new[] { ActiveCategory?.Query, SearchText?.Trim() }.Where(s => !string.IsNullOrWhiteSpace(s)));

    partial void OnPartialWarningChanged(string? value) => OnPropertyChanged(nameof(HasPartialWarning));

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsBrowse));
        ScheduleRefresh(SearchDebounce);
    }

    partial void OnActiveCategoryChanged(CategoryChip? value)
    {
        foreach (var chip in CategoryChips) chip.IsActive = ReferenceEquals(chip, value);
        OnPropertyChanged(nameof(IsBrowse));
        ScheduleRefresh(TimeSpan.Zero);
    }

    partial void OnSelectedSourceChanged(SourceOption value)
    {
        if (value is null) return;
        RefreshCapabilityFlags();
        ScheduleRefresh(TimeSpan.Zero);
    }

    public override async Task OnActivatedAsync()
    {
        if (_initialised) return;
        _initialised = true;

        var preferred = _settings.Current.DefaultSource;
        var source = Sources.FirstOrDefault(s => s.Id == preferred) ?? Sources[0];
        _suppressRefresh = true;
        SelectedSource = source;
        _suppressRefresh = false;
        await RefreshAsync();
    }

    /// <summary>Applies a navigation request from the sidebar or the global search box.</summary>
    public void ApplyRequest(string? sourceId, string? query)
    {
        _initialised = true;
        _suppressRefresh = true;
        if (sourceId is not null) SelectedSource = Sources.FirstOrDefault(s => s.Id == sourceId) ?? Sources[0];
        if (query is not null)
        {
            ActiveCategory = null;
            SearchText = query;
        }
        _suppressRefresh = false;
        _debounceCts?.Cancel();
        _ = RefreshAsync();
    }

    [RelayCommand]
    private void SelectCategory(CategoryChip? chip) => ActiveCategory = ReferenceEquals(ActiveCategory, chip) ? null : chip;

    [RelayCommand]
    private void SubmitSearch()
    {
        _debounceCts?.Cancel();
        _ = RefreshAsync();
    }

    [RelayCommand]
    private void ClearSearch()
    {
        _suppressRefresh = true;
        SearchText = string.Empty;
        ActiveCategory = null;
        _suppressRefresh = false;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private void ClearFilters() => Filters.Reset();

    [RelayCommand]
    public Task RefreshAsync() => LoadFirstPageAsync();

    [RelayCommand]
    private async Task RetryAsync()
    {
        _autoLoadBlocked = false;
        if (_all.Count == 0) await LoadFirstPageAsync();
        else await LoadMoreAsync();
    }

    [RelayCommand]
    public async Task LoadMoreAsync()
    {
        if (IsLoadingMore || State != GalleryState.Loaded || !_cursors.Any(c => c.HasMore)) return;
        var ct = _loadCts?.Token ?? CancellationToken.None;
        IsLoadingMore = true;
        try
        {
            await FetchAsync(ct, first: false);
        }
        catch (OperationCanceledException)
        {
            // a newer search replaced this one
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    /// <summary>Called by the gallery when the user scrolls near the end.</summary>
    public void OnScrolledNearEnd()
    {
        if (!_autoLoadBlocked && HasMore) _ = LoadMoreAsync();
    }

    private void ScheduleRefresh(TimeSpan delay)
    {
        if (_suppressRefresh || !_initialised) return;
        _debounceCts?.Cancel();
        var cts = _debounceCts = new CancellationTokenSource();
        _ = DebounceAsync(delay, cts.Token);
    }

    private async Task DebounceAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            if (!ct.IsCancellationRequested) await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer keystroke
        }
    }

    private async Task LoadFirstPageAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var ct = cts.Token;

        var scope = ProvidersInScope().ToList();
        _cursors = scope.Select(p => new Cursor(p)).ToList();
        _seed = Filters.SortOption.Value == WallpaperSort.Random ? NewSeed() : null;
        _autoLoadBlocked = false;
        _keys.Clear();
        _all.Clear();
        Items.Clear();
        Featured = null;
        PartialWarning = null;
        HasMore = false;
        IsLoadingMore = false;
        OnPropertyChanged(nameof(IsBrowse));

        if (scope.Count == 0)
        {
            ShowError("No sources are turned on", "Enable Wallhaven or Openverse in Settings to start discovering wallpapers.", offline: false);
            return;
        }

        State = GalleryState.Loading;
        try
        {
            await _startup.BeginAsync();
            await _startup.Ready;
            await FetchAsync(ct, first: true);
        }
        catch (OperationCanceledException)
        {
            // replaced by a newer search
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discover failed unexpectedly");
            if (!ct.IsCancellationRequested) ShowError("Something went wrong", "Luma couldn't load wallpapers. Details were written to the log.", offline: false);
        }
    }

    private async Task FetchAsync(CancellationToken ct, bool first)
    {
        var query = Filters.ToQuery(EffectiveText, _seed);
        var active = _cursors.Where(c => c.HasMore).ToList();
        if (active.Count == 0)
        {
            HasMore = false;
            return;
        }

        var tasks = active.Select(async cursor =>
        {
            try
            {
                var result = await cursor.Provider.SearchAsync(query with { Page = cursor.NextPage, Seed = cursor.Seed ?? _seed }, ct);
                return (cursor, result, error: (Exception?)null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (cursor, result: (WallpaperSearchResult?)null, error: ex);
            }
        }).ToList();

        var outcomes = await Task.WhenAll(tasks);
        ct.ThrowIfCancellationRequested();

        var lists = new List<List<Wallpaper>>();
        var failures = new List<(Cursor Cursor, Exception Error)>();
        foreach (var (cursor, result, error) in outcomes)
        {
            if (result is not null)
            {
                cursor.Error = null;
                cursor.NextPage = result.Page + 1;
                cursor.HasMore = result.HasMore;
                cursor.Seed = result.Seed ?? cursor.Seed;
                lists.Add(result.Items.ToList());
            }
            else
            {
                cursor.Error = DescribeError(error!);
                failures.Add((cursor, error!));
                _logger.LogWarning(error, "Provider {Provider} failed during discovery", cursor.Provider.Id);
            }
        }

        var fresh = Interleave(lists).Where(w => _keys.Add(w.Key)).ToList();
        var created = fresh.Select(w => _factory.Create(w, () => _all)).ToList();

        if (first && created.Count > 0 && IsBrowse)
        {
            // The featured image is one real file, so only a modest one (<= 2.5 MB) qualifies; otherwise there is simply no hero.
            var hero = created.FirstOrDefault(i => i.Wallpaper.Resolution is { Width: >= 1920 } && i.Wallpaper.FileSizeBytes is > 0 and <= 2_500_000 && !string.IsNullOrEmpty(i.Wallpaper.PreviewUrl));
            if (hero is not null)
            {
                Featured = hero;
                created.Remove(hero);
                _all.Add(hero);
            }
        }

        foreach (var item in created)
        {
            _all.Add(item);
            Items.Add(item);
        }

        HasMore = _cursors.Any(c => c.HasMore);
        var failedAny = failures.Count > 0;
        _autoLoadBlocked = failedAny;
        PartialWarning = failedAny && _all.Count > 0
            ? string.Join("  ·  ", failures.Select(f => $"{f.Cursor.Provider.DisplayName}: {f.Cursor.Error}"))
            : null;

        if (_all.Count == 0)
        {
            if (failedAny && failures.Count == outcomes.Length)
            {
                var kind = (failures[0].Error as ProviderException)?.Kind ?? ProviderErrorKind.Unknown;
                var (title, _) = ErrorText(kind);
                var detail = failures.Count == 1 ? failures[0].Cursor.Error! : string.Join("\n", failures.Select(f => $"{f.Cursor.Provider.DisplayName}: {f.Cursor.Error}"));
                ShowError(title, detail, kind == ProviderErrorKind.Network);
            }
            else
            {
                State = GalleryState.Empty;
            }
        }
        else
        {
            State = GalleryState.Loaded;
        }

        Summary = BuildSummary();
    }

    /// <summary>Heading for an untitled featured wallpaper that states honestly why it is shown.</summary>
    private string DescribeFeatured(WallpaperItemViewModel item) => item.Wallpaper.ProviderId == "wallhaven"
        ? Filters.SortOption.Value switch
        {
            WallpaperSort.Newest => "Fresh on Wallhaven",
            WallpaperSort.MostViewed => "Most viewed on Wallhaven",
            WallpaperSort.MostFavorited => "Most favorited on Wallhaven",
            WallpaperSort.Random => "A random pick from Wallhaven",
            _ => $"Top rated on Wallhaven, {Filters.TopRangeOption.Label.ToLowerInvariant()}"
        }
        : $"Featured from {item.ProviderName}";

    private string BuildSummary() =>
        _all.Count == 0 ? string.Empty : _all.Count == 1 ? "1 wallpaper loaded" : $"{_all.Count} wallpapers loaded";

    private IEnumerable<IWallpaperProvider> ProvidersInScope() =>
        SelectedSource.Id == AppSettings.AllSourcesId
            ? _providers.Enabled
            : _providers.Enabled.Where(p => p.Id == SelectedSource.Id);

    private static IEnumerable<Wallpaper> Interleave(List<List<Wallpaper>> lists)
    {
        var max = lists.Count == 0 ? 0 : lists.Max(l => l.Count);
        for (var i = 0; i < max; i++)
        {
            foreach (var list in lists)
            {
                if (i < list.Count) yield return list[i];
            }
        }
    }

    private void ShowError(string title, string detail, bool offline)
    {
        ErrorTitle = title;
        ErrorDetail = detail;
        ErrorIsOffline = offline;
        State = GalleryState.Error;
    }

    private static string DescribeError(Exception ex) => ex is ProviderException or LumaException
        ? ex.Message
        : "Something unexpected went wrong.";

    private static (string Title, string Icon) ErrorText(ProviderErrorKind kind) => kind switch
    {
        ProviderErrorKind.Network => ("You appear to be offline", "wifi"),
        ProviderErrorKind.Timeout => ("The source is taking too long", "clock"),
        ProviderErrorKind.RateLimited => ("Let's slow down for a moment", "clock"),
        ProviderErrorKind.ServerError => ("The source is having trouble", "alert"),
        ProviderErrorKind.Unauthorized => ("The source rejected the request", "key"),
        ProviderErrorKind.InvalidResponse => ("The source sent something unexpected", "alert"),
        _ => ("Couldn't load wallpapers", "alert")
    };

    private static string NewSeed()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        return string.Create(6, chars, static (span, c) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = c[Random.Shared.Next(c.Length)];
        });
    }

    private void OnSettingsChanged()
    {
        var previous = SelectedSource.Id;
        var before = string.Join(',', Sources.Select(s => s.Id));
        RebuildSources();
        if (string.Join(',', Sources.Select(s => s.Id)) == before) return;

        _suppressRefresh = true;
        SelectedSource = Sources.FirstOrDefault(s => s.Id == previous) ?? Sources[0];
        _suppressRefresh = false;
        RefreshCapabilityFlags();
        if (_initialised) ScheduleRefresh(FilterDebounce);
    }

    private void RebuildSources()
    {
        Sources.Clear();
        var enabled = _providers.Enabled.ToList();
        if (enabled.Count > 1) Sources.Add(new SourceOption(AppSettings.AllSourcesId, "All sources", "Icon.Layers"));
        foreach (var provider in enabled)
            Sources.Add(new SourceOption(provider.Id, provider.DisplayName, provider.Id == "wallhaven" ? "Icon.Mountain" : "Icon.Globe"));
        if (Sources.Count == 0) Sources.Add(new SourceOption(AppSettings.AllSourcesId, "No sources enabled", "Icon.Globe"));
    }

    private void RefreshCapabilityFlags()
    {
        var scope = ProvidersInScope().ToList();
        if (scope.Count == 0) scope = _providers.Enabled.ToList();
        var caps = scope.Select(p => p.Capabilities).ToList();

        ShowCategories = caps.Any(c => c.SupportsCategories);
        ShowPurity = caps.Any(c => c.SupportsPurity);
        ShowExplicit = caps.Any(c => c.SupportsExplicitContent) && !string.IsNullOrWhiteSpace(_settings.Current.WallhavenApiKey);
        ShowColors = caps.Any(c => c.SupportsColors);
        ShowContentKind = caps.Any(c => c.SupportsContentKind);
        ShowLicense = caps.Any(c => c.SupportsLicenseFilter);
        ShowMinResolution = caps.Any(c => c.SupportsMinimumResolution);
        ShowAspect = caps.Any(c => c.SupportsAspectRatio);
        ShowTopRange = scope.Any(p => p.Capabilities.Sorts.Contains(WallpaperSort.TopRated)) &&
                       (Filters.SortOption.Value == WallpaperSort.TopRated || (Filters.SortOption.Value == WallpaperSort.Relevance && string.IsNullOrWhiteSpace(EffectiveText)));

        var allowed = DiscoverFilters.AllSorts.Where(o => caps.Any(c => c.Sorts.Contains(o.Value))).ToList();
        if (allowed.Count == 0) allowed = [DiscoverFilters.AllSorts[0]];
        if (!SortOptions.SequenceEqual(allowed))
        {
            SortOptions = allowed;
            OnPropertyChanged(nameof(SortOptions));
            if (allowed.All(o => o.Value != Filters.SortOption.Value)) Filters.SortOption = allowed[0];
        }

        FilterScopeNote = scope.Count > 1
            ? "Some filters only apply to one source: categories, purity and colours to Wallhaven; type and license to Openverse."
            : null;
        OnPropertyChanged(nameof(HasFilters));
    }
}


