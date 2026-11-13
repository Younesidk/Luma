using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Luma.App.Services;
using Luma.Core.Enums;
using Luma.Core.Exceptions;
using Luma.Core.Interfaces;
using Luma.Core.Logic;
using Luma.Core.Models;
using Microsoft.Extensions.Logging;

namespace Luma.App.ViewModels;

public sealed partial class AccentSwatchViewModel(AccentPreset preset, Action<AccentSwatchViewModel> select) : ObservableObject
{
    public AccentPreset Preset { get; } = preset;
    public string Name => Preset.Name;
    public IBrush Brush { get; } = new SolidColorBrush(preset.Dark);

    [ObservableProperty]
    private bool _isSelected;

    [RelayCommand]
    private void Select() => select(this);
}

public sealed partial class ProviderStatusViewModel : ObservableObject
{
    private readonly IWallpaperProvider _provider;
    private readonly Func<bool> _isEnabled;
    private readonly Action<bool> _setEnabled;

    public ProviderStatusViewModel(IWallpaperProvider provider, Func<bool> isEnabled, Action<bool> setEnabled)
    {
        _provider = provider;
        _isEnabled = isEnabled;
        _setEnabled = setEnabled;
    }

    public string Name => _provider.DisplayName;
    public string Id => _provider.Id;
    public string Limits => _provider.Capabilities.RateLimitSummary ?? "No published limit";

    public bool IsEnabled
    {
        get => _isEnabled();
        set
        {
            if (_isEnabled() == value) return;
            _setEnabled(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(HasRateInfo))]
    private string? _checkResult;

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private bool _lastCheckFailed;

    public string StatusText => !IsEnabled ? "Turned off" : CheckResult ?? "Not checked yet";
    public string? RateInfo => _provider.LastRateLimitInfo;
    public bool HasRateInfo => !string.IsNullOrEmpty(_provider.LastRateLimitInfo);

    [RelayCommand]
    private async Task CheckAsync()
    {
        if (IsChecking || !IsEnabled) return;
        IsChecking = true;
        LastCheckFailed = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var result = await _provider.SearchAsync(new WallpaperSearchQuery { Text = "wallpaper", Page = 1 }, cts.Token);
            CheckResult = $"Working  ·  {result.Items.Count} results in the last check";
        }
        catch (Exception ex)
        {
            LastCheckFailed = true;
            CheckResult = ex is ProviderException or LumaException ? ex.Message : "The check failed unexpectedly.";
        }
        finally
        {
            IsChecking = false;
            OnPropertyChanged(nameof(RateInfo));
            OnPropertyChanged(nameof(HasRateInfo));
        }
    }
}

public sealed partial class SettingsViewModel : PageViewModel
{
    private static readonly Dictionary<ThemePreference, string> ThemeNames = new()
    {
        [ThemePreference.System] = "Match system",
        [ThemePreference.Dark] = "Dark",
        [ThemePreference.Light] = "Light"
    };

    private readonly ISettingsService _settings;
    private readonly ThemeService _theme;
    private readonly IProviderRegistry _providers;
    private readonly IImageFetcher _fetcher;
    private readonly ImageLoader _imageLoader;
    private readonly ICollectionService _collections;
    private readonly IRotationService _rotation;
    private readonly IShellLauncher _shell;
    private readonly ILumaPaths _paths;
    private readonly IFileDialogService _files;
    private readonly IDialogService _dialogs;
    private readonly IToastService _toasts;
    private readonly IWallpaperApplyService _apply;
    private readonly ShellStartup _startup;
    private readonly ILogger<SettingsViewModel> _logger;

