using CameraMcp.Server.Configuration;
using CameraMcp.Server.Models;
using CameraMcp.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CameraMcp.Tests.Services;

public class CaptureQueueTests
{
    private static CaptureQueue NewQueue(StubCamera camera) =>
        new(camera, Options.Create(new CameraMcpOptions()), NullLogger<CaptureQueue>.Instance);

    [Fact]
    public async Task Enqueue_then_wait_returns_completed_with_result()
    {
        var queue = NewQueue(new StubCamera());

        var job = await queue.EnqueueImageAsync(new ImageCaptureOptions { DeviceId = "cam0" }, CancellationToken.None);
        Assert.StartsWith("cap_", job.Id);
        Assert.True(job.EtaSeconds > 0);

        var done = await queue.WaitAsync(job.Id, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(JobStatus.Completed, done.Status);
        Assert.IsType<ImageCaptureResult>(done.Result);
        Assert.NotNull(done.CompletedAt);
    }

    [Fact]
    public async Task Same_device_jobs_queue_behind_each_other()
    {
        var gate = new SemaphoreSlim(0, 1); // hold the first capture open
        var camera = new StubCamera { Hold = gate };
        var queue = NewQueue(camera);

        var first = await queue.EnqueueImageAsync(new ImageCaptureOptions { DeviceId = "cam0" }, CancellationToken.None);
        var second = await queue.EnqueueImageAsync(new ImageCaptureOptions { DeviceId = "cam0" }, CancellationToken.None);

        Assert.Equal(0, first.QueuePosition);
        Assert.Equal(1, second.QueuePosition);                 // queued behind the first
        Assert.True(second.EtaSeconds > first.EtaSeconds);      // and its ETA includes the wait

        gate.Release(); // let the captures drain
        await queue.WaitAsync(second.Id, TimeSpan.FromSeconds(5), CancellationToken.None);
    }

    [Fact]
    public async Task Different_device_jobs_are_both_at_the_front()
    {
        var queue = NewQueue(new StubCamera { Hold = new SemaphoreSlim(0, 2) });

        var a = await queue.EnqueueImageAsync(new ImageCaptureOptions { DeviceId = "cam0" }, CancellationToken.None);
        var b = await queue.EnqueueImageAsync(new ImageCaptureOptions { DeviceId = "cam1" }, CancellationToken.None);

        Assert.Equal(0, a.QueuePosition);
        Assert.Equal(0, b.QueuePosition); // different device -> not behind a
    }

    [Fact]
    public async Task Cancel_marks_the_job_canceled()
    {
        var gate = new SemaphoreSlim(0, 1);
        var queue = NewQueue(new StubCamera { Hold = gate });

        var job = await queue.EnqueueImageAsync(new ImageCaptureOptions { DeviceId = "cam0" }, CancellationToken.None);
        Assert.True(queue.Cancel(job.Id));

        var done = await queue.WaitAsync(job.Id, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(JobStatus.Canceled, done.Status);
    }

    [Fact]
    public async Task Wait_returns_running_status_on_timeout()
    {
        var gate = new SemaphoreSlim(0, 1);
        var queue = NewQueue(new StubCamera { Hold = gate });

        var job = await queue.EnqueueImageAsync(new ImageCaptureOptions { DeviceId = "cam0" }, CancellationToken.None);
        var polled = await queue.WaitAsync(job.Id, TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.False(polled.IsTerminal); // still running/queued — not done within the short wait
        gate.Release();
    }

    [Fact]
    public async Task Wait_throws_for_unknown_job()
    {
        var queue = NewQueue(new StubCamera());
        await Assert.ThrowsAsync<CaptureValidationException>(() =>
            queue.WaitAsync("cap_9999", TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    private sealed class StubCamera : ICameraService
    {
        /// <summary>When set, each capture waits on this before completing (to hold jobs running).</summary>
        public SemaphoreSlim? Hold { get; init; }

        public Task<ResolvedCaptureInput> ResolveInputAsync(string? deviceId, int? width, int? height, int fps, CancellationToken cancellationToken) =>
            Task.FromResult(new ResolvedCaptureInput(deviceId ?? "cam", "lock:" + (deviceId ?? "cam"), [], 640, 480));

        public async Task<ImageCaptureResult> CaptureImageAsync(ImageCaptureOptions options, CancellationToken cancellationToken)
        {
            if (Hold is not null)
            {
                await Hold.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return new ImageCaptureResult([1, 2, 3], ImageFormat.Jpeg, 640, 480, "cam", "/tmp/x.jpg");
        }

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
}
