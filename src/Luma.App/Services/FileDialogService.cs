using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Luma.App.Services;

public interface IFileDialogService
{
    Task<IReadOnlyList<string>> PickImagesAsync();

    Task<string?> PickFolderAsync(string title);
}

/// <summary>Opens the platform's native file/folder pickers for the main window.</summary>
public sealed class FileDialogService : IFileDialogService
{
    public TopLevel? Owner { get; set; }

    public async Task<IReadOnlyList<string>> PickImagesAsync()
    {
        if (Owner?.StorageProvider is not { CanOpen: true } storage) return [];
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import wallpapers",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Images") { Patterns = ["*.jpg", "*.jpeg", "*.png", "*.webp", "*.bmp", "*.gif"] }
            ]
        });
        return files.Select(f => f.TryGetLocalPath()).Where(p => p is not null).Cast<string>().ToList();
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        if (Owner?.StorageProvider is not { CanPickFolder: true } storage) return null;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }
}
