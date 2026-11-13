using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Luma.App.Services;
using Luma.Core.Enums;
using Luma.Core.Interfaces;
using Luma.Core.Logic;
using Luma.Core.Models;
using Microsoft.Extensions.Logging;

namespace Luma.App.ViewModels;

public sealed partial class PreviewService(IServiceProvider services) : ObservableObject, IPreviewService
{
    [ObservableProperty]
    private PreviewViewModel? _current;

    public void Open(WallpaperItemViewModel item, IReadOnlyList<WallpaperItemViewModel> context)
    {
        Current?.Dispose();
        Current = Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<PreviewViewModel>(services, item, context, (IPreviewService)this);
    }

    public void Close()
    {
        var current = Current;
        Current = null;
        current?.Dispose();
    }
}

/// <summary>
/// Immersive wallpaper preview. It shows the cached thumbnail instantly, then swaps in a single larger image (decoded to
/// at most ~2400 px) and releases it as soon as the preview closes or moves on.
/// </summary>
public sealed partial class PreviewViewModel : ObservableObject, IDisposable
{
    private const int MaxDecodeWidth = 2400;

    private readonly IReadOnlyList<WallpaperItemViewModel> _context;
    private readonly ImageLoader _loader;
    private readonly WallpaperActions _actions;
    private readonly IProviderRegistry _providers;
    private readonly ISettingsService _settings;
    private readonly IDownloadLibrary _library;
    private readonly IPreviewService _service;
    private readonly ILogger<PreviewViewModel> _logger;
    private CancellationTokenSource? _cts;
    private ImageLease? _thumbLease;
    private ImageLease? _fullLease;

    public PreviewViewModel(WallpaperItemViewModel item, IReadOnlyList<WallpaperItemViewModel> context, IPreviewService service,
        ImageLoader loader, WallpaperActions actions, IProviderRegistry providers, ISettingsService settings,
        IDownloadLibrary library, ILogger<PreviewViewModel> logger)
    {
        _context = context;
        _service = service;
        _loader = loader;
        _actions = actions;
        _providers = providers;
        _settings = settings;
        _library = library;
        _logger = logger;

        var capabilities = actions.ApplierCapabilities;
        CanApply = capabilities.IsSupported;
        ApplyNote = capabilities.Notes;
        FitModes = capabilities.SupportedFitModes.Count > 0 ? capabilities.SupportedFitModes : [WallpaperFitMode.Fill];
        _selectedFit = FitModes.Contains(settings.Current.DefaultFitMode) ? settings.Current.DefaultFitMode : FitModes[0];

        _item = item;
        _details = item.Wallpaper;
        _ = ShowAsync(item);
    }

    public IReadOnlyList<WallpaperFitMode> FitModes { get; }
    public bool CanApply { get; }
    public string? ApplyNote { get; }

