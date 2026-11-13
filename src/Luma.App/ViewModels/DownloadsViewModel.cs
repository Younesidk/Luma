using System.Collections.ObjectModel;
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

/// <summary>A transfer that is queued, running or recently finished.</summary>
public sealed partial class DownloadJobViewModel : ObservableObject
{
    private readonly DownloadJob _job;

    public DownloadJobViewModel(DownloadJob job)
    {
        _job = job;
        job.Changed += OnChanged;
        Update();
    }

    public Wallpaper Wallpaper => _job.Wallpaper;
    public string Title => Wallpaper.DisplayTitle;
    public string? ThumbnailSource => Wallpaper.ThumbnailUrl;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isFailed;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private bool _isIndeterminate;

    private void OnChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Update);

    private void Update()
    {
        Progress = _job.Progress * 100;
        IsActive = _job.Status is DownloadStatus.Queued or DownloadStatus.Downloading;
        IsFailed = _job.Status is DownloadStatus.Failed;
        IsDone = _job.Status is DownloadStatus.Completed;
        IsIndeterminate = _job.Status == DownloadStatus.Queued || (_job.Status == DownloadStatus.Downloading && _job.TotalBytes is not > 0);
        StatusText = _job.Status switch
        {
            DownloadStatus.Queued => "Waiting…",
            DownloadStatus.Downloading => _job.TotalBytes is > 0
                ? $"{ByteSizeFormatter.Format(_job.BytesReceived)} of {ByteSizeFormatter.Format(_job.TotalBytes)}"
                : ByteSizeFormatter.Format(_job.BytesReceived),
            DownloadStatus.Completed => _job.Note ?? "Saved to your library",
            DownloadStatus.Failed => _job.ErrorMessage ?? "Download failed",
            _ => "Cancelled"
        };
    }

    [RelayCommand]
    private void Cancel() => _job.Cancel();

    public void Detach() => _job.Changed -= OnChanged;
}

/// <summary>A row of the local library.</summary>
public sealed partial class LibraryItemViewModel : ObservableObject
{
    private readonly DownloadsViewModel _owner;

    public LibraryItemViewModel(DownloadedWallpaper download, WallpaperItemViewModel item, string providerName, DownloadsViewModel owner)
    {
        Download = download;
        Item = item;
        ProviderName = providerName;
        _owner = owner;
    }

    public DownloadedWallpaper Download { get; }
    public WallpaperItemViewModel Item { get; }
    public string ProviderName { get; }
    public string Title => Download.Wallpaper.DisplayTitle;
    public string FileName => Path.GetFileName(Download.FilePath);
    public string ThumbnailSource => Download.FileExists ? (Download.Wallpaper.ThumbnailUrl ?? Download.FilePath) : (Download.Wallpaper.ThumbnailUrl ?? string.Empty);
    public bool FileMissing => !Download.FileExists;
    public bool FileExists => Download.FileExists;
    public string ResolutionText => Download.Wallpaper.Resolution?.Label ?? "Unknown size";
    public string SizeText => ByteSizeFormatter.Format(Download.FileSizeBytes);
    public string DateText => Download.DownloadedAt.ToLocalTime().ToString("MMM d, yyyy");
    public string MetaText => $"{ResolutionText}  ·  {SizeText}  ·  {DateText}";

    [RelayCommand]
    private void Preview() => Item.OpenCommand.Execute(null);

    [RelayCommand]
    private Task ApplyAsync() => _owner.ApplyAsync(this);

    [RelayCommand]
    private void ShowInFolder() => _owner.ShowInFolder(this);

    [RelayCommand]
    private Task RemoveAsync() => _owner.RemoveAsync(this);

    [RelayCommand]
    private Task DeleteFileAsync() => _owner.DeleteFileAsync(this);
}

public sealed partial class DownloadsViewModel : PageViewModel
{
    public static readonly IReadOnlyList<Option<LibrarySort>> SortChoices =
    [
        new("Newest first", LibrarySort.Newest), new("Oldest first", LibrarySort.Oldest), new("Name", LibrarySort.Name),
        new("Resolution", LibrarySort.Resolution), new("File size", LibrarySort.FileSize)
    ];

    private readonly IDownloadService _downloads;
    private readonly IDownloadLibrary _library;
    private readonly WallpaperActions _actions;
    private readonly WallpaperItemFactory _factory;
    private readonly IProviderRegistry _providers;
    private readonly IFileDialogService _files;
    private readonly IToastService _toasts;
    private readonly INavigationService _navigation;
    private readonly ShellStartup _startup;
    private readonly ILogger<DownloadsViewModel> _logger;
    private readonly List<WallpaperItemViewModel> _contextItems = [];
    private CancellationTokenSource? _debounce;
    private bool _active;


