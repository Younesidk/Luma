using Luma.Core.Enums;

namespace Luma.App.Services;

public sealed record FavoriteChangedMessage(string Key, bool IsFavorite);

public sealed record LibraryChangedMessage(string Key, bool IsDownloaded);

public sealed record DownloadProgressMessage(string Key, DownloadStatus Status, double Progress);

public enum AppPage
{
    Discover,
    Favorites,
    Collections,
    Downloads,
    History,
    Feedback,
    Settings
}
