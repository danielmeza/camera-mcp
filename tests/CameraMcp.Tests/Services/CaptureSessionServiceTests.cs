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

            var capture = await service.AwaitNextAsync(info.SessionId, TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.NotNull(capture);
            Assert.Equal(1, capture!.Seq);
            Assert.False(capture.IsBurst);
            Assert.Equal(640, capture.Still!.Width);
        }
        finally
        {
            await service.StopAsync(info.SessionId);
        }
    }

    [Fact]
    public async Task Trigger_with_count_produces_a_rapid_fire_burst()
    {
        using var service = NewService();
        var info = await service.StartAsync(new SessionStartOptions { DeviceId = "cam0" }, CancellationToken.None);
        try
        {
            using var http = new HttpClient();
            var response = await http.PostAsync($"{info.TriggerUrl}&count=4&interval=0.1", content: null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var capture = await service.AwaitNextAsync(info.SessionId, TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.NotNull(capture);
            Assert.True(capture!.IsBurst);
            Assert.Equal(4, capture.FrameCount);
            Assert.NotNull(capture.Burst);
        }
        finally
        {
            await service.StopAsync(info.SessionId);
        }
    }

    [Fact]
    public async Task Trigger_carries_name_and_description_to_the_agent()
    {
        using var service = NewService();
        var info = await service.StartAsync(new SessionStartOptions { DeviceId = "cam0" }, CancellationToken.None);
        try
        {
            using var http = new HttpClient();
            await http.PostAsync($"{info.TriggerUrl}&name=front-door&description=motion%20detected", content: null);

            var capture = await service.AwaitNextAsync(info.SessionId, TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.NotNull(capture);
            Assert.Equal("front-door", capture!.Name);
            Assert.Equal("motion detected", capture.Description);
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
    public async Task Await_with_zero_timeout_polls_without_blocking()
    {
        using var service = NewService();
        var info = await service.StartAsync(new SessionStartOptions(), CancellationToken.None);
        try
        {
            // No trigger yet: a zero-timeout poll must return immediately (not hang).
            var poll = await service.AwaitNextAsync(info.SessionId, TimeSpan.Zero, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Null(poll);
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

        public Task<SceneCaptureResult> CaptureSceneAsync(SceneCaptureOptions options, CancellationToken cancellationToken)
        {
            var frames = Enumerable.Range(1, options.FrameCount)
                .Select(i => new SceneFrame(i, $"/tmp/frame-{i}.jpg", 3, [1, 2, 3]))
                .ToList();
            return Task.FromResult(new SceneCaptureResult("cam", ImageFormat.Jpeg, 640, 480, "/tmp/scene", frames));
        }

        public Task<IReadOnlyList<CameraDevice>> ListDevicesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<VideoCaptureResult> CaptureVideoAsync(VideoCaptureOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

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
