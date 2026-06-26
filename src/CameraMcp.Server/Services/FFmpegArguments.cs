using System.Globalization;
using CameraMcp.Server.Models;

namespace CameraMcp.Server.Services;

/// <summary>
/// Pure builders that turn capture options into FFmpeg argument lists. The per-format/codec specifics
/// live on the smart enums (<see cref="ImageFormat"/>, <see cref="VideoCodec"/>,
/// <see cref="VideoContainer"/>); this class just orchestrates them. No process is spawned here, so
/// every branch is unit-testable.
///
/// Arguments are returned as discrete tokens (never a single shell string) so the caller can hand
/// them to <c>ProcessStartInfo.ArgumentList</c>, which quotes each one correctly — device names with
/// spaces need no special handling.
/// </summary>
public static class FFmpegArguments
{
    private static readonly string[] CommonPrefix = ["-hide_banner", "-loglevel", "error", "-y"];

    /// <summary>
    /// Captures a single still directly from a device, discarding <paramref name="warmupFrames"/>
    /// initial frames so a cold sensor / not-yet-settled stream does not yield a black image. The
    /// encoded frame is written to <paramref name="outputPath"/>.
    /// </summary>
    public static IReadOnlyList<string> BuildImageCaptureArgs(
        IReadOnlyList<string> inputArgs,
        ImageFormat format,
        int quality,
        int warmupFrames,
        string outputPath)
    {
        ArgumentNullException.ThrowIfNull(inputArgs);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var args = new List<string>(CommonPrefix) { "-nostdin" };
        args.AddRange(inputArgs);

        if (warmupFrames > 0)
        {
            args.Add("-vf");
            args.Add(WarmupSelect(warmupFrames));
        }

        args.Add("-frames:v");
        args.Add("1");
        format.AppendEncoderArgs(args, quality);
        args.Add("-an");
        args.Add(outputPath);
        return args;
    }

    /// <summary>
    /// Captures a sequence of stills at a fixed interval — a "scene" the agent can read frame by frame
    /// instead of as video. Discards <paramref name="warmupSeconds"/> of cold frames, then samples one
    /// frame every <paramref name="intervalSeconds"/> up to <paramref name="frameCount"/> frames,
    /// writing them to <paramref name="outputPattern"/> (e.g. <c>scene-%03d.jpg</c>).
    /// </summary>
    public static IReadOnlyList<string> BuildSceneCaptureArgs(
        IReadOnlyList<string> inputArgs,
        ImageFormat format,
        int quality,
        int frameCount,
        double intervalSeconds,
        double warmupSeconds,
        string outputPattern)
    {
        ArgumentNullException.ThrowIfNull(inputArgs);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPattern);

        var args = new List<string>(CommonPrefix) { "-nostdin" };
        args.AddRange(inputArgs);

        // Output-side -ss decodes and discards the warmup window from the live stream.
        if (warmupSeconds > 0)
        {
            args.Add("-ss");
            args.Add(warmupSeconds.ToString(CultureInfo.InvariantCulture));
        }

