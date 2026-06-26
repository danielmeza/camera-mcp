using Ardalis.SmartEnum;

namespace CameraMcp.Server.Models;

/// <summary>
/// The OS capture backend FFmpeg must drive for a device. A smart enum whose <see cref="SmartEnum{T}.Name"/>
/// is the lowercase label shown to agents and whose <see cref="FfmpegFormat"/> is the ffmpeg input format.
/// </summary>
public sealed class CapturePlatform : SmartEnum<CapturePlatform>
{
    /// <summary>Windows DirectShow (<c>-f dshow</c>).</summary>
    public static readonly CapturePlatform DirectShow = new(name: "directshow", value: 0, ffmpegFormat: "dshow");

    /// <summary>Linux Video4Linux2 (<c>-f v4l2</c>).</summary>
    public static readonly CapturePlatform V4L2 = new(name: "v4l2", value: 1, ffmpegFormat: "v4l2");

    /// <summary>macOS AVFoundation (<c>-f avfoundation</c>).</summary>
    public static readonly CapturePlatform AvFoundation = new(name: "avfoundation", value: 2, ffmpegFormat: "avfoundation");

    private CapturePlatform(string name, int value, string ffmpegFormat)
        : base(name, value)
    {
        FfmpegFormat = ffmpegFormat;
    }

    public string FfmpegFormat { get; }
}
