using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Luma.App.ViewModels;

namespace Luma.App.Views;

public partial class MainWindow : Window
{
    private ShellViewModel? _shell;

    public MainWindow()
    {
        InitializeComponent();

        this.FindControl<Button>("MinimizeButton")!.Click += (_, _) => WindowState = WindowState.Minimized;
        this.FindControl<Button>("MaximizeButton")!.Click += (_, _) => ToggleMaximize();
        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();
        this.FindControl<Border>("TitleBar")!.PointerPressed += OnDragAreaPressed;
        this.FindControl<Border>("SidebarDragArea")!.PointerPressed += OnDragAreaPressed;

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => AttachShell();

        // The design size (1360×860) is larger than many laptop screens once Windows display scaling is applied
        // (1366×768 at 125% is only ~1090×580 usable), which pushed the close button and the sidebar footer off-screen.
        // Size the window to the screen it opens on before it is shown, then keep it inside whatever screen it lands on.
        FitToScreen(Screens.Primary, center: true);
        Opened += (_, _) => FitToScreen(Screens.ScreenFromWindow(this), center: true);
        ScalingChanged += (_, _) => FitToScreen(Screens.ScreenFromWindow(this), center: false);
    }

    private const double PreferredWidth = 1360;
    private const double PreferredHeight = 860;
    private const double DesignMinWidth = 860;
    private const double DesignMinHeight = 560;

    /// <summary>
    /// Clamps the window (and its minimum size) to the screen's working area, in device-independent pixels.
    /// Only ever shrinks: a user who has already resized the window larger on a big monitor keeps that size.
    /// </summary>
    private void FitToScreen(Screen? screen, bool center)
    {
        if (screen is null || WindowState != WindowState.Normal) return;
        var scaling = screen.Scaling > 0 ? screen.Scaling : 1;
        var area = screen.WorkingArea;
        var areaWidth = area.Width / scaling;
        var areaHeight = area.Height / scaling;
        if (areaWidth <= 0 || areaHeight <= 0) return;

        MinWidth = Math.Min(DesignMinWidth, areaWidth);
        MinHeight = Math.Min(DesignMinHeight, areaHeight);

        // Leave a little breathing room around the window so it never looks jammed against the taskbar.
        var maxWidth = Math.Floor(areaWidth * 0.94);
        var maxHeight = Math.Floor(areaHeight * 0.94);
        var width = Math.Max(MinWidth, Math.Min(double.IsNaN(Width) ? PreferredWidth : Width, maxWidth));
        var height = Math.Max(MinHeight, Math.Min(double.IsNaN(Height) ? PreferredHeight : Height, maxHeight));
        var resized = Math.Abs(width - Width) > 0.5 || Math.Abs(height - Height) > 0.5;
        Width = width;
        Height = height;

        if (!center && !resized && IsInside(area, scaling)) return;
        var x = area.X + (int)Math.Round((area.Width - width * scaling) / 2);
        var y = area.Y + (int)Math.Round((area.Height - height * scaling) / 2);
        if (center || !IsInside(area, scaling)) Position = new PixelPoint(Math.Max(area.X, x), Math.Max(area.Y, y));
    }

    private bool IsInside(PixelRect area, double scaling)
    {
        var right = Position.X + (int)Math.Round(Width * scaling);
        var bottom = Position.Y + (int)Math.Round(Height * scaling);
        return Position.X >= area.X && Position.Y >= area.Y && right <= area.Right && bottom <= area.Bottom;
    }

    private void InitializeComponent() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty) UpdateWindowChrome();
        else if (change.Property == ClientSizeProperty) UpdateNarrow();
        else if (change.Property == OffScreenMarginProperty) UpdateWindowChrome();
    }

    /// <summary>Below this width the sidebar collapses to icons so the gallery keeps a usable amount of room.</summary>
    private const double NarrowWidth = 1040;

    private void UpdateNarrow()
    {
        if (_shell is null) return;
        var narrow = ClientSize.Width > 0 && ClientSize.Width < NarrowWidth;
        if (_shell.IsWindowNarrow != narrow) _shell.IsWindowNarrow = narrow;
    }

    private void UpdateWindowChrome()
    {
        var maximized = WindowState == WindowState.Maximized;
        var icon = this.FindControl<Controls.Icon>("MaximizeIcon");
        if (icon is not null && Application.Current is { } app && app.TryGetResource(maximized ? "Icon.Restore" : "Icon.Maximize", ActualThemeVariant, out var geometry))
            icon.Data = geometry as Geometry;
        this.FindControl<Grid>("RootGrid")!.Margin = maximized ? OffScreenMargin : default;
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnDragAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        // Clicks on the search box inside the title bar must reach the text box, not start a drag.
        if (e.Source is Visual source && source.FindAncestorOfType<TextBox>() is not null) return;
        if (e.Source is Visual s2 && s2.FindAncestorOfType<Button>() is not null) return;
        if (e.ClickCount == 2) ToggleMaximize();
        else BeginMoveDrag(e);
    }

    private void AttachShell()
    {
        if (_shell is not null)
            _shell.FocusPageSearchRequested -= OnFocusPageSearch;
        _shell = DataContext as ShellViewModel;
        if (_shell is null) return;
        _shell.FocusPageSearchRequested += OnFocusPageSearch;
        UpdateNarrow();
    }

    private void OnFocusPageSearch(object? sender, EventArgs e)
    {
        var host = this.FindControl<TransitioningContentControl>("PageHost");
        var box = host?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "PageSearch" && t.IsEffectivelyVisible);
        if (box is null) return;
        box.Focus();
        box.SelectAll();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_shell is null) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (e.Key == Key.Escape)
        {
            if (_shell.Dialogs.IsOpen) _shell.Dialogs.CancelCurrent();
            else if (_shell.PreviewViewModel is not null) _shell.ClosePreview();
            else return;
            e.Handled = true;
            return;
        }

        if (_shell.Dialogs.IsOpen) return;

        if (ctrl && e.Key == Key.K)
        {
            // Ctrl+K jumps to Discover and focuses its search box (the page may need a layout pass to appear first).
            if (_shell.CurrentPage is not DiscoverViewModel) _shell.NavigateTo(Services.AppPage.Discover);
            Avalonia.Threading.Dispatcher.UIThread.Post(_shell.RequestFocusPageSearch, Avalonia.Threading.DispatcherPriority.Loaded);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.F)
        {
            _shell.RequestFocusPageSearch();
            e.Handled = true;
        }
        else if (ctrl && (e.Key == Key.OemComma || e.Key == Key.Decimal))
        {
            _shell.NavigateToCommand.Execute(Services.AppPage.Settings);
            e.Handled = true;
        }
        else if ((ctrl && e.Key == Key.R) || e.Key == Key.F5)
        {
            _shell.Refresh();
            e.Handled = true;
        }
        else if (_shell.PreviewViewModel is { } preview && (e.Key is Key.Left or Key.Right) && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            if (FocusManager?.GetFocusedElement() is TextBox or ComboBox) return;
            if (e.Key == Key.Right) preview.NextCommand.Execute(null);
            else preview.PreviousCommand.Execute(null);
            e.Handled = true;
        }
    }
}

