using Luma.App.ViewModels;
using Luma.Core.Models;

namespace Luma.App.Services;

public interface INavigationService
{
    void NavigateTo(AppPage page);

    /// <summary>Opens Discover scoped to a provider ("all" for every enabled provider) with an optional query.</summary>
    void ShowDiscover(string sourceId, string? query = null);
}

public enum ToastKind
{
    Info,
    Success,
    Warning,
    Error
}

public interface IToastService
{
    void Show(string message, ToastKind kind = ToastKind.Info, string? actionText = null, Action? action = null);
}

public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "Confirm", bool destructive = false);

    /// <summary>Asks for a collection name and optional description. Returns null when cancelled.</summary>
    Task<CollectionEditResult?> EditCollectionAsync(string title, string confirmText, string? name = null, string? description = null,
        Func<string, string?, Task<string?>>? validate = null);

    Task PickCollectionAsync(Wallpaper wallpaper);
}

public sealed record CollectionEditResult(string Name, string? Description);

public interface IPreviewService
{
    void Open(WallpaperItemViewModel item, IReadOnlyList<WallpaperItemViewModel> context);

    void Close();
}

/// <summary>Marshals work onto the UI thread. View models and services use this instead of a UI framework's dispatcher.</summary>
public interface IUiDispatcher
{
    /// <summary>Queues <paramref name="action"/> to run on the UI thread and returns immediately.</summary>
    void Post(Action action);

    /// <summary>Runs <paramref name="action"/> on the UI thread (inline when already on it) and completes when it has run.</summary>
    Task InvokeAsync(Action action);
}
