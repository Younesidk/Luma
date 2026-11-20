using System.Security.Cryptography;
using System.Text;

namespace Luma.App.Services;

/// <summary>
/// Keeps one Luma per installation and Windows session. Luma keeps running in the notification area after its window is
/// closed, so launching it again (Start menu, desktop shortcut) must bring the existing window back instead of starting
/// a second copy that would fight over the live wallpaper. A development build and the installed app use different
/// folders and therefore don't block each other.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showRequest;
    private RegisteredWaitHandle? _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle showRequest)
    {
        _mutex = mutex;
        _showRequest = showRequest;
    }

    /// <summary>
    /// Returns the guard when this is the first copy; otherwise asks the running copy to show its window and returns null
    /// (the caller should exit).
    /// </summary>
    public static SingleInstance? TryAcquire()
    {
        var id = InstanceId();
        var showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\Luma.App.{id}.Show");
        var mutex = new Mutex(false, $@"Local\Luma.App.{id}");
        bool owned;
        try
        {
            owned = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            owned = true; // the previous copy crashed
        }

        if (owned) return new SingleInstance(mutex, showRequest);

        showRequest.Set();
        showRequest.Dispose();
        mutex.Dispose();
        return null;
    }

    /// <summary>Calls <paramref name="onShowRequested"/> (on a thread-pool thread) whenever another launch asks for the window.</summary>
    public void Listen(Action onShowRequested) =>
        _registration = ThreadPool.RegisterWaitForSingleObject(_showRequest, (_, _) => onShowRequested(), null, Timeout.Infinite, executeOnlyOnce: false);

    private static string InstanceId()
    {
        var folder = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folder)))[..16];
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // not owned by this thread; closing the handle releases it
        }
        _mutex.Dispose();
        _showRequest.Dispose();
    }
}
