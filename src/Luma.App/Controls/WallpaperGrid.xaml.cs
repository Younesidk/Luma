using System.Collections.Specialized;
using System.Windows.Input;
using Luma.App.Services;
using Luma.Core.Enums;
using Luma.Core.Interfaces;
using Luma.Core.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Luma.App.Controls;

/// <summary>
/// Responsive, virtualised wallpaper grid. Cards are laid out by an <see cref="ItemsRepeater"/> with a
/// <see cref="UniformGridLayout"/> whose column count and card size follow the available width (cards keep a 0.62
/// aspect ratio), so only the cards near the viewport exist as elements. An optional <see cref="Header"/> scrolls with
/// the gallery, skeleton cards show while the first page loads, and the footer offers "Load more" / "Loading more…" /
/// an end note. Scrolling within ~700 px of the end runs <see cref="LoadMoreCommand"/> automatically.
/// </summary>
public sealed partial class WallpaperGrid : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(object), typeof(WallpaperGrid),
        new PropertyMetadata(null, (d, e) => ((WallpaperGrid)d).OnItemsSourceChanged(e)));

    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(object), typeof(WallpaperGrid), new PropertyMetadata(null, (d, _) => ((WallpaperGrid)d).ApplyHeaderContext()));

    public static readonly DependencyProperty MinCardWidthProperty = DependencyProperty.Register(
        nameof(MinCardWidth), typeof(double), typeof(WallpaperGrid), new PropertyMetadata(270d, (d, _) => ((WallpaperGrid)d).UpdateLayoutMetrics()));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(WallpaperGrid), new PropertyMetadata(16d, (d, _) => ((WallpaperGrid)d).UpdateLayoutMetrics()));

    public static readonly DependencyProperty GridPaddingProperty = DependencyProperty.Register(
        nameof(GridPadding), typeof(Thickness), typeof(WallpaperGrid), new PropertyMetadata(new Thickness(32, 0, 32, 24), (d, _) => ((WallpaperGrid)d).UpdateLayoutMetrics()));

    public static readonly DependencyProperty IsLoadingProperty = DependencyProperty.Register(
        nameof(IsLoading), typeof(bool), typeof(WallpaperGrid), new PropertyMetadata(false, (d, _) => ((WallpaperGrid)d).UpdateFlags()));

    public static readonly DependencyProperty IsLoadingMoreProperty = DependencyProperty.Register(
        nameof(IsLoadingMore), typeof(bool), typeof(WallpaperGrid), new PropertyMetadata(false, (d, _) => ((WallpaperGrid)d).UpdateFlags()));

    public static readonly DependencyProperty HasMoreProperty = DependencyProperty.Register(
        nameof(HasMore), typeof(bool), typeof(WallpaperGrid), new PropertyMetadata(false, (d, _) => ((WallpaperGrid)d).UpdateFlags()));

    public static readonly DependencyProperty LoadMoreCommandProperty = DependencyProperty.Register(
        nameof(LoadMoreCommand), typeof(ICommand), typeof(WallpaperGrid), new PropertyMetadata(null));

    public static readonly DependencyProperty CardWidthProperty = DependencyProperty.Register(
        nameof(CardWidth), typeof(double), typeof(WallpaperGrid), new PropertyMetadata(280d));

    public static readonly DependencyProperty CardHeightProperty = DependencyProperty.Register(
        nameof(CardHeight), typeof(double), typeof(WallpaperGrid), new PropertyMetadata(175d));

    public static readonly DependencyProperty ShowLoadMoreButtonProperty = DependencyProperty.Register(
        nameof(ShowLoadMoreButton), typeof(bool), typeof(WallpaperGrid), new PropertyMetadata(false));

    public static readonly DependencyProperty ShowEndNoteProperty = DependencyProperty.Register(
        nameof(ShowEndNote), typeof(bool), typeof(WallpaperGrid), new PropertyMetadata(false));

    public static readonly DependencyProperty ShowSkeletonsProperty = DependencyProperty.Register(
        nameof(ShowSkeletons), typeof(bool), typeof(WallpaperGrid), new PropertyMetadata(false));

    private const double CardAspect = 0.62;
    private const double RowGap = 16;
    private const double ScrollbarAllowance = 14;
    private const int SkeletonRows = 3;
    private const double LoadMoreThreshold = 700;

    private int _columns = 1;
    private bool _loadMoreQueued;
    private ISettingsService? _settings;

    public WallpaperGrid()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateLayoutMetrics();
        Scroller.ViewChanged += (_, _) => QueueLoadMoreCheck();
        DataContextChanged += (_, _) => ApplyHeaderContext();
        // ItemsRepeater does not hand the item to a template root that has no x:DataType; give each card its item.
        Repeater.ElementPrepared += (_, e) => AssignItem(e.Element, e.Index);
        Repeater.ElementIndexChanged += (_, e) => AssignItem(e.Element, e.NewIndex);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ApplyLayout();
        UpdateFlags();
    }

    /// <summary>
    /// The wallpapers to show: an <c>ObservableCollection&lt;WallpaperItemViewModel&gt;</c>. Appending items (paging) only
    /// lays out the new cards. Typed as object so the XAML compiler does not emit metadata for the view model graph
    /// (its Core <c>Wallpaper</c> model has required members, which breaks the generated activators).
    /// </summary>
    public object? ItemsSource { get => GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }

    private int ItemCount => ItemsSource is System.Collections.ICollection collection ? collection.Count : 0;

    /// <summary>Optional element shown above the first row (e.g. FeaturedHero); it scrolls with the gallery and gets the grid's DataContext.</summary>
    public object? Header { get => GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }

    public double MinCardWidth { get => (double)GetValue(MinCardWidthProperty); set => SetValue(MinCardWidthProperty, value); }

    /// <summary>Horizontal gap between cards (rows are always 16 apart).</summary>
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

    public Thickness GridPadding { get => (Thickness)GetValue(GridPaddingProperty); set => SetValue(GridPaddingProperty, value); }

    /// <summary>First page loading: shows skeleton cards while there are no items yet.</summary>
    public bool IsLoading { get => (bool)GetValue(IsLoadingProperty); set => SetValue(IsLoadingProperty, value); }

    public bool IsLoadingMore { get => (bool)GetValue(IsLoadingMoreProperty); set => SetValue(IsLoadingMoreProperty, value); }

    public bool HasMore { get => (bool)GetValue(HasMoreProperty); set => SetValue(HasMoreProperty, value); }

    public ICommand? LoadMoreCommand { get => (ICommand?)GetValue(LoadMoreCommandProperty); set => SetValue(LoadMoreCommandProperty, value); }

    public double CardWidth { get => (double)GetValue(CardWidthProperty); private set => SetValue(CardWidthProperty, value); }

    public double CardHeight { get => (double)GetValue(CardHeightProperty); private set => SetValue(CardHeightProperty, value); }

    public bool ShowLoadMoreButton { get => (bool)GetValue(ShowLoadMoreButtonProperty); private set => SetValue(ShowLoadMoreButtonProperty, value); }

    public bool ShowEndNote { get => (bool)GetValue(ShowEndNoteProperty); private set => SetValue(ShowEndNoteProperty, value); }

    public bool ShowSkeletons { get => (bool)GetValue(ShowSkeletonsProperty); private set => SetValue(ShowSkeletonsProperty, value); }

    public Visibility Invert(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public void ScrollToStart() => Scroller.ChangeView(null, 0, null, disableAnimation: true);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_settings is not null) return;
        try
        {
            _settings = LumaServices.Get<ISettingsService>();
        }
        catch (InvalidOperationException)
        {
            return; // designer / services not ready
        }
        _settings.Changed += OnSettingsChanged;
        ApplyDensity(_settings.Current.Density);
        QueueLoadMoreCheck();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (IsLoaded || _settings is null) return;
        _settings.Changed -= OnSettingsChanged;
        _settings = null;
    }

    private void OnSettingsChanged(object? sender, AppSettings settings) =>
        DispatcherQueue.TryEnqueue(() => ApplyDensity(settings.Density));

    /// <summary>Comfortable galleries use larger cards; compact ones fit more wallpapers on screen.</summary>
    private void ApplyDensity(GalleryDensity density)
    {
        MinCardWidth = density == GalleryDensity.Compact ? 210 : 270;
        Gap = density == GalleryDensity.Compact ? 12 : 16;
    }

    private void OnItemsSourceChanged(DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyCollectionChanged old) old.CollectionChanged -= OnCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged next) next.CollectionChanged += OnCollectionChanged;
        UpdateFlags();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateFlags();

    private void AssignItem(UIElement element, int index)
    {
        if (element is not FrameworkElement card || Repeater.ItemsSourceView is not { } items || index < 0 || index >= items.Count) return;
        var item = items.GetAt(index);
        if (!ReferenceEquals(card.DataContext, item)) card.DataContext = item;
    }

    private void ApplyHeaderContext()
    {
        if (Header is FrameworkElement header) header.DataContext = DataContext;
    }

    private void UpdateLayoutMetrics()
    {
        var padding = GridPadding;
        var available = ActualWidth - padding.Left - padding.Right - ScrollbarAllowance;
        if (available <= 0) return;

        var min = Math.Max(120, MinCardWidth);
        var gap = Math.Max(0, Gap);
        var columns = Math.Max(1, (int)Math.Floor((available + gap) / (min + gap)));
        // On narrow windows flooring left one or two oversized cards; allow cards down to ~80% of the minimum instead.
        if ((available - gap * (columns - 1)) / columns > min * 1.6 && (available - gap * columns) / (columns + 1) >= min * 0.8) columns++;

        var width = Math.Max(60, Math.Floor((available - gap * (columns - 1)) / columns));
        var changedColumns = columns != _columns;
        _columns = columns;
        if (Math.Abs(width - CardWidth) > 0.5 || changedColumns)
        {
            CardWidth = width;
            CardHeight = Math.Round(width * CardAspect);
        }
        ApplyLayout();
        if (changedColumns && ShowSkeletons) UpdateSkeletonSource();
        QueueLoadMoreCheck();
    }

    private void ApplyLayout()
    {
        foreach (var layout in new[] { CardLayout, SkeletonLayout })
        {
            layout.MinItemWidth = CardWidth;
            layout.MinItemHeight = CardHeight;
            layout.MinColumnSpacing = Math.Max(0, Gap);
            layout.MinRowSpacing = RowGap;
            layout.MaximumRowsOrColumns = _columns;
        }
    }

    private void UpdateFlags()
    {
        var hasItems = ItemCount > 0;
        var skeletons = IsLoading && !hasItems;
        if (skeletons != ShowSkeletons)
        {
            ShowSkeletons = skeletons;
            UpdateSkeletonSource();
        }
        ShowLoadMoreButton = hasItems && HasMore && !IsLoadingMore;
        ShowEndNote = hasItems && !HasMore && !IsLoadingMore && !IsLoading;
        QueueLoadMoreCheck();
    }

    private void UpdateSkeletonSource() =>
        SkeletonRepeater.ItemsSource = ShowSkeletons ? Enumerable.Range(0, SkeletonRows * _columns).ToList() : null;

    private double RowHeight => CardHeight + RowGap;

    private double HeaderHeight => Header is UIElement { Visibility: Visibility.Visible } ? HeaderPresenter.ActualHeight : 0;

    private void QueueLoadMoreCheck()
    {
        if (_loadMoreQueued) return;
        _loadMoreQueued = DispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Low, CheckLoadMore) == true;
    }

    private void CheckLoadMore()
    {
        _loadMoreQueued = false;
        if (!IsLoaded || !HasMore || IsLoadingMore || IsLoading || Scroller.ViewportHeight <= 0) return;

        // Do not trust the scroll extent while cards are still being realised; derive the content height from the
        // (fixed) card geometry instead so a half-finished layout can never trigger a cascade of extra page loads.
        var rows = (int)Math.Ceiling(ItemCount / (double)Math.Max(1, _columns));
        var expected = GridPadding.Top + HeaderHeight + rows * RowHeight + 70;
        var content = Math.Max(Scroller.ExtentHeight, expected);
        var remaining = content - (Scroller.VerticalOffset + Scroller.ViewportHeight);
        if (remaining < LoadMoreThreshold && LoadMoreCommand?.CanExecute(null) == true) LoadMoreCommand.Execute(null);
    }
}
