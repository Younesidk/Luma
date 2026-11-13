using System.Globalization;
using System.Net;
using System.Text.Json;
using Luma.Core.Enums;
using Luma.Core.Exceptions;
using Microsoft.Extensions.Logging;

namespace Luma.Infrastructure.Providers;

public sealed class LumaOptions
{
    public const string SectionName = "Luma";

    public string UserAgent { get; set; } = "Luma/1.0 (desktop wallpaper manager)";
    public int ApiTimeoutSeconds { get; set; } = 20;
    public int ThumbnailConcurrency { get; set; } = 6;
    public WallhavenOptions Wallhaven { get; set; } = new();
    public OpenverseOptions Openverse { get; set; } = new();
}

public sealed class WallhavenOptions
{
    public string BaseUrl { get; set; } = "https://wallhaven.cc/api/v1/";

    /// <summary>Wallhaven allows 45 calls per minute; stay a little below.</summary>
    public int MaxRequestsPerMinute { get; set; } = 40;
}

public sealed class OpenverseOptions
{
    public string BaseUrl { get; set; } = "https://api.openverse.org/v1/";

    /// <summary>Anonymous clients are limited to 20 requests per minute and 200 per day.</summary>
    public int MaxRequestsPerMinute { get; set; } = 18;
}

/// <summary>
/// Client-side limiter so Luma never hammers a provider: a sliding window plus a "blocked until" gate that is
/// armed whenever the server answers HTTP 429.
/// </summary>
public sealed class RequestGate(string providerId, int maxRequests, TimeSpan window, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Queue<DateTimeOffset> _stamps = new();
    private readonly object _lock = new();
    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;

    /// <summary>Maximum time a request may wait for a slot before it is rejected instead.</summary>
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromSeconds(8);

    public DateTimeOffset BlockedUntil => _blockedUntil;

    public void BlockFor(TimeSpan duration)
    {
        lock (_lock) _blockedUntil = _time.GetUtcNow() + duration;
    }

    public async Task EnterAsync(CancellationToken cancellationToken)
    {
        TimeSpan wait;
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (_blockedUntil > now)
            {
                var remaining = _blockedUntil - now;
                throw new ProviderException(providerId, ProviderErrorKind.RateLimited,
                    $"Rate limit reached. Try again in {FormatWait(remaining)}.", retryAfter: remaining);
            }

            while (_stamps.Count > 0 && now - _stamps.Peek() >= window) _stamps.Dequeue();

            if (_stamps.Count < maxRequests)
            {
                _stamps.Enqueue(now);
                return;
            }

            wait = _stamps.Peek() + window - now;
            if (wait > MaxWait)
            {
                throw new ProviderException(providerId, ProviderErrorKind.RateLimited,
                    $"Too many requests in a short time. Try again in {FormatWait(wait)}.", retryAfter: wait);
            }
        }

        await Task.Delay(wait, _time, cancellationToken);
        await EnterAsync(cancellationToken);
    }

    private static string FormatWait(TimeSpan span) =>
        span.TotalSeconds < 90 ? $"{Math.Max(1, (int)Math.Ceiling(span.TotalSeconds))} seconds" : $"{(int)Math.Ceiling(span.TotalMinutes)} minutes";
}

/// <summary>Shared request pipeline: rate gate, error classification and tolerant JSON parsing.</summary>
internal static class ProviderHttp
{
    public static async Task<JsonDocument> GetJsonAsync(HttpClient client, HttpRequestMessage request, RequestGate gate,
        string providerId, ILogger logger, Action<HttpResponseMessage>? onResponse, CancellationToken ct)
    {
        await gate.EnterAsync(ct);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException(providerId, ProviderErrorKind.Timeout, "The server took too long to respond.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Network failure talking to {Provider}", providerId);
            throw new ProviderException(providerId, ProviderErrorKind.Network,
                "Couldn't reach the server. Check your internet connection.", ex);
        }

        using (response)
        {
            onResponse?.Invoke(response);

            if (!response.IsSuccessStatusCode)
            {
                throw Classify(providerId, response, gate, logger);
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "{Provider} returned invalid JSON", providerId);
                throw new ProviderException(providerId, ProviderErrorKind.InvalidResponse,
                    "The server sent a response Luma couldn't understand.", ex);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ProviderException(providerId, ProviderErrorKind.Timeout, "The server took too long to respond.");
            }
            catch (HttpRequestException ex)
            {
                throw new ProviderException(providerId, ProviderErrorKind.Network,
                    "The connection was interrupted while loading results.", ex);
            }
        }
    }

    private static ProviderException Classify(string providerId, HttpResponseMessage response, RequestGate gate, ILogger logger)
    {
        var status = response.StatusCode;
        logger.LogWarning("{Provider} responded with HTTP {Status}", providerId, (int)status);

        if (status == HttpStatusCode.TooManyRequests)
        {
            var retry = ParseRetryAfter(response) ?? TimeSpan.FromSeconds(60);
            gate.BlockFor(retry);
            return new ProviderException(providerId, ProviderErrorKind.RateLimited,
                $"Rate limit reached. Try again in about {Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds))} seconds.", retryAfter: retry);
        }

        return status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new ProviderException(providerId, ProviderErrorKind.Unauthorized,
                "The provider rejected the request. Check your API credentials in Settings."),
            HttpStatusCode.NotFound => new ProviderException(providerId, ProviderErrorKind.NotFound, "That wallpaper could not be found."),
            >= HttpStatusCode.InternalServerError => new ProviderException(providerId, ProviderErrorKind.ServerError,
                "The provider is having problems right now. Please try again in a moment."),
            _ => new ProviderException(providerId, ProviderErrorKind.Unknown, $"The provider returned an unexpected error ({(int)status}).")
        };
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta;
        if (header?.Date is { } date)
        {
            var span = date - DateTimeOffset.UtcNow;
            return span > TimeSpan.Zero ? span : TimeSpan.FromSeconds(1);
        }
        return null;
    }
}

/// <summary>Tolerant accessors: a missing or oddly typed field yields null instead of throwing.</summary>
internal static class JsonExtensions
{
    public static JsonElement? Get(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value
            : null;

    public static string? GetString(this JsonElement element, string name)
    {
        var value = element.Get(name);
        if (value is null) return null;
        var text = value.Value.ValueKind switch
        {
            JsonValueKind.String => value.Value.GetString(),
            JsonValueKind.Number => value.Value.GetRawText(),
            _ => null
        };
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public static long? GetLong(this JsonElement element, string name)
    {
        var value = element.Get(name);
        if (value is null) return null;
        if (value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetInt64(out var n)) return n;
        if (value.Value.ValueKind == JsonValueKind.String && long.TryParse(value.Value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)) return s;
        return null;
    }

    public static int? GetInt(this JsonElement element, string name) => element.GetLong(name) is { } l && l is >= int.MinValue and <= int.MaxValue ? (int)l : null;

    public static IEnumerable<JsonElement> GetArray(this JsonElement element, string name)
    {
        var value = element.Get(name);
        return value is { ValueKind: JsonValueKind.Array } ? value.Value.EnumerateArray() : [];
    }
}
