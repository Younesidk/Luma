using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Luma.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Luma.Infrastructure.Platform;

public sealed class PlatformInfo : IPlatformInfo
{
    public bool IsWindows => OperatingSystem.IsWindows();
    public bool IsLinux => OperatingSystem.IsLinux();
    public bool IsMacOS => OperatingSystem.IsMacOS();

    public string Name => IsWindows ? "Windows" : IsLinux ? "Linux" : IsMacOS ? "macOS" : "Unknown";

    public string DesktopEnvironment => IsLinux
        ? (GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? GetEnvironmentVariable("DESKTOP_SESSION") ?? string.Empty).ToLowerInvariant()
        : string.Empty;

    public string SessionType => IsLinux
        ? (GetEnvironmentVariable("XDG_SESSION_TYPE") ?? (GetEnvironmentVariable("WAYLAND_DISPLAY") is not null ? "wayland" : GetEnvironmentVariable("DISPLAY") is not null ? "x11" : string.Empty)).ToLowerInvariant()
        : string.Empty;

    public string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);
}

/// <summary>Runs external programs without a shell (arguments are passed as a list, never concatenated into a command line).</summary>
public sealed class ProcessRunner(ILogger<ProcessRunner> logger) : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        try
        {
            using var process = new Process { StartInfo = info };
            process.Start();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            var stdout = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                if (cancellationToken.IsCancellationRequested) throw;
                return new ProcessResult(-1, string.Empty, "The command timed out.");
            }
            return new ProcessResult(process.ExitCode, await stdout, await stderr);
        }
        catch (Win32Exception ex)
        {
            logger.LogDebug(ex, "Command not found: {FileName}", fileName);
            return new ProcessResult(127, string.Empty, ex.Message, CommandNotFound: true);
        }
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { /* already exited */ }
    }
}

/// <summary>Opens files, folders and URLs with the operating system's default handler.</summary>
public sealed class ShellLauncher(IPlatformInfo platform, ILogger<ShellLauncher> logger) : IShellLauncher
{
    public bool OpenFolder(string directory)
    {
        if (!Directory.Exists(directory)) return false;
        return Launch(platform.IsWindows ? "explorer.exe" : platform.IsMacOS ? "open" : "xdg-open", [directory]);
    }

    public bool ShowInFolder(string filePath)
    {
        if (!File.Exists(filePath)) return false;
        if (platform.IsWindows) return Launch("explorer.exe", [$"/select,{filePath}"]);
        if (platform.IsMacOS) return Launch("open", ["-R", filePath]);
        return OpenFolder(Path.GetDirectoryName(filePath)!);
    }

    public bool OpenUrl(string url)
    {
        // Only web links are ever handed to the shell; this blocks file:// and custom scheme tricks.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        return Launch(platform.IsWindows ? "explorer.exe" : platform.IsMacOS ? "open" : "xdg-open", [uri.AbsoluteUri]);
    }

    private bool Launch(string fileName, IReadOnlyList<string> args)
    {
        try
        {
            var info = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in args) info.ArgumentList.Add(arg);
            using var process = Process.Start(info);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not launch {FileName}", fileName);
            return false;
        }
    }
}
