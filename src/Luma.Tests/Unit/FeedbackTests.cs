using System.Net;
using System.Net.Http;
using Luma.App.Services;
using Luma.App.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Luma.Tests.Unit;

public class FeedbackTests
{
    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(status);
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class NullToasts : IToastService
    {
        public void Show(string message, ToastKind kind = ToastKind.Info, string? actionText = null, Action? action = null) { }
    }

    private static FeedbackService Service(StubHandler handler, string url = "https://discord.example/api/webhooks/1/x") =>
        new(new StubFactory(handler), Options.Create(new LumaFeedbackOptions { WebhookUrl = url }), NullLogger<FeedbackService>.Instance);

    [Fact]
    public void Feedback_page_highlights_the_feedback_sidebar_item()
    {
        var vm = new FeedbackViewModel(Service(new StubHandler(HttpStatusCode.NoContent)), new NullToasts());
        Assert.Equal("feedback", ShellViewModel.ActiveNavId(vm));
    }

    [Fact]
    public void Payload_has_type_in_title_and_blocks_mentions()
    {
        var payload = FeedbackService.BuildPayload(new FeedbackSubmission(FeedbackType.Bug, "  Crash  ", "@everyone it broke"), "1.1.0", "Windows");
        Assert.Empty(payload.AllowedMentions.Parse);
        var embed = Assert.Single(payload.Embeds);
        Assert.Equal("[Bug report] Crash", embed.Title);
        Assert.Equal("@everyone it broke", embed.Description);
        Assert.Contains(embed.Fields, f => f.Name == "Version" && f.Value == "1.1.0");
    }

    [Fact]
    public void Long_text_is_truncated_to_discord_safe_lengths()
    {
        var payload = FeedbackService.BuildPayload(new FeedbackSubmission(FeedbackType.Other, new string('t', 500), new string('d', 9000)), "1", "os");
        Assert.True(payload.Embeds[0].Description.Length <= FeedbackService.MaxDescriptionLength);
        Assert.True(payload.Embeds[0].Title.Length <= FeedbackService.MaxTitleLength + 20);
    }

    [Fact]
    public async Task Missing_webhook_reports_an_error_without_sending()
    {
        var handler = new StubHandler(HttpStatusCode.NoContent);
        Assert.NotNull(await Service(handler, url: "").SendAsync(new FeedbackSubmission(FeedbackType.Bug, "a", "b")));
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task Server_errors_are_reported()
    {
        var error = await Service(new StubHandler(HttpStatusCode.InternalServerError)).SendAsync(new FeedbackSubmission(FeedbackType.Bug, "a", "b"));
        Assert.NotNull(error);
    }

    [Fact]
    public async Task Successful_submit_clears_the_form_and_enforces_a_cooldown()
    {
        var handler = new StubHandler(HttpStatusCode.NoContent);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var vm = new FeedbackViewModel(Service(handler), new NullToasts(), time);

        Assert.False(vm.SubmitCommand.CanExecute(null));
        vm.FeedbackTitle = "Title";
        vm.Description = "Details";
        Assert.True(vm.SubmitCommand.CanExecute(null));

        await vm.SubmitCommand.ExecuteAsync(null);
        Assert.Single(handler.Bodies);
        Assert.Contains("[Bug report] Title", handler.Bodies[0]);
        Assert.Equal(string.Empty, vm.FeedbackTitle);
        Assert.Null(vm.Error);

        vm.FeedbackTitle = "Again";
        vm.Description = "More";
        await vm.SubmitCommand.ExecuteAsync(null);
        Assert.Single(handler.Bodies);
        Assert.NotNull(vm.Error);

        time.Advance(TimeSpan.FromSeconds(31));
        await vm.SubmitCommand.ExecuteAsync(null);
        Assert.Equal(2, handler.Bodies.Count);
    }
}
