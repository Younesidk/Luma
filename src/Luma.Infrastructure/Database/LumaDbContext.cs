using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Luma.Infrastructure.Database;

public class LumaDbContext(DbContextOptions<LumaDbContext> options) : DbContext(options)
{
    public DbSet<WallpaperEntity> Wallpapers => Set<WallpaperEntity>();
    public DbSet<FavoriteEntity> Favorites => Set<FavoriteEntity>();
    public DbSet<CollectionEntity> Collections => Set<CollectionEntity>();
    public DbSet<CollectionWallpaperEntity> CollectionWallpapers => Set<CollectionWallpaperEntity>();
    public DbSet<DownloadEntity> Downloads => Set<DownloadEntity>();
    public DbSet<WallpaperHistoryEntity> History => Set<WallpaperHistoryEntity>();
    public DbSet<AppSettingEntity> AppSettings => Set<AppSettingEntity>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite cannot order or compare DateTimeOffset when stored as text; ticks make it orderable.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WallpaperEntity>(e =>
        {
            e.ToTable("Wallpapers");
            e.HasKey(x => x.Id);
            e.Property(x => x.ProviderId).IsRequired().HasMaxLength(40);
            e.Property(x => x.ExternalId).IsRequired().HasMaxLength(200);
            e.HasIndex(x => new { x.ProviderId, x.ExternalId }).IsUnique();
            e.HasIndex(x => x.Title);
        });

        modelBuilder.Entity<FavoriteEntity>(e =>
        {
            e.ToTable("Favorites");
            e.HasKey(x => x.WallpaperId);
            e.HasOne(x => x.Wallpaper).WithMany().HasForeignKey(x => x.WallpaperId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.CreatedAt);
        });

        modelBuilder.Entity<CollectionEntity>(e =>
        {
            e.ToTable("Collections");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).IsRequired().HasMaxLength(120).UseCollation("NOCASE");
            e.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<CollectionWallpaperEntity>(e =>
        {
            e.ToTable("CollectionWallpapers");
            e.HasKey(x => new { x.CollectionId, x.WallpaperId });
            e.HasOne(x => x.Collection).WithMany(c => c.Items).HasForeignKey(x => x.CollectionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Wallpaper).WithMany().HasForeignKey(x => x.WallpaperId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.WallpaperId);
        });

        modelBuilder.Entity<DownloadEntity>(e =>
        {
            e.ToTable("Downloads");
            e.HasKey(x => x.Id);
            e.Property(x => x.FilePath).IsRequired();
            e.HasOne(x => x.Wallpaper).WithMany().HasForeignKey(x => x.WallpaperId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.WallpaperId).IsUnique();
            e.HasIndex(x => x.Sha256);
            e.HasIndex(x => x.DownloadedAt);
        });

        modelBuilder.Entity<WallpaperHistoryEntity>(e =>
        {
            e.ToTable("WallpaperHistory");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Wallpaper).WithMany().HasForeignKey(x => x.WallpaperId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.AppliedAt);
        });

        modelBuilder.Entity<AppSettingEntity>(e =>
        {
            e.ToTable("AppSettings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(100);
        });
    }
}
