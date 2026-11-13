using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Luma.App.Services;

/// <summary>
/// Optional memory sampling for performance work. Enabled with the environment variable LUMA_MEMLOG=1; it only reads
/// counters (it never forces a collection) and writes one log line every few seconds.
/// </summary>
public sealed class MemoryDiagnostics : IDisposable
{
    private readonly Timer? _timer;

    public MemoryDiagnostics(ImageLoader images, ILogger<MemoryDiagnostics> logger)
    {
        if (Environment.GetEnvironmentVariable("LUMA_MEMLOG") != "1") return;
        _timer = new Timer(_ =>
        {
            using var process = Process.GetCurrentProcess();
            var info = GC.GetGCMemoryInfo();
            logger.LogInformation(
                "MEM workingSet={WorkingSetMb:N0}MB private={PrivateMb:N0}MB gcHeap={HeapMb:N0}MB gcCommitted={CommittedMb:N0}MB gen0={Gen0} gen1={Gen1} gen2={Gen2} decodedThumbs={Thumbs} ({ThumbMb:N1}MB)",
                process.WorkingSet64 / 1048576d, process.PrivateMemorySize64 / 1048576d, GC.GetTotalMemory(false) / 1048576d,
                info.TotalCommittedBytes / 1048576d, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
                images.CachedCount, images.CachedBytes / 1048576d);
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4));
    }

    public void Dispose() => _timer?.Dispose();
}