    public SettingsViewModel(ISettingsService settings, ThemeService theme, IProviderRegistry providers, IImageFetcher fetcher,
        ImageLoader imageLoader, ICollectionService collections, IRotationService rotation, IShellLauncher shell, ILumaPaths paths,
        IFileDialogService files, IDialogService dialogs, IToastService toasts, IWallpaperApplyService apply, ShellStartup startup,
        ILogger<SettingsViewModel> logger)
    {
        _settings = settings;
        _theme = theme;
        _providers = providers;
        _fetcher = fetcher;
        _imageLoader = imageLoader;
        _collections = collections;
        _rotation = rotation;
        _shell = shell;
        _paths = paths;
        _files = files;
        _dialogs = dialogs;
        _toasts = toasts;
        _apply = apply;
        _startup = startup;
        _logger = logger;

        ThemeOptions = Enum.GetValues<ThemePreference>().Select(t => new Option<ThemePreference>(ThemeNames[t], t)).ToList();
        DensityOptions = [new("Comfortable", GalleryDensity.Comfortable), new("Compact", GalleryDensity.Compact)];
        FitOptions = _apply.Capabilities.SupportedFitModes.Count > 0
            ? _apply.Capabilities.SupportedFitModes.Select(m => new Option<WallpaperFitMode>(m.ToString(), m)).ToList()
            : Enum.GetValues<WallpaperFitMode>().Select(m => new Option<WallpaperFitMode>(m.ToString(), m)).ToList();
        IntervalOptions =
        [
            new("Every 15 minutes", 15), new("Every 30 minutes", 30), new("Every hour", 60), new("Every 3 hours", 180),
            new("Every 6 hours", 360), new("Every 12 hours", 720), new("Every day", 1440)
        ];
        RotationSourceOptions =
        [
            new("All downloaded wallpapers", RotationSource.AllDownloads), new("Downloaded favorites", RotationSource.Favorites),
            new("A collection", RotationSource.Collection)
        ];
        Accents = ThemeService.Presets.Select(p => new AccentSwatchViewModel(p, SelectAccent)).ToList();
        foreach (var accent in Accents) accent.IsSelected = accent.Preset.Id == S.AccentPreset;

        ProviderStatuses = _providers.All.Select(p => new ProviderStatusViewModel(p,
            () => p.Id == "wallhaven" ? S.WallhavenEnabled : S.OpenverseEnabled,
            enabled => _settings.Update(s =>
            {
                if (p.Id == "wallhaven") s.WallhavenEnabled = enabled;
                else s.OpenverseEnabled = enabled;
            }))).ToList();
        RebuildSourceOptions();

        Version = typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";
        _rotation.Rotated += (_, _) => Dispatcher.UIThread.Post(RefreshRotationText);
    }

    public override string Title => "Settings";

    private AppSettings S => _settings.Current;

    public IReadOnlyList<Option<ThemePreference>> ThemeOptions { get; }
    public IReadOnlyList<Option<GalleryDensity>> DensityOptions { get; }
    public IReadOnlyList<Option<WallpaperFitMode>> FitOptions { get; }
    public IReadOnlyList<Option<int>> IntervalOptions { get; }
    public IReadOnlyList<Option<RotationSource>> RotationSourceOptions { get; }
    public IReadOnlyList<AccentSwatchViewModel> Accents { get; }
    public IReadOnlyList<ProviderStatusViewModel> ProviderStatuses { get; }
    public ObservableCollection<Option<string>> SourceOptions { get; } = [];
    public ObservableCollection<Option<long?>> CollectionOptions { get; } = [];

    public string Version { get; }
    public string DataDirectory => _paths.DataRoot;
    public string LogsDirectory => _paths.LogsDirectory;
    public string PlatformSummary => _apply.Capabilities.IsSupported
        ? $"{_apply.Capabilities.PlatformName}  ·  wallpapers can be applied directly"
        : $"{_apply.Capabilities.PlatformName}  ·  applying wallpapers isn't supported here";
    public string? PlatformNote => _apply.Capabilities.Notes;
    public bool CanApplyWallpapers => _apply.Capabilities.IsSupported;
    public string DownloadDirectoryText => _settings.EffectiveDownloadDirectory;
    public bool UsesCustomDownloadDirectory => !string.IsNullOrWhiteSpace(S.DownloadDirectory);

    [ObservableProperty]
    private string _cacheSizeText = "Calculating…";

    [ObservableProperty]
    private string _rotationStatus = string.Empty;

