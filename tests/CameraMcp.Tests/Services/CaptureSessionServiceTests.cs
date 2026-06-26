using CameraMcp.Server.Models;
using CameraMcp.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace CameraMcp.Tests.Services;

public class CaptureSessionServiceTests
{
    private static CaptureSessionService NewService() =>
        new(new StubCamera(), new StubTunnel(), new StubHostInfo(), NullLogger<CaptureSessionService>.Instance);

    private static TriggerRequest Trigger(int? count = null, double? interval = null, string? name = null, string? description = null) =>
        new() { Count = count, IntervalSeconds = interval, Name = name, Description = description };

    [Fact]
    public async Task Start_then_trigger_delivers_a_still()
    {
        var service = NewService();
        var info = await service.StartAsync(new SessionStartOptions { DeviceId = "cam0" }, CancellationToken.None);
        Assert.StartsWith("sess_", info.SessionId);
        Assert.Contains($"/sessions/{info.SessionId}/trigger?token={info.Token}", info.TriggerUrl);

        var result = await service.TriggerAsync(info.SessionId, info.Token, Trigger(), CancellationToken.None);
        Assert.Equal(SessionOutcome.Ok, result.Outcome);

        var capture = await service.AwaitNextAsync(info.SessionId, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.NotNull(capture);
        Assert.Equal(1, capture!.Seq);
        Assert.False(capture.IsBurst);
        Assert.Equal(640, capture.Still!.Width);
    }

    [Fact]
    public async Task Trigger_with_count_produces_a_rapid_fire_burst()
    {
        var service = NewService();
        var info = await service.StartAsync(new SessionStartOptions { DeviceId = "cam0" }, CancellationToken.None);

        var result = await service.TriggerAsync(info.SessionId, info.Token, Trigger(count: 4, interval: 0.1), CancellationToken.None);
        Assert.Equal(SessionOutcome.Ok, result.Outcome);
        Assert.True(result.IsBurst);
        Assert.Equal(4, result.FrameCount);

        var capture = await service.AwaitNextAsync(info.SessionId, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.True(capture!.IsBurst);
        Assert.Equal(4, capture.FrameCount);
    }

    [Fact]
    public async Task Trigger_carries_name_and_description()
    {
        var service = NewService();
        var info = await service.StartAsync(new SessionStartOptions(), CancellationToken.None);

        await service.TriggerAsync(info.SessionId, info.Token, Trigger(name: "front-door", description: "motion detected"), CancellationToken.None);

        var capture = await service.AwaitNextAsync(info.SessionId, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("front-door", capture!.Name);
        Assert.Equal("motion detected", capture.Description);
    }

    [Fact]
    public async Task Trigger_with_bad_token_is_unauthorized()
    {
        var service = NewService();
        var info = await service.StartAsync(new SessionStartOptions(), CancellationToken.None);

        var result = await service.TriggerAsync(info.SessionId, "wrong-token", Trigger(), CancellationToken.None);
        Assert.Equal(SessionOutcome.Unauthorized, result.Outcome);

        var missing = await service.TriggerAsync(info.SessionId, null, Trigger(), CancellationToken.None);
        Assert.Equal(SessionOutcome.Unauthorized, missing.Outcome);
    }

    [Fact]
    public async Task Trigger_for_unknown_session_is_not_found()
    {
        var service = NewService();
        var result = await service.TriggerAsync("sess_nope", "t", Trigger(), CancellationToken.None);
        Assert.Equal(SessionOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task Describe_validates_token_and_returns_device()
    {
        var service = NewService();
        var info = await service.StartAsync(new SessionStartOptions { DeviceId = "cam0" }, CancellationToken.None);

        Assert.Equal(SessionOutcome.Ok, service.Describe(info.SessionId, info.Token).Outcome);
        Assert.Equal(SessionOutcome.Unauthorized, service.Describe(info.SessionId, "nope").Outcome);
        Assert.Equal(SessionOutcome.NotFound, service.Describe("sess_x", info.Token).Outcome);
    }

    [Fact]
    public async Task Await_with_zero_timeout_polls_without_blocking()
    {
        var service = NewService();
        var info = await service.StartAsync(new SessionStartOptions(), CancellationToken.None);

        var poll = await service.AwaitNextAsync(info.SessionId, TimeSpan.Zero, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(poll);
    }

    [Fact]
    public async Task Await_times_out_to_null_when_no_trigger()
    {
        var service = NewService();
        var info = await service.StartAsync(new SessionStartOptions(), CancellationToken.None);

        var frame = await service.AwaitNextAsync(info.SessionId, TimeSpan.FromMilliseconds(200), CancellationToken.None);
        Assert.Null(frame);
    }

    [Fact]
    public async Task Stop_removes_the_session()
    {
        var service = NewService();
        var info = await service.StartAsync(new SessionStartOptions(), CancellationToken.None);

        Assert.True(await service.StopAsync(info.SessionId));
        Assert.Empty(service.ActiveSessionIds);
        Assert.False(await service.StopAsync(info.SessionId));
        await Assert.ThrowsAsync<CaptureValidationException>(() =>
            service.AwaitNextAsync(info.SessionId, TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [Fact]
    public async Task Multiple_sessions_run_concurrently()
    {
        var service = NewService();
        var a = await service.StartAsync(new SessionStartOptions { DeviceId = "cam0" }, CancellationToken.None);
        var b = await service.StartAsync(new SessionStartOptions { DeviceId = "cam1" }, CancellationToken.None);

        Assert.NotEqual(a.SessionId, b.SessionId);
        Assert.Equal(2, service.ActiveSessionIds.Count);

        // Each session's token only works for itself.
        Assert.Equal(SessionOutcome.Unauthorized, (await service.TriggerAsync(a.SessionId, b.Token, Trigger(), CancellationToken.None)).Outcome);
        Assert.Equal(SessionOutcome.Ok, (await service.TriggerAsync(a.SessionId, a.Token, Trigger(), CancellationToken.None)).Outcome);
    }

    private sealed class StubCamera : ICameraService
    {
        public Task<ResolvedCaptureInput> ResolveInputAsync(string? deviceId, int? width, int? height, int fps, CancellationToken cancellationToken) =>
            Task.FromResult(new ResolvedCaptureInput(deviceId ?? "cam", "lock:" + (deviceId ?? "cam"), [], 640, 480));

        public Task<ImageCaptureResult> CaptureImageAsync(ImageCaptureOptions options, CancellationToken cancellationToken) =>
            Task.FromResult(new ImageCaptureResult([1, 2, 3], ImageFormat.Jpeg, 640, 480, "cam", "/tmp/x.jpg"));

        public Task<SceneCaptureResult> CaptureSceneAsync(SceneCaptureOptions options, CancellationToken cancellationToken)
        {
            var frames = Enumerable.Range(1, options.FrameCount)
                .Select(i => new SceneFrame(i, $"/tmp/frame-{i}.jpg", 3, [1, 2, 3]))
                .ToList();
            return Task.FromResult(new SceneCaptureResult("cam", ImageFormat.Jpeg, 640, 480, "/tmp/scene", frames));
        }

        public Task<IAsyncDisposable> AcquireDeviceLockAsync(string lockKey, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable>(new NoopLock());

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

    private sealed class StubHostInfo : IHttpHostInfo
    {
        public int Port => 5005;
        public string BaseUrl => "http://127.0.0.1:5005";
    }
}
