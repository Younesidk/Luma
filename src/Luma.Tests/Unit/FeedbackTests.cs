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

    private static FeedbackService Service(StubHandler handler, string? url = "https://relay.example/webhook/luma-feedback") =>
        new(new StubFactory(handler), Options.Create(new LumaFeedbackOptions { EndpointUrl = url }), NullLogger<FeedbackService>.Instance);

    [Fact]
    public void Feedback_page_highlights_the_feedback_sidebar_item()
    {
        var vm = new FeedbackViewModel(Service(new StubHandler(HttpStatusCode.NoContent)), new NullToasts());
        Assert.Equal("feedback", ShellViewModel.ActiveNavId(vm));
    }

    [Fact]
    public void Payload_matches_the_relay_schema()
    {
        var payload = FeedbackService.BuildPayload(new FeedbackSubmission(FeedbackType.FeatureRequest, "  Dark mode  ", " please "), "2.0.0", "Windows 11");
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        Assert.Equal("""{"type":"feature_request","title":"Dark mode","description":"please","version":"2.0.0","os":"Windows 11"}""", json);
    }

    [Fact]
    public void Long_text_is_truncated_to_what_the_relay_accepts()
    {
        var payload = FeedbackService.BuildPayload(new FeedbackSubmission(FeedbackType.Other, new string('t', 500), new string('d', 9000)), "1", "os");
        Assert.True(payload.Description.Length <= 2000);
        Assert.True(payload.Title.Length <= 120);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("http://relay.example/insecure")]
    public async Task Blank_or_invalid_endpoint_disables_feedback_without_sending(string? url)
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var service = Service(handler, url);
        Assert.False(service.IsConfigured);
        Assert.NotNull(await service.SendAsync(new FeedbackSubmission(FeedbackType.Bug, "a", "b")));
        Assert.Empty(handler.Bodies);

        var vm = new FeedbackViewModel(service, new NullToasts()) { FeedbackTitle = "Title", Description = "Details" };
        Assert.False(vm.SubmitCommand.CanExecute(null));
        Assert.NotNull(vm.Error);
    }

    [Fact]
    public async Task Network_failure_is_reported_not_thrown()
    {
        var service = new FeedbackService(new StubFactory(new ThrowingHandler()),
            Options.Create(new LumaFeedbackOptions { EndpointUrl = "https://relay.example/x" }), NullLogger<FeedbackService>.Instance);
        Assert.NotNull(await service.SendAsync(new FeedbackSubmission(FeedbackType.Bug, "a", "b")));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("offline");
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
        Assert.Contains("\"type\":\"bug\",\"title\":\"Title\"", handler.Bodies[0]);
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