    public DownloadsViewModel(IDownloadService downloads, IDownloadLibrary library, WallpaperActions actions, WallpaperItemFactory factory,
        IProviderRegistry providers, IFileDialogService files, IToastService toasts, INavigationService navigation, ShellStartup startup,
        ILogger<DownloadsViewModel> logger)
    {
        _downloads = downloads;
        _library = library;
        _actions = actions;
        _factory = factory;
        _providers = providers;
        _files = files;
        _toasts = toasts;
        _navigation = navigation;
        _startup = startup;
        _logger = logger;
        _selectedSort = SortChoices[0];

        downloads.JobAdded += (_, job) => Dispatcher.UIThread.Post(() => AddJob(job));
        library.Changed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (_active) _ = ReloadAsync();
        });
    }

    public override string Title => "Downloads";

    public ObservableCollection<DownloadJobViewModel> Jobs { get; } = [];

    public ObservableCollection<LibraryItemViewModel> Items { get; } = [];

    public IReadOnlyList<Option<LibrarySort>> Sorts => SortChoices;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private Option<LibrarySort> _selectedSort;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(HasItems), nameof(NoResults))]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _summary = string.Empty;

    public bool HasJobs => Jobs.Count > 0;
    public bool HasFinishedJobs => Jobs.Any(j => !j.IsActive);
    public bool HasItems => Items.Count > 0;
    public bool IsEmpty => !IsLoading && Items.Count == 0 && string.IsNullOrWhiteSpace(SearchText) && !HasError;
    public bool NoResults => !IsLoading && Items.Count == 0 && !string.IsNullOrWhiteSpace(SearchText);

    partial void OnSearchTextChanged(string value) => Schedule(TimeSpan.FromMilliseconds(250));

    partial void OnSelectedSortChanged(Option<LibrarySort> value) => Schedule(TimeSpan.Zero);

    public override async Task OnActivatedAsync()
    {
        _active = true;
        SyncExistingJobs();
        await _startup.BeginAsync();
        await ReloadAsync();
    }

    public override void OnDeactivated() => _active = false;

    [RelayCommand]
    public async Task ReloadAsync()
    {
        IsLoading = Items.Count == 0;
        HasError = false;
        try
        {
            await _startup.Ready;
            var rows = await _library.QueryAsync(new LibraryQuery { Text = SearchText, Sort = SelectedSort.Value });
            Items.Clear();
            _contextItems.Clear();
            foreach (var row in rows)
            {
                var wallpaper = row.Wallpaper with { LocalFilePath = row.FileExists ? row.FilePath : null };
                var item = _factory.Create(wallpaper, () => _contextItems);
                _contextItems.Add(item);
                Items.Add(new LibraryItemViewModel(row, item, _providers.GetDisplayName(wallpaper.ProviderId), this));
            }

            var total = rows.Sum(r => r.FileSizeBytes);
            Summary = rows.Count == 0 ? string.Empty : $"{rows.Count} {(rows.Count == 1 ? "wallpaper" : "wallpapers")}  ·  {ByteSizeFormatter.Format(total)}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading the library failed");
            HasError = true;
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(NoResults));
        }
    }

    [RelayCommand]
    private void BrowseDiscover() => _navigation.NavigateTo(AppPage.Discover);

    [RelayCommand]
    private void ClearFinished()
    {
        _downloads.ClearFinished();
        foreach (var job in Jobs.Where(j => !j.IsActive).ToList())
        {
            job.Detach();
            Jobs.Remove(job);
        }
        NotifyJobs();
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var paths = await _files.PickImagesAsync();
        if (paths.Count == 0) return;

        var imported = 0;
        var failed = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                await _library.ImportFileAsync(path);
                imported++;
            }
            catch (LumaException ex)
            {
                failed.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Importing {Path} failed", path);
                failed.Add($"{Path.GetFileName(path)}: couldn't be imported");
            }
        }

        if (imported > 0) _toasts.Show(imported == 1 ? "Imported 1 wallpaper" : $"Imported {imported} wallpapers", ToastKind.Success);
        if (failed.Count > 0) _toasts.Show(failed[0] + (failed.Count > 1 ? $" (+{failed.Count - 1} more)" : string.Empty), ToastKind.Warning);
        await ReloadAsync();
    }

    internal Task ApplyAsync(LibraryItemViewModel row) => _actions.ApplyLocalAsync(row.Download);

    internal void ShowInFolder(LibraryItemViewModel row) => _actions.ShowInFolder(row.Download.FilePath);

    internal async Task RemoveAsync(LibraryItemViewModel row)
    {
        await _actions.RemoveFromLibraryAsync(row.Download);
        await ReloadAsync();
    }

    internal async Task DeleteFileAsync(LibraryItemViewModel row)
    {
        if (await _actions.DeleteFileAsync(row.Download)) await ReloadAsync();
    }

    private void SyncExistingJobs()
    {
        foreach (var job in _downloads.Jobs)
        {
            if (Jobs.All(j => j.Wallpaper.Key != job.Wallpaper.Key || !ReferenceEquals(j.Wallpaper, job.Wallpaper))) AddJob(job);
        }
    }

    private void AddJob(DownloadJob job)
    {
        if (Jobs.Any(j => ReferenceEquals(j.Wallpaper, job.Wallpaper))) return;
        Jobs.Insert(0, new DownloadJobViewModel(job));
        job.Changed += (_, _) => Dispatcher.UIThread.Post(NotifyJobs);
        NotifyJobs();
    }

    private void NotifyJobs()
    {
        OnPropertyChanged(nameof(HasJobs));
        OnPropertyChanged(nameof(HasFinishedJobs));
    }

    private void Schedule(TimeSpan delay)
    {
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
                // newer input
            }
        }
    }
}


