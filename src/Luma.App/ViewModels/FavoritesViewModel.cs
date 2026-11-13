using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Luma.App.Services;
using Luma.Core.Enums;
using Luma.Core.Interfaces;
using Luma.Core.Models;
using Microsoft.Extensions.Logging;

namespace Luma.App.ViewModels;

/// <summary>Saved wallpapers. Favouriting never downloads the file; metadata is stored locally so this works offline.</summary>
public sealed partial class FavoritesViewModel : PageViewModel
{
    public static readonly IReadOnlyList<Option<LibrarySort>> SortChoices =
    [
        new("Recently added", LibrarySort.Newest), new("Oldest first", LibrarySort.Oldest),
        new("Name", LibrarySort.Name), new("Resolution", LibrarySort.Resolution)
    ];

    private readonly IFavoritesService _favorites;
    private readonly WallpaperItemFactory _factory;
    private readonly ShellStartup _startup;
    private readonly IProviderRegistry _providers;
    private readonly INavigationService _navigation;
    private readonly ILogger<FavoritesViewModel> _logger;
    private readonly List<WallpaperItemViewModel> _all = [];
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _debounce;
    private bool _suppress;

    public FavoritesViewModel(IFavoritesService favorites, WallpaperItemFactory factory, ShellStartup startup,
        IProviderRegistry providers, INavigationService navigation, ILogger<FavoritesViewModel> logger)
    {
        _favorites = favorites;
        _factory = factory;
        _startup = startup;
        _providers = providers;
        _navigation = navigation;
        _logger = logger;
        _selectedSort = SortChoices[0];
        ProviderChoices = [new Option<string?>("All sources", null), .. providers.All.Select(p => new Option<string?>(p.DisplayName, p.Id)), new Option<string?>("Local imports", Wallpaper.LocalProviderId)];
        _selectedProvider = ProviderChoices[0];
    }

    public override string Title => "Favorites";

    public ObservableCollection<WallpaperItemViewModel> Items { get; } = [];

    public IReadOnlyList<Option<LibrarySort>> Sorts => SortChoices;

    public IReadOnlyList<Option<string?>> ProviderChoices { get; }

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private Option<LibrarySort> _selectedSort;

    [ObservableProperty]
    private Option<string?> _selectedProvider;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsEmpty), nameof(IsError), nameof(HasContent), nameof(EmptyBecauseOfFilters))]
    private GalleryState _state = GalleryState.Idle;

    [ObservableProperty]
    private int _total;

    public bool IsLoading => State == GalleryState.Loading;
    public bool IsEmpty => State == GalleryState.Empty;
    public bool IsError => State == GalleryState.Error;
    public bool HasContent => State == GalleryState.Loaded;
    public bool EmptyBecauseOfFilters => IsEmpty && (!string.IsNullOrWhiteSpace(SearchText) || SelectedProvider.Value is not null);
    public string CountText => Total == 1 ? "1 favorite" : $"{Total} favorites";

    partial void OnSearchTextChanged(string value) => Schedule(TimeSpan.FromMilliseconds(250));

    partial void OnSelectedSortChanged(Option<LibrarySort> value) => Schedule(TimeSpan.Zero);

    partial void OnSelectedProviderChanged(Option<string?> value) => Schedule(TimeSpan.Zero);

    public override async Task OnActivatedAsync()
    {
        await _startup.BeginAsync();
        await ReloadAsync();
    }

    [RelayCommand]
    private void BrowseDiscover() => _navigation.NavigateTo(AppPage.Discover);

    [RelayCommand]
    private void ClearFilters()
    {
        _suppress = true;
        SearchText = string.Empty;
        SelectedProvider = ProviderChoices[0];
        _suppress = false;
        _ = ReloadAsync();
    }

    [RelayCommand]
    public async Task ReloadAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        State = _all.Count == 0 ? GalleryState.Loading : State;
        try
        {
            await _startup.Ready;
            var wallpapers = await _favorites.QueryAsync(new LibraryQuery
            {
                Text = SearchText,
                Sort = SelectedSort.Value,
                ProviderId = SelectedProvider.Value
            }, cts.Token);
            if (cts.IsCancellationRequested) return;

            _all.Clear();
            Items.Clear();
            foreach (var wallpaper in wallpapers)
            {
                var item = _factory.Create(wallpaper, () => _all);
                _all.Add(item);
                Items.Add(item);
            }
            Total = _all.Count;
            OnPropertyChanged(nameof(CountText));
            State = _all.Count == 0 ? GalleryState.Empty : GalleryState.Loaded;
        }
        catch (OperationCanceledException)
        {
            // superseded
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading favourites failed");
            State = GalleryState.Error;
        }
    }

    private void Schedule(TimeSpan delay)
    {
        if (_suppress) return;
        _debounce?.Cancel();
        var cts = _debounce = new CancellationTokenSource();
        _ = Run();

        async Task Run()
        {
            try
            {
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cts.Token);
                await ReloadAsync();
            }
            catch (OperationCanceledException)
            {
                // newer input arrived
            }
        }
    }
}
