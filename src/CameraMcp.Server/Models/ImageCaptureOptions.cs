namespace CameraMcp.Server.Models;

/// <summary>Validated request for a single still capture.</summary>
public sealed class ImageCaptureOptions
{
    /// <summary>Target device id; when null the first enumerated device is used.</summary>
    public string? DeviceId { get; init; }

    /// <summary>Requested frame width; snapped to the nearest supported mode. Paired with <see cref="Height"/>.</summary>
    public int? Width { get; init; }

    /// <summary>Requested frame height; snapped to the nearest supported mode. Paired with <see cref="Width"/>.</summary>
    public int? Height { get; init; }

    /// <summary>Output encoding for the still image.</summary>
    public ImageFormat Format { get; init; } = ImageFormat.Jpeg;

    /// <summary>Encoder quality from 1 (smallest) to 100 (best). Ignored for the lossless PNG format.</summary>
    public int Quality { get; init; } = 85;

    /// <summary>Optional explicit output file path; otherwise a file is created in the configured output directory.</summary>
    public string? OutputPath { get; init; }

    /// <summary>Seconds to wait before capturing. Zero (default) captures as soon as possible.</summary>
    public double StartDelaySeconds { get; init; }

    public void Validate(int maxStartDelaySeconds)
    {
        CaptureOptionsValidation.ValidateQuality(Quality);
        CaptureOptionsValidation.ValidateDimensions(Width, Height);
        CaptureOptionsValidation.ValidateStartDelay(StartDelaySeconds, maxStartDelaySeconds);
    }
}