    [ObservableProperty]
    private string _memoryUsageText = string.Empty;

    // ----- Appearance -----

    public Option<ThemePreference> SelectedTheme
    {
        get => ThemeOptions.First(o => o.Value == S.Theme);
        set => Set(s => s.Theme, (s, v) => s.Theme = v, value.Value, after: () => _theme.Apply(S.Theme, S.AccentPreset));
    }

    public Option<GalleryDensity> SelectedDensity
    {
        get => DensityOptions.First(o => o.Value == S.Density);
        set => Set(s => s.Density, (s, v) => s.Density = v, value.Value);
    }

    public bool SidebarCompact
    {
        get => S.SidebarCompact;
        set => Set(s => s.SidebarCompact, (s, v) => s.SidebarCompact = v, value);
    }

    private void SelectAccent(AccentSwatchViewModel swatch)
    {
        foreach (var a in Accents) a.IsSelected = ReferenceEquals(a, swatch);
        _settings.Update(s => s.AccentPreset = swatch.Preset.Id);
        _theme.ApplyAccent(swatch.Preset.Id);
    }

    public IReadOnlyList<Option<RenderingMode>> RenderingOptions { get; } =
    [
        new("Efficient (low memory)", RenderingMode.Efficient),
        new("Accelerated (GPU)", RenderingMode.Accelerated)
    ];

    public Option<RenderingMode> SelectedRendering
    {
        get => RenderingOptions.First(o => o.Value == S.RenderingMode);
        set => Set(s => s.RenderingMode, (s, v) => s.RenderingMode = v, value.Value, after: () => OnPropertyChanged(nameof(RenderingNote)));
    }

    public string RenderingNote => S.RenderingMode == RenderingMode.Efficient
        ? "Uses the CPU to draw the interface, which keeps memory low. Takes effect the next time Luma starts."
        : "Uses the graphics card for smoother animation on very high resolution displays, at the cost of roughly 100 MB more memory. Takes effect the next time Luma starts.";

    // ----- Downloads -----

    public bool ConfirmBeforeDelete
    {
        get => S.ConfirmBeforeDelete;
        set => Set(s => s.ConfirmBeforeDelete, (s, v) => s.ConfirmBeforeDelete = v, value);
    }

