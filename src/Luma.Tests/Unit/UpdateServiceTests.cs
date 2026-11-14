using System.Net.Http;
using Luma.App.Services.Updates;
using Luma.App.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Luma.Tests.Unit;

public class UpdateVersioningTests
{
    [Theory]
    [InlineData("1.1.0", "1.0.0", true)]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("1.10.0", "1.9.0", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("0.9.0", "1.0.0", false)]
    [InlineData("1.1.0-beta.1", "1.0.0", false)]
    [InlineData("not-a-version", "1.0.0", false)]
    [InlineData("1.1.0", null, false)]
    [InlineData(null, "1.0.0", false)]
    public void Only_newer_stable_versions_are_offered(string? candidate, string? current, bool expected) =>
        Assert.Equal(expected, UpdateVersioning.IsNewerStable(candidate, current));
}

public class UpdateServiceTests
{
    private static UpdateService Create(FakeUpdateClient client, bool enabled = true) =>
        new(client, Options.Create(new LumaUpdateOptions { Enabled = enabled, CheckTimeoutSeconds = 5 }), NullLogger<UpdateService>.Instance);

    [Fact]
    public async Task Development_builds_are_unsupported_and_never_call_the_client()
    {
        var client = new FakeUpdateClient { IsSupported = false };
        using var service = Create(client);

        Assert.Equal(UpdateStatus.Unsupported, service.Current.Status);
        Assert.Equal(UpdateCheckOutcome.Unsupported, await service.CheckAsync());
        Assert.False(await service.DownloadAsync());
        Assert.Equal(0, client.CheckCalls);
    }

    [Fact]
    public async Task Disabled_updates_do_nothing()
    {
        var client = new FakeUpdateClient();
        using var service = Create(client, enabled: false);

        Assert.Equal(UpdateCheckOutcome.Disabled, await service.CheckAsync());
        Assert.Equal(0, client.CheckCalls);
    }

    [Fact]
    public async Task No_release_means_up_to_date()
    {
        var client = new FakeUpdateClient { NextRelease = null };
        using var service = Create(client);

        Assert.Equal(UpdateCheckOutcome.UpToDate, await service.CheckAsync());
        Assert.Equal(UpdateStatus.UpToDate, service.Current.Status);
        Assert.Null(service.Current.Release);
        Assert.NotNull(service.Current.LastCheckedAt);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("0.9.0")]
    [InlineData("1.2.0-rc.1")]
    public async Task Same_older_or_prerelease_versions_are_treated_as_up_to_date(string version)
    {
        var client = new FakeUpdateClient { NextRelease = new UpdateRelease(version, null, 10) };
        using var service = Create(client);

        Assert.Equal(UpdateCheckOutcome.UpToDate, await service.CheckAsync());
        Assert.Null(service.Current.Release);
    }

