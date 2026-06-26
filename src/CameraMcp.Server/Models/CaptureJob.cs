namespace CameraMcp.Server.Models;

/// <summary>What a queued capture produces.</summary>
public enum CaptureKind
{
    Image,
    Scene,
    Video,
}

/// <summary>Lifecycle of a queued capture job.</summary>
public enum JobStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Canceled,
}

/// <summary>A queued capture job: its identity, schedule estimate, status, and (when done) result.</summary>
public sealed class CaptureJob
{
    public required string Id { get; init; }
    public required CaptureKind Kind { get; init; }
    public required string DeviceName { get; init; }

    /// <summary>Per-device serialization key (jobs with the same key run one at a time).</summary>
    public required string LockKey { get; init; }

    /// <summary>Estimated seconds of work for this job alone (excludes time waiting behind other jobs).</summary>
    public required double EstimatedWorkSeconds { get; init; }

    public JobStatus Status { get; set; } = JobStatus.Queued;

    /// <summary>Estimated total seconds until this job completes, measured from enqueue.</summary>
    public double EtaSeconds { get; set; }

    /// <summary>Number of same-device jobs ahead of this one at enqueue time.</summary>
    public int QueuePosition { get; set; }

    public DateTimeOffset EnqueuedAt { get; init; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>The typed capture result when completed (ImageCaptureResult / SceneCaptureResult / VideoCaptureResult).</summary>
    public object? Result { get; set; }

    public string? Error { get; set; }

    public bool IsTerminal => Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Canceled;

    /// <summary>Completes when the job reaches a terminal state (used by the long-poll wait).</summary>
    internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal CancellationTokenSource Cts { get; } = new();
}
