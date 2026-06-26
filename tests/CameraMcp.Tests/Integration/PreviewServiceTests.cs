using System.Net;
using CameraMcp.Server;
using CameraMcp.Server.Configuration;
using CameraMcp.Server.Models;
using CameraMcp.Server.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CameraMcp.Tests.Integration;

/// <summary>
/// Exercises the real preview pipeline — a Kestrel route relaying a live ffmpeg MJPEG stream — using a
/// synthetic lavfi source (no camera) over the in-memory TestServer. The live test is skipped when ffmpeg
/// is unavailable.
/// </summary>
[Trait("Category", "Integration")]
public class PreviewServiceTests
{
    private static readonly IOptions<CameraMcpOptions> Cfg =
        Options.Create(new CameraMcpOptions { FFmpegTimeoutSeconds = 30 });

    private sealed record Scope(WebApplication App, HttpClient Client, IPreviewService Preview) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private static string? ResolveFfmpeg()
    {
        try { return new FFmpegLocator(Cfg).Resolve(); }
        catch (FFmpegNotFoundException) { return null; }
    }

    private static async Task<Scope> StartHostAsync(string ffmpegPath)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ICameraService, LavfiCameraService>();
        builder.Services.AddSingleton<IFFmpegLocator>(new StubLocator(ffmpegPath));
        builder.Services.AddSingleton<ITunnelLauncher, StubTunnel>();
        builder.Services.AddSingleton<IHttpHostInfo, StubHostInfo>();
        builder.Services.AddSingleton<IPreviewService, PreviewService>();

        var app = builder.Build();
        app.MapDeviceEndpoints();
        await app.StartAsync();
        return new Scope(app, app.GetTestClient(), app.Services.GetRequiredService<IPreviewService>());
    }

    [Fact]
    public async Task Preview_streams_live_mjpeg_over_a_route()
    {
        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null)
        {
            return; // ffmpeg unavailable: integration test not applicable.
        }

        await using var scope = await StartHostAsync(ffmpeg);
        var info = await scope.Preview.StartAsync(
            new PreviewOptions { Fps = 10, Quality = 70, Tunnel = TunnelProvider.None }, CancellationToken.None);
        Assert.StartsWith("prev_", info.PreviewId);
        Assert.Null(info.TunnelUrl);

        var streamPath = new Uri(info.StreamUrl).PathAndQuery;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await scope.Client.GetAsync(streamPath, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("multipart/x-mixed-replace", response.Content.Headers.ContentType!.ToString());

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        var buffer = new byte[64 * 1024];
        var total = 0;
        var foundJpeg = false;
        while (total < buffer.Length && !foundJpeg)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total), cts.Token);
            if (n <= 0) break;
            total += n;
            for (var i = 1; i < total; i++)
            {
                if (buffer[i - 1] == 0xFF && buffer[i] == 0xD8) { foundJpeg = true; break; }
            }
        }

        Assert.True(foundJpeg, "expected a JPEG start-of-image marker in the MJPEG stream");
        await scope.Preview.StopAsync(info.PreviewId);
    }

    [Fact]
    public async Task Stream_with_wrong_token_is_401()
    {
        var ffmpeg = ResolveFfmpeg() ?? "ffmpeg";
        await using var scope = await StartHostAsync(ffmpeg);
        var info = await scope.Preview.StartAsync(new PreviewOptions(), CancellationToken.None);

        var resp = await scope.Client.GetAsync($"/preview/{info.PreviewId}/stream?token=wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Stream_for_unknown_preview_is_404()
    {
        var ffmpeg = ResolveFfmpeg() ?? "ffmpeg";
        await using var scope = await StartHostAsync(ffmpeg);

        var resp = await scope.Client.GetAsync("/preview/prev_nope/stream?token=x");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Page_returns_viewer_html_with_the_stream_img()
    {
        var ffmpeg = ResolveFfmpeg() ?? "ffmpeg";
        await using var scope = await StartHostAsync(ffmpeg);
        var info = await scope.Preview.StartAsync(new PreviewOptions(), CancellationToken.None);

        var pagePath = new Uri(info.LocalUrl).PathAndQuery;
        var resp = await scope.Client.GetAsync(pagePath);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains($"/preview/{info.PreviewId}/stream?token=", html);
    }

    [Fact]
    public async Task StopAsync_returns_false_for_unknown_preview()
    {
        await using var scope = await StartHostAsync(ResolveFfmpeg() ?? "ffmpeg");
        Assert.False(await scope.Preview.StopAsync("prev_nope"));
    }

    /// <summary>A camera service that resolves to an ffmpeg lavfi test pattern instead of a real device.</summary>
    private sealed class LavfiCameraService : ICameraService
    {
        public Task<ResolvedCaptureInput> ResolveInputAsync(string? deviceId, int? width, int? height, int fps, CancellationToken cancellationToken) =>
            Task.FromResult(new ResolvedCaptureInput(
                "lavfi", "lavfi:test", ["-f", "lavfi", "-i", "testsrc=size=160x120:rate=10"], 160, 120));

        public Task<IAsyncDisposable> AcquireDeviceLockAsync(string lockKey, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable>(new NoopLock());

        private sealed class NoopLock : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        public Task<IReadOnlyList<CameraDevice>> ListDevicesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ImageCaptureResult> CaptureImageAsync(ImageCaptureOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<VideoCaptureResult> CaptureVideoAsync(VideoCaptureOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SceneCaptureResult> CaptureSceneAsync(SceneCaptureOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubLocator(string path) : IFFmpegLocator
    {
        public string Resolve() => path;
    }

    private sealed class StubTunnel : ITunnelLauncher
    {
        public Task<(TunnelHandle? Handle, TunnelProvider Effective, string? Note)> StartAsync(int port, TunnelProvider provider, CancellationToken cancellationToken) =>
            Task.FromResult<(TunnelHandle?, TunnelProvider, string?)>((null, TunnelProvider.None, null));
    }

    private sealed class StubHostInfo : IHttpHostInfo
    {
        public int Port => 5005;
        public string BaseUrl => "http://127.0.0.1:5005";
    }
}
