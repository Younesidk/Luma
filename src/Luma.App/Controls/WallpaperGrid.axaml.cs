using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Luma.App.ViewModels;

namespace Luma.App.Controls;

public sealed class HeaderRow(Control content)
{
    public Control Content { get; } = content;
}

public sealed class WallpaperRow
{
    public ObservableCollection<WallpaperItemViewModel> Items { get; } = [];
}

public sealed class SkeletonRow(int count)
{
    public IReadOnlyList<int> Slots { get; } = Enumerable.Range(0, count).ToList();
}

public sealed class FooterRow;

/// <summary>
/// Responsive, virtualised wallpaper grid. Wallpapers are laid out in rows that match the current width; the rows are
/// the virtualised items, so only the visible rows (plus a small buffer) exist as controls at any moment.
/// </summary>
public partial class WallpaperGrid : UserControl
{
    public static readonly StyledProperty<ObservableCollection<WallpaperItemViewModel>?> ItemsSourceProperty =
        AvaloniaProperty.Register<WallpaperGrid, ObservableCollection<WallpaperItemViewModel>?>(nameof(ItemsSource));

    public static readonly StyledProperty<Control?> HeaderProperty = AvaloniaProperty.Register<WallpaperGrid, Control?>(nameof(Header));
    public static readonly StyledProperty<double> MinCardWidthProperty = AvaloniaProperty.Register<WallpaperGrid, double>(nameof(MinCardWidth), 270);
    public static readonly StyledProperty<double> GapProperty = AvaloniaProperty.Register<WallpaperGrid, double>(nameof(Gap), 16);
    public static readonly StyledProperty<Thickness> GridPaddingProperty = AvaloniaProperty.Register<WallpaperGrid, Thickness>(nameof(GridPadding), new Thickness(32, 0, 32, 24));
    public static readonly StyledProperty<bool> IsLoadingProperty = AvaloniaProperty.Register<WallpaperGrid, bool>(nameof(IsLoading));
    public static readonly StyledProperty<bool> IsLoadingMoreProperty = AvaloniaProperty.Register<WallpaperGrid, bool>(nameof(IsLoadingMore));
    public static readonly StyledProperty<bool> HasMoreProperty = AvaloniaProperty.Register<WallpaperGrid, bool>(nameof(HasMore));
    public static readonly StyledProperty<ICommand?> LoadMoreCommandProperty = AvaloniaProperty.Register<WallpaperGrid, ICommand?>(nameof(LoadMoreCommand));
    public static readonly StyledProperty<double> CardWidthProperty = AvaloniaProperty.Register<WallpaperGrid, double>(nameof(CardWidth), 280);
    public static readonly StyledProperty<double> CardHeightProperty = AvaloniaProperty.Register<WallpaperGrid, double>(nameof(CardHeight), 175);
    public static readonly StyledProperty<bool> ShowLoadMoreButtonProperty = AvaloniaProperty.Register<WallpaperGrid, bool>(nameof(ShowLoadMoreButton));
    public static readonly StyledProperty<bool> ShowEndNoteProperty = AvaloniaProperty.Register<WallpaperGrid, bool>(nameof(ShowEndNote));

    private const double CardAspect = 0.62;
    private const double ScrollbarAllowance = 14;

    private readonly FooterRow _footer = new();
    private readonly List<WallpaperRow> _wallRows = [];
    private readonly ListBox _list;
    private HeaderRow? _headerRow;
    private int _columns = 1;
    private ScrollViewer? _scroll;
    private bool _loadMoreQueued;

    static WallpaperGrid()
    {
        ItemsSourceProperty.Changed.AddClassHandler<WallpaperGrid>((g, e) => g.OnItemsSourceChanged(e));
        HeaderProperty.Changed.AddClassHandler<WallpaperGrid>((g, _) => g.OnHeaderChanged());
        MinCardWidthProperty.Changed.AddClassHandler<WallpaperGrid>((g, _) => g.UpdateLayoutMetrics(force: true));
        GapProperty.Changed.AddClassHandler<WallpaperGrid>((g, _) => g.UpdateLayoutMetrics(force: true));
        IsLoadingProperty.Changed.AddClassHandler<WallpaperGrid>((g, _) => g.Rebuild());
        IsLoadingMoreProperty.Changed.AddClassHandler<WallpaperGrid>((g, _) => g.UpdateFooterFlags());
        HasMoreProperty.Changed.AddClassHandler<WallpaperGrid>((g, _) => g.UpdateFooterFlags());
    }

