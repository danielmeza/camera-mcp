using System.Collections.Concurrent;
using CameraMcp.Server.Configuration;
using CameraMcp.Server.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CameraMcp.Server.Services;

/// <summary>An async capture queue: submit jobs (get an id + ETA), then poll / long-poll for results.</summary>
public interface ICaptureQueue
{
    Task<CaptureJob> EnqueueImageAsync(ImageCaptureOptions options, CancellationToken cancellationToken);
    Task<CaptureJob> EnqueueSceneAsync(SceneCaptureOptions options, CancellationToken cancellationToken);
    Task<CaptureJob> EnqueueVideoAsync(VideoCaptureOptions options, CancellationToken cancellationToken);

    CaptureJob? Get(string jobId);
    IReadOnlyList<CaptureJob> List();
    bool Cancel(string jobId);

    /// <summary>Long-poll: returns when the job is terminal or <paramref name="timeout"/> elapses.</summary>
    Task<CaptureJob> WaitAsync(string jobId, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// Runs queued captures concurrently across devices while serializing same-device jobs through a
/// per-device gate, so multiple cameras work in parallel and one camera never opens twice. ETAs are
/// computed at enqueue from the work already queued ahead on the same device.
/// </summary>
public sealed class CaptureQueue : ICaptureQueue
{
    private readonly ICameraService _camera;
    private readonly CameraMcpOptions _options;
    private readonly ILogger<CaptureQueue> _logger;

    private readonly ConcurrentDictionary<string, CaptureJob> _jobs = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _deviceGates = new();
    private readonly object _scheduleLock = new();
    private int _counter;

    public CaptureQueue(ICameraService camera, IOptions<CameraMcpOptions> options, ILogger<CaptureQueue> logger)
    {
        _camera = camera;
        _options = options.Value;
        _logger = logger;
    }

    public Task<CaptureJob> EnqueueImageAsync(ImageCaptureOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(_options.MaxStartDelaySeconds);
        var work = CaptureEstimator.EstimateImage(options, _options);
        return EnqueueCoreAsync(CaptureKind.Image, options.DeviceId, options.Width, options.Height, 0, work,
            ct => CastAsync(_camera.CaptureImageAsync(options, ct)), cancellationToken);
    }

    public Task<CaptureJob> EnqueueSceneAsync(SceneCaptureOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(_options.MaxSceneFrames, _options.MaxVideoDurationSeconds, _options.DefaultSceneIntervalSeconds, _options.MaxStartDelaySeconds);
        var work = CaptureEstimator.EstimateScene(options, _options);
        return EnqueueCoreAsync(CaptureKind.Scene, options.DeviceId, options.Width, options.Height, 0, work,
            ct => CastAsync(_camera.CaptureSceneAsync(options, ct)), cancellationToken);
    }

    public Task<CaptureJob> EnqueueVideoAsync(VideoCaptureOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(_options.MaxVideoDurationSeconds, _options.MaxStartDelaySeconds);
        var work = CaptureEstimator.EstimateVideo(options);
        return EnqueueCoreAsync(CaptureKind.Video, options.DeviceId, options.Width, options.Height, options.Fps, work,
            ct => CastAsync(_camera.CaptureVideoAsync(options, ct)), cancellationToken);
    }

    public CaptureJob? Get(string jobId) => _jobs.GetValueOrDefault(jobId);

    public IReadOnlyList<CaptureJob> List() => _jobs.Values.OrderBy(j => j.EnqueuedAt).ToList();

    public bool Cancel(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            return false;
        }

        job.Cts.Cancel();
        return true;
    }

    public async Task<CaptureJob> WaitAsync(string jobId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var job = Get(jobId) ?? throw new CaptureValidationException($"No capture job '{jobId}'.");
        if (job.IsTerminal || timeout <= TimeSpan.Zero)
        {
            return job;
        }

        try
        {
            await job.Completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Not done yet — return the current (still-running) status.
        }

        return job;
    }

    private async Task<CaptureJob> EnqueueCoreAsync(
        CaptureKind kind, string? deviceId, int? width, int? height, int fps,
        double work, Func<CancellationToken, Task<object>> run, CancellationToken cancellationToken)
    {
        var resolved = await _camera.ResolveInputAsync(deviceId, width, height, fps, cancellationToken).ConfigureAwait(false);
        var id = "cap_" + Interlocked.Increment(ref _counter).ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
        var now = DateTimeOffset.UtcNow;

        CaptureJob job;
        lock (_scheduleLock)
        {
            var ahead = _jobs.Values
                .Where(j => j.LockKey == resolved.LockKey && !j.IsTerminal)
                .ToList();

            job = new CaptureJob
            {
                Id = id,
                Kind = kind,
                DeviceName = resolved.DeviceName,
                LockKey = resolved.LockKey,
                EstimatedWorkSeconds = Math.Round(work, 1),
                EnqueuedAt = now,
                QueuePosition = ahead.Count,
                EtaSeconds = Math.Round(ahead.Sum(j => j.EstimatedWorkSeconds) + work, 1),
                Status = JobStatus.Queued,
            };
            _jobs[id] = job;
        }

        _logger.LogInformation("Queued {Kind} job {Id} on {Device} (eta ~{Eta}s).", kind, id, resolved.DeviceName, job.EtaSeconds);
        _ = Task.Run(() => ProcessAsync(job, run), CancellationToken.None);
        return job;
    }

    private async Task ProcessAsync(CaptureJob job, Func<CancellationToken, Task<object>> run)
    {
        var gate = _deviceGates.GetOrAdd(job.LockKey, _ => new SemaphoreSlim(1, 1));

        try
        {
            await gate.WaitAsync(job.Cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            job.Status = JobStatus.Canceled;
            Finish(job);
            return;
        }

        try
        {
            job.StartedAt = DateTimeOffset.UtcNow;
            job.Status = JobStatus.Running;
            job.Result = await run(job.Cts.Token).ConfigureAwait(false);
            job.Status = JobStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            job.Status = JobStatus.Canceled;
        }
        catch (Exception ex)
        {
            job.Status = JobStatus.Failed;
            job.Error = ex.Message;
            _logger.LogWarning(ex, "Capture job {Id} failed.", job.Id);
        }
        finally
        {
            gate.Release();
            Finish(job);
        }
    }

    private static void Finish(CaptureJob job)
    {
        job.CompletedAt = DateTimeOffset.UtcNow;
        job.Completion.TrySetResult();
    }

    private static async Task<object> CastAsync<T>(Task<T> task) where T : notnull => await task.ConfigureAwait(false);
}
