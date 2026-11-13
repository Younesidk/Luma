using Luma.Core.Enums;
using Luma.Core.Exceptions;
using Luma.Core.Models;
using Luma.Infrastructure.Database;
using Luma.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Luma.Tests.Integration;

public class DatabaseInitializationTests
{
    [Fact]
    public async Task Initialization_creates_schema_and_is_idempotent()
    {
        await using var env = await TestEnvironment.CreateAsync();
        Assert.True(File.Exists(env.Paths.DatabasePath));

        await using (var db = await env.Factory.CreateDbContextAsync())
        {
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());
            Assert.Equal(0, await db.Wallpapers.CountAsync());
            Assert.Equal(0, await db.Favorites.CountAsync());
            Assert.Equal(0, await db.Collections.CountAsync());
            Assert.Equal(0, await db.CollectionWallpapers.CountAsync());
            Assert.Equal(0, await db.Downloads.CountAsync());
            Assert.Equal(0, await db.History.CountAsync());
            Assert.Equal(0, await db.AppSettings.CountAsync());
        }

        // Running again over an existing database must not fail or drop data.
        var fav = new FavoritesService(env.Factory);
        await fav.SetFavoriteAsync(Samples.Wallpaper("keep"), true);
        await new DatabaseInitializer(env.Factory, env.Paths, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        await fav.LoadAsync();
        Assert.True(fav.IsFavorite("wallhaven:keep"));
    }

    [Fact]
    public async Task Model_snapshot_matches_migrations()
    {
        await using var env = await TestEnvironment.CreateAsync();
        await using var db = await env.Factory.CreateDbContextAsync();
        // If the entities changed without a new migration this reports pending model changes.
        Assert.False(db.Database.HasPendingModelChanges(), "The EF model has changes that are not covered by a migration. Run `dotnet ef migrations add`.");
    }
}

public class FavoritesServiceTests
{
    [Fact]
    public async Task Favorites_persist_across_service_instances()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var first = new FavoritesService(env.Factory);
        var wallpaper = Samples.Wallpaper("fav1", title: "Aurora");

        var events = new List<FavoriteChangedEventArgsSnapshot>();
        first.FavoriteChanged += (_, e) => events.Add(new(e.Key, e.IsFavorite));

        Assert.True(await first.ToggleAsync(wallpaper));
        Assert.True(first.IsFavorite(wallpaper.Key));
        Assert.Equal(1, first.Count);

        var second = new FavoritesService(env.Factory);
        Assert.False(second.IsFavorite(wallpaper.Key)); // not loaded yet
        await second.LoadAsync();
        Assert.True(second.IsFavorite(wallpaper.Key));

        var stored = Assert.Single(await second.QueryAsync(new LibraryQuery()));
        Assert.Equal("Aurora", stored.Title);
        Assert.Equal(new WallpaperResolution(1920, 1080), stored.Resolution);
        Assert.Equal("Tester", stored.Metadata.CreatorName);
        Assert.Equal(new[] { "a", "b" }, stored.Metadata.Tags);
        Assert.Equal("CC BY 4.0", stored.Metadata.LicenseName);

        Assert.False(await second.ToggleAsync(wallpaper));
        Assert.Empty(await second.QueryAsync(new LibraryQuery()));
        Assert.Equal([new("wallhaven:fav1", true)], events);
    }

    [Fact]
    public async Task Search_filter_and_sort_work()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var service = new FavoritesService(env.Factory);
        await service.SetFavoriteAsync(Samples.Wallpaper("a1", title: "Misty Forest") with { Resolution = new(1920, 1080) }, true);
        await Task.Delay(15);
        await service.SetFavoriteAsync(Samples.Wallpaper("b2", title: "Neon City", provider: "openverse") with { Resolution = new(3840, 2160) }, true);
        await Task.Delay(15);
        await service.SetFavoriteAsync(Samples.Wallpaper("c3", title: "100% Pure_Gold") with { Resolution = new(1280, 720) }, true);

        Assert.Equal(["c3", "b2", "a1"], (await service.QueryAsync(new LibraryQuery())).Select(w => w.ExternalId));
        Assert.Equal(["a1", "b2", "c3"], (await service.QueryAsync(new LibraryQuery { Sort = LibrarySort.Oldest })).Select(w => w.ExternalId));
        Assert.Equal(["b2", "a1", "c3"], (await service.QueryAsync(new LibraryQuery { Sort = LibrarySort.Resolution })).Select(w => w.ExternalId));
        Assert.Equal(["c3", "a1", "b2"], (await service.QueryAsync(new LibraryQuery { Sort = LibrarySort.Name })).Select(w => w.ExternalId));

        Assert.Equal(["a1"], (await service.QueryAsync(new LibraryQuery { Text = "forest" })).Select(w => w.ExternalId));
        Assert.Equal(["b2"], (await service.QueryAsync(new LibraryQuery { ProviderId = "openverse" })).Select(w => w.ExternalId));
        // LIKE wildcards typed by the user are literal characters.
        Assert.Equal(["c3"], (await service.QueryAsync(new LibraryQuery { Text = "100%" })).Select(w => w.ExternalId));
        Assert.Empty(await service.QueryAsync(new LibraryQuery { Text = "%" , ProviderId = "openverse"}));
        Assert.Empty(await service.QueryAsync(new LibraryQuery { Text = "_____" }));
    }

    private sealed record FavoriteChangedEventArgsSnapshot(string Key, bool IsFavorite);
}

