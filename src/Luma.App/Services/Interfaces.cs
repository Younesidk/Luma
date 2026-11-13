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
