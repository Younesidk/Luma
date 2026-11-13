using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
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
    }

    private void InitializeComponent() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty) UpdateWindowChrome();
        else if (change.Property == OffScreenMarginProperty) UpdateWindowChrome();
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
        {
            _shell.FocusGlobalSearchRequested -= OnFocusGlobalSearch;
            _shell.FocusPageSearchRequested -= OnFocusPageSearch;
        }
        _shell = DataContext as ShellViewModel;
        if (_shell is null) return;
        _shell.FocusGlobalSearchRequested += OnFocusGlobalSearch;
        _shell.FocusPageSearchRequested += OnFocusPageSearch;
    }

    private void OnFocusGlobalSearch(object? sender, EventArgs e)
    {
        var box = this.FindControl<TextBox>("GlobalSearch");
        if (box is null) return;
        box.Focus();
        box.SelectAll();
    }

    private void OnFocusPageSearch(object? sender, EventArgs e)
    {
        var host = this.FindControl<TransitioningContentControl>("PageHost");
        var box = host?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "PageSearch" && t.IsEffectivelyVisible);
        if (box is null)
        {
            OnFocusGlobalSearch(sender, e);
            return;
        }
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
            _shell.RequestFocusGlobalSearch();
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