    [RelayCommand]
    private async Task ChooseDownloadFolderAsync()
    {
        var folder = await _files.PickFolderAsync("Choose where Luma saves wallpapers");
        if (folder is null) return;
        try
        {
            Directory.CreateDirectory(folder);
            _settings.Update(s => s.DownloadDirectory = folder);
            OnPropertyChanged(nameof(DownloadDirectoryText));
            OnPropertyChanged(nameof(UsesCustomDownloadDirectory));
            _toasts.Show("New downloads will be saved there. Existing files stay where they are.", ToastKind.Info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _toasts.Show("Luma can't write to that folder. Pick another one.", ToastKind.Error);
        }
    }

    [RelayCommand]
    private void ResetDownloadFolder()
    {
        _settings.Update(s => s.DownloadDirectory = null);
        OnPropertyChanged(nameof(DownloadDirectoryText));
        OnPropertyChanged(nameof(UsesCustomDownloadDirectory));
    }

    [RelayCommand]
    private void OpenDownloadFolder()
    {
        Directory.CreateDirectory(_settings.EffectiveDownloadDirectory);
        if (!_shell.OpenFolder(_settings.EffectiveDownloadDirectory)) _toasts.Show("The folder couldn't be opened.", ToastKind.Warning);
    }

    // ----- Providers -----

    public string WallhavenApiKey
    {
        get => S.WallhavenApiKey ?? string.Empty;
        set => Set(s => s.WallhavenApiKey ?? string.Empty, (s, v) => s.WallhavenApiKey = string.IsNullOrWhiteSpace(v) ? null : v.Trim(), value);
    }

    public string OpenverseClientId
    {
        get => S.OpenverseClientId ?? string.Empty;
        set => Set(s => s.OpenverseClientId ?? string.Empty, (s, v) => s.OpenverseClientId = string.IsNullOrWhiteSpace(v) ? null : v.Trim(), value);
    }

    public string OpenverseClientSecret
    {
        get => S.OpenverseClientSecret ?? string.Empty;
        set => Set(s => s.OpenverseClientSecret ?? string.Empty, (s, v) => s.OpenverseClientSecret = string.IsNullOrWhiteSpace(v) ? null : v.Trim(), value);
    }

    // ----- Wallpaper -----

    public Option<WallpaperFitMode> SelectedFit
    {
        get => FitOptions.FirstOrDefault(o => o.Value == S.DefaultFitMode) ?? FitOptions[0];
        set => Set(s => s.DefaultFitMode, (s, v) => s.DefaultFitMode = v, value.Value);
    }

    public Option<string> SelectedDefaultSource
    {
        get => SourceOptions.FirstOrDefault(o => o.Value == S.DefaultSource) ?? SourceOptions[0];
        set => Set(s => s.DefaultSource, (s, v) => s.DefaultSource = v, value.Value);
    }

    public bool RotationEnabled
    {
        get => S.RotationEnabled;
        set => Set(s => s.RotationEnabled, (s, v) => s.RotationEnabled = v, value, after: RefreshRotationText);
    }

    public Option<int> SelectedInterval
    {
        get => IntervalOptions.FirstOrDefault(o => o.Value == S.RotationIntervalMinutes) ?? IntervalOptions[2];
        set => Set(s => s.RotationIntervalMinutes, (s, v) => s.RotationIntervalMinutes = v, value.Value, after: RefreshRotationText);
    }

    public Option<RotationSource> SelectedRotationSource
    {
        get => RotationSourceOptions.First(o => o.Value == S.RotationSource);
        set => Set(s => s.RotationSource, (s, v) => s.RotationSource = v, value.Value, after: () => OnPropertyChanged(nameof(ShowRotationCollection)));
    }

    public bool ShowRotationCollection => S.RotationSource == RotationSource.Collection;

    public Option<long?>? SelectedRotationCollection
    {
        get => CollectionOptions.FirstOrDefault(o => o.Value == S.RotationCollectionId) ?? CollectionOptions.FirstOrDefault();
        set
        {
            if (value is null) return;
            Set(s => s.RotationCollectionId, (s, v) => s.RotationCollectionId = v, value.Value);
        }
    }

    public bool RotationRandom
    {
        get => S.RotationRandom;
        set => Set(s => s.RotationRandom, (s, v) => s.RotationRandom = v, value);
    }

    [RelayCommand]
    private async Task RotateNowAsync()
    {
        try
        {
            var result = await _rotation.RotateNowAsync();
            _toasts.Show(result.Success ? "Wallpaper changed" : result.Message ?? "Nothing to rotate through yet.", result.Success ? ToastKind.Success : ToastKind.Warning);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual rotation failed");
            _toasts.Show("The wallpaper couldn't be changed.", ToastKind.Error);
        }
        RefreshRotationText();
    }

    // ----- Performance -----

    public int ThumbnailCacheMegabytes
    {
        get => S.ThumbnailCacheMegabytes;
        set => Set(s => s.ThumbnailCacheMegabytes, (s, v) => s.ThumbnailCacheMegabytes = v, value, after: () => OnPropertyChanged(nameof(ThumbnailCacheText)));
    }

    public string ThumbnailCacheText => $"{S.ThumbnailCacheMegabytes} MB";

    public int MemoryCacheItems
    {
        get => S.MemoryCacheItems;
        set => Set(s => s.MemoryCacheItems, (s, v) => s.MemoryCacheItems = v, value, after: () => OnPropertyChanged(nameof(MemoryCacheItemsText)));
    }

    public string MemoryCacheItemsText => $"{S.MemoryCacheItems} thumbnails";

    public int MaxConcurrentDownloads
    {
        get => S.MaxConcurrentDownloads;
        set => Set(s => s.MaxConcurrentDownloads, (s, v) => s.MaxConcurrentDownloads = v, value, after: () => OnPropertyChanged(nameof(MaxConcurrentDownloadsText)));
    }

    public string MaxConcurrentDownloadsText => S.MaxConcurrentDownloads == 1 ? "1 at a time" : $"{S.MaxConcurrentDownloads} at a time";

    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        try
        {
            await _fetcher.ClearCacheAsync();
            _imageLoader.Clear();
            await RefreshCacheSizeAsync();
            _toasts.Show("Thumbnail cache cleared", ToastKind.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clearing the cache failed");
            _toasts.Show("The cache couldn't be fully cleared.", ToastKind.Error);
        }
    }

    // ----- Application -----

    [RelayCommand]
    private void OpenDataFolder() => OpenFolder(_paths.DataRoot);

    [RelayCommand]
    private void OpenLogsFolder() => OpenFolder(_paths.LogsDirectory);

    [RelayCommand]
    private void OpenWebsite(string? url)
    {
        if (!_shell.OpenUrl(url ?? string.Empty)) _toasts.Show("That link couldn't be opened.", ToastKind.Warning);
    }

    [RelayCommand]
    private async Task ResetSettingsAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync("Reset all settings?",
            "Appearance, providers, API keys, rotation and performance options return to their defaults. Your favorites, collections and downloaded files are not affected.",
            "Reset settings", destructive: true);
        if (!confirmed) return;

        await _settings.ResetAsync();
        _theme.Apply(S.Theme, S.AccentPreset);
        foreach (var accent in Accents) accent.IsSelected = accent.Preset.Id == S.AccentPreset;
        OnPropertyChanged(string.Empty); // refresh every binding
        RebuildSourceOptions();
        _toasts.Show("Settings were reset", ToastKind.Success);
    }

