using CameraMcp.Server.Models;

namespace CameraMcp.Server.Services;

/// <summary>The resolved ffmpeg input for a device, used to drive a live preview stream.</summary>
/// <param name="DeviceName">Friendly device name.</param>
/// <param name="LockKey">The per-device serialization key (shared with captures).</param>
/// <param name="FfmpegInputArgs">Platform input arguments up to and including <c>-i</c>.</param>
/// <param name="Width">Selected frame width.</param>
/// <param name="Height">Selected frame height.</param>
public sealed record ResolvedCaptureInput(string DeviceName, string LockKey, IReadOnlyList<string> FfmpegInputArgs, int Width, int Height);

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

    /// <summary>Resolves the ffmpeg input arguments + device info for a live preview stream.</summary>
    Task<ResolvedCaptureInput> ResolveInputAsync(string? deviceId, int? width, int? height, int fps, CancellationToken cancellationToken);

    /// <summary>
    /// Acquires the per-device serialization lock (the same one captures use), so the preview and
    /// captures never open the same physical camera at once. Dispose the returned handle to release.
    /// </summary>
    Task<IAsyncDisposable> AcquireDeviceLockAsync(string lockKey, CancellationToken cancellationToken);
}