public class CollectionServiceTests
{
    [Fact]
    public async Task Collections_support_the_full_lifecycle_and_persist()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var service = new CollectionService(env.Factory);
        var changes = 0;
        service.CollectionsChanged += (_, _) => changes++;

        var anime = await service.CreateAsync("  Anime  ", "Big eyes");
        var nature = await service.CreateAsync("Nature", null);
        Assert.Equal("Anime", anime.Name);

        await service.AddWallpaperAsync(anime.Id, Samples.Wallpaper("w1"));
        await service.AddWallpaperAsync(anime.Id, Samples.Wallpaper("w2"));
        await service.AddWallpaperAsync(anime.Id, Samples.Wallpaper("w1")); // duplicate add is a no-op
        await service.AddWallpaperAsync(nature.Id, Samples.Wallpaper("w1"));

        var reloaded = await new CollectionService(env.Factory).GetAllAsync();
        Assert.Equal(["Anime", "Nature"], reloaded.Select(c => c.Name));
        Assert.Equal(2, reloaded[0].WallpaperCount);
        Assert.Equal(1, reloaded[1].WallpaperCount);
        Assert.Equal("Big eyes", reloaded[0].Description);
        Assert.Equal(2, reloaded[0].CoverWallpapers.Count);

        Assert.Equal([anime.Id, nature.Id], (await service.GetCollectionIdsContainingAsync("wallhaven:w1")).OrderBy(x => x));
        Assert.Equal([anime.Id], await service.GetCollectionIdsContainingAsync("wallhaven:w2"));

        await service.RenameAsync(anime.Id, "Anime Girls", "renamed");
        await service.RemoveWallpaperAsync(anime.Id, "wallhaven:w2");
        var contents = await service.GetWallpapersAsync(anime.Id);
        Assert.Equal(["w1"], contents.Select(w => w.ExternalId));

        await service.DeleteAsync(nature.Id);
        Assert.Equal(["Anime Girls"], (await service.GetAllAsync()).Select(c => c.Name));
        Assert.True(changes >= 7);
    }

    [Fact]
    public async Task Names_are_validated_and_unique_ignoring_case()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var service = new CollectionService(env.Factory);
        await service.CreateAsync("Dark", null);

        await Assert.ThrowsAsync<LumaException>(() => service.CreateAsync("dark", null));
        await Assert.ThrowsAsync<LumaException>(() => service.CreateAsync("   ", null));
        await Assert.ThrowsAsync<LumaException>(() => service.CreateAsync(new string('x', 200), null));
        var other = await service.CreateAsync("Light", null);
        await Assert.ThrowsAsync<LumaException>(() => service.RenameAsync(other.Id, "DARK", null));
        await Assert.ThrowsAsync<LumaException>(() => service.AddWallpaperAsync(9999, Samples.Wallpaper()));
    }

    [Fact]
    public async Task Deleting_a_collection_never_touches_downloaded_files_or_favorites()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var collections = new CollectionService(env.Factory);
        var library = new DownloadLibrary(env.Factory, NullLogger<DownloadLibrary>.Instance);
        var favorites = new FavoritesService(env.Factory);

        var file = Path.Combine(env.Root, "keep.png");
        await File.WriteAllBytesAsync(file, Samples.Png(100, 50));
        var wallpaper = Samples.Wallpaper("kept");
        await library.AddAsync(wallpaper, file, "hash1");
        await favorites.SetFavoriteAsync(wallpaper, true);
        var collection = await collections.CreateAsync("Temp", null);
        await collections.AddWallpaperAsync(collection.Id, wallpaper);

        await collections.DeleteAsync(collection.Id);

        Assert.True(File.Exists(file));
        Assert.Single(await library.QueryAsync(new LibraryQuery()));
        Assert.Single(await favorites.QueryAsync(new LibraryQuery()));
    }
}