    public WallpaperGrid()
    {
        InitializeComponent();
        _list = this.FindControl<ListBox>("List")!;
        _list.AttachedToVisualTree += (_, _) => HookScroll();
        AttachedToVisualTree += (_, _) =>
        {
            var settings = Services.LumaServices.Get<Core.Interfaces.ISettingsService>();
            settings.Changed += OnSettingsChanged;
            ApplyDensity(settings.Current.Density);
        };
        DetachedFromVisualTree += (_, _) => Services.LumaServices.Get<Core.Interfaces.ISettingsService>().Changed -= OnSettingsChanged;
        DataContextChanged += (_, _) => ApplyHeaderContext();
    }

    private void InitializeComponent() => Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);

    private void OnSettingsChanged(object? sender, Core.Models.AppSettings settings) =>
        Dispatcher.UIThread.Post(() => ApplyDensity(settings.Density));

    /// <summary>Comfortable galleries use larger cards; compact ones fit more wallpapers on screen.</summary>
    private void ApplyDensity(Core.Enums.GalleryDensity density)
    {
        MinCardWidth = density == Core.Enums.GalleryDensity.Compact ? 210 : 270;
        Gap = density == Core.Enums.GalleryDensity.Compact ? 12 : 16;
    }

    public ObservableCollection<object> Rows { get; } = [];

    public ObservableCollection<WallpaperItemViewModel>? ItemsSource { get => GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }

    /// <summary>Optional content shown above the first row; it scrolls with the gallery.</summary>
    public Control? Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }

    public double MinCardWidth { get => GetValue(MinCardWidthProperty); set => SetValue(MinCardWidthProperty, value); }
    public double Gap { get => GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public Thickness GridPadding { get => GetValue(GridPaddingProperty); set => SetValue(GridPaddingProperty, value); }
    public bool IsLoading { get => GetValue(IsLoadingProperty); set => SetValue(IsLoadingProperty, value); }
    public bool IsLoadingMore { get => GetValue(IsLoadingMoreProperty); set => SetValue(IsLoadingMoreProperty, value); }
    public bool HasMore { get => GetValue(HasMoreProperty); set => SetValue(HasMoreProperty, value); }
    public ICommand? LoadMoreCommand { get => GetValue(LoadMoreCommandProperty); set => SetValue(LoadMoreCommandProperty, value); }
    public double CardWidth { get => GetValue(CardWidthProperty); private set => SetValue(CardWidthProperty, value); }
    public double CardHeight { get => GetValue(CardHeightProperty); private set => SetValue(CardHeightProperty, value); }
    public bool ShowLoadMoreButton { get => GetValue(ShowLoadMoreButtonProperty); private set => SetValue(ShowLoadMoreButtonProperty, value); }
    public bool ShowEndNote { get => GetValue(ShowEndNoteProperty); private set => SetValue(ShowEndNoteProperty, value); }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateLayoutMetrics(force: false);
    }

    private void OnItemsSourceChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyCollectionChanged old) old.CollectionChanged -= OnCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged next) next.CollectionChanged += OnCollectionChanged;
        Rebuild();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex == (ItemsSource?.Count ?? 0) - (e.NewItems?.Count ?? 0) && !IsLoading)
        {
            foreach (WallpaperItemViewModel item in e.NewItems!) Append(item);
            UpdateFooterFlags();
            QueueLoadMoreCheck();
            return;
        }
        Rebuild();
    }

    private void OnHeaderChanged()
    {
        ApplyHeaderContext();
        if (_headerRow is not null) Rows.Remove(_headerRow);
        _headerRow = Header is null ? null : new HeaderRow(Header);
        if (_headerRow is not null) Rows.Insert(0, _headerRow);
    }

    private void ApplyHeaderContext()
    {
        if (Header is not null) Header.DataContext = DataContext;
    }

    private void UpdateLayoutMetrics(bool force)
    {
        var padding = GridPadding;
        var available = Bounds.Width - padding.Left - padding.Right - ScrollbarAllowance;
        if (available <= 0) return;

        var min = Math.Max(120, MinCardWidth);
        var gap = Math.Max(0, Gap);
        var columns = Math.Max(1, (int)Math.Floor((available + gap) / (min + gap)));
        var width = Math.Floor((available - gap * (columns - 1)) / columns);

        var changedColumns = columns != _columns;
        _columns = columns;
        if (Math.Abs(width - CardWidth) > 0.5)
        {
            CardWidth = width;
            CardHeight = Math.Round(width * CardAspect);
        }
        if (changedColumns || force) Rebuild();
    }

    private void Rebuild()
    {
        // Keep the header row (its control cannot be re-parented while still attached) and replace the rest.
        for (var i = Rows.Count - 1; i >= (_headerRow is null ? 0 : 1); i--) Rows.RemoveAt(i);
        _wallRows.Clear();
        if (_headerRow is null && Header is not null) OnHeaderChanged();

        var items = ItemsSource;
        if (IsLoading && (items is null || items.Count == 0))
        {
            for (var i = 0; i < 3; i++) Rows.Add(new SkeletonRow(_columns));
            return;
        }

        if (items is not null)
        {
            foreach (var item in items) AppendWithoutFooter(item);
        }
        Rows.Add(_footer);
        UpdateFooterFlags();
        QueueLoadMoreCheck();
    }

    private void Append(WallpaperItemViewModel item)
    {
        var footerIndex = Rows.IndexOf(_footer);
        var last = _wallRows.LastOrDefault();
        if (last is not null && last.Items.Count < _columns)
        {
            last.Items.Add(item);
            return;
        }

        var row = new WallpaperRow();
        row.Items.Add(item);
        _wallRows.Add(row);
        if (footerIndex >= 0) Rows.Insert(footerIndex, row);
        else Rows.Add(row);
    }

    private void AppendWithoutFooter(WallpaperItemViewModel item)
    {
        var last = _wallRows.LastOrDefault();
        if (last is not null && last.Items.Count < _columns)
        {
            last.Items.Add(item);
            return;
        }
        var row = new WallpaperRow();
        row.Items.Add(item);
        _wallRows.Add(row);
        Rows.Add(row);
    }

    private void UpdateFooterFlags()
    {
        var hasItems = ItemsSource is { Count: > 0 };
        ShowLoadMoreButton = hasItems && HasMore && !IsLoadingMore;
        ShowEndNote = hasItems && !HasMore && !IsLoadingMore && !IsLoading;
    }

    private void HookScroll()
    {
        if (_scroll is not null) return;
        _scroll = _list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (_scroll is not null) _scroll.ScrollChanged += (_, _) => QueueLoadMoreCheck();
    }

    private void QueueLoadMoreCheck()
    {
        if (_loadMoreQueued) return;
        _loadMoreQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _loadMoreQueued = false;
            HookScroll();            if (_scroll is null || !HasMore || IsLoadingMore || IsLoading || _scroll.Viewport.Height <= 0) return;

            // Do not trust the scroll extent while rows are still being realised; derive the content height from the
            // (fixed) row geometry instead so a half-finished layout can never trigger a cascade of extra page loads.
            var rowHeight = CardHeight + 16;
            var headerHeight = _headerRow is { Content.IsVisible: true } ? 340 : 0;
            var expected = headerHeight + _wallRows.Count * rowHeight + 70;
            var content = Math.Max(_scroll.Extent.Height, expected);
            var remaining = content - (_scroll.Offset.Y + _scroll.Viewport.Height);            if (remaining < 700 && LoadMoreCommand?.CanExecute(null) == true) LoadMoreCommand.Execute(null);
        }, DispatcherPriority.Background);
    }

    public void ScrollToStart() => _scroll?.ScrollToHome();
}


