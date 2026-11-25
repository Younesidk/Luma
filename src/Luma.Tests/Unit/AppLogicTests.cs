using Luma.App.ViewModels;
using Luma.App.Services;
using Luma.Core.Enums;

namespace Luma.Tests.Unit;

public class DiscoverFiltersTests
{
    [Fact]
    public void Defaults_produce_a_safe_unfiltered_query()
    {
        var filters = new DiscoverFilters();
        var query = filters.ToQuery("  forest ", "abc123");

        Assert.Equal("  forest ", query.Text);
        Assert.Equal("forest", query.NormalizedText);
        Assert.Equal(WallpaperCategories.All, query.Categories);
        Assert.Equal(ContentPurity.Safe, query.Purity);
        Assert.Equal(WallpaperSort.Relevance, query.Sort);
        Assert.Equal(MinimumResolution.Any, query.MinimumResolution);
        Assert.Empty(query.Colors);
        Assert.Equal("abc123", query.Seed);
        Assert.Equal(0, filters.ActiveCount);
        Assert.False(filters.HasActiveFilters);
    }

    [Fact]
    public void Selected_options_flow_into_the_query_and_are_counted()
    {
        var filters = new DiscoverFilters
        {
            SortOption = DiscoverFilters.AllSorts.First(o => o.Value == WallpaperSort.Newest),
            MinResolutionOption = DiscoverFilters.Resolutions.First(o => o.Value == MinimumResolution.QuadHd),
            AspectOption = DiscoverFilters.Aspects.First(o => o.Value == AspectRatioFilter.UltraWide21x9),
            ContentKindOption = DiscoverFilters.ContentKinds.First(o => o.Value == ContentKind.Photograph),
            LicenseOption = DiscoverFilters.Licenses.First(o => o.Value == LicenseFilter.Commercial),
            Anime = false,
            Sketchy = true
        };
        filters.ToggleColorCommand.Execute(filters.Colors.First(c => c.Hex == "336600"));

        var query = filters.ToQuery(null, null);

        Assert.Equal(WallpaperSort.Newest, query.Sort);
        Assert.Equal(MinimumResolution.QuadHd, query.MinimumResolution);
        Assert.Equal(AspectRatioFilter.UltraWide21x9, query.AspectRatio);
        Assert.Equal(ContentKind.Photograph, query.ContentKind);
        Assert.Equal(LicenseFilter.Commercial, query.License);
        Assert.Equal(WallpaperCategories.General | WallpaperCategories.People, query.Categories);
        Assert.Equal(ContentPurity.Safe | ContentPurity.Sketchy, query.Purity);
        Assert.Equal(["336600"], query.Colors);
        Assert.Equal(8, filters.ActiveCount);
    }

    [Fact]
    public void At_least_one_category_and_one_purity_always_stay_enabled()
    {
        var filters = new DiscoverFilters();
        filters.Anime = false;
        filters.People = false;
        filters.General = false; // would leave nothing selected

        Assert.True(filters.General || filters.Anime || filters.People);

        filters.Safe = false; // would leave nothing selected
        Assert.True(filters.Safe || filters.Sketchy || filters.ExplicitContent);
    }

    [Fact]
    public void Colour_selection_is_exclusive_and_toggles_off()
    {
        var filters = new DiscoverFilters();
        var red = filters.Colors.First(c => c.Hex == "cc0000");
        var blue = filters.Colors.First(c => c.Hex == "0066cc");

        filters.ToggleColorCommand.Execute(red);
        filters.ToggleColorCommand.Execute(blue);
        Assert.Equal("0066cc", filters.SelectedColor);
        Assert.False(red.IsSelected);

        filters.ToggleColorCommand.Execute(blue);
        Assert.Null(filters.SelectedColor);
    }

    [Fact]
    public void Reset_restores_defaults_and_raises_change_once()
    {
        var filters = new DiscoverFilters { Sketchy = true, Anime = false };
        filters.ToggleColorCommand.Execute(filters.Colors[0]);
        var changes = 0;
        filters.Changed += (_, _) => changes++;

        filters.Reset();

        Assert.Equal(0, filters.ActiveCount);
        Assert.True(filters.Anime);
        Assert.False(filters.Sketchy);
        Assert.Null(filters.SelectedColor);
        Assert.Equal(1, changes);
    }
}

public class AppSupportTests
{
    [Fact]
    public void Accent_presets_are_unique_and_include_the_brand_violet()
    {
        Assert.Equal(ThemeService.Presets.Count, ThemeService.Presets.Select(p => p.Id).Distinct().Count());
        var violet = Assert.Single(ThemeService.Presets, p => p.Id == "violet");
        Assert.Equal(Windows.UI.Color.FromArgb(0xFF, 0x8B, 0x7C, 0xFF), violet.Dark);
    }

    [Fact]
    public void Category_shortcuts_cover_the_required_set_with_search_queries()
    {
        var labels = DiscoverViewModel.CategoryChips.Select(c => c.Label).ToArray();
        foreach (var expected in new[] { "Anime", "Nature", "Architecture", "Minimal", "Space", "Gaming", "Abstract", "Photography", "Cyberpunk", "Landscapes" })
            Assert.Contains(expected, labels);
        Assert.All(DiscoverViewModel.CategoryChips, c => Assert.False(string.IsNullOrWhiteSpace(c.Query)));
    }
}
