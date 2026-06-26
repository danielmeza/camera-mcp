namespace CameraMcp.Server.Models;

/// <summary>A single capture mode advertised by a camera (resolution, pixel format, frame rate).</summary>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
/// <param name="PixelFormat">Device pixel/compression format (e.g. <c>YUYV</c>, <c>MJPG</c>, <c>RGB24</c>).</param>
/// <param name="FramesPerSecond">Advertised frame rate for this mode.</param>
public sealed record CaptureFormat(int Width, int Height, string PixelFormat, double FramesPerSecond);

/// <summary>
/// A camera attached to the host, as exposed to the agent by <c>list_cameras</c>.
/// </summary>
/// <param name="Id">Stable opaque identifier accepted by the capture tools.</param>
/// <param name="Name">Human-friendly device name reported by the OS.</param>
/// <param name="Platform">Capture backend in use (<c>directshow</c>, <c>v4l2</c>, <c>avfoundation</c>).</param>
/// <param name="Formats">Supported capture modes, de-duplicated and ordered by descending resolution.</param>
public sealed record CameraDevice(
    string Id,
    string Name,
    string Platform,
    IReadOnlyList<CaptureFormat> Formats);
