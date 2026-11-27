using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Luma.App.Services;

public sealed class LumaFeedbackOptions
{
    public const string SectionName = "Luma:Feedback";

    /// <summary>
    /// The feedback relay (the "Luma Feedback Relay" n8n workflow, see relay/). It forwards to Discord, so no Discord
    /// webhook ever ships in the app. Empty disables feedback. Override with LUMA_Luma__Feedback__EndpointUrl.
    /// </summary>
    public string? EndpointUrl { get; set; }
}

public enum FeedbackType
{
    Bug,
    FeatureRequest,
    Suggestion,
    Other
}

public sealed record FeedbackSubmission(FeedbackType Type, string Title, string Description);

/// <summary>
/// Posts feedback as a small JSON document to the relay. Never throws: a missing URL or any network/server problem
/// comes back as a short message for the user.
/// </summary>
public sealed class FeedbackService
{
    public const string HttpClientName = "feedback";
    public const int MaxTitleLength = 120;

    /// <summary>The relay drops longer descriptions, so the form never lets users type more.</summary>
    public const int MaxDescriptionLength = 2000;

    private readonly IHttpClientFactory _httpFactory;
    private readonly Uri? _endpoint;
    private readonly ILogger<FeedbackService> _logger;
    private readonly string _version;

    public FeedbackService(IHttpClientFactory httpFactory, IOptions<LumaFeedbackOptions> options, ILogger<FeedbackService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
        _endpoint = ParseEndpoint(options.Value.EndpointUrl);
        _version = typeof(FeedbackService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";
    }

    /// <summary>False when no valid https relay URL is configured; the Feedback page then says it's unavailable.</summary>
    public bool IsConfigured => _endpoint is not null;

    /// <summary>Returns null on success, otherwise a short error to show the user.</summary>
    public async Task<string?> SendAsync(FeedbackSubmission feedback, CancellationToken ct = default)
    {
        if (_endpoint is null) return "Feedback isn't available in this build.";

        var payload = BuildPayload(feedback, _version, RuntimeInformation.OSDescription);
        try
        {
            var client = _httpFactory.CreateClient(HttpClientName);
            using var response = await client.PostAsJsonAsync(_endpoint, payload, ct);
            if (response.IsSuccessStatusCode) return null;
            _logger.LogWarning("Feedback relay returned {Status}", (int)response.StatusCode);
            return "Couldn't send your feedback. Try again later.";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "Sending timed out. Check your connection and try again.";
        }
        catch (Exception ex) when (ex is HttpRequestException or NotSupportedException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(ex, "Feedback could not be sent");
            return "Couldn't reach the server. Check your connection and try again.";
        }
    }

    public static string Label(FeedbackType type) => type switch
    {
        FeedbackType.Bug => "Bug report",
        FeedbackType.FeatureRequest => "Feature request",
        FeedbackType.Suggestion => "Suggestion",
        _ => "Other"
    };

    /// <summary>The <c>type</c> values the relay accepts.</summary>
    public static string WireType(FeedbackType type) => type switch
    {
        FeedbackType.Bug => "bug",
        FeedbackType.FeatureRequest => "feature_request",
        FeedbackType.Suggestion => "suggestion",
        _ => "other"
    };

    /// <summary>Blank, relative, or non-https values mean "not configured".</summary>
    public static Uri? ParseEndpoint(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri
            : null;

    public static FeedbackPayload BuildPayload(FeedbackSubmission feedback, string version, string os) =>
        new(WireType(feedback.Type),
            Truncate(feedback.Title.Trim(), MaxTitleLength),
            Truncate(feedback.Description.Trim(), MaxDescriptionLength),
            Truncate(version, 40),
            Truncate(os, 200));

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";

    /// <summary>What the relay expects. Discord formatting (embed, colours, mention blocking) happens in the relay.</summary>
    public sealed record FeedbackPayload(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("os")] string Os);
}
