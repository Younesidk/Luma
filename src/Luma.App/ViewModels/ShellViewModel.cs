using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Luma.App.Services;
using Luma.Core.Models;
using Luma.Core.Interfaces;

namespace Luma.App.ViewModels;

public sealed partial class NavItemViewModel : ObservableObject
{
    public NavItemViewModel(string id, string title, string iconKey, AppPage page, string? sourceId = null)
    {
        Id = id;
        Title = title;
        IconKey = iconKey;
        Page = page;
        SourceId = sourceId;
    }

    public string Id { get; }
    public string Title { get; }
    public string IconKey { get; }
    public AppPage Page { get; }
    public string? SourceId { get; }

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _isCompact;

    public bool ShowLabel => !IsCompact;

    public Action? OpenAction { get; set; }

    [RelayCommand]
    private void Activate() => OpenAction?.Invoke();

    partial void OnIsCompactChanged(bool value) => OnPropertyChanged(nameof(ShowLabel));
}

/// <summary>Owns navigation, the persistent sidebar and the overlays (preview, dialogs, toasts).</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly Dictionary<AppPage, PageViewModel> _pages;
    private readonly NavigationService _navigation;
    private readonly IUiDispatcher _dispatcher;
    private readonly ISettingsService _settings;
    private PageViewModel? _activePage;

    public ShellViewModel(DiscoverViewModel discover, FavoritesViewModel favorites, CollectionsViewModel collections,
        DownloadsViewModel downloads, LiveWallpapersViewModel live, HistoryViewModel history, FeedbackViewModel feedback, SettingsViewModel settings, NavigationService navigation,
        PreviewService preview, DialogService dialogs, ToastService toasts, ISettingsService settingsService, ShellStartup startup,
        UpdateViewModel updates, IUiDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        Discover = discover;
        _navigation = navigation;
        _settings = settingsService;
        Preview = preview;
        Dialogs = dialogs;
        Toasts = toasts;
        Startup = startup;
        Updates = updates;

        _pages = new Dictionary<AppPage, PageViewModel>
        {
            [AppPage.Discover] = discover,
            [AppPage.Favorites] = favorites,
            [AppPage.Collections] = collections,
            [AppPage.Downloads] = downloads,
            [AppPage.Live] = live,
            [AppPage.History] = history,
            [AppPage.Feedback] = feedback,
            [AppPage.Settings] = settings
        };

        LibraryItems =
        [
            new("discover", "Discover", "Icon.Compass", AppPage.Discover, AppSettings.AllSourcesId),
            new("favorites", "Favorites", "Icon.Heart", AppPage.Favorites),
            new("collections", "Collections", "Icon.Layers", AppPage.Collections),
            new("downloads", "Downloads", "Icon.Download", AppPage.Downloads),
            new("live", "Live", "Icon.Film", AppPage.Live),
            new("history", "History", "Icon.History", AppPage.History)
        ];
        SourceItems =
        [
            new("wallhaven", "Wallhaven", "Icon.Mountain", AppPage.Discover, "wallhaven"),
            new("openverse", "Openverse", "Icon.Globe", AppPage.Discover, "openverse")
        ];
        SystemItems =
        [
            new("feedback", "Feedback", "Icon.Message", AppPage.Feedback),
            new("settings", "Settings", "Icon.Sliders", AppPage.Settings)
        ];

        _currentPage = discover;
        _isSidebarCompact = settingsService.Current.SidebarCompact;
        foreach (var item in LibraryItems.Concat(SourceItems).Concat(SystemItems))
        {
            item.IsCompact = IsSidebarCollapsed;
            var captured = item;
            item.OpenAction = () => Open(captured);
        }

        navigation.Requested += (_, request) => Navigate(request);
        Preview.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PreviewService.Current)) OnPropertyChanged(nameof(PreviewViewModel));
        };
        discover.PropertyChanged += OnDiscoverChanged;
        settingsService.Changed += (_, s) => _dispatcher.Post(() =>
        {
            if (IsSidebarCompact != s.SidebarCompact) IsSidebarCompact = s.SidebarCompact;
        });
        UpdateActive();
    }

    public DiscoverViewModel Discover { get; }
    public PreviewService Preview { get; }
    public DialogService Dialogs { get; }
    public ToastService Toasts { get; }
    public ShellStartup Startup { get; }
    public UpdateViewModel Updates { get; }

    public IReadOnlyList<NavItemViewModel> LibraryItems { get; }
    public IReadOnlyList<NavItemViewModel> SourceItems { get; }
    public IReadOnlyList<NavItemViewModel> SystemItems { get; }

    public PreviewViewModel? PreviewViewModel => Preview.Current;

    public string Version { get; } = typeof(ShellViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

    [ObservableProperty]
    private PageViewModel _currentPage;

    [ObservableProperty]
    private bool _isSidebarCompact;

    /// <summary>Set by the window: on narrow windows the sidebar collapses to icons without changing the saved preference.</summary>
    [ObservableProperty]
    private bool _isWindowNarrow;

    /// <summary>The user's explicit choice while the window is narrow; cleared when the window becomes wide again.</summary>
    private bool? _narrowOverride;

    /// <summary>What the sidebar actually shows: the saved preference, or icons-only while the window is narrow.</summary>
    public bool IsSidebarCollapsed => IsWindowNarrow ? _narrowOverride ?? true : IsSidebarCompact;

    public double SidebarWidth => IsSidebarCollapsed ? 76 : 248;

    partial void OnIsSidebarCompactChanged(bool value)
    {
        if (_settings.Current.SidebarCompact != value) _settings.Update(s => s.SidebarCompact = value);
        RefreshSidebar();
    }

    partial void OnIsWindowNarrowChanged(bool value)
    {
        _narrowOverride = null;
        RefreshSidebar();
    }

    private void RefreshSidebar()
    {
        var collapsed = IsSidebarCollapsed;
        foreach (var item in LibraryItems.Concat(SourceItems).Concat(SystemItems)) item.IsCompact = collapsed;
        OnPropertyChanged(nameof(IsSidebarCollapsed));
        OnPropertyChanged(nameof(SidebarWidth));
    }

    public event EventHandler? FocusPageSearchRequested;

    public void RequestFocusPageSearch() => FocusPageSearchRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Shows the initial page once the window is open.</summary>
    public async Task StartAsync()
    {
        _ = Startup.BeginAsync();
        await ActivateAsync(CurrentPage);
    }

    /// <summary>The window was closed while Luma keeps running in the background: stop page work that only matters on screen.</summary>
    public void OnWindowClosed()
    {
        Preview.Close();
        Dialogs.CancelCurrent();
        _activePage?.OnDeactivated();
        _activePage = null;
    }

    /// <summary>A new window was opened from the notification area.</summary>
    public Task OnWindowReopenedAsync() => ActivateAsync(CurrentPage);

    [RelayCommand]
    private void ToggleSidebar()
    {
        if (IsWindowNarrow)
        {
            // Temporary: expanding on a narrow window should not overwrite the saved preference.
            _narrowOverride = !IsSidebarCollapsed;
            RefreshSidebar();
        }
        else
        {
            IsSidebarCompact = !IsSidebarCompact;
        }
    }

    [RelayCommand]
    private void Open(NavItemViewModel item)
    {
        if (item.Page == AppPage.Discover) Navigate(new NavigationRequest(AppPage.Discover, item.SourceId, null));
        else Navigate(new NavigationRequest(item.Page, null, null));
    }

    [RelayCommand]
    public void NavigateTo(AppPage page) => Navigate(new NavigationRequest(page, null, null));

    [RelayCommand]
    public void ClosePreview() => Preview.Close();

    public void Refresh()
    {
        switch (CurrentPage)
        {
            case DiscoverViewModel d: _ = d.RefreshAsync(); break;
            case FavoritesViewModel f: _ = f.ReloadAsync(); break;
            case CollectionsViewModel c: _ = c.ReloadAsync(); break;
            case DownloadsViewModel dl: _ = dl.ReloadAsync(); break;
            case LiveWallpapersViewModel lv: _ = lv.ReloadAsync(); break;
            case HistoryViewModel h: _ = h.ReloadAsync(); break;
        }
    }

    private void Navigate(NavigationRequest request)
    {
        Preview.Close();
        if (request.Page == AppPage.Discover && (request.SourceId is not null || request.Query is not null))
            Discover.ApplyRequest(request.SourceId, request.Query);

        var target = _pages[request.Page];
        if (!ReferenceEquals(target, CurrentPage)) CurrentPage = target;
        _ = ActivateAsync(target);
        UpdateActive();
    }

    private async Task ActivateAsync(PageViewModel page)
    {
        if (!ReferenceEquals(_activePage, page))
        {
            _activePage?.OnDeactivated();
            _activePage = page;
        }
        await page.OnActivatedAsync();
    }

    private void OnDiscoverChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiscoverViewModel.SelectedSource)) UpdateActive();
    }

    partial void OnCurrentPageChanged(PageViewModel value) => UpdateActive();

    private void UpdateActive()
    {
        var active = ActiveNavId(CurrentPage);
        foreach (var item in LibraryItems.Concat(SourceItems).Concat(SystemItems)) item.IsActive = item.Id == active;
    }

    /// <summary>The sidebar item to highlight for a page. Unknown pages highlight nothing rather than a wrong item.</summary>
    public static string? ActiveNavId(PageViewModel page) => page switch
    {
        DiscoverViewModel d => d.SelectedSource.Id == AppSettings.AllSourcesId ? "discover" : d.SelectedSource.Id,
        FavoritesViewModel => "favorites",
        CollectionsViewModel => "collections",
        DownloadsViewModel => "downloads",
        LiveWallpapersViewModel => "live",
        HistoryViewModel => "history",
        FeedbackViewModel => "feedback",
        SettingsViewModel => "settings",
        _ => null
    };
}
