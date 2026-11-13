using Luma.Core.Interfaces;

namespace Luma.Infrastructure.Services;

/// <summary>
/// Resolves Luma's storage locations using the operating system's application-data folder.
/// Set the LUMA_DATA_DIR environment variable (or pass a root) to relocate everything, e.g. for portable use or tests.
/// </summary>
public sealed class LumaPaths : ILumaPaths
{
    public const string DataDirEnvironmentVariable = "LUMA_DATA_DIR";

    public LumaPaths(string? rootOverride = null)
    {
        var root = rootOverride ?? Environment.GetEnvironmentVariable(DataDirEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(root))
        {
            // %LOCALAPPDATA% on Windows, ~/.local/share on Linux, ~/Library/Application Support on macOS.
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Luma");
        }
        DataRoot = Path.GetFullPath(root);
    }

    public string DataRoot { get; }
    public string DatabasePath => Path.Combine(DataRoot, "luma.db");
    public string SettingsPath => Path.Combine(DataRoot, "settings.json");
    public string ThumbnailCacheDirectory => Path.Combine(DataRoot, "Cache", "Thumbnails");
    public string DefaultDownloadDirectory => Path.Combine(DataRoot, "Downloads");
    public string LogsDirectory => Path.Combine(DataRoot, "Logs");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(ThumbnailCacheDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
