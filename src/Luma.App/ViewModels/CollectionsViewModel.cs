using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Luma.App.Services;
using Luma.Core.Exceptions;
using Luma.Core.Interfaces;
using Luma.Core.Models;
using Microsoft.Extensions.Logging;

namespace Luma.App.ViewModels;

public sealed class CollectionCardViewModel(WallpaperCollection collection)
{
    public WallpaperCollection Collection { get; } = collection;
    public string Name => Collection.Name;
    public string? Description => Collection.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public string CountText => Collection.WallpaperCount switch { 0 => "Empty", 1 => "1 wallpaper", var n => $"{n} wallpapers" };
    public string CreatedText => $"Created {Collection.CreatedAt.ToLocalTime():MMM d, yyyy}";
    public bool IsEmpty => Collection.WallpaperCount == 0;

    // Up to four covers for the mosaic; missing slots stay empty.
    public string? Cover1 => Cover(0);
    public string? Cover2 => Cover(1);
    public string? Cover3 => Cover(2);
    public string? Cover4 => Cover(3);
    public bool HasMosaic => Collection.CoverWallpapers.Count >= 4;
    public bool HasSingleCover => Collection.CoverWallpapers.Count is > 0 and < 4;

    private string? Cover(int index) =>
        index < Collection.CoverWallpapers.Count ? Collection.CoverWallpapers[index].ThumbnailUrl ?? Collection.CoverWallpapers[index].LocalFilePath : null;
}

public sealed partial class CollectionsViewModel : PageViewModel
{
    private readonly IUiDispatcher _dispatcher;
    private readonly ICollectionService _collections;
    private readonly WallpaperItemFactory _factory;
    private readonly IDialogService _dialogs;
    private readonly IToastService _toasts;
    private readonly ShellStartup _startup;
    private readonly ILogger<CollectionsViewModel> _logger;
    private readonly List<WallpaperItemViewModel> _all = [];
    private bool _active;


    public CollectionsViewModel(ICollectionService collections, WallpaperItemFactory factory, IDialogService dialogs,
        IToastService toasts, ShellStartup startup, IUiDispatcher dispatcher, ILogger<CollectionsViewModel> logger)
    {
        _dispatcher = dispatcher;
        _collections = collections;
        _factory = factory;
        _dialogs = dialogs;
        _toasts = toasts;
        _startup = startup;
        _logger = logger;
        _collections.CollectionsChanged += (_, _) => _dispatcher.Post(() =>
        {
            if (_active) _ = ReloadAsync();
        });
    }

    public override string Title => "Collections";

    public ObservableCollection<CollectionCardViewModel> Collections { get; } = [];

    public ObservableCollection<WallpaperItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverview), nameof(IsDetail), nameof(IsOverviewEmpty), nameof(DetailIsEmpty))]
    private CollectionCardViewModel? _current;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverviewEmpty), nameof(DetailIsEmpty))]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasError;

    public bool IsOverview => Current is null;
    public bool IsDetail => Current is not null;
    public bool IsOverviewEmpty => IsOverview && !IsLoading && Collections.Count == 0 && !HasError;
    public bool DetailIsEmpty => IsDetail && !IsLoading && Items.Count == 0;

    public override async Task OnActivatedAsync()
    {
        _active = true;
        await _startup.BeginAsync();
        await ReloadAsync();
    }

    public override void OnDeactivated() => _active = false;

    [RelayCommand]
    public async Task ReloadAsync()
    {
        IsLoading = Collections.Count == 0;
        HasError = false;
        try
        {
            await _startup.Ready;
            var all = await _collections.GetAllAsync();
            Collections.Clear();
            foreach (var c in all) Collections.Add(new CollectionCardViewModel(c));

            if (Current is not null)
            {
                var refreshed = Collections.FirstOrDefault(c => c.Collection.Id == Current.Collection.Id);
                if (refreshed is null)
                {
                    Current = null;
                    Items.Clear();
                }
                else
                {
                    Current = refreshed;
                    await LoadItemsAsync(refreshed);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading collections failed");
            HasError = true;
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsOverviewEmpty));
        }
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        var result = await _dialogs.EditCollectionAsync("New collection", "Create", validate: ValidateNameAsync(null));
        if (result is null) return;
        try
        {
            await _collections.CreateAsync(result.Name, result.Description);
            _toasts.Show($"Created \"{result.Name}\"", ToastKind.Success);
        }
        catch (Exception ex)
        {
            Report(ex, "The collection couldn't be created.");
        }
    }

    [RelayCommand]
    private async Task OpenAsync(CollectionCardViewModel card)
    {
        Current = card;
        await LoadItemsAsync(card);
    }

    [RelayCommand]
    private void Back()
    {
        Current = null;
        Items.Clear();
        _all.Clear();
    }

    [RelayCommand]
    private async Task RenameAsync(CollectionCardViewModel? card)
    {
        card ??= Current;
        if (card is null) return;
        var result = await _dialogs.EditCollectionAsync("Edit collection", "Save", card.Name, card.Description, ValidateNameAsync(card.Collection.Id));
        if (result is null) return;
        try
        {
            await _collections.RenameAsync(card.Collection.Id, result.Name, result.Description);
        }
        catch (Exception ex)
        {
            Report(ex, "The collection couldn't be updated.");
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(CollectionCardViewModel? card)
    {
        card ??= Current;
        if (card is null) return;
        var confirmed = await _dialogs.ConfirmAsync(
            $"Delete \"{card.Name}\"?",
            "Only the collection is deleted. The wallpapers in it, and any files you downloaded, stay exactly where they are.",
            "Delete collection", destructive: true);
        if (!confirmed) return;
        try
        {
            await _collections.DeleteAsync(card.Collection.Id);
            _toasts.Show("Collection deleted", ToastKind.Info);
        }
        catch (Exception ex)
        {
            Report(ex, "The collection couldn't be deleted.");
        }
    }

    private async Task LoadItemsAsync(CollectionCardViewModel card)
    {
        try
        {
            var wallpapers = await _collections.GetWallpapersAsync(card.Collection.Id);
            _all.Clear();
            Items.Clear();
            foreach (var wallpaper in wallpapers)
            {
                var item = _factory.Create(wallpaper, () => _all);
                item.RemoveLabel = "Remove from collection";
                item.RemoveHandler = RemoveItemAsync;
                _all.Add(item);
                Items.Add(item);
            }
            OnPropertyChanged(nameof(DetailIsEmpty));
        }
        catch (Exception ex)
        {
            Report(ex, "The collection couldn't be opened.");
        }
    }

    private async Task RemoveItemAsync(WallpaperItemViewModel item)
    {
        if (Current is null) return;
        try
        {
            await _collections.RemoveWallpaperAsync(Current.Collection.Id, item.Key);
        }
        catch (Exception ex)
        {
            Report(ex, "The wallpaper couldn't be removed.");
        }
    }

    private Func<string, string?, Task<string?>> ValidateNameAsync(long? ignoreId) => async (name, _) =>
    {
        if (name.Length > 120) return "Names can be at most 120 characters.";
        var existing = await _collections.GetAllAsync();
        return existing.Any(c => c.Id != ignoreId && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
            ? $"You already have a collection called \"{name}\"."
            : null;
    };

    private void Report(Exception ex, string fallback)
    {
        _logger.LogError(ex, "{Message}", fallback);
        _toasts.Show(ex is LumaException ? ex.Message : fallback, ToastKind.Error);
    }
}