    public override async Task OnActivatedAsync()
    {
        RebuildSourceOptions();
        RefreshRotationText();
        UpdateMemory();
        await RefreshCacheSizeAsync();
        try
        {
            await _startup.BeginAsync();
            await _startup.Ready;
            var collections = await _collections.GetAllAsync();
            CollectionOptions.Clear();
            CollectionOptions.Add(new Option<long?>("Choose a collection…", null));
            foreach (var c in collections) CollectionOptions.Add(new Option<long?>(c.Name, c.Id));
            OnPropertyChanged(nameof(SelectedRotationCollection));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load collections for the rotation picker");
        }
    }

    private void RebuildSourceOptions()
    {
        SourceOptions.Clear();
        SourceOptions.Add(new Option<string>("All sources", AppSettings.AllSourcesId));
        foreach (var provider in _providers.All) SourceOptions.Add(new Option<string>(provider.DisplayName, provider.Id));
        OnPropertyChanged(nameof(SelectedDefaultSource));
    }

    private async Task RefreshCacheSizeAsync()
    {
        var size = await Task.Run(() => _fetcher.GetCacheSizeBytes());
        CacheSizeText = $"{ByteSizeFormatter.Format(size)} used";
    }

    private void RefreshRotationText()
    {
        RotationStatus = !S.RotationEnabled
            ? "Rotation is off."
            : _rotation.NextRotationAt is { } next
                ? $"Next change {(next.ToLocalTime().Date == DateTime.Today ? "today" : next.ToLocalTime().ToString("MMM d"))} at {next.ToLocalTime():t}. Luma has to be running."
                : "Rotation is on. Luma has to be running for wallpapers to change.";
    }

    private void UpdateMemory()
    {
        var working = Environment.WorkingSet;
        MemoryUsageText = $"{ByteSizeFormatter.Format(working)} in use  ·  {_imageLoader.CachedCount} thumbnails decoded";
    }

    private void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        if (!_shell.OpenFolder(path)) _toasts.Show("The folder couldn't be opened.", ToastKind.Warning);
    }

    private void Set<T>(Func<AppSettings, T> get, Action<AppSettings, T> set, T value, Action? after = null, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(get(S), value)) return;
        _settings.Update(s => set(s, value));
        OnPropertyChanged(name);
        after?.Invoke();
    }
}
