using System.Net;
using CameraMcp.Server.Models;
using CameraMcp.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace CameraMcp.Tests.Services;

public class CaptureSessionServiceTests
{
    private static CaptureSessionService NewService() =>
        new(new StubCamera(), new StubTunnel(), NullLogger<CaptureSessionService>.Instance);

    [Fact]
    public async Task Start_then_trigger_delivers_a_frame()
    {
        using var service = NewService();
        var info = await service.StartAsync(new SessionStartOptions { DeviceId = "cam0" }, CancellationToken.None);
        try
        {
            Assert.StartsWith("sess_", info.SessionId);
            Assert.Equal(service.CurrentSessionId, info.SessionId);
            Assert.Contains("/trigger?token=", info.TriggerUrl);

            using var http = new HttpClient();
            var response = await http.PostAsync(info.TriggerUrl, content: null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var frame = await service.AwaitNextAsync(info.SessionId, TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.NotNull(frame);
            Assert.Equal(1, frame!.Seq);
            Assert.Equal(640, frame.Result.Width);
        }
        finally
        {
            await service.StopAsync(info.SessionId);
        }
    }

    [Fact]
    public async Task Trigger_without_token_is_rejected()
    {
        using var service = NewService();
        var info = await service.StartAsync(new SessionStartOptions(), CancellationToken.None);
        try
        {
            var baseUrl = info.TriggerUrl[..info.TriggerUrl.IndexOf('?')];
            using var http = new HttpClient();
            var response = await http.PostAsync(baseUrl, content: null); // no token
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            await service.StopAsync(info.SessionId);
        }
    }

    [Fact]
    public async Task Get_session_returns_the_current_id()
    {
        using var service = NewService();
        var info = await service.StartAsync(new SessionStartOptions(), CancellationToken.None);
        try
        {
            var port = new Uri(info.TriggerUrl).Port;
            using var http = new HttpClient();
            var body = await http.GetStringAsync($"http://127.0.0.1:{port}/session?token={info.Token}");
            Assert.Contains(info.SessionId, body);
        }
        finally
        {
            await service.StopAsync(info.SessionId);
        }
    }

    [Fact]
    public async Task Await_times_out_to_null_when_no_trigger()
    {
        using var service = NewService();
        var info = await service.StartAsync(new SessionStartOptions(), CancellationToken.None);
        try
        {
            var frame = await service.AwaitNextAsync(info.SessionId, TimeSpan.FromMilliseconds(200), CancellationToken.None);
            Assert.Null(frame);
        }
        finally
        {
            await service.StopAsync(info.SessionId);
        }
    }

    [Fact]
    public async Task Stop_clears_the_session()
    {
        using var service = NewService();
        var info = await service.StartAsync(new SessionStartOptions(), CancellationToken.None);

        Assert.True(await service.StopAsync(info.SessionId));
        Assert.Null(service.CurrentSessionId);
        await Assert.ThrowsAsync<CaptureValidationException>(() =>
            service.AwaitNextAsync(info.SessionId, TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [Fact]
    public async Task Starting_a_new_session_replaces_the_old_one()
    {
        using var service = NewService();
        var first = await service.StartAsync(new SessionStartOptions(), CancellationToken.None);
        var second = await service.StartAsync(new SessionStartOptions(), CancellationToken.None);
        try
        {
            Assert.NotEqual(first.SessionId, second.SessionId);
            Assert.Equal(second.SessionId, service.CurrentSessionId);
            await Assert.ThrowsAsync<CaptureValidationException>(() =>
                service.AwaitNextAsync(first.SessionId, TimeSpan.FromSeconds(1), CancellationToken.None));
        }
        finally
        {
            await service.StopAsync(second.SessionId);
        }
    }

    private sealed class StubCamera : ICameraService
    {
        public Task<ResolvedCaptureInput> ResolveInputAsync(string? deviceId, int? width, int? height, int fps, CancellationToken cancellationToken) =>
            Task.FromResult(new ResolvedCaptureInput(deviceId ?? "cam", "lock:" + (deviceId ?? "cam"), [], 640, 480));

        public Task<ImageCaptureResult> CaptureImageAsync(ImageCaptureOptions options, CancellationToken cancellationToken) =>
            Task.FromResult(new ImageCaptureResult([1, 2, 3], ImageFormat.Jpeg, 640, 480, "cam", "/tmp/x.jpg"));

        public Task<IAsyncDisposable> AcquireDeviceLockAsync(string lockKey, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable>(new NoopLock());

        public Task<IReadOnlyList<CameraDevice>> ListDevicesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<VideoCaptureResult> CaptureVideoAsync(VideoCaptureOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SceneCaptureResult> CaptureSceneAsync(SceneCaptureOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        private sealed class NoopLock : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class StubTunnel : ITunnelLauncher
    {
        public Task<(TunnelHandle? Handle, TunnelProvider Effective, string? Note)> StartAsync(int port, TunnelProvider provider, CancellationToken cancellationToken) =>
            Task.FromResult<(TunnelHandle?, TunnelProvider, string?)>((null, TunnelProvider.None, "test: no tunnel"));
    }
}
