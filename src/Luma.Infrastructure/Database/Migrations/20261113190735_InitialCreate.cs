using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Luma.Infrastructure.Database.Migrations;

/// <inheritdoc />
public partial class _20261113190735_InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AppSettings",
            columns: table => new
            {
                Key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                Value = table.Column<string>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AppSettings", x => x.Key);
            });

        migrationBuilder.CreateTable(
            name: "Collections",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false, collation: "NOCASE"),
                Description = table.Column<string>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Collections", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Wallpapers",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                ProviderId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                ExternalId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Title = table.Column<string>(type: "TEXT", nullable: true),
                CreatorName = table.Column<string>(type: "TEXT", nullable: true),
                ThumbnailUrl = table.Column<string>(type: "TEXT", nullable: true),
                PreviewUrl = table.Column<string>(type: "TEXT", nullable: true),
                FullUrl = table.Column<string>(type: "TEXT", nullable: true),
                SourcePageUrl = table.Column<string>(type: "TEXT", nullable: true),
                Width = table.Column<int>(type: "INTEGER", nullable: true),
                Height = table.Column<int>(type: "INTEGER", nullable: true),
                FileSizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                FileType = table.Column<string>(type: "TEXT", nullable: true),
                SourceCreatedAt = table.Column<long>(type: "INTEGER", nullable: true),
                MetadataJson = table.Column<string>(type: "TEXT", nullable: true),
                AddedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Wallpapers", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "CollectionWallpapers",
            columns: table => new
            {
                CollectionId = table.Column<long>(type: "INTEGER", nullable: false),
                WallpaperId = table.Column<long>(type: "INTEGER", nullable: false),
                AddedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CollectionWallpapers", x => new { x.CollectionId, x.WallpaperId });
                table.ForeignKey(
                    name: "FK_CollectionWallpapers_Collections_CollectionId",
                    column: x => x.CollectionId,
                    principalTable: "Collections",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_CollectionWallpapers_Wallpapers_WallpaperId",
                    column: x => x.WallpaperId,
                    principalTable: "Wallpapers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Downloads",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                WallpaperId = table.Column<long>(type: "INTEGER", nullable: false),
                FilePath = table.Column<string>(type: "TEXT", nullable: false),
                FileSizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                Sha256 = table.Column<string>(type: "TEXT", nullable: true),
                DownloadedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Downloads", x => x.Id);
                table.ForeignKey(
                    name: "FK_Downloads_Wallpapers_WallpaperId",
                    column: x => x.WallpaperId,
                    principalTable: "Wallpapers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Favorites",
            columns: table => new
            {
                WallpaperId = table.Column<long>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Favorites", x => x.WallpaperId);
                table.ForeignKey(
                    name: "FK_Favorites_Wallpapers_WallpaperId",
                    column: x => x.WallpaperId,
                    principalTable: "Wallpapers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "WallpaperHistory",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                WallpaperId = table.Column<long>(type: "INTEGER", nullable: false),
                AppliedAt = table.Column<long>(type: "INTEGER", nullable: false),
                FilePath = table.Column<string>(type: "TEXT", nullable: true),
                FitMode = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WallpaperHistory", x => x.Id);
                table.ForeignKey(
                    name: "FK_WallpaperHistory_Wallpapers_WallpaperId",
                    column: x => x.WallpaperId,
                    principalTable: "Wallpapers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Collections_Name",
            table: "Collections",
            column: "Name",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_CollectionWallpapers_WallpaperId",
            table: "CollectionWallpapers",
            column: "WallpaperId");

        migrationBuilder.CreateIndex(
            name: "IX_Downloads_DownloadedAt",
            table: "Downloads",
            column: "DownloadedAt");

        migrationBuilder.CreateIndex(
            name: "IX_Downloads_Sha256",
            table: "Downloads",
            column: "Sha256");

        migrationBuilder.CreateIndex(
            name: "IX_Downloads_WallpaperId",
            table: "Downloads",
            column: "WallpaperId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Favorites_CreatedAt",
            table: "Favorites",
            column: "CreatedAt");

        migrationBuilder.CreateIndex(
            name: "IX_WallpaperHistory_AppliedAt",
            table: "WallpaperHistory",
            column: "AppliedAt");

        migrationBuilder.CreateIndex(
            name: "IX_WallpaperHistory_WallpaperId",
            table: "WallpaperHistory",
            column: "WallpaperId");

        migrationBuilder.CreateIndex(
            name: "IX_Wallpapers_ProviderId_ExternalId",
            table: "Wallpapers",
            columns: new[] { "ProviderId", "ExternalId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Wallpapers_Title",
            table: "Wallpapers",
            column: "Title");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "AppSettings");

        migrationBuilder.DropTable(
            name: "CollectionWallpapers");

        migrationBuilder.DropTable(
            name: "Downloads");

        migrationBuilder.DropTable(
            name: "Favorites");

        migrationBuilder.DropTable(
            name: "WallpaperHistory");

        migrationBuilder.DropTable(
            name: "Collections");

        migrationBuilder.DropTable(
            name: "Wallpapers");
    }
}
