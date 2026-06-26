namespace CameraMcp.Server.Models;

/// <summary>Shared, pure validation checks used by the capture option records.</summary>
public static class CaptureOptionsValidation
{
    public const int MinQuality = 1;
    public const int MaxQuality = 100;
    public const int MaxFps = 240;

    public static void ValidateQuality(int quality)
    {
        if (quality is < MinQuality or > MaxQuality)
        {
            throw new CaptureValidationException(
                $"quality must be between {MinQuality} and {MaxQuality} (got {quality}).");
        }
    }

    /// <summary>
    /// Width and height must be supplied together (or both omitted) and be positive when present.
    /// </summary>
    public static void ValidateDimensions(int? width, int? height)
    {
        if (width.HasValue != height.HasValue)
        {
            throw new CaptureValidationException(
                "width and height must be specified together, or both left unset to use the device default.");
        }

        if (width is <= 0 || height is <= 0)
        {
            throw new CaptureValidationException(
                $"width and height must be positive (got {width}x{height}).");
        }
    }

    public static void ValidateStartDelay(double startDelaySeconds, int maxStartDelaySeconds)
    {
        if (startDelaySeconds < 0)
        {
            throw new CaptureValidationException(
                $"startDelaySeconds must be zero or greater (got {startDelaySeconds}).");
        }

        if (startDelaySeconds > maxStartDelaySeconds)
        {
            throw new CaptureValidationException(
                $"startDelaySeconds {startDelaySeconds} exceeds the configured maximum of {maxStartDelaySeconds}s.");
        }
    }

    public static void ValidateFps(int fps)
    {
        if (fps is < 1 or > MaxFps)
        {
            throw new CaptureValidationException($"fps must be between 1 and {MaxFps} (got {fps}).");
        }
    }

    public static void ValidateDuration(double durationSeconds, int maxDurationSeconds)
    {
        if (durationSeconds <= 0)
        {
            throw new CaptureValidationException(
                $"durationSeconds must be greater than zero (got {durationSeconds}).");
        }

        if (durationSeconds > maxDurationSeconds)
        {
            throw new CaptureValidationException(
                $"durationSeconds {durationSeconds} exceeds the configured maximum of {maxDurationSeconds}s.");
        }
    }

    /// <summary>
    /// Rejects codec/container pairings that FFmpeg cannot mux. WebM carries VP9 only; MP4 carries
    /// the H.26x family; MKV accepts any of them.
    /// </summary>
    public static void ValidateCodecContainer(VideoCodec codec, VideoContainer container)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(container);

        if (!container.Supports(codec))
        {
            throw new CaptureValidationException(
                $"codec '{codec.Name}' is not compatible with container '{container.Name}'. " +
                $"Use {container.CompatibleCodecHint}, or switch to the mkv container.");
        }
    }
}