    [Fact]
    public async Task Newer_release_is_reported_with_its_notes()
    {
        var client = new FakeUpdateClient { NextRelease = new UpdateRelease("1.1.0", "## Fixes\n- Faster", 42) };
        using var service = Create(client);
        var states = new List<UpdateStatus>();
        service.Changed += (_, s) => states.Add(s.Status);

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, await service.CheckAsync());
        Assert.Equal(UpdateStatus.Available, service.Current.Status);
        Assert.Equal("1.1.0", service.Current.Release!.Version);
        Assert.Equal("## Fixes\n- Faster", service.Current.Release.ReleaseNotes);
        Assert.Equal([UpdateStatus.Checking, UpdateStatus.Available], states);
    }

    [Fact]
    public async Task Network_failure_becomes_a_failed_state_and_never_throws()
    {
        var client = new FakeUpdateClient { CheckException = new HttpRequestException("No such host is known.") };
        using var service = Create(client);

        Assert.Equal(UpdateCheckOutcome.Failed, await service.CheckAsync());
        Assert.Equal(UpdateStatus.Failed, service.Current.Status);
        Assert.Equal(UpdateOperation.Check, service.Current.FailedOperation);
        Assert.Contains("internet connection", service.Current.Error);
    }

    [Fact]
    public async Task Unexpected_failure_is_reported_generically()
    {
        var client = new FakeUpdateClient { CheckException = new InvalidDataException("releases.win.json is malformed") };
        using var service = Create(client);

        Assert.Equal(UpdateCheckOutcome.Failed, await service.CheckAsync());
        Assert.Equal("Luma couldn't check for updates right now. Try again later.", service.Current.Error);
    }

    [Fact]
    public async Task A_failed_check_can_be_followed_by_a_successful_one()
    {
        var client = new FakeUpdateClient { CheckException = new HttpRequestException("offline") };
        using var service = Create(client);
        await service.CheckAsync();

        client.CheckException = null;
        client.NextRelease = new UpdateRelease("1.1.0", null, 1);

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, await service.CheckAsync());
        Assert.Null(service.Current.Error);
    }

    [Fact]
    public async Task Cancelling_a_check_restores_the_previous_state()
    {
        var client = new FakeUpdateClient { CheckGate = new TaskCompletionSource() };
        using var service = Create(client);
        using var cts = new CancellationTokenSource();

        var check = service.CheckAsync(cancellationToken: cts.Token);
        await cts.CancelAsync();

        Assert.Equal(UpdateCheckOutcome.Cancelled, await check);
        Assert.Equal(UpdateStatus.Idle, service.Current.Status);
    }

    [Fact]
    public async Task Concurrent_checks_do_not_overlap()
    {
        var client = new FakeUpdateClient { CheckGate = new TaskCompletionSource(), NextRelease = new UpdateRelease("1.1.0", null, 1) };
        using var service = Create(client);

        var first = service.CheckAsync();
        var second = await service.CheckAsync();
        var download = await service.DownloadAsync();
        client.CheckGate.SetResult();

        Assert.Equal(UpdateCheckOutcome.Busy, second);
        Assert.False(download);
        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, await first);
        Assert.Equal(1, client.CheckCalls);
    }

    [Fact]
    public async Task Check_is_refused_while_a_download_is_running()
    {
        var client = new FakeUpdateClient { NextRelease = new UpdateRelease("1.1.0", null, 1), DownloadGate = new TaskCompletionSource() };
        using var service = Create(client);
        await service.CheckAsync();

        var download = service.DownloadAsync();
        Assert.Equal(UpdateStatus.Downloading, service.Current.Status);
        Assert.Equal(UpdateCheckOutcome.Busy, await service.CheckAsync());

        client.DownloadGate.SetResult();
        Assert.True(await download);
        Assert.Equal(1, client.CheckCalls);
    }

    [Fact]
    public async Task Download_reports_progress_and_ends_ready_to_restart()
    {
        var client = new FakeUpdateClient { NextRelease = new UpdateRelease("1.1.0", null, 1), ProgressSteps = [10, 10, 55, 100] };
        using var service = Create(client);
        await service.CheckAsync();
        var progress = new List<int>();
        service.Changed += (_, s) => { if (s.Status == UpdateStatus.Downloading) progress.Add(s.Progress); };

        Assert.True(await service.DownloadAsync());

        Assert.Equal(UpdateStatus.ReadyToRestart, service.Current.Status);
        Assert.Equal(100, service.Current.Progress);
        Assert.Equal([0, 10, 55, 100], progress); // duplicate 10 is collapsed
    }

    [Fact]
    public async Task Download_without_an_available_release_does_nothing()
    {
        var client = new FakeUpdateClient();
        using var service = Create(client);

        Assert.False(await service.DownloadAsync());
        Assert.Equal(0, client.DownloadCalls);
    }

    [Fact]
    public async Task Failed_or_corrupt_download_is_reported_and_can_be_retried()
    {
        var client = new FakeUpdateClient
        {
            NextRelease = new UpdateRelease("1.1.0", null, 1),
            DownloadException = new InvalidDataException("SHA256 checksum mismatch")
        };
        using var service = Create(client);
        await service.CheckAsync();

        Assert.False(await service.DownloadAsync());
        Assert.Equal(UpdateStatus.Failed, service.Current.Status);
        Assert.Equal(UpdateOperation.Download, service.Current.FailedOperation);
        Assert.Contains("verification", service.Current.Error);
        Assert.Equal("1.1.0", service.Current.Release!.Version);

        client.DownloadException = null;
        Assert.True(await service.DownloadAsync());
        Assert.Equal(UpdateStatus.ReadyToRestart, service.Current.Status);
    }

    [Fact]
    public async Task Cancelling_a_download_returns_to_available()
    {
        var client = new FakeUpdateClient { NextRelease = new UpdateRelease("1.1.0", null, 1), DownloadGate = new TaskCompletionSource() };
        using var service = Create(client);
        await service.CheckAsync();

        var download = service.DownloadAsync();
        service.CancelDownload();

        Assert.False(await download);
        Assert.Equal(UpdateStatus.Available, service.Current.Status);
    }

    [Fact]
    public async Task Already_downloaded_update_is_ready_at_start_and_checks_keep_it()
    {
        var client = new FakeUpdateClient { PendingRestart = new UpdateRelease("1.1.0", null, 1) };
        using var service = Create(client);

        Assert.Equal(UpdateStatus.ReadyToRestart, service.Current.Status);
        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, await service.CheckAsync());
        Assert.Equal(0, client.CheckCalls);
        Assert.True(await service.DownloadAsync());
        Assert.Equal(0, client.DownloadCalls);
    }

    [Fact]
    public async Task Restart_hands_over_to_the_updater_and_requests_shutdown()
    {
        var client = new FakeUpdateClient { NextRelease = new UpdateRelease("1.1.0", null, 1) };
        using var service = Create(client);
        var shutdownRequested = false;
        service.RestartRequested += (_, _) => shutdownRequested = true;

        Assert.False(service.RestartToApply()); // nothing downloaded yet
        await service.CheckAsync();
        await service.DownloadAsync();

        Assert.True(service.RestartToApply());
        Assert.Equal("1.1.0", client.AppliedVersion);
        Assert.True(shutdownRequested);
        Assert.Equal(UpdateStatus.Restarting, service.Current.Status);
    }

    [Fact]
    public async Task Updater_failing_to_start_keeps_luma_running()
    {
        var client = new FakeUpdateClient { NextRelease = new UpdateRelease("1.1.0", null, 1), ApplyException = new IOException("Update.exe missing") };
        using var service = Create(client);
        var shutdownRequested = false;
        service.RestartRequested += (_, _) => shutdownRequested = true;
        await service.CheckAsync();
        await service.DownloadAsync();

        Assert.False(service.RestartToApply());
        Assert.False(shutdownRequested);
        Assert.Equal(UpdateOperation.Apply, service.Current.FailedOperation);
    }

    [Fact]
    public async Task Postponing_hides_the_offer_until_a_manual_check()
    {
        var client = new FakeUpdateClient { NextRelease = new UpdateRelease("1.1.0", null, 1) };
        using var service = Create(client);
        await service.CheckAsync();

        service.Postpone();
        Assert.True(service.IsPostponed);

        await service.CheckAsync(userInitiated: false);
        Assert.True(service.IsPostponed);

        await service.CheckAsync(userInitiated: true);
        Assert.False(service.IsPostponed);
    }

    [Fact]
    public async Task A_newer_release_than_the_postponed_one_is_offered_again()
    {
        var client = new FakeUpdateClient { NextRelease = new UpdateRelease("1.1.0", null, 1) };
        using var service = Create(client);
        await service.CheckAsync();
        service.Postpone();

        client.NextRelease = new UpdateRelease("1.2.0", null, 1);
        await service.CheckAsync();

        Assert.False(service.IsPostponed);
    }

    [Fact]
    public void Failure_messages_distinguish_connectivity_from_other_errors()
    {
        Assert.Contains("internet", UpdateService.DescribeFailure(new HttpRequestException("x"), UpdateOperation.Check));
        Assert.Contains("interrupted", UpdateService.DescribeFailure(new IOException("x", new System.Net.Sockets.SocketException()), UpdateOperation.Download));
        Assert.Contains("updater", UpdateService.DescribeFailure(new IOException("x"), UpdateOperation.Apply));
    }

    [Fact]
    public void GitHub_repository_urls_are_recognised()
    {
        Assert.True(VelopackUpdateClient.IsGitHubRepository("https://github.com/Younesidk/Luma"));
        Assert.False(VelopackUpdateClient.IsGitHubRepository(@"C:\Releases"));
        Assert.False(VelopackUpdateClient.IsGitHubRepository("http://github.com/Younesidk/Luma"));
        Assert.False(VelopackUpdateClient.IsGitHubRepository("https://example.com/releases"));
    }

    [Fact]
    public void Release_notes_are_converted_to_plain_text()
    {
        var text = UpdateViewModel.FormatReleaseNotes("## What's new\r\n\r\n- **Faster** search\n* `Fixed` crash\n\n\nThanks!");
        Assert.Equal("What's new\n\n•  Faster search\n•  Fixed crash\n\nThanks!", text);
        Assert.Null(UpdateViewModel.FormatReleaseNotes("   \n "));
    }
}

