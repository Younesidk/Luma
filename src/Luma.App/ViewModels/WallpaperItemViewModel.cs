using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Luma.App.Services;
using Luma.Core.Enums;
using Luma.Core.Interfaces;
using Luma.Core.Models;

namespace Luma.App.ViewModels;

/// <summary>
/// One wallpaper as shown in a gallery card. It follows favourite / download state through weak messages, so a heart
/// toggled in the preview updates the grid card (and vice versa) without any page-level bookkeeping.
/// </summary>
public sealed partial class WallpaperItemViewModel : ObservableObject,
    IRecipient<FavoriteChangedMessage>, IRecipient<LibraryChangedMessage>, IRecipient<DownloadProgressMessage>
{
    private readonly WallpaperActions _actions;
    private readonly IPreviewService _preview;
    private readonly string _providerName;

    public WallpaperItemViewModel(Wallpaper wallpaper, WallpaperActions actions, IPreviewService preview,
        IFavoritesService favorites, IDownloadLibrary library, string providerName)
    {
        Wallpaper = wallpaper;
        _actions = actions;
        _preview = preview;
        _providerName = providerName;
        _isFavorite = favorites.IsFavorite(wallpaper.Key);
        _isDownloaded = library.IsDownloaded(wallpaper.Key);
        WeakReferenceMessenger.Default.RegisterAll(this);
    }

    public Wallpaper Wallpaper { get; }

    /// <summary>The list this card belongs to; used for previous/next navigation inside the preview.</summary>
    public Func<IReadOnlyList<WallpaperItemViewModel>>? ContextProvider { get; set; }

    public string Key => Wallpaper.Key;
    public string Title => Wallpaper.DisplayTitle;
    public string ProviderName => _providerName;
    public bool HasResolution => Wallpaper.Resolution is { IsValid: true };
    public string ResolutionText => Wallpaper.Resolution?.Label ?? string.Empty;
    public string ShortResolutionText => Wallpaper.Resolution is { IsValid: true } r ? $"{r.Width}×{r.Height}" : string.Empty;

    /// <summary>Thumbnail address: the remote thumbnail, or the file itself for local imports without one.</summary>
    public string? ThumbnailSource => Wallpaper.ThumbnailUrl ?? Wallpaper.LocalFilePath ?? Wallpaper.PreviewUrl;

    /// <summary>
    /// Used only when the provider's thumbnail cannot be loaded (some Openverse thumbnails are unavailable). The original
    /// is only considered when it is small, so a failed thumbnail never triggers a large download.
    /// </summary>
    public string? ThumbnailFallbackSource =>
        Wallpaper.FullUrl is { Length: > 0 } url && url != ThumbnailSource && Wallpaper.FileSizeBytes is > 0 and <= FallbackMaxBytes ? url : null;

    private const long FallbackMaxBytes = 1_200_000;

    public string? CreatorText => Wallpaper.Metadata.CreatorName;

    [ObservableProperty]
    private bool _isFavorite;

    [ObservableProperty]
    private bool _isDownloaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    private bool _isDownloading;

    [ObservableProperty]
    private double _downloadProgress;

    public bool CanDownload => !IsDownloaded && !IsDownloading;

    partial void OnIsDownloadedChanged(bool value) => OnPropertyChanged(nameof(CanDownload));

    /// <summary>Set by lists that can drop an item (e.g. a collection); enables "Remove from this collection".</summary>
    public Func<WallpaperItemViewModel, Task>? RemoveHandler { get; set; }

    public string RemoveLabel { get; set; } = "Remove";

    public bool CanRemove => RemoveHandler is not null;

    [RelayCommand]
    private Task RemoveAsync() => RemoveHandler?.Invoke(this) ?? Task.CompletedTask;

    [RelayCommand]
    private void Open() => _preview.Open(this, ContextProvider?.Invoke() ?? [this]);

    [RelayCommand]
    private Task ToggleFavoriteAsync() => _actions.ToggleFavoriteAsync(Wallpaper);

    [RelayCommand]
    private Task DownloadAsync() => _actions.DownloadAsync(Wallpaper);

    [RelayCommand]
    private Task ApplyAsync() => _actions.ApplyAsync(Wallpaper);

    [RelayCommand]
    private Task AddToCollectionAsync() => _actions.AddToCollectionAsync(Wallpaper);

    [RelayCommand]
    private void OpenSource() => _actions.OpenSource(Wallpaper);

    public void Receive(FavoriteChangedMessage message)
    {
        if (message.Key == Key) IsFavorite = message.IsFavorite;
    }

    public void Receive(LibraryChangedMessage message)
    {
        if (message.Key != Key) return;
        IsDownloaded = message.IsDownloaded;
        if (message.IsDownloaded) IsDownloading = false;
    }

    public void Receive(DownloadProgressMessage message)
    {
        if (message.Key != Key) return;
        switch (message.Status)
        {
            case DownloadStatus.Queued:
            case DownloadStatus.Downloading:
                IsDownloading = true;
                DownloadProgress = message.Progress;
                break;
            default:
                IsDownloading = false;
                DownloadProgress = 0;
                break;
        }
    }
}

/// <summary>Creates card view models with the shared services they need.</summary>
public sealed class WallpaperItemFactory(WallpaperActions actions, IPreviewService preview, IFavoritesService favorites,
    IDownloadLibrary library, IProviderRegistry providers)
{
    public WallpaperItemViewModel Create(Wallpaper wallpaper, Func<IReadOnlyList<WallpaperItemViewModel>>? context = null) =>
        new(wallpaper, actions, preview, favorites, library, providers.GetDisplayName(wallpaper.ProviderId))
        {
            ContextProvider = context
        };
}
