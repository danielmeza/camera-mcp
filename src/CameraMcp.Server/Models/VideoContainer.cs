using Ardalis.SmartEnum;

namespace CameraMcp.Server.Models;

/// <summary>
/// Video container. A smart enum carrying its ffmpeg muxer, default audio encoder, file extension,
/// and which codecs it can mux.
/// </summary>
public sealed class VideoContainer : SmartEnum<VideoContainer>
{
    public static readonly VideoContainer Mp4 =
        new(name: "mp4", value: 0, ffmpegMuxer: "mp4", audioEncoder: "aac");

    public static readonly VideoContainer Webm =
        new(name: "webm", value: 1, ffmpegMuxer: "webm", audioEncoder: "libopus");

    public static readonly VideoContainer Mkv =
        new(name: "mkv", value: 2, ffmpegMuxer: "matroska", audioEncoder: "aac");

    private VideoContainer(string name, int value, string ffmpegMuxer, string audioEncoder)
        : base(name, value)
    {
        FfmpegMuxer = ffmpegMuxer;
        AudioEncoder = audioEncoder;
    }

    public string FfmpegMuxer { get; }
    public string AudioEncoder { get; }
    public string FileExtension => Name;

    /// <summary>Whether this container can mux <paramref name="codec"/>.</summary>
    public bool Supports(VideoCodec codec)
    {
        if (this == Mkv)
        {
            return true;
        }

        if (this == Mp4)
        {
            return codec == VideoCodec.H264 || codec == VideoCodec.H265;
        }

        return codec == VideoCodec.Vp9; // Webm
    }

    /// <summary>A human hint for the codecs this container accepts, used in validation messages.</summary>
    public string CompatibleCodecHint => this == Webm ? "vp9" : "h264 or h265";

    public static VideoContainer FromToken(string? token, VideoContainer fallback)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return fallback;
        }

        return TryFromName(token.Trim(), ignoreCase: true, out var container)
            ? container
            : throw new CaptureValidationException(
                $"Unknown video container '{token}'. Allowed values: {AllowedTokens}.");
    }

    public static string AllowedTokens => string.Join(", ", List.Select(c => c.Name));
}
