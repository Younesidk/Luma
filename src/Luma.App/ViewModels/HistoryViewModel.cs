using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Luma.App.Services;
using Luma.Core.Interfaces;
using Luma.Core.Models;
using Microsoft.Extensions.Logging;

namespace Luma.App.ViewModels;

public sealed partial class HistoryEntryViewModel : ObservableObject
{
    private readonly HistoryViewModel _owner;

    public HistoryEntryViewModel(HistoryEntry entry, WallpaperItemViewModel item, string providerName, HistoryViewModel owner)
    {
        Entry = entry;
        Item = item;
        ProviderName = providerName;
        _owner = owner;
    }

    public HistoryEntry Entry { get; }
    public WallpaperItemViewModel Item { get; }
    public string ProviderName { get; }
    public string Title => Entry.Wallpaper.DisplayTitle;
    public string ThumbnailSource => Entry.Wallpaper.ThumbnailUrl ?? Entry.FilePath ?? string.Empty;
    public string AppliedText => Describe(Entry.AppliedAt.LocalDateTime);
    public string DetailText => $"{ProviderName}  ·  {Entry.FitMode}" + (Entry.Wallpaper.Resolution is { IsValid: true } r ? $"  ·  {r.Label}" : string.Empty);
    public bool HasFile => Entry.FilePath is not null && File.Exists(Entry.FilePath);

    [RelayCommand]
    private Task ReapplyAsync() => _owner.ReapplyAsync(this);

    [RelayCommand]
    private void Preview() => Item.OpenCommand.Execute(null);

    [RelayCommand]
    private void ShowInFolder() => _owner.ShowInFolder(this);

    private static string Describe(DateTime local)
    {
        var today = DateTime.Today;
        var time = local.ToString("t");
        if (local.Date == today) return $"Today, {time}";
        if (local.Date == today.AddDays(-1)) return $"Yesterday, {time}";
        return local.Year == today.Year ? $"{local:MMM d}, {time}" : $"{local:MMM d, yyyy}";
    }
}

public sealed partial class HistoryViewModel : PageViewModel
{
    private readonly IHistoryService _history;
    private readonly WallpaperActions _actions;
    private readonly WallpaperItemFactory _factory;
    private readonly IProviderRegistry _providers;
    private readonly IDialogService _dialogs;
    private readonly IDownloadLibrary _library;
    private readonly INavigationService _navigation;
    private readonly ShellStartup _startup;
    private readonly ILogger<HistoryViewModel> _logger;
    private readonly List<WallpaperItemViewModel> _contextItems = [];
    private bool _active;

    public HistoryViewModel(IHistoryService history, WallpaperActions actions, WallpaperItemFactory factory, IProviderRegistry providers,
        IDialogService dialogs, IDownloadLibrary library, INavigationService navigation, ShellStartup startup, ILogger<HistoryViewModel> logger)
    {
        _history = history;
        _actions = actions;
        _factory = factory;
        _providers = providers;
        _dialogs = dialogs;
        _library = library;
        _navigation = navigation;
        _startup = startup;
        _logger = logger;
        history.Changed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (_active) _ = ReloadAsync();
        });
    }

    public override string Title => "History";

    public ObservableCollection<HistoryEntryViewModel> Entries { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasError;

    public bool IsEmpty => !IsLoading && Entries.Count == 0 && !HasError;
    public bool HasEntries => Entries.Count > 0;

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
        IsLoading = Entries.Count == 0;
        HasError = false;
        try
        {
            await _startup.Ready;
            var rows = await _history.GetAsync(200);
            Entries.Clear();
            _contextItems.Clear();
            foreach (var row in rows)
            {
                var item = _factory.Create(row.Wallpaper, () => _contextItems);
                _contextItems.Add(item);
                Entries.Add(new HistoryEntryViewModel(row, item, _providers.GetDisplayName(row.Wallpaper.ProviderId), this));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading history failed");
            HasError = true;
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasEntries));
        }
    }

    [RelayCommand]
    private void BrowseDiscover() => _navigation.NavigateTo(AppPage.Discover);

    [RelayCommand]
    private async Task ClearAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync("Clear history?",
            "This only clears the list of wallpapers you applied. Your downloads and favorites are not touched.", "Clear history", destructive: true);
        if (!confirmed) return;
        await _history.ClearAsync();
    }

    internal async Task ReapplyAsync(HistoryEntryViewModel entry)
    {
        // Prefer the file we applied last time; otherwise the normal flow (library copy or fresh download).
        var local = entry.Entry.FilePath is { } p && File.Exists(p) ? p : _library.GetLocalPath(entry.Item.Key);
        if (local is not null)
        {
            var found = await _library.FindByKeyAsync(entry.Item.Key);
            if (found is not null) await _actions.ApplyLocalAsync(found);
            else await _actions.ApplyAsync(entry.Item.Wallpaper with { LocalFilePath = local });
        }
        else
        {
            await _actions.ApplyAsync(entry.Item.Wallpaper);
        }
    }

    internal void ShowInFolder(HistoryEntryViewModel entry)
    {
        var path = entry.Entry.FilePath ?? _library.GetLocalPath(entry.Item.Key);
        if (path is not null) _actions.ShowInFolder(path);
    }
}

