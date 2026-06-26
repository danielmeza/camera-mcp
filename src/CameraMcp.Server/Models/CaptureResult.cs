namespace CameraMcp.Server.Models;

/// <summary>Result of a still capture: the encoded image bytes plus a little metadata.</summary>
/// <param name="Bytes">Encoded image payload.</param>
/// <param name="Format">Output format the bytes are encoded in.</param>
/// <param name="Width">Captured frame width in pixels.</param>
/// <param name="Height">Captured frame height in pixels.</param>
/// <param name="DeviceName">Friendly name of the device the frame came from.</param>
/// <param name="FilePath">Absolute path to the saved still on disk.</param>
public sealed record ImageCaptureResult(
    byte[] Bytes,
    ImageFormat Format,
    int Width,
    int Height,
    string DeviceName,
    string FilePath)
{
    public string MimeType => Format.MimeType;
}

/// <summary>
/// Result of a video capture: the on-disk file and a single inline poster frame the agent can view.
/// </summary>
/// <param name="FilePath">Absolute path to the encoded video file.</param>
/// <param name="Container">Container format of the file.</param>
/// <param name="Codec">Video codec used.</param>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
/// <param name="Fps">Frame rate.</param>
/// <param name="DurationSeconds">Requested recording duration.</param>
/// <param name="FileSizeBytes">Size of the encoded file on disk.</param>
/// <param name="DeviceName">Friendly name of the device the recording came from.</param>
/// <param name="PosterFrame">A still extracted from the recording (JPEG) for the agent to view, if available.</param>
public sealed record VideoCaptureResult(
    string FilePath,
    VideoContainer Container,
    VideoCodec Codec,
    int Width,
    int Height,
    int Fps,
    double DurationSeconds,
    long FileSizeBytes,
    string DeviceName,
    byte[]? PosterFrame)
{
    public const string PosterMimeType = "image/jpeg";
}