    [ObservableProperty]
    private WallpaperItemViewModel _item;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(ResolutionText), nameof(AspectText), nameof(FileSizeText), nameof(FileTypeText),
        nameof(CreatorName), nameof(HasCreator), nameof(LicenseName), nameof(HasLicense), nameof(Attribution), nameof(HasAttribution),
        nameof(Tags), nameof(HasTags), nameof(Colors), nameof(HasColors), nameof(CategoryText), nameof(HasCategory), nameof(UpstreamText), nameof(HasUpstream))]
    private Wallpaper _details;

    [ObservableProperty]
    private Bitmap? _image;

    [ObservableProperty]
    private bool _isLoadingImage;

    [ObservableProperty]
    private bool _isRefiningImage;

    [ObservableProperty]
    private string? _imageError;

    [ObservableProperty]
    private WallpaperFitMode _selectedFit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApplyLabel))]
    private bool _isApplying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApplyLabel))]
    private double _applyProgress;

    public string ApplyLabel => IsApplying ? (ApplyProgress is > 0 and < 1 ? $"Downloading {ApplyProgress:P0}" : "Applying…") : "Set as wallpaper";

    public bool HasPrevious => Index > 0;
    public bool HasNext => Index >= 0 && Index < _context.Count - 1;
    private int Index => IndexOf(Item);
    public string PositionText => _context.Count > 1 && Index >= 0 ? $"{Index + 1} of {_context.Count}" : string.Empty;

    public string Title => Details.DisplayTitle;
    public string ResolutionText => Details.Resolution?.Label ?? "Unknown";
    public string AspectText => Details.Resolution?.AspectLabel ?? "Unknown";
    public string FileSizeText => ByteSizeFormatter.Format(Details.FileSizeBytes);
    public string FileTypeText => FormatFileType(Details.FileType);
    public string ProviderName => Item.ProviderName;
    public string? CreatorName => Details.Metadata.CreatorName;
    public bool HasCreator => !string.IsNullOrWhiteSpace(CreatorName);
    public string? LicenseName => Details.Metadata.LicenseName;
    public bool HasLicense => !string.IsNullOrWhiteSpace(LicenseName);
    public string? Attribution => Details.Metadata.Attribution;
    public bool HasAttribution => !string.IsNullOrWhiteSpace(Attribution);
    public IReadOnlyList<string> Tags => Details.Metadata.Tags.Take(14).ToList();
    public bool HasTags => Tags.Count > 0;
    public IReadOnlyList<string> Colors => Details.Metadata.Colors;
    public bool HasColors => Colors.Count > 0;
    public string? CategoryText => string.IsNullOrWhiteSpace(Details.Metadata.Category) ? null : char.ToUpperInvariant(Details.Metadata.Category[0]) + Details.Metadata.Category[1..];
    public bool HasCategory => CategoryText is not null;
    public string? UpstreamText => Details.Metadata.UpstreamSource;
    public bool HasUpstream => !string.IsNullOrWhiteSpace(UpstreamText) && !string.Equals(UpstreamText, Item.ProviderName, StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private void Close() => _service.Close();

    [RelayCommand]
    private async Task NextAsync()
    {
        if (HasNext) await ShowAsync(_context[Index + 1]);
    }

    [RelayCommand]
    private async Task PreviousAsync()
    {
        if (HasPrevious) await ShowAsync(_context[Index - 1]);
    }

    [RelayCommand]
    private Task ToggleFavoriteAsync() => _actions.ToggleFavoriteAsync(Item.Wallpaper);

    [RelayCommand]
    private Task DownloadAsync() => _actions.DownloadAsync(Item.Wallpaper);

    [RelayCommand]
    private Task AddToCollectionAsync() => _actions.AddToCollectionAsync(Item.Wallpaper);

    [RelayCommand]
    private void OpenSource() => _actions.OpenSource(Item.Wallpaper);

    [RelayCommand]
    private void OpenCreator() => _actions.OpenUrl(Details.Metadata.CreatorUrl);

    [RelayCommand]
    private void OpenLicense() => _actions.OpenUrl(Details.Metadata.LicenseUrl);

    [RelayCommand]
    private void ShowInFolder()
    {
        if (_library.GetLocalPath(Item.Key) is { } path) _actions.ShowInFolder(path);
    }

    [RelayCommand]
    private void Retry() => _ = ShowAsync(Item);

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (IsApplying || !CanApply) return;
        IsApplying = true;
        ApplyProgress = 0;
        try
        {
            var progress = new Progress<double>(p => ApplyProgress = p);
            await _actions.ApplyAsync(Item.Wallpaper, SelectedFit, progress);
        }
        finally
        {
            IsApplying = false;
            ApplyProgress = 0;
        }
    }

    private async Task ShowAsync(WallpaperItemViewModel item)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;

        Item = item;
        Details = item.Wallpaper;
        ImageError = null;
        NotifyNavigation();
        ReleaseImages();

        try
        {
            IsLoadingImage = true;

            // 1. The thumbnail is almost always cached already, so the preview opens with something to look at.
            if (item.ThumbnailSource is { } thumb)
            {
                try
                {
                    var lease = await _loader.AcquireThumbnailAsync(thumb, 420, ct);
                    if (ct.IsCancellationRequested) { lease.Dispose(); return; }
                    _thumbLease = lease;
                    Image = lease.Bitmap;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Preview thumbnail unavailable");
                }
            }

            _ = EnrichDetailsAsync(item, ct);

            // 2. Then a single larger image, taken from disk when the wallpaper is already downloaded.
            var source = _library.GetLocalPath(item.Key) ?? item.Wallpaper.PreviewUrl ?? item.Wallpaper.FullUrl;
            if (source is null || source == item.ThumbnailSource)
            {
                if (Image is null) ImageError = "This wallpaper has no preview image.";
                return;
            }

            IsRefiningImage = true;
            var width = Math.Min(item.Wallpaper.Resolution?.Width ?? 1920, MaxDecodeWidth);
            var full = await _loader.AcquirePreviewAsync(source, Math.Max(640, width), ct);
            if (ct.IsCancellationRequested) { full.Dispose(); return; }

            _fullLease = full;
            Image = full.Bitmap;
            _thumbLease?.Dispose();
            _thumbLease = null;
        }
        catch (OperationCanceledException)
        {
            // moved to another wallpaper or closed
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            _logger.LogWarning(ex, "Preview image failed for {Key}", item.Key);
            if (Image is null) ImageError = "The preview couldn't be loaded.";
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsLoadingImage = false;
                IsRefiningImage = false;
            }
        }
    }

    private async Task EnrichDetailsAsync(WallpaperItemViewModel item, CancellationToken ct)
    {
        var wallpaper = item.Wallpaper;
        if (wallpaper.Metadata.Tags.Count > 0 || _providers.Get(wallpaper.ProviderId) is not { Capabilities.SupportsDetails: true } provider) return;

        try
        {
            await Task.Delay(500, ct); // do not spend an API call while the user flicks through wallpapers
            var detailed = await provider.GetDetailsAsync(wallpaper.ExternalId, ct);
            if (detailed is null || ct.IsCancellationRequested || !ReferenceEquals(Item, item)) return;
            Details = wallpaper with
            {
                Metadata = detailed.Metadata with
                {
                    ViewCount = detailed.Metadata.ViewCount ?? wallpaper.Metadata.ViewCount,
                    FavoriteCount = detailed.Metadata.FavoriteCount ?? wallpaper.Metadata.FavoriteCount
                }
            };
        }
        catch (OperationCanceledException)
        {
            // fine
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not load details for {Key}", wallpaper.Key);
        }
    }

    private void NotifyNavigation()
    {
        OnPropertyChanged(nameof(HasPrevious));
        OnPropertyChanged(nameof(HasNext));
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(ProviderName));
        OnPropertyChanged(nameof(HasUpstream));
    }

    private int IndexOf(WallpaperItemViewModel item)
    {
        for (var i = 0; i < _context.Count; i++)
            if (ReferenceEquals(_context[i], item) || _context[i].Key == item.Key) return i;
        return -1;
    }

    private void ReleaseImages()
    {
        Image = null;
        _thumbLease?.Dispose();
        _thumbLease = null;
        _fullLease?.Dispose();
        _fullLease = null;
    }

    private static string FormatFileType(string? type) => type switch
    {
        null or "" => "Unknown",
        { } t when t.StartsWith("image/", StringComparison.OrdinalIgnoreCase) => t[6..].ToUpperInvariant().Replace("JPEG", "JPG"),
        { } t => t.ToUpperInvariant()
    };

    public void Dispose()
    {
        _cts?.Cancel();
        _cts = null;
        ReleaseImages();
    }
}
