namespace Luma.Core.Logic;

/// <summary>Pure selection logic for automatic wallpaper rotation.</summary>
public static class RotationSelector
{
    /// <summary>
    /// Chooses the next item. Random mode avoids <paramref name="currentKey"/> whenever another candidate exists;
    /// sequential mode walks the (stable ordered) list and wraps around.
    /// </summary>
    public static T? Pick<T>(IReadOnlyList<T> candidates, Func<T, string> keyOf, string? currentKey, bool random, Random? rng = null)
        where T : class
    {
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];

        if (random)
        {
            rng ??= Random.Shared;
            var pool = candidates.Where(c => !string.Equals(keyOf(c), currentKey, StringComparison.Ordinal)).ToList();
            if (pool.Count == 0) pool = candidates.ToList();
            return pool[rng.Next(pool.Count)];
        }

        var ordered = candidates.OrderBy(keyOf, StringComparer.Ordinal).ToList();
        var index = currentKey is null ? -1 : ordered.FindIndex(c => string.Equals(keyOf(c), currentKey, StringComparison.Ordinal));
        return ordered[(index + 1) % ordered.Count];
    }

    /// <summary>Time until the next rotation given the last one; never negative.</summary>
    public static TimeSpan DelayUntilNext(DateTimeOffset? lastRotation, TimeSpan interval, DateTimeOffset now)
    {
        if (lastRotation is null) return interval;
        var due = lastRotation.Value + interval;
        var delay = due - now;
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }
}