        // Sample one frame every interval; let ffmpeg evaluate 1/interval.
        args.Add("-vf");
        args.Add($"fps=1/{intervalSeconds.ToString(CultureInfo.InvariantCulture)}");
        args.Add("-frames:v");
        args.Add(frameCount.ToString(CultureInfo.InvariantCulture));
        format.AppendEncoderArgs(args, quality);
        args.Add("-an");
        args.Add(outputPattern);
        return args;
    }

    /// <summary>
    /// Captures a sequence of stills at explicit (non-uniform) timestamps in a single pass, using a
    /// <c>select</c> expression that picks the first frame at or after each target time. The first
    /// timestamp absorbs the warmup window. Produces one file per timestamp at <paramref name="outputPattern"/>.
    /// </summary>
    public static IReadOnlyList<string> BuildTimedSceneArgs(
        IReadOnlyList<string> inputArgs,
        ImageFormat format,
        int quality,
        IReadOnlyList<double> timestamps,
        string outputPattern)
    {
        ArgumentNullException.ThrowIfNull(inputArgs);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(timestamps);
        if (timestamps.Count == 0)
        {
            throw new ArgumentException("At least one timestamp is required.", nameof(timestamps));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(outputPattern);

        var args = new List<string>(CommonPrefix) { "-nostdin" };
        args.AddRange(inputArgs);
        args.Add("-vf");
        args.Add(BuildSelectExpression(timestamps));
        args.Add("-fps_mode");
        args.Add("passthrough");
        args.Add("-frames:v");
        args.Add(timestamps.Count.ToString(CultureInfo.InvariantCulture));
        format.AppendEncoderArgs(args, quality);
        args.Add("-an");
        args.Add(outputPattern);
        return args;
    }

    /// <summary>
    /// A <c>select</c> expression that fires once per target timestamp: the first via the cold-start
    /// guard, the rest when <c>t</c> crosses the target and the previous selection was earlier. Commas
    /// inside the functions are escaped (\,) for ffmpeg's filtergraph parser.
    /// </summary>
    internal static string BuildSelectExpression(IReadOnlyList<double> timestamps)
    {
        var terms = new List<string>(timestamps.Count)
        {
            $"isnan(prev_selected_t)*gte(t\\,{Time(timestamps[0])})",
        };

        for (var k = 1; k < timestamps.Count; k++)
        {
            var t = Time(timestamps[k]);
            terms.Add($"gte(t\\,{t})*lt(prev_selected_t\\,{t})");
        }

        return "select=" + string.Join("+", terms);

        static string Time(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Builds the full video capture command: the platform input arguments (from
    /// <see cref="PlatformDeviceMapper"/>) followed by duration, codec, rate-control, and the output file.
    /// </summary>
    public static IReadOnlyList<string> BuildVideoCaptureArgs(
        IReadOnlyList<string> inputArgs,
        VideoCaptureOptions options,
        string outputPath)
    {
        ArgumentNullException.ThrowIfNull(inputArgs);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var args = new List<string>(CommonPrefix)
        {
            // Capture reads from the device, never our stdin pipe; stop ffmpeg grabbing the console.
            "-nostdin",
        };
        args.AddRange(inputArgs);

        // Bound the recording length.
        args.Add("-t");
        args.Add(options.DurationSeconds.ToString(CultureInfo.InvariantCulture));

        args.Add("-c:v");
        args.Add(options.Codec.FfmpegEncoder);

        if (options.BitrateKbps is int kbps)
        {
            args.Add("-b:v");
            args.Add($"{kbps}k");
        }
        else
        {
            args.Add("-crf");
            args.Add(options.Codec.MapCrf(options.Quality).ToString(CultureInfo.InvariantCulture));

            if (options.Codec.RequiresZeroBitrateForCrf)
            {
                args.Add("-b:v");
                args.Add("0");
            }
        }

        if (options.Codec.UsesPreset)
        {
            // Keep real-time capture from falling behind.
            args.Add("-preset");
            args.Add("veryfast");
        }

        // Broadly compatible chroma subsampling (players and H.264 hardware decoders expect 4:2:0).
        args.Add("-pix_fmt");
        args.Add("yuv420p");

        // Force the requested output frame rate regardless of what the device actually delivered.
        args.Add("-r");
        args.Add(options.Fps.ToString(CultureInfo.InvariantCulture));

        if (options.Audio)
        {
            args.Add("-c:a");
            args.Add(options.Container.AudioEncoder);
        }
        else
        {
            args.Add("-an");
        }

        args.Add("-f");
        args.Add(options.Container.FfmpegMuxer);
        args.Add(outputPath);
        return args;
    }

    /// <summary>
    /// Builds a continuous MJPEG (<c>multipart/x-mixed-replace</c>, boundary <c>ffmpeg</c>) stream from a
    /// device to stdout (<c>pipe:1</c>) — relayed to a browser for a live preview.
    /// </summary>
    public static IReadOnlyList<string> BuildMjpegStreamArgs(IReadOnlyList<string> inputArgs, int quality)
    {
        ArgumentNullException.ThrowIfNull(inputArgs);
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        args.AddRange(inputArgs);
        args.Add("-q:v");
        args.Add(ImageFormat.MapJpegQscale(quality).ToString(CultureInfo.InvariantCulture));
        args.Add("-f");
        args.Add("mpjpeg");
        args.Add("pipe:1");
        return args;
    }

    /// <summary>The MIME boundary FFmpeg's mpjpeg muxer uses for the multipart stream.</summary>
    public const string MjpegBoundary = "ffmpeg";

    /// <summary>
    /// Extracts the first frame of an encoded video file as a JPEG on stdout (<c>pipe:1</c>),
    /// used to produce the inline poster frame returned alongside a recording.
    /// </summary>
    public static IReadOnlyList<string> BuildPosterFrameArgs(string videoPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        return new List<string>(CommonPrefix)
        {
            "-nostdin",
            "-i", videoPath,
            "-frames:v", "1",
            "-c:v", "mjpeg",
            "-f", "image2pipe",
            "pipe:1",
        };
    }

    /// <summary>
    /// A <c>select</c> filter that drops the first <paramref name="warmupFrames"/> frames. The comma
    /// inside gte() is escaped (\,) so ffmpeg's filtergraph parser does not treat it as a filter
    /// separator (passed as a single argv token, so no shell quoting is involved).
    /// </summary>
    private static string WarmupSelect(int warmupFrames) =>
        $"select=gte(n\\,{warmupFrames.ToString(CultureInfo.InvariantCulture)})";
}
