namespace CameraMcp.Server.Models;

/// <summary>Validated request for a fixed-duration video capture.</summary>
public sealed class VideoCaptureOptions
{
    /// <summary>Target device id; when null the first enumerated device is used.</summary>
    public string? DeviceId { get; init; }

    /// <summary>Recording length in seconds. Required; capped by <c>CameraMcpOptions.MaxVideoDurationSeconds</c>.</summary>
    public double DurationSeconds { get; init; }

    /// <summary>Requested frame width; snapped to the nearest supported mode. Paired with <see cref="Height"/>.</summary>
    public int? Width { get; init; }

    /// <summary>Requested frame height; snapped to the nearest supported mode. Paired with <see cref="Width"/>.</summary>
    public int? Height { get; init; }

    /// <summary>Output frame rate.</summary>
    public int Fps { get; init; } = 30;

    /// <summary>Output container format.</summary>
    public VideoContainer Container { get; init; } = VideoContainer.Mp4;

    /// <summary>Output video codec.</summary>
    public VideoCodec Codec { get; init; } = VideoCodec.H264;

    /// <summary>Quality from 1 (smallest) to 100 (best), mapped to a codec CRF when no bitrate is set.</summary>
    public int Quality { get; init; } = 75;

    /// <summary>Optional constant target bitrate in kbit/s. When set, overrides the quality-derived CRF.</summary>
    public int? BitrateKbps { get; init; }

    /// <summary>Whether to also record audio from the device's default microphone.</summary>
    public bool Audio { get; init; }

    /// <summary>Optional explicit output file path; otherwise a file is created in the configured output directory.</summary>
    public string? OutputPath { get; init; }

    /// <summary>Seconds to wait before recording. Zero (default) records as soon as possible.</summary>
    public double StartDelaySeconds { get; init; }

    public void Validate(int maxDurationSeconds, int maxStartDelaySeconds)
    {
        CaptureOptionsValidation.ValidateDuration(DurationSeconds, maxDurationSeconds);
        CaptureOptionsValidation.ValidateFps(Fps);
        CaptureOptionsValidation.ValidateQuality(Quality);
        CaptureOptionsValidation.ValidateDimensions(Width, Height);
        CaptureOptionsValidation.ValidateCodecContainer(Codec, Container);
        CaptureOptionsValidation.ValidateStartDelay(StartDelaySeconds, maxStartDelaySeconds);

        if (BitrateKbps is <= 0)
        {
            throw new CaptureValidationException(
                $"bitrateKbps must be greater than zero when specified (got {BitrateKbps}).");
        }
    }
}
