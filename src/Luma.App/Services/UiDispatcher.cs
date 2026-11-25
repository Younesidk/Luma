using Microsoft.UI.Dispatching;

namespace Luma.App.Services;

/// <summary>
/// <see cref="IUiDispatcher"/> backed by the WinUI <see cref="DispatcherQueue"/> of the UI thread. The queue is attached
/// once the UI thread exists (<see cref="Attach"/>); until then — and in tests, where there is no UI thread — work runs inline.
/// </summary>
public sealed class UiDispatcher : IUiDispatcher
{
    private DispatcherQueue? _queue;

    /// <summary>The UI thread's queue, or null before <see cref="Attach"/>.</summary>
    public DispatcherQueue? Queue => _queue;

    /// <summary>Binds the dispatcher to the calling thread's queue. Call on the UI thread during start-up.</summary>
    public void Attach(DispatcherQueue? queue = null) =>
        _queue = queue ?? DispatcherQueue.GetForCurrentThread()
                 ?? throw new InvalidOperationException("The calling thread has no DispatcherQueue.");

    public bool HasThreadAccess => _queue is null || _queue.HasThreadAccess;

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_queue is null)
        {
            action();
            return;
        }
        // TryEnqueue only fails while the queue is shutting down; the work is moot then.
        _queue.TryEnqueue(() => action());
    }

    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = _queue!.TryEnqueue(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        if (!queued) tcs.TrySetCanceled();
        return tcs.Task;
    }
}
