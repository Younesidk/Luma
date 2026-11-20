using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Luma.Infrastructure.Database.Migrations;

/// <inheritdoc />
public partial class _20261116070757_AddVideoWallpapers : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "VideoWallpapers",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                WallpaperId = table.Column<long>(type: "INTEGER", nullable: false),
                FilePath = table.Column<string>(type: "TEXT", nullable: false),
                OriginalPath = table.Column<string>(type: "TEXT", nullable: true),
                Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                FileSizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                Width = table.Column<int>(type: "INTEGER", nullable: false),
                Height = table.Column<int>(type: "INTEGER", nullable: false),
                DurationMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                Container = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                HasAudio = table.Column<bool>(type: "INTEGER", nullable: false),
                ThumbnailPath = table.Column<string>(type: "TEXT", nullable: true),
                PosterPath = table.Column<string>(type: "TEXT", nullable: true),
                ImportedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_VideoWallpapers", x => x.Id);
                table.ForeignKey(
                    name: "FK_VideoWallpapers_Wallpapers_WallpaperId",
                    column: x => x.WallpaperId,
                    principalTable: "Wallpapers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_VideoWallpapers_ImportedAt",
            table: "VideoWallpapers",
            column: "ImportedAt");

        migrationBuilder.CreateIndex(
            name: "IX_VideoWallpapers_Sha256",
            table: "VideoWallpapers",
            column: "Sha256",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_VideoWallpapers_WallpaperId",
            table: "VideoWallpapers",
            column: "WallpaperId",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "VideoWallpapers");
    }
}
