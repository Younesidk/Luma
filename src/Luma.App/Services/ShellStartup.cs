using Luma.App.ViewModels;
using Luma.Core.Interfaces;
using Luma.Infrastructure.Database;
using Microsoft.Extensions.Logging;

namespace Luma.App.Services;

/// <summary>
/// Runs after the window is visible: opens the database, loads the small in-memory indexes (favourite keys, library
/// paths) and starts the rotation scheduler. Nothing here touches the network.
/// </summary>
public sealed class ShellStartup
{
    private readonly DatabaseInitializer _database;
    private readonly IFavoritesService _favorites;
    private readonly IDownloadLibrary _library;
    private readonly LibraryEventsBridge _bridge;
    private readonly IRotationService _rotation;
    private readonly IToastService _toasts;
    private readonly ILogger<ShellStartup> _logger;
    private Task? _ready;

    public ShellStartup(DatabaseInitializer database, IFavoritesService favorites, IDownloadLibrary library,
        LibraryEventsBridge bridge, IRotationService rotation, IToastService toasts, ILogger<ShellStartup> logger)
    {
        _database = database;
        _favorites = favorites;
        _library = library;
        _bridge = bridge;
        _rotation = rotation;
        _toasts = toasts;
        _logger = logger;
    }

    /// <summary>Completes when the local data is ready. Pages await this before using the library.</summary>
    public Task Ready => _ready ?? throw new InvalidOperationException("Startup has not begun.");

    public bool Failed { get; private set; }

    public Task BeginAsync() => _ready ??= InitializeAsync();

    private async Task InitializeAsync()
    {
        try
        {
            _bridge.Start();
            await Task.Run(async () =>
            {
                await _database.InitializeAsync();
                await _favorites.LoadAsync();
                await _library.LoadAsync();
            });
            _rotation.Start();
        }
        catch (Exception ex)
        {
            Failed = true;
            _logger.LogError(ex, "Startup failed while preparing the local library");
            _toasts.Show("Luma couldn't open its local library, so favourites and downloads are unavailable. Check the log for details.", ToastKind.Error);
        }
    }
}
