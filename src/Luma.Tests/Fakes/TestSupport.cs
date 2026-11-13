using System.Net;
using System.Text;
using Luma.Core.Interfaces;
using Luma.Core.Models;
using Luma.Infrastructure.Database;
using Luma.Infrastructure.Providers;
using Luma.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Luma.Tests.Fakes;

/// <summary>A throw-away data directory plus a migrated SQLite database for one test.</summary>
public sealed class TestEnvironment : IAsyncDisposable
{
    private TestEnvironment(string root, LumaPaths paths, IDbContextFactory<LumaDbContext> factory)
    {
        Root = root;
        Paths = paths;
        Factory = factory;
    }

    public string Root { get; }
    public LumaPaths Paths { get; }
    public IDbContextFactory<LumaDbContext> Factory { get; }

    public static async Task<TestEnvironment> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "luma-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = new LumaPaths(root);
        paths.EnsureCreated();
        var factory = new SimpleFactory(paths.DatabasePath);
        var initializer = new DatabaseInitializer(factory, paths, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();
        return new TestEnvironment(root, paths, factory);
    }

    public SettingsService CreateSettings() => new(Paths, NullLogger<SettingsService>.Instance);

    public async ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { /* temp files, best effort */ }
        await Task.CompletedTask;
    }

    private sealed class SimpleFactory(string dbPath) : IDbContextFactory<LumaDbContext>
    {
        public LumaDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<LumaDbContext>().UseSqlite($"Data Source={dbPath};Pooling=True").Options);
    }
}

public sealed class FakeSettings : ISettingsService
{
    public AppSettings Current { get; private set; } = new();

    public event EventHandler<AppSettings>? Changed;

    public string EffectiveDownloadDirectory => Current.DownloadDirectory ?? Path.Combine(Path.GetTempPath(), "luma-fake-downloads");

    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void Update(Action<AppSettings> mutate)
    {
        mutate(Current);
        Changed?.Invoke(this, Current);
    }

    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ResetAsync(CancellationToken cancellationToken = default)
    {
        Current = new AppSettings();
        return Task.CompletedTask;
    }
}

/// <summary>HTTP handler returning canned responses; records every request.</summary>
public sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    public static StubHandler Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(_ => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

    public static StubHandler Status(HttpStatusCode status, Action<HttpResponseMessage>? configure = null) =>
        new(_ =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent("{}") };
            configure?.Invoke(response);
            return response;
        });

    public static StubHandler Throwing(Exception exception) => new(_ => throw exception);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(responder(request));
    }
}

public sealed class StubHttpClientFactory(HttpMessageHandler handler, string baseAddress) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri(baseAddress) };
}

public static class Providers
{
    public static WallhavenProvider Wallhaven(StubHandler handler, FakeSettings? settings = null, TimeProvider? time = null) =>
        new(new StubHttpClientFactory(handler, "https://wallhaven.cc/api/v1/"), settings ?? new FakeSettings(),
            Options.Create(new LumaOptions()), NullLogger<WallhavenProvider>.Instance, time);

    public static OpenverseProvider Openverse(StubHandler handler, FakeSettings? settings = null, TimeProvider? time = null) =>
        new(new StubHttpClientFactory(handler, "https://api.openverse.org/v1/"), settings ?? new FakeSettings(),
            Options.Create(new LumaOptions()), NullLogger<OpenverseProvider>.Instance, time);
}

public static class Samples
{
    public static Wallpaper Wallpaper(string id = "abc123", string provider = "wallhaven", string? title = null) => new()
    {
        ProviderId = provider,
        ExternalId = id,
        Title = title,
        ThumbnailUrl = $"https://example.test/{id}-thumb.jpg",
        PreviewUrl = $"https://example.test/{id}-preview.jpg",
        FullUrl = $"https://example.test/{id}.jpg",
        SourcePageUrl = $"https://example.test/w/{id}",
        Resolution = new WallpaperResolution(1920, 1080),
        FileSizeBytes = 123456,
        FileType = "image/jpeg",
        Metadata = new WallpaperMetadata { CreatorName = "Tester", Tags = ["a", "b"], Colors = ["#112233"], LicenseName = "CC BY 4.0" }
    };

    /// <summary>A minimal valid PNG (1x1) so file validation accepts it.</summary>
    public static byte[] Png(int width = 1, int height = 1)
    {
        var bytes = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(bytes, 0);
        BitConverter.GetBytes(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(width)).CopyTo(bytes, 16);
        BitConverter.GetBytes(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(height)).CopyTo(bytes, 20);
        return bytes;
    }
}
