using CommunityToolkit.Mvvm.Messaging;
using Luma.Core.Interfaces;

namespace Luma.App.Services;

/// <summary>
/// Turns events raised by background services (favourites, library, downloads) into UI-thread messages. View models
/// register for those messages through the weak messenger, so short-lived card view models never leak.
/// </summary>
public sealed class LibraryEventsBridge
{
    private readonly IUiDispatcher _dispatcher;
    private readonly IFavoritesService _favorites;
    private readonly IDownloadLibrary _library;
    private readonly IDownloadService _downloads;
    private bool _started;

    public LibraryEventsBridge(IFavoritesService favorites, IDownloadLibrary library, IDownloadService downloads, IUiDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _favorites = favorites;
        _library = library;
        _downloads = downloads;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;

        _favorites.FavoriteChanged += (_, e) =>
            _dispatcher.Post(() => WeakReferenceMessenger.Default.Send(new FavoriteChangedMessage(e.Key, e.IsFavorite)));

        _library.Changed += (_, e) =>
            _dispatcher.Post(() => WeakReferenceMessenger.Default.Send(new LibraryChangedMessage(e.WallpaperKey, e.IsDownloaded)));

        _downloads.JobAdded += (_, job) =>
        {
            void Publish() => _dispatcher.Post(() =>
                WeakReferenceMessenger.Default.Send(new DownloadProgressMessage(job.Wallpaper.Key, job.Status, job.Progress)));

            job.Changed += (_, _) => Publish();
            Publish();
        };
    }
}
