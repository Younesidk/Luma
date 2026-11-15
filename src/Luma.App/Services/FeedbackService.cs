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

    /// <summary>Discord webhook that receives feedback. Override with the LUMA_Luma__Feedback__WebhookUrl environment variable.</summary>
    public string WebhookUrl { get; set; } = string.Empty;
}

public enum FeedbackType
{
    Bug,
    FeatureRequest,
    Suggestion,
    Other
}

public sealed record FeedbackSubmission(FeedbackType Type, string Title, string Description);

/// <summary>Sends feedback to a Discord webhook as a single embed. Never throws: failures come back as a message for the user.</summary>
public sealed class FeedbackService
{
    public const string HttpClientName = "feedback";
    public const int MaxTitleLength = 120;
    public const int MaxDescriptionLength = 3000;

    private readonly IHttpClientFactory _httpFactory;
    private readonly LumaFeedbackOptions _options;
    private readonly ILogger<FeedbackService> _logger;
    private readonly string _version;

    public FeedbackService(IHttpClientFactory httpFactory, IOptions<LumaFeedbackOptions> options, ILogger<FeedbackService> logger)
    {
        _httpFactory = httpFactory;
        _options = options.Value;
        _logger = logger;
        _version = typeof(FeedbackService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";
    }

    public bool IsConfigured => Uri.TryCreate(_options.WebhookUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>Returns null on success, otherwise a short error to show the user.</summary>
    public async Task<string?> SendAsync(FeedbackSubmission feedback, CancellationToken ct = default)
    {
        if (!IsConfigured) return "Feedback isn't set up in this build.";

        var payload = BuildPayload(feedback, _version, RuntimeInformation.OSDescription);
        try
        {
            var client = _httpFactory.CreateClient(HttpClientName);
            using var response = await client.PostAsJsonAsync(_options.WebhookUrl, payload, ct);
            if (response.IsSuccessStatusCode) return null;
            _logger.LogWarning("Feedback webhook returned {Status}", (int)response.StatusCode);
            return (int)response.StatusCode == 429
                ? "Too much feedback at once. Try again in a minute."
                : "Couldn't send your feedback. Try again later.";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "Sending timed out. Check your connection and try again.";
        }
        catch (HttpRequestException ex)
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

    public static WebhookPayload BuildPayload(FeedbackSubmission feedback, string version, string os)
    {
        var title = Truncate(feedback.Title.Trim(), MaxTitleLength);
        var description = Truncate(feedback.Description.Trim(), MaxDescriptionLength);
        var color = feedback.Type switch
        {
            FeedbackType.Bug => 0xE5484D,
            FeedbackType.FeatureRequest => 0x3E63DD,
            FeedbackType.Suggestion => 0x30A46C,
            _ => 0x8B8D98
        };
        return new WebhookPayload(
            Username: "Luma Feedback",
            // Feedback text is user-written: never let it ping @everyone, roles or users.
            AllowedMentions: new AllowedMentions([]),
            Embeds:
            [
                new Embed(
                    Title: $"[{Label(feedback.Type)}] {title}",
                    Description: description,
                    Color: color,
                    Fields: [new EmbedField("Version", version, true), new EmbedField("OS", Truncate(os, 200), true)],
                    Timestamp: DateTimeOffset.UtcNow.ToString("O"))
            ]);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";

    public sealed record WebhookPayload(
        [property: JsonPropertyName("username")] string Username,
        [property: JsonPropertyName("allowed_mentions")] AllowedMentions AllowedMentions,
        [property: JsonPropertyName("embeds")] IReadOnlyList<Embed> Embeds);

    public sealed record AllowedMentions([property: JsonPropertyName("parse")] IReadOnlyList<string> Parse);

    public sealed record Embed(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("color")] int Color,
        [property: JsonPropertyName("fields")] IReadOnlyList<EmbedField> Fields,
        [property: JsonPropertyName("timestamp")] string Timestamp);

    public sealed record EmbedField(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("value")] string Value,
        [property: JsonPropertyName("inline")] bool Inline);
}
