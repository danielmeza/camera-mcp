using CameraMcp.Server.Models;

namespace CameraMcp.Server.Services;

/// <summary>The capture operations exposed to the MCP tools.</summary>
public interface ICameraService
{
    /// <summary>Enumerates cameras attached to the host with their supported formats.</summary>
    Task<IReadOnlyList<CameraDevice>> ListDevicesAsync(CancellationToken cancellationToken);

    /// <summary>Captures and encodes a single still image.</summary>
    Task<ImageCaptureResult> CaptureImageAsync(ImageCaptureOptions options, CancellationToken cancellationToken);

    /// <summary>Records a fixed-duration clip to disk and returns its metadata plus a poster frame.</summary>
    Task<VideoCaptureResult> CaptureVideoAsync(VideoCaptureOptions options, CancellationToken cancellationToken);

    /// <summary>Captures a sequence of stills at an interval, returned as ordered image frames.</summary>
    Task<SceneCaptureResult> CaptureSceneAsync(SceneCaptureOptions options, CancellationToken cancellationToken);
}
