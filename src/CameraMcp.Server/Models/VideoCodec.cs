using Ardalis.SmartEnum;

namespace CameraMcp.Server.Models;

/// <summary>
/// Video codec. A smart enum carrying its ffmpeg encoder, its usable CRF range, and the quality→CRF
/// mapping. The CRF range is the practical sweet spot per encoder, not the full theoretical span.
/// </summary>
public sealed class VideoCodec : SmartEnum<VideoCodec>
{
    public static readonly VideoCodec H264 =
        new(name: "h264", value: 0, ffmpegEncoder: "libx264", crfWorst: 40, crfBest: 18, usesPreset: true);

    public static readonly VideoCodec H265 =
        new(name: "h265", value: 1, ffmpegEncoder: "libx265", crfWorst: 40, crfBest: 18, usesPreset: true);

    public static readonly VideoCodec Vp9 =
        new(name: "vp9", value: 2, ffmpegEncoder: "libvpx-vp9", crfWorst: 45, crfBest: 20, usesPreset: false);

    private VideoCodec(string name, int value, string ffmpegEncoder, int crfWorst, int crfBest, bool usesPreset)
        : base(name, value)
    {
        FfmpegEncoder = ffmpegEncoder;
        CrfWorst = crfWorst;
        CrfBest = crfBest;
        UsesPreset = usesPreset;
    }

    public string FfmpegEncoder { get; }

    /// <summary>CRF for quality 1 (smallest file, worst picture).</summary>
    public int CrfWorst { get; }

    /// <summary>CRF for quality 100 (best picture, largest file).</summary>
    public int CrfBest { get; }

    /// <summary>Whether this encoder takes an x264/x265-style <c>-preset</c>.</summary>
    public bool UsesPreset { get; }

    /// <summary>libvpx-vp9 only honours <c>-crf</c> as a quality target when the bitrate ceiling is 0.</summary>
    public bool RequiresZeroBitrateForCrf => this == Vp9;

    /// <summary>Maps a 1..100 quality onto this codec's CRF range (higher quality → lower CRF).</summary>
    public int MapCrf(int quality)
    {
        CaptureOptionsValidation.ValidateQuality(quality);
        var t = (quality - 1) / 99.0;
        return (int)Math.Round(CrfWorst + t * (CrfBest - CrfWorst), MidpointRounding.AwayFromZero);
    }

    public static VideoCodec FromToken(string? token, VideoCodec fallback)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return fallback;
        }

        var normalized = token.Trim().ToLowerInvariant() switch
        {
            "avc" => "h264",
            "hevc" => "h265",
            var other => other,
        };

        return TryFromName(normalized, ignoreCase: true, out var codec)
            ? codec
            : throw new CaptureValidationException(
                $"Unknown video codec '{token}'. Allowed values: {AllowedTokens}.");
    }

    public static string AllowedTokens => string.Join(", ", List.Select(c => c.Name));
}
