using System.Globalization;
using Ardalis.SmartEnum;

namespace CameraMcp.Server.Models;

/// <summary>
/// Still-image output format. A smart enum: each value carries its MIME type, file extension, ffmpeg
/// codec, and the logic to append the right encoder + quality arguments.
/// </summary>
public sealed class ImageFormat : SmartEnum<ImageFormat>
{
    public static readonly ImageFormat Jpeg =
        new(name: "jpeg", value: 0, mimeType: "image/jpeg", ffmpegCodec: "mjpeg", fileExtension: "jpg", isLossless: false);

    public static readonly ImageFormat Png =
        new(name: "png", value: 1, mimeType: "image/png", ffmpegCodec: "png", fileExtension: "png", isLossless: true);

    public static readonly ImageFormat Webp =
        new(name: "webp", value: 2, mimeType: "image/webp", ffmpegCodec: "libwebp", fileExtension: "webp", isLossless: false);

    private ImageFormat(string name, int value, string mimeType, string ffmpegCodec, string fileExtension, bool isLossless)
        : base(name, value)
    {
        MimeType = mimeType;
        FfmpegCodec = ffmpegCodec;
        FileExtension = fileExtension;
        IsLossless = isLossless;
    }

    public string MimeType { get; }
    public string FfmpegCodec { get; }
    public string FileExtension { get; }
    public bool IsLossless { get; }

    /// <summary>Parses a user token (case-insensitive, with the <c>jpg</c> alias), or throws.</summary>
    public static ImageFormat FromToken(string? token, ImageFormat fallback)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return fallback;
        }

        var normalized = token.Trim().ToLowerInvariant();
        if (normalized == "jpg")
        {
            normalized = "jpeg";
        }

        return TryFromName(normalized, ignoreCase: true, out var format)
            ? format
            : throw new CaptureValidationException(
                $"Unknown image format '{token}'. Allowed values: {AllowedTokens}.");
    }

    public static string AllowedTokens => string.Join(", ", List.Select(f => f.Name));

    /// <summary>Maps a 1..100 quality to an MJPEG <c>-q:v</c> qscale (2 = best, 31 = worst).</summary>
    public static int MapJpegQscale(int quality)
    {
        CaptureOptionsValidation.ValidateQuality(quality);
        var t = (quality - 1) / 99.0;
        return (int)Math.Round(31 + t * (2 - 31), MidpointRounding.AwayFromZero);
    }

    /// <summary>Appends the codec and quality arguments for this format to <paramref name="args"/>.</summary>
    public void AppendEncoderArgs(IList<string> args, int quality)
    {
        args.Add("-c:v");
        args.Add(FfmpegCodec);

        if (this == Jpeg)
        {
            args.Add("-q:v");
            args.Add(MapJpegQscale(quality).ToString(CultureInfo.InvariantCulture));
        }
        else if (this == Webp)
        {
            // libwebp quality maps straight through (0..100, higher is better).
            args.Add("-quality");
            args.Add(quality.ToString(CultureInfo.InvariantCulture));
        }

        // PNG is lossless: quality has no pixel effect, so it is intentionally not applied.
    }
}