public class DownloadLibraryTests
{
    [Fact]
    public async Task Downloads_store_metadata_and_stay_available_offline()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var library = new DownloadLibrary(env.Factory, NullLogger<DownloadLibrary>.Instance);
        var file = Path.Combine(env.Root, "pic.png");
        await File.WriteAllBytesAsync(file, Samples.Png(2560, 1440));

        var stored = await library.AddAsync(Samples.Wallpaper("dl1", title: "Canyon"), file, "abc");
        Assert.Equal(new WallpaperResolution(2560, 1440), stored.Wallpaper.Resolution); // read from the real file
        Assert.Equal(new FileInfo(file).Length, stored.FileSizeBytes);
        Assert.Equal(file, library.GetLocalPath("wallhaven:dl1"));

        // A fresh instance (e.g. after restart) knows the file without any network access.
        var restarted = new DownloadLibrary(env.Factory, NullLogger<DownloadLibrary>.Instance);
        await restarted.LoadAsync();
        Assert.True(restarted.IsDownloaded("wallhaven:dl1"));
        var item = Assert.Single(await restarted.QueryAsync(new LibraryQuery { Text = "canyon" }));
        Assert.Equal(file, item.Wallpaper.LocalFilePath);
        Assert.Equal("abc", (await restarted.FindBySha256Async("abc"))!.Sha256);
        Assert.Null(await restarted.FindBySha256Async("missing"));
    }

    [Fact]
    public async Task Removing_an_entry_keeps_the_file_unless_deletion_is_requested()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var library = new DownloadLibrary(env.Factory, NullLogger<DownloadLibrary>.Instance);
        var keep = Path.Combine(env.Root, "keep.png");
        var drop = Path.Combine(env.Root, "drop.png");
        await File.WriteAllBytesAsync(keep, Samples.Png());
        await File.WriteAllBytesAsync(drop, Samples.Png());
        var a = await library.AddAsync(Samples.Wallpaper("k"), keep, "h1");
        var b = await library.AddAsync(Samples.Wallpaper("d"), drop, "h2");

        var changes = new List<(string, bool)>();
        library.Changed += (_, e) => changes.Add((e.WallpaperKey, e.IsDownloaded));

        await library.RemoveAsync(a.Id, deleteFile: false);
        await library.RemoveAsync(b.Id, deleteFile: true);

        Assert.True(File.Exists(keep));
        Assert.False(File.Exists(drop));
        Assert.Empty(await library.QueryAsync(new LibraryQuery()));
        Assert.False(library.IsDownloaded("wallhaven:k"));
        Assert.Equal([("wallhaven:k", false), ("wallhaven:d", false)], changes);
    }

    [Fact]
    public async Task Missing_files_are_flagged_and_excluded_from_rotation()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var library = new DownloadLibrary(env.Factory, NullLogger<DownloadLibrary>.Instance);
        var present = Path.Combine(env.Root, "present.png");
        var gone = Path.Combine(env.Root, "gone.png");
        await File.WriteAllBytesAsync(present, Samples.Png());
        await File.WriteAllBytesAsync(gone, Samples.Png());
        await library.AddAsync(Samples.Wallpaper("p"), present, "p");
        await library.AddAsync(Samples.Wallpaper("g"), gone, "g");
        File.Delete(gone);

        var all = await library.QueryAsync(new LibraryQuery());
        Assert.Equal(1, all.Count(d => d.FileExists));
        Assert.Null(library.GetLocalPath("wallhaven:g"));
        var candidates = await library.GetRotationCandidatesAsync(RotationSource.AllDownloads, null);
        Assert.Equal(["p"], candidates.Select(c => c.Wallpaper.ExternalId));
    }

    [Fact]
    public async Task Rotation_candidates_respect_favorites_and_collections()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var library = new DownloadLibrary(env.Factory, NullLogger<DownloadLibrary>.Instance);
        var favorites = new FavoritesService(env.Factory);
        var collections = new CollectionService(env.Factory);
        foreach (var id in new[] { "a", "b", "c" })
        {
            var path = Path.Combine(env.Root, id + ".png");
            await File.WriteAllBytesAsync(path, Samples.Png());
            await library.AddAsync(Samples.Wallpaper(id), path, id);
        }
        await favorites.SetFavoriteAsync(Samples.Wallpaper("b"), true);
        var collection = await collections.CreateAsync("Mix", null);
        await collections.AddWallpaperAsync(collection.Id, Samples.Wallpaper("c"));

        Assert.Equal(3, (await library.GetRotationCandidatesAsync(RotationSource.AllDownloads, null)).Count);
        Assert.Equal(["b"], (await library.GetRotationCandidatesAsync(RotationSource.Favorites, null)).Select(c => c.Wallpaper.ExternalId));
        Assert.Equal(["c"], (await library.GetRotationCandidatesAsync(RotationSource.Collection, collection.Id)).Select(c => c.Wallpaper.ExternalId));
        Assert.Empty(await library.GetRotationCandidatesAsync(RotationSource.Collection, null));
    }

    [Fact]
    public async Task Importing_local_images_validates_content_and_detects_duplicates()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var library = new DownloadLibrary(env.Factory, NullLogger<DownloadLibrary>.Instance);
        var image = Path.Combine(env.Root, "holiday.png");
        var copy = Path.Combine(env.Root, "holiday copy.png");
        var fake = Path.Combine(env.Root, "notes.jpg");
        await File.WriteAllBytesAsync(image, Samples.Png(1600, 900));
        File.Copy(image, copy);
        await File.WriteAllTextAsync(fake, "definitely not a jpeg");

        var first = await library.ImportFileAsync(image);
        Assert.Equal(Wallpaper.LocalProviderId, first.Wallpaper.ProviderId);
        Assert.Equal("holiday", first.Wallpaper.Title);
        Assert.Equal(new WallpaperResolution(1600, 900), first.Wallpaper.Resolution);

        var duplicate = await library.ImportFileAsync(copy);
        Assert.Equal(first.Id, duplicate.Id);
        Assert.Single(await library.QueryAsync(new LibraryQuery()));

        await Assert.ThrowsAsync<InvalidImageException>(() => library.ImportFileAsync(fake));
        await Assert.ThrowsAsync<LumaException>(() => library.ImportFileAsync(Path.Combine(env.Root, "missing.png")));
    }
}

