using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace Luma.App.Services;

public interface IFileDialogService
{
    Task<IReadOnlyList<string>> PickImagesAsync();

    Task<IReadOnlyList<string>> PickVideosAsync();

    Task<string?> PickFolderAsync(string title);
}

/// <summary>Opens the native file/folder pickers, owned by the main window (unpackaged apps must pass the HWND).</summary>
public sealed class FileDialogService : IFileDialogService
{
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif"];

    public Window? Owner { get; set; }

    public async Task<IReadOnlyList<string>> PickImagesAsync()
    {
        if (OwnerHandle() is not { } hwnd) return [];
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.Thumbnail,
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            CommitButtonText = "Import wallpapers"
        };
        foreach (var extension in ImageExtensions) picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var files = await picker.PickMultipleFilesAsync();
        return files?.Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToList() ?? [];
    }

    public async Task<IReadOnlyList<string>> PickVideosAsync()
    {
        if (OwnerHandle() is not { } hwnd) return [];
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.Thumbnail,
            SuggestedStartLocation = PickerLocationId.VideosLibrary,
            CommitButtonText = "Import videos"
        };
        foreach (var extension in Luma.Core.Logic.VideoFileValidator.SupportedExtensions) picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var files = await picker.PickMultipleFilesAsync();
        return files?.Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToList() ?? [];
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        if (OwnerHandle() is not { } hwnd) return null;
        // The Windows folder picker has no title; the commit button carries the purpose instead.
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, CommitButtonText = title };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var folder = await picker.PickSingleFolderAsync();
        return string.IsNullOrEmpty(folder?.Path) ? null : folder.Path;
    }

    private nint? OwnerHandle()
    {
        if (Owner is null) return null;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(Owner);
        return hwnd == 0 ? null : hwnd;
    }
}
