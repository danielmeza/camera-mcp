namespace CameraMcp.Server.Models;

/// <summary>
/// Validated request for a "scene": a sequence of stills returned as images so a vision model can read
/// the progression frame by frame. Timing is either uniform (one interval, optionally falling back to a
/// server default) or non-uniform (an explicit per-gap <see cref="Intervals"/> list).
/// </summary>
public sealed class SceneCaptureOptions
{
    /// <summary>Target device id; when null the first enumerated device is used.</summary>
    public string? DeviceId { get; init; }

    /// <summary>Frame count for the uniform case. Ignored when <see cref="Intervals"/> is supplied.</summary>
    public int FrameCount { get; init; }

    /// <summary>Per-call uniform interval (seconds). When null and no <see cref="Intervals"/>, the server default is used.</summary>
    public double? IntervalSeconds { get; init; }

    /// <summary>Explicit per-gap intervals (seconds) for non-uniform timing; produces <c>Count + 1</c> frames.</summary>
    public IReadOnlyList<double>? Intervals { get; init; }

    /// <summary>Requested frame width; snapped to the nearest supported mode. Paired with <see cref="Height"/>.</summary>
    public int? Width { get; init; }

    /// <summary>Requested frame height; snapped to the nearest supported mode. Paired with <see cref="Width"/>.</summary>
    public int? Height { get; init; }

    /// <summary>Output image format for every frame.</summary>
    public ImageFormat Format { get; init; } = ImageFormat.Jpeg;

    /// <summary>Encoder quality from 1 (smallest) to 100 (best). Ignored for the lossless PNG format.</summary>
    public int Quality { get; init; } = 85;

    /// <summary>Optional output directory for the frame files; otherwise a per-scene folder is created.</summary>
    public string? OutputDirectory { get; init; }

    /// <summary>Seconds to wait before the scene begins. Zero (default) starts as soon as possible.</summary>
    public double StartDelaySeconds { get; init; }

    /// <summary>True when explicit per-gap intervals were supplied (non-uniform timing).</summary>
    public bool IsNonUniform => Intervals is { Count: > 0 };

    /// <summary>The number of frames the scene will produce.</summary>
    public int ResolveFrameCount() => IsNonUniform ? Intervals!.Count + 1 : FrameCount;

    /// <summary>The uniform interval to use, falling back to <paramref name="defaultInterval"/>.</summary>
    public double ResolveUniformInterval(double defaultInterval) => IntervalSeconds ?? defaultInterval;

    /// <summary>
    /// Absolute capture timestamps (non-uniform), with <paramref name="warmupSeconds"/> baked into the
    /// first one so the cold start is skipped: <c>[warmup, warmup+i0, warmup+i0+i1, ...]</c>.
    /// </summary>
    public IReadOnlyList<double> ResolveTimestamps(double warmupSeconds)
    {
        var intervals = Intervals ?? throw new InvalidOperationException("ResolveTimestamps requires non-uniform intervals.");
        var timestamps = new double[intervals.Count + 1];
        timestamps[0] = warmupSeconds;
        for (var i = 0; i < intervals.Count; i++)
        {
            timestamps[i + 1] = timestamps[i] + intervals[i];
        }

        return timestamps;
    }

    /// <summary>Total capture span (excluding start delay), used for timeouts and the duration cap.</summary>
    public double SpanSeconds(double defaultInterval) =>
        IsNonUniform
            ? Intervals!.Sum()
            : ResolveFrameCount() * ResolveUniformInterval(defaultInterval);

    public void Validate(int maxFrames, int maxDurationSeconds, double defaultInterval, int maxStartDelaySeconds)
    {
        CaptureOptionsValidation.ValidateStartDelay(StartDelaySeconds, maxStartDelaySeconds);
        CaptureOptionsValidation.ValidateQuality(Quality);
        CaptureOptionsValidation.ValidateDimensions(Width, Height);

        if (IsNonUniform)
        {
            if (Intervals!.Any(i => i <= 0))
            {
                throw new CaptureValidationException("every value in intervals must be greater than zero.");
            }
        }
        else
        {
            if (FrameCount < 2)
            {
                throw new CaptureValidationException(
                    Intervals is null
                        ? "provide either frameCount (>= 2) or an intervals array."
                        : "intervals must contain at least one value.");
            }

            if (ResolveUniformInterval(defaultInterval) <= 0)
            {
                throw new CaptureValidationException("intervalSeconds must be greater than zero.");
            }
        }

        var frames = ResolveFrameCount();
        if (frames < 2)
        {
            throw new CaptureValidationException($"a scene needs at least 2 frames (got {frames}).");
        }

        if (frames > maxFrames)
        {
            throw new CaptureValidationException($"frameCount {frames} exceeds the configured maximum of {maxFrames}.");
        }

        var span = SpanSeconds(defaultInterval);
        if (span > maxDurationSeconds)
        {
            throw new CaptureValidationException(
                $"the scene span ({span}s) exceeds the configured maximum of {maxDurationSeconds}s.");
        }
    }
}