public class HistoryAndStateTests
{
    [Fact]
    public async Task History_persists_and_collapses_consecutive_duplicates()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var history = new HistoryService(env.Factory);
        var a = Samples.Wallpaper("a");
        var b = Samples.Wallpaper("b");

        await history.RecordAsync(a, "C:\\a.jpg", WallpaperFitMode.Fill);
        await history.RecordAsync(a, "C:\\a.jpg", WallpaperFitMode.Fit); // same wallpaper twice in a row
        await history.RecordAsync(b, "C:\\b.jpg", WallpaperFitMode.Center);
        await history.RecordAsync(a, "C:\\a.jpg", WallpaperFitMode.Tile);

        var entries = await new HistoryService(env.Factory).GetAsync(50);
        Assert.Equal(["a", "b", "a"], entries.Select(e => e.Wallpaper.ExternalId));
        Assert.Equal(WallpaperFitMode.Tile, entries[0].FitMode);
        Assert.Equal("C:\\b.jpg", entries[1].FilePath);
        Assert.True(entries[0].AppliedAt >= entries[1].AppliedAt);

        Assert.Single(await history.GetAsync(1));
        await history.ClearAsync();
        Assert.Empty(await history.GetAsync(50));
    }

    [Fact]
    public async Task State_store_round_trips_values()
    {
        await using var env = await TestEnvironment.CreateAsync();
        var store = new StateStore(env.Factory);
        Assert.Null(await store.GetAsync("k"));
        await store.SetAsync("k", "v1");
        await store.SetAsync("k", "v2");
        Assert.Equal("v2", await new StateStore(env.Factory).GetAsync("k"));
        await store.SetAsync("k", null);
        Assert.Null(await store.GetAsync("k"));
    }
}
