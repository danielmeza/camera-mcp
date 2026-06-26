using System.Globalization;
using CameraMcp.Server.Models;

namespace CameraMcp.Server.Services;

/// <summary>
/// Builds the platform-specific FFmpeg <em>input</em> arguments (everything up to and including
/// <c>-i</c>) for a capture device. This is the single place that knows how DirectShow, V4L2, and
/// AVFoundation differ, and it is a pure function so it can be exhaustively unit-tested without
/// hardware or spawning a process.
/// </summary>
public static class PlatformDeviceMapper
{
    /// <summary>
    /// Builds the ordered input arguments for a video (optionally audio) capture.
    /// </summary>
    /// <param name="platform">Capture backend.</param>
    /// <param name="videoTarget">
    /// Platform device locator: a friendly name (DirectShow / AVFoundation) or a device path such as
    /// <c>/dev/video0</c> (V4L2).
    /// </param>
    /// <param name="width">Requested width; emitted as <c>-video_size</c> only when paired with <paramref name="height"/>.</param>
    /// <param name="height">Requested height.</param>
    /// <param name="fps">Requested frame rate; emitted as <c>-framerate</c> when positive.</param>
    /// <param name="audioTarget">Optional audio device locator; when null no audio input is added.</param>
    public static IReadOnlyList<string> BuildVideoInput(
        CapturePlatform platform,
        string videoTarget,
        int? width,
        int? height,
        int fps,
        string? audioTarget = null)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentException.ThrowIfNullOrWhiteSpace(videoTarget);

        var args = new List<string> { "-f", platform.FfmpegFormat };

        // Frame rate and size are input options and must precede -i so the device negotiates them.
        if (fps > 0)
        {
            args.Add("-framerate");
            args.Add(fps.ToString(CultureInfo.InvariantCulture));
        }

        if (width is int w && height is int h)
        {
            args.Add("-video_size");
            args.Add($"{w}x{h}");
        }

        if (platform == CapturePlatform.DirectShow)
        {
            // DirectShow muxes video and audio into a single -i specifier.
            args.Add("-i");
            args.Add(audioTarget is null
                ? $"video={videoTarget}"
                : $"video={videoTarget}:audio={audioTarget}");
        }
        else if (platform == CapturePlatform.V4L2)
        {
            args.Add("-i");
            args.Add(videoTarget);
            if (audioTarget is not null)
            {
                // ALSA is a separate input on Linux.
                args.Add("-f");
                args.Add("alsa");
                args.Add("-i");
                args.Add(audioTarget);
            }
        }
        else // AvFoundation: combined "video:audio" specifier.
        {
            args.Add("-i");
            args.Add(audioTarget is null ? videoTarget : $"{videoTarget}:{audioTarget}");
        }

        return args;
    }
}
