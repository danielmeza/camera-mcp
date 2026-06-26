namespace CameraMcp.Server.Configuration;

/// <summary>
/// Server-wide configuration, bound from configuration/environment/CLI. The
/// <c>CameraMcp__</c> environment prefix maps to these properties (e.g. <c>CameraMcp__OutputDirectory</c>).
/// </summary>
public sealed class CameraMcpOptions
{
    public const string SectionName = "CameraMcp";

    /// <summary>Directory where captured video files are written when no explicit path is given.</summary>
    public string OutputDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "camera-mcp", "captures");

    /// <summary>Hard cap on requested video duration, protecting the host from runaway recordings.</summary>
    public int MaxVideoDurationSeconds { get; set; } = 300;

    /// <summary>Hard cap on the number of frames a single <c>capture_scene</c> may request.</summary>
    public int MaxSceneFrames { get; set; } = 60;

    /// <summary>Default interval (seconds) between scene frames when a call doesn't specify one.</summary>
    public double DefaultSceneIntervalSeconds { get; set; } = 1.0;

    /// <summary>Maximum delay (seconds) a capture may wait before starting.</summary>
    public int MaxStartDelaySeconds { get; set; } = 3600;

    /// <summary>
    /// Maximum number of scene frames returned inline (as images) in one response. Every frame is
    /// always saved to disk and listed by path; only the inline payload is bounded.
    /// </summary>
    public int MaxInlineSceneFrames { get; set; } = 30;

    /// <summary>Maximum total bytes of scene frames read into memory and returned inline.</summary>
    public long MaxInlineSceneBytes { get; set; } = 24L * 1024 * 1024;

    /// <summary>Explicit path to an ffmpeg executable; when null the locator searches bundled/PATH locations.</summary>
    public string? FFmpegPath { get; set; }

    /// <summary>Timeout applied to a single ffmpeg invocation beyond the recording duration itself.</summary>
    public int FFmpegTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Number of initial frames to discard before grabbing a still. Cameras (and especially
    /// phone-as-webcam / virtual devices) deliver black or unstable frames on cold open; skipping a
    /// few lets exposure and the stream settle.
    /// </summary>
    public int ImageWarmupFrames { get; set; } = 15;
}