/// <summary>Scriptable stand-in for Velopack, so update tests never touch the network or GitHub.</summary>
internal sealed class FakeUpdateClient : IUpdateClient
{
    public bool IsSupported { get; set; } = true;
    public string? CurrentVersion { get; set; } = "1.0.0";
    public UpdateRelease? PendingRestart { get; set; }

    public UpdateRelease? NextRelease { get; set; }
    public Exception? CheckException { get; set; }
    public TaskCompletionSource? CheckGate { get; set; }
    public int CheckCalls { get; private set; }

    public Exception? DownloadException { get; set; }
    public TaskCompletionSource? DownloadGate { get; set; }
    public int[] ProgressSteps { get; set; } = [100];
    public int DownloadCalls { get; private set; }

    public Exception? ApplyException { get; set; }
    public string? AppliedVersion { get; private set; }

    public async Task<UpdateRelease?> CheckForUpdateAsync(CancellationToken cancellationToken)
    {
        CheckCalls++;
        if (CheckGate is not null) await CheckGate.Task.WaitAsync(cancellationToken);
        if (CheckException is not null) throw CheckException;
        return NextRelease;
    }

    public async Task DownloadAsync(UpdateRelease release, Action<int> progress, CancellationToken cancellationToken)
    {
        DownloadCalls++;
        if (DownloadGate is not null) await DownloadGate.Task.WaitAsync(cancellationToken);
        if (DownloadException is not null) throw DownloadException;
        foreach (var step in ProgressSteps) progress(step);
    }

    public void ApplyOnExitAndRestart(UpdateRelease release)
    {
        if (ApplyException is not null) throw ApplyException;
        AppliedVersion = release.Version;
    }
}
