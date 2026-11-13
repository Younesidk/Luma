using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Luma.Core.Enums;
using Luma.Core.Models;

namespace Luma.App.ViewModels;

public sealed partial class ColorChoice(string hex) : ObservableObject
{
    public string Hex { get; } = hex;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>User-selected filters of the Discover page; converts itself into a provider-neutral query.</summary>
public sealed partial class DiscoverFilters : ObservableObject
{
    // Colours from Wallhaven's documented palette.
    private static readonly string[] Palette =
    [
        "660000", "990000", "cc0000", "ea4c88", "993399", "663399", "333399", "0066cc", "0099cc", "66cccc",
        "77cc33", "336600", "999900", "ffff00", "ff9900", "ff6600", "996633", "000000", "999999", "ffffff"
    ];

    public static readonly IReadOnlyList<Option<WallpaperSort>> AllSorts =
    [
        new("Best match", WallpaperSort.Relevance),
        new("Top rated", WallpaperSort.TopRated),
        new("Newest", WallpaperSort.Newest),
        new("Most viewed", WallpaperSort.MostViewed),
        new("Most favorited", WallpaperSort.MostFavorited),
        new("Random", WallpaperSort.Random)
    ];

    public static readonly IReadOnlyList<Option<TopRange>> TopRanges =
    [
        new("Past day", TopRange.Day), new("Past 3 days", TopRange.ThreeDays), new("Past week", TopRange.Week),
        new("Past month", TopRange.Month), new("Past 3 months", TopRange.ThreeMonths), new("Past 6 months", TopRange.SixMonths),
        new("Past year", TopRange.Year)
    ];

    public static readonly IReadOnlyList<Option<MinimumResolution>> Resolutions =
    [
        new("Any size", MinimumResolution.Any), new("1080p and up", MinimumResolution.FullHd),
        new("1440p and up", MinimumResolution.QuadHd), new("4K and up", MinimumResolution.UltraHd4K)
    ];

    public static readonly IReadOnlyList<Option<AspectRatioFilter>> Aspects =
    [
        new("Any shape", AspectRatioFilter.Any), new("16:9", AspectRatioFilter.Wide16x9), new("16:10", AspectRatioFilter.Wide16x10),
        new("Ultrawide", AspectRatioFilter.UltraWide21x9), new("Portrait", AspectRatioFilter.Portrait), new("Square", AspectRatioFilter.Square)
    ];

    public static readonly IReadOnlyList<Option<ContentKind>> ContentKinds =
    [
        new("Any type", ContentKind.Any), new("Photographs", ContentKind.Photograph),
        new("Illustrations", ContentKind.Illustration), new("Digitized artwork", ContentKind.DigitizedArtwork)
    ];

    public static readonly IReadOnlyList<Option<LicenseFilter>> Licenses =
    [
        new("Any license", LicenseFilter.Any), new("Free for commercial use", LicenseFilter.Commercial),
        new("Free to modify", LicenseFilter.Modification)
    ];

    private bool _suppress;

    public DiscoverFilters()
    {
        Colors = Palette.Select(hex => new ColorChoice(hex)).ToList();
        _sortOption = AllSorts[0];
        _topRangeOption = TopRanges[3];
        _minResolutionOption = Resolutions[0];
        _aspectOption = Aspects[0];
        _contentKindOption = ContentKinds[0];
        _licenseOption = Licenses[0];
        _general = _anime = _people = _safe = true;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<ColorChoice> Colors { get; }

    [ObservableProperty] private Option<WallpaperSort> _sortOption;
    [ObservableProperty] private Option<TopRange> _topRangeOption;
    [ObservableProperty] private Option<MinimumResolution> _minResolutionOption;
    [ObservableProperty] private Option<AspectRatioFilter> _aspectOption;
    [ObservableProperty] private Option<ContentKind> _contentKindOption;
    [ObservableProperty] private Option<LicenseFilter> _licenseOption;
    [ObservableProperty] private bool _general;
    [ObservableProperty] private bool _anime;
    [ObservableProperty] private bool _people;
    [ObservableProperty] private bool _safe;
    [ObservableProperty] private bool _sketchy;
    [ObservableProperty] private bool _explicitContent;

    public int ActiveCount
    {
        get
        {
            var count = 0;
            if (SortOption.Value != WallpaperSort.Relevance) count++;
            if (!(General && Anime && People)) count++;
            if (!(Safe && !Sketchy && !ExplicitContent)) count++;
            if (MinResolutionOption.Value != MinimumResolution.Any) count++;
            if (AspectOption.Value != AspectRatioFilter.Any) count++;
            if (ContentKindOption.Value != Core.Enums.ContentKind.Any) count++;
            if (LicenseOption.Value != LicenseFilter.Any) count++;
            if (Colors.Any(c => c.IsSelected)) count++;
            return count;
        }
    }

    public bool HasActiveFilters => ActiveCount > 0;

    public string? SelectedColor => Colors.FirstOrDefault(c => c.IsSelected)?.Hex;

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_suppress || e.PropertyName is nameof(ActiveCount) or nameof(HasActiveFilters) or nameof(SelectedColor)) return;
        if (e.PropertyName == nameof(General) || e.PropertyName == nameof(Anime) || e.PropertyName == nameof(People))
        {
            // At least one category must stay on; Wallhaven would otherwise return nothing.
            if (!General && !Anime && !People)
            {
                _suppress = true;
                General = true;
                _suppress = false;
            }
        }
        if (e.PropertyName == nameof(Safe) || e.PropertyName == nameof(Sketchy) || e.PropertyName == nameof(ExplicitContent))
        {
            if (!Safe && !Sketchy && !ExplicitContent)
            {
                _suppress = true;
                Safe = true;
                _suppress = false;
            }
        }
        RaiseChanged();
    }

    [RelayCommand]
    private void ToggleColor(ColorChoice choice)
    {
        var wasSelected = choice.IsSelected;
        foreach (var c in Colors) c.IsSelected = false;
        choice.IsSelected = !wasSelected;
        RaiseChanged();
    }

    [RelayCommand]
    public void Reset()
    {
        _suppress = true;
        SortOption = AllSorts[0];
        TopRangeOption = TopRanges[3];
        MinResolutionOption = Resolutions[0];
        AspectOption = Aspects[0];
        ContentKindOption = ContentKinds[0];
        LicenseOption = Licenses[0];
        General = Anime = People = Safe = true;
        Sketchy = ExplicitContent = false;
        foreach (var c in Colors) c.IsSelected = false;
        _suppress = false;
        RaiseChanged();
    }

    public WallpaperSearchQuery ToQuery(string? text, string? seed)
    {
        var categories = (General ? WallpaperCategories.General : 0) | (Anime ? WallpaperCategories.Anime : 0) | (People ? WallpaperCategories.People : 0);
        var purity = (Safe ? ContentPurity.Safe : 0) | (Sketchy ? ContentPurity.Sketchy : 0) | (ExplicitContent ? ContentPurity.Explicit : 0);
        return new WallpaperSearchQuery
        {
            Text = text,
            Sort = SortOption.Value,
            TopRange = TopRangeOption.Value,
            Categories = categories,
            Purity = purity,
            MinimumResolution = MinResolutionOption.Value,
            AspectRatio = AspectOption.Value,
            ContentKind = ContentKindOption.Value,
            License = LicenseOption.Value,
            Colors = SelectedColor is { } c ? [c] : [],
            Seed = seed
        };
    }

    private void RaiseChanged()
    {
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(SelectedColor));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

