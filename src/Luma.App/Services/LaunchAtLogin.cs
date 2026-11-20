using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Luma.App.Services;

/// <summary>
/// Starts Luma when the user signs in to Windows, through the per-user Run key (no admin rights, shows up in Task
/// Manager's Startup apps). The registry entry is the only source of truth, so turning Luma off in Task Manager or
/// deleting the entry is respected. Luma started this way opens straight into the notification area.
/// </summary>
public static class LaunchAtLogin
{
    /// <summary>Command-line flag for a start without the window (used by the Run entry).</summary>
    public const string BackgroundArgument = "--background";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Luma";

    public static bool IsSupported => OperatingSystem.IsWindows();

    public static bool IsEnabled
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(ValueName) is string { Length: > 0 };
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }
    }

    /// <summary>Adds or removes the Run entry. Throws when the registry can't be written.</summary>
    public static void SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled) key.SetValue(ValueName, Command(), RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Rewrites an existing entry when Luma's location changed (e.g. a portable copy was moved).</summary>
    public static void RefreshIfEnabled()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(ValueName) is not string { Length: > 0 } current) return;
            var expected = Command();
            if (!string.Equals(current, expected, StringComparison.OrdinalIgnoreCase)) key.SetValue(ValueName, expected, RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Best effort: a stale entry still starts the old path, or does nothing.
        }
    }

    /// <summary>Removes the entry quietly (used by the uninstaller hook).</summary>
    public static void RemoveQuietly()
    {
        try
        {
            SetEnabled(false);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
    }

    [SupportedOSPlatform("windows")]
    private static string Command() => $"\"{ExecutablePath()}\" {BackgroundArgument}";

    /// <summary>
    /// The path that survives updates. A Velopack install runs from &lt;root&gt;\current\Luma.exe and keeps a launcher
    /// stub at &lt;root&gt;\Luma.exe; the stub is what shortcuts should point at. Otherwise (portable, dev) the running exe.
    /// </summary>
    private static string ExecutablePath()
    {
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Luma.exe");
        var directory = Path.GetDirectoryName(exe);
        if (directory is not null && string.Equals(Path.GetFileName(directory), "current", StringComparison.OrdinalIgnoreCase))
        {
            var root = Path.GetDirectoryName(directory);
            var stub = root is null ? null : Path.Combine(root, Path.GetFileName(exe));
            if (stub is not null && File.Exists(stub) && File.Exists(Path.Combine(root!, "Update.exe"))) return stub;
        }
        return exe;
    }
}
