namespace CameraMcp.Server.Models;

/// <summary>A single frame of a captured scene.</summary>
/// <param name="Index">1-based position in the sequence, parsed from the frame filename.</param>
/// <param name="FilePath">Absolute path to the saved frame.</param>
/// <param name="SizeBytes">Size of the saved frame on disk.</param>
/// <param name="Bytes">
/// Encoded payload, present only for frames returned inline (bounded by the inline caps); null for
/// frames that exceeded the cap and are available on disk only.
/// </param>
public sealed record SceneFrame(int Index, string FilePath, long SizeBytes, byte[]? Bytes);

/// <summary>
/// Result of a scene capture: an ordered set of stills plus the metadata describing the sequence.
/// </summary>
/// <param name="DeviceName">Friendly name of the device the frames came from.</param>
/// <param name="Format">Image format every frame is encoded in.</param>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
/// <param name="OutputDirectory">Directory the frame files were written to.</param>
/// <param name="Frames">The captured frames, in order.</param>
public sealed record SceneCaptureResult(
    string DeviceName,
    ImageFormat Format,
    int Width,
    int Height,
    string OutputDirectory,
    IReadOnlyList<SceneFrame> Frames);
