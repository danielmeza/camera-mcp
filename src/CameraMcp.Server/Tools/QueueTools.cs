using System.ComponentModel;
using CameraMcp.Server.Models;
using CameraMcp.Server.Services;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CameraMcp.Server.Tools;

/// <summary>
/// Async capture queue: submit a capture and get a job id + ETA back immediately, then retrieve the
/// result with get_capture (which can long-poll until it's ready). Jobs for different cameras run in
/// parallel; same-camera jobs serialize. Use these instead of the blocking capture_* tools when you
/// want to queue several captures, schedule a delayed one, or do other work while one runs.
/// </summary>
[McpServerToolType]
public sealed class QueueTools
{
    private const int MaxWaitSeconds = 300;

    private readonly ICaptureQueue _queue;
    private readonly ICaptureStore _store;

    public QueueTools(ICaptureQueue queue, ICaptureStore store)
    {
        _queue = queue;
        _store = store;
    }

    [McpServerTool(Name = "queue_image"),
     Description("Queues a still capture and returns { jobId, etaSeconds, queuePosition } immediately. " +
                 "Retrieve it with get_capture(jobId, waitSeconds).")]
    public async Task<string> QueueImageAsync(
        [Description("Camera id or name. Omit to use the first camera.")] string? deviceId = null,
        int? width = null, int? height = null,
        [Description("jpeg, png, or webp. Default jpeg.")] string? format = null,
        [Description("Quality 1-100. Default 85.")] int quality = 85,
        [Description("Seconds to wait before capturing. Default 0.")] double startDelaySeconds = 0,
        CancellationToken cancellationToken = default)
    {
        return await EnqueueAsync(() => _queue.EnqueueImageAsync(new ImageCaptureOptions
        {
            DeviceId = deviceId,
            Width = width,
            Height = height,
            Format = ImageFormat.FromToken(format, ImageFormat.Jpeg),
            Quality = quality,
            StartDelaySeconds = startDelaySeconds,
        }, cancellationToken)).ConfigureAwait(false);
    }

    [McpServerTool(Name = "queue_scene"),
     Description("Queues a frame-sequence (scene) capture and returns { jobId, etaSeconds } immediately. " +
                 "Same timing options as capture_scene (frameCount+intervalSeconds, or non-uniform intervals).")]
    public async Task<string> QueueSceneAsync(
        int? frameCount = null, double? intervalSeconds = null, double[]? intervals = null,
        string? deviceId = null, int? width = null, int? height = null,
        string? format = null, int quality = 85, string? outputDirectory = null,
        double startDelaySeconds = 0,
        CancellationToken cancellationToken = default)
    {
        return await EnqueueAsync(() => _queue.EnqueueSceneAsync(new SceneCaptureOptions
        {
            DeviceId = deviceId,
            FrameCount = frameCount ?? 0,
            IntervalSeconds = intervalSeconds,
            Intervals = intervals,
            Width = width,
            Height = height,
            Format = ImageFormat.FromToken(format, ImageFormat.Jpeg),
            Quality = quality,
            OutputDirectory = outputDirectory,
            StartDelaySeconds = startDelaySeconds,
        }, cancellationToken)).ConfigureAwait(false);
    }

    [McpServerTool(Name = "queue_video"),
     Description("Queues a fixed-duration video recording and returns { jobId, etaSeconds } immediately. " +
                 "Same options as capture_video.")]
    public async Task<string> QueueVideoAsync(
        double durationSeconds,
        string? deviceId = null, int? width = null, int? height = null, int fps = 30,
        string? container = null, string? codec = null, int quality = 75, int? bitrateKbps = null,
        string? outputPath = null, double startDelaySeconds = 0,
        CancellationToken cancellationToken = default)
    {
        return await EnqueueAsync(() => _queue.EnqueueVideoAsync(new VideoCaptureOptions
        {
            DeviceId = deviceId,
            DurationSeconds = durationSeconds,
            Width = width,
            Height = height,
            Fps = fps,
            Container = VideoContainer.FromToken(container, VideoContainer.Mp4),
            Codec = VideoCodec.FromToken(codec, VideoCodec.H264),
            Quality = quality,
            BitrateKbps = bitrateKbps,
            OutputPath = outputPath,
            StartDelaySeconds = startDelaySeconds,
        }, cancellationToken)).ConfigureAwait(false);
    }

    [McpServerTool(Name = "get_capture"),
     Description("Retrieves a queued capture by jobId. With waitSeconds > 0 it LONG-POLLS, returning the " +
                 "result the instant the job completes (or the live status if it isn't done within the wait). " +
                 "When complete, returns the same content (inline images + resource links) as the capture_* tools.")]
    public async Task<IEnumerable<ContentBlock>> GetCaptureAsync(
        [Description("The jobId returned by a queue_* tool.")] string jobId,
        [Description("Seconds to wait for completion before returning (0 = return current status now; capped at 300).")]
        double waitSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var timeout = TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 0, MaxWaitSeconds));
            var job = await _queue.WaitAsync(jobId, timeout, cancellationToken).ConfigureAwait(false);

            var blocks = new List<ContentBlock> { new TextContentBlock { Text = Describe(job) } };
            if (job.Status == JobStatus.Completed && job.Result is not null)
            {
                blocks.AddRange(CaptureRendering.Render(job.Kind, job.Result, _store));
            }

            return blocks;
        }
        catch (CaptureValidationException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "list_captures"),
     Description("Lists all capture jobs (queued, running, and finished) with their status, ETA, and queue position.")]
    public string ListCaptures() =>
        CameraJson.Serialize(new { count = _queue.List().Count, jobs = _queue.List().Select(SummarizeObject) });

    [McpServerTool(Name = "cancel_capture"),
     Description("Cancels a queued or running capture job by jobId.")]
    public string CancelCapture([Description("The jobId to cancel.")] string jobId) =>
        CameraJson.Serialize(new { jobId, canceled = _queue.Cancel(jobId) });

    private async Task<string> EnqueueAsync(Func<Task<CaptureJob>> enqueue)
    {
        try
        {
            var job = await enqueue().ConfigureAwait(false);
            return CameraJson.Serialize(new
            {
                jobId = job.Id,
                kind = job.Kind.ToString().ToLowerInvariant(),
                device = job.DeviceName,
                etaSeconds = job.EtaSeconds,
                queuePosition = job.QueuePosition,
                status = job.Status.ToString().ToLowerInvariant(),
            });
        }
        catch (Exception ex) when (ex is CaptureValidationException or CaptureFailedException or FFmpegNotFoundException)
        {
            throw new McpException(ex.Message);
        }
    }

    private static string Describe(CaptureJob job) => CameraJson.Serialize(SummarizeObject(job));

    private static object SummarizeObject(CaptureJob job) => new
    {
        jobId = job.Id,
        kind = job.Kind.ToString().ToLowerInvariant(),
        device = job.DeviceName,
        status = job.Status.ToString().ToLowerInvariant(),
        etaSeconds = job.EtaSeconds,
        queuePosition = job.QueuePosition,
        enqueuedAt = job.EnqueuedAt,
        startedAt = job.StartedAt,
        completedAt = job.CompletedAt,
        error = job.Error,
    };
}
