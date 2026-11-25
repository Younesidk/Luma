using System.ComponentModel;
using System.Runtime.InteropServices;
using Luma.App.Services;
using Luma.App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;

namespace Luma.App.Views;

/// <summary>
/// The shell window: sidebar, page host and the overlays (preview, dialogs, update card, toasts). Content extends into the
/// title bar; the system caption buttons stay at the top-right and the top 56px strip is the drag region.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const double PreferredWidth = 1360;
    private const double PreferredHeight = 860;
    private const double DesignMinWidth = 860;
    private const double DesignMinHeight = 560;

    /// <summary>Below this width (DIPs) the sidebar collapses to icons so the gallery keeps a usable amount of room.</summary>
    private const double NarrowWidth = 1040;

    private readonly Dictionary<PageViewModel, FrameworkElement> _pageViews = [];
    private readonly ThemeService _theme;
    private PreviewViewModel? _shownPreview;
    private bool _sizedOnce;

    public MainWindow(ShellViewModel viewModel)
    {
        ViewModel = viewModel;
        _theme = LumaServices.Get<ThemeService>();
        InitializeComponent();

        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "luma.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragArea);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

        // The design size is larger than many laptop screens once display scaling applies; fit it to the work area.
        FitToScreen(center: true);

        Root.SizeChanged += (_, _) =>
        {
            UpdateNarrow();
            UpdatePassthroughRegions();
        };
        Root.ActualThemeChanged += (_, _) => UpdateThemedChrome();
        Root.Loaded += OnRootLoaded;
        Root.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown), true);
        Preview.TopBarLayoutChanged += (_, _) => UpdatePassthroughRegions();

        _theme.Changed += OnThemeChanged;
        ViewModel.PropertyChanged += OnShellChanged;
        ViewModel.Dialogs.PropertyChanged += OnDialogsChanged;
        ViewModel.FocusPageSearchRequested += OnFocusPageSearch;
        Closed += OnClosed;

        ShowPage(ViewModel.CurrentPage);
        UpdatePreview();
        UpdateDialog();
        UpdateThemedChrome();
    }

    public ShellViewModel ViewModel { get; }

    /// <summary>The root element of the window content; <see cref="ThemeService"/> sets its RequestedTheme.</summary>
    public FrameworkElement RootElement => Root;

    /// <summary>Win32 handle of the window (for pickers and other HWND-based APIs).</summary>
    public nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    public string VersionLabel(string version) => $"Version {version}";

    public string ShortVersionLabel(string version) => $"v{version}";

    public double ToDouble(int value) => value;

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        Root.Loaded -= OnRootLoaded;
        if (Root.XamlRoot is { } xamlRoot) xamlRoot.Changed += OnXamlRootChanged;
        FitToScreen(center: true);
        UpdateNarrow();
        UpdateThemedChrome();
    }

    private double _lastScale;

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        // Moved to a monitor with a different scale factor: keep the window inside that monitor's work area.
        if (Math.Abs(sender.RasterizationScale - _lastScale) < 0.001) return;
        _lastScale = sender.RasterizationScale;
        FitToScreen(center: false);
        UpdatePassthroughRegions();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        // The shell view model outlives the window (Luma keeps running in the notification area): detach everything.
        Closed -= OnClosed;
        _theme.Changed -= OnThemeChanged;
        ViewModel.PropertyChanged -= OnShellChanged;
        ViewModel.Dialogs.PropertyChanged -= OnDialogsChanged;
        ViewModel.FocusPageSearchRequested -= OnFocusPageSearch;
        if (Root.XamlRoot is { } xamlRoot) xamlRoot.Changed -= OnXamlRootChanged;
        Preview.ViewModel = null;
        DialogHost.Content = null;
        PageHost.Content = null;
        foreach (var view in _pageViews.Values)
        {
            if (view is IDisposable disposable) disposable.Dispose();
        }
        _pageViews.Clear();
    }

    // ------------------------------------------------------------------ Pages

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ShellViewModel.CurrentPage):
                ShowPage(ViewModel.CurrentPage);
                break;
            case nameof(ShellViewModel.PreviewViewModel):
                UpdatePreview();
                break;
        }
    }

    private void ShowPage(PageViewModel page)
    {
        if (!_pageViews.TryGetValue(page, out var view))
        {
            view = CreatePageView(page);
            _pageViews[page] = view;
        }
        if (ReferenceEquals(PageHost.Content, view)) return;
        PageHost.Content = view;
        FadeIn(view);
    }

    /// <summary>
    /// Builds the view for a page view model. Views are created once per window and cached, so a page keeps its scroll
    /// position and state while you move around.
    /// </summary>
    private static FrameworkElement CreatePageView(PageViewModel page) => page switch
    {
        DiscoverViewModel discover => new DiscoverView { ViewModel = discover },
        FavoritesViewModel favorites => new FavoritesView { ViewModel = favorites },
        DownloadsViewModel downloads => new DownloadsView { ViewModel = downloads },
        CollectionsViewModel collections => new CollectionsView { ViewModel = collections },
        HistoryViewModel history => new HistoryView { ViewModel = history },
        LiveWallpapersViewModel live => new LiveWallpapersView { ViewModel = live },
        SettingsViewModel settings => new SettingsView { ViewModel = settings },
        FeedbackViewModel feedback => new FeedbackView { ViewModel = feedback },
        _ => throw new InvalidOperationException($"No view for {page.GetType().Name}.")
    };

    private static void FadeIn(UIElement element)
    {
        element.Opacity = 0;
        var animation = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(160), EnableDependentAnimation = false };
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, nameof(UIElement.Opacity));
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) => element.Opacity = 1;
        storyboard.Begin();
    }

    private void OnFocusPageSearch(object? sender, EventArgs e)
    {
        if (PageHost.Content is not FrameworkElement view) return;
        if (view.IsLoaded) FocusSearch(view);
        else
        {
            void OnLoaded(object s, RoutedEventArgs a)
            {
                view.Loaded -= OnLoaded;
                FocusSearch(view);
            }
            view.Loaded += OnLoaded;
        }
    }

    private static void FocusSearch(FrameworkElement view)
    {
        if (view is ISearchFocusable searchable && searchable.FocusSearch()) return;
        // Fallback for pages without the interface: the first visible text box named "PageSearch".
        ViewHelpers.FocusAndSelect(ViewHelpers.FindDescendant<TextBox>(view, t => t.Name == "PageSearch" && t.Visibility == Visibility.Visible));
    }

    // ------------------------------------------------------------------ Overlays

    private void UpdatePreview()
    {
        var preview = ViewModel.PreviewViewModel;
        if (ReferenceEquals(preview, _shownPreview)) return;
        _shownPreview = preview;
        Preview.ViewModel = preview;
        PreviewHost.Visibility = preview is null ? Visibility.Collapsed : Visibility.Visible;
        if (preview is not null) Preview.Focus(FocusState.Programmatic);
        UpdatePassthroughRegions();
    }

    private void OnDialogsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DialogService.Current) or null or "") UpdateDialog();
    }

    private void UpdateDialog()
    {
        DialogHost.Content = ViewModel.Dialogs.Current switch
        {
            ConfirmDialogViewModel confirm => new ConfirmDialogView(confirm),
            CollectionEditDialogViewModel edit => new CollectionEditDialogView(edit),
            CollectionPickerDialogViewModel picker => new CollectionPickerDialogView(picker),
            _ => null
        };
    }

    /// <summary>
    /// The top strip is a caption (drag) region. While the preview is open its close button sits inside that strip, so
    /// mark it pass-through or it could never be clicked.
    /// </summary>
    private void UpdatePassthroughRegions()
    {
        try
        {
            var source = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            if (PreviewHost.Visibility != Visibility.Visible || Root.XamlRoot is null)
            {
                source.ClearRegionRects(NonClientRegionKind.Passthrough);
                return;
            }
            var scale = Root.XamlRoot.RasterizationScale;
            var rects = new List<RectInt32>();
            foreach (var element in Preview.TopBarElements)
            {
                if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0) continue;
                var bounds = element.TransformToVisual(null).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                rects.Add(new RectInt32((int)Math.Round(bounds.X * scale), (int)Math.Round(bounds.Y * scale),
                    (int)Math.Round(bounds.Width * scale), (int)Math.Round(bounds.Height * scale)));
            }
            source.SetRegionRects(NonClientRegionKind.Passthrough, rects.ToArray());
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            // Window is closing; nothing to update.
        }
    }

    // ------------------------------------------------------------------ Keyboard

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = IsDown(VirtualKey.Control);
        var alt = IsDown(VirtualKey.Menu);

        if (e.Key == VirtualKey.Escape)
        {
            if (ViewModel.Dialogs.IsOpen) ViewModel.Dialogs.CancelCurrent();
            else if (ViewModel.PreviewViewModel is not null) ViewModel.ClosePreview();
            else if (PageHost.Content is IEscapeHandler page && page.HandleEscape()) { }
            else return;
            e.Handled = true;
            return;
        }

        if (ViewModel.Dialogs.IsOpen) return;

        if (ctrl && e.Key == VirtualKey.K)
        {
            // Ctrl+K jumps to Discover and focuses its search box (the page may need a layout pass to appear first).
            if (ViewModel.CurrentPage is not DiscoverViewModel) ViewModel.NavigateTo(AppPage.Discover);
            else if (ViewModel.PreviewViewModel is not null) ViewModel.ClosePreview();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ViewModel.RequestFocusPageSearch);
            e.Handled = true;
        }
        else if (ctrl && e.Key == VirtualKey.F)
        {
            ViewModel.RequestFocusPageSearch();
            e.Handled = true;
        }
        else if (ctrl && (e.Key == (VirtualKey)188 || e.Key == VirtualKey.Decimal))
        {
            ViewModel.NavigateTo(AppPage.Settings);
            e.Handled = true;
        }
        else if ((ctrl && e.Key == VirtualKey.R) || e.Key == VirtualKey.F5)
        {
            ViewModel.Refresh();
            e.Handled = true;
        }
        else if (ViewModel.PreviewViewModel is { } preview && e.Key is VirtualKey.Left or VirtualKey.Right && !alt)
        {
            if (Root.XamlRoot is { } xamlRoot && FocusManager.GetFocusedElement(xamlRoot) is TextBox or ComboBox) return;
            if (e.Key == VirtualKey.Right) preview.NextCommand.Execute(null);
            else preview.PreviousCommand.Execute(null);
            e.Handled = true;
        }
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    // ------------------------------------------------------------------ Window chrome & sizing

    private void UpdateNarrow()
    {
        var width = Root.ActualWidth;
        if (width <= 0) return;
        var narrow = width < NarrowWidth;
        if (ViewModel.IsWindowNarrow != narrow) ViewModel.IsWindowNarrow = narrow;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(UpdateThemedChrome);

    /// <summary>Recolours the caption buttons for the current theme and rebuilds the accent gradients.</summary>
    private void UpdateThemedChrome()
    {
        var gradient = AccentGradient();
        if (gradient is not null)
        {
            LogoBadge.Background = gradient;
            UpdateBadge.Background = AccentGradient();
        }

        var dark = Root.ActualTheme == ElementTheme.Dark;
        var bar = AppWindow.TitleBar;
        var foreground = dark ? ColorHelper.FromArgb(0xFF, 0xF3, 0xF3, 0xFA) : ColorHelper.FromArgb(0xFF, 0x15, 0x18, 0x29);
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = foreground;
        bar.ButtonInactiveForegroundColor = dark ? ColorHelper.FromArgb(0xFF, 0x80, 0x84, 0x96) : ColorHelper.FromArgb(0xFF, 0x8A, 0x8E, 0xA0);
        bar.ButtonHoverBackgroundColor = dark ? ColorHelper.FromArgb(0x1F, 0xFF, 0xFF, 0xFF) : ColorHelper.FromArgb(0x14, 0x00, 0x00, 0x00);
        bar.ButtonHoverForegroundColor = foreground;
        bar.ButtonPressedBackgroundColor = dark ? ColorHelper.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : ColorHelper.FromArgb(0x24, 0x00, 0x00, 0x00);
        bar.ButtonPressedForegroundColor = foreground;
    }

    /// <summary>Diagonal accent-secondary → accent gradient (logo and update badges), built from the live accent brushes.</summary>
    private static LinearGradientBrush? AccentGradient()
    {
        if (ThemeResources.GetBrush("LumaAccentSecondaryBrush") is not SolidColorBrush from ||
            ThemeResources.GetBrush("LumaAccentBrush") is not SolidColorBrush to) return null;
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        brush.GradientStops.Add(new GradientStop { Color = from.Color, Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = to.Color, Offset = 1 });
        return brush;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    /// <summary>
    /// Clamps the window (and its minimum size) to the work area of the monitor it is on, in device-independent pixels.
    /// Only ever shrinks: a window already resized larger on a big monitor keeps its size.
    /// </summary>
    private void FitToScreen(bool center)
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter || presenter.State != OverlappedPresenterState.Restored) return;
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        if (display is null) return;
        var dpi = GetDpiForWindow(Handle);
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;
        var area = display.WorkArea;
        var areaWidth = area.Width / scale;
        var areaHeight = area.Height / scale;
        if (areaWidth <= 0 || areaHeight <= 0) return;

        var minWidth = Math.Min(DesignMinWidth, areaWidth);
        var minHeight = Math.Min(DesignMinHeight, areaHeight);
        presenter.PreferredMinimumWidth = (int)Math.Round(minWidth * scale);
        presenter.PreferredMinimumHeight = (int)Math.Round(minHeight * scale);

        // Leave a little breathing room so the window never looks jammed against the taskbar.
        var maxWidth = Math.Floor(areaWidth * 0.94);
        var maxHeight = Math.Floor(areaHeight * 0.94);
        var currentWidth = _sizedOnce ? AppWindow.Size.Width / scale : PreferredWidth;
        var currentHeight = _sizedOnce ? AppWindow.Size.Height / scale : PreferredHeight;
        var width = Math.Max(minWidth, Math.Min(currentWidth, maxWidth));
        var height = Math.Max(minHeight, Math.Min(currentHeight, maxHeight));
        var pixelWidth = (int)Math.Round(width * scale);
        var pixelHeight = (int)Math.Round(height * scale);
        var resized = pixelWidth != AppWindow.Size.Width || pixelHeight != AppWindow.Size.Height;
        if (resized) AppWindow.Resize(new SizeInt32(pixelWidth, pixelHeight));
        _sizedOnce = true;

        var position = AppWindow.Position;
        var inside = position.X >= area.X && position.Y >= area.Y &&
                     position.X + pixelWidth <= area.X + area.Width && position.Y + pixelHeight <= area.Y + area.Height;
        if (!center && inside) return;
        var x = area.X + (area.Width - pixelWidth) / 2;
        var y = area.Y + (area.Height - pixelHeight) / 2;
        AppWindow.Move(new PointInt32(Math.Max(area.X, x), Math.Max(area.Y, y)));
    }
}
