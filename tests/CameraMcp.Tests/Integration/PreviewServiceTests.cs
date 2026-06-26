using System.Net;
using System.Text;
using CameraMcp.Server.Configuration;
using CameraMcp.Server.Models;
using CameraMcp.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CameraMcp.Tests.Integration;

/// <summary>
/// Exercises the real preview pipeline — HttpListener relaying a live ffmpeg MJPEG stream — using a
/// synthetic lavfi source (no camera). Skipped when ffmpeg is unavailable.
/// </summary>
[Trait("Category", "Integration")]
public class PreviewServiceTests
{
    private static readonly IOptions<CameraMcpOptions> Options =
        Microsoft.Extensions.Options.Options.Create(new CameraMcpOptions { FFmpegTimeoutSeconds = 30 });

    [Fact]
    public async Task Preview_streams_live_mjpeg_over_http()
    {
        FFmpegLocator locator;
        try
        {
            locator = new FFmpegLocator(Options);
            locator.Resolve();
        }
        catch (FFmpegNotFoundException)
        {
            return; // ffmpeg unavailable: integration test not applicable.
        }

        var preview = new PreviewService(
            new LavfiCameraService(), locator, new TunnelLauncher(), NullLogger<PreviewService>.Instance);

        try
        {
            var info = await preview.StartAsync(
                new PreviewOptions { Fps = 10, Quality = 70, Tunnel = TunnelProvider.None }, CancellationToken.None);

            Assert.True(preview.IsRunning);
            Assert.Null(info.TunnelUrl);                       // no tunnel requested
            Assert.Contains("127.0.0.1", info.StreamUrl);

            using var http = new HttpClient();
            using var response = await http.GetAsync(info.StreamUrl, HttpCompletionOption.ResponseHeadersRead);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.StartsWith("multipart/x-mixed-replace", response.Content.Headers.ContentType!.ToString());

            await using var stream = await response.Content.ReadAsStreamAsync();
            var buffer = new byte[16 * 1024];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var read = await stream.ReadAtLeastAsync(buffer, 1024, throwOnEndOfStream: false, cts.Token);

            var head = Encoding.ASCII.GetString(buffer, 0, read);
            Assert.Contains("--" + FFmpegArguments.MjpegBoundary, head); // multipart boundary
            Assert.Contains("image/jpeg", head);                          // a JPEG part
        }
        finally
        {
            await preview.StopAsync();
            preview.Dispose();
            Assert.False(preview.IsRunning);
        }
    }

    [Fact]
    public async Task StopAsync_returns_false_when_nothing_is_running()
    {
        var preview = new PreviewService(
            new LavfiCameraService(), new FFmpegLocator(Options), new TunnelLauncher(), NullLogger<PreviewService>.Instance);
        Assert.False(await preview.StopAsync());
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

        public Task<IReadOnlyList<CameraDevice>> ListDevicesAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<ImageCaptureResult> CaptureImageAsync(ImageCaptureOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<VideoCaptureResult> CaptureVideoAsync(VideoCaptureOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<SceneCaptureResult> CaptureSceneAsync(SceneCaptureOptions options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
