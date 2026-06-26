using CameraMcp.Server.Models;
using CameraMcp.Server.Services;

namespace CameraMcp.Tests.Services;

public class FFmpegArgumentsTests
{
    private static readonly string[] DeviceInput = ["-f", "dshow", "-i", "video=Cam"];

    // ---- image capture (device + warmup) ----

    [Fact]
    public void ImageCapture_jpeg_grabs_one_frame_after_warmup_to_file()
    {
        var args = FFmpegArguments.BuildImageCaptureArgs(DeviceInput, ImageFormat.Jpeg, 85, warmupFrames: 12, "out.jpg");

        Assert.Equal(DeviceInput, args.Skip(args.ToList().IndexOf("-f")).Take(4));
        // Warmup discards the first frames; the comma inside gte() is escaped for the filter parser.
        AssertAdjacent(args, "-vf", "select=gte(n\\,12)");
        AssertAdjacent(args, "-frames:v", "1");
        AssertAdjacent(args, "-c:v", "mjpeg");
        AssertAdjacent(args, "-q:v", ImageFormat.MapJpegQscale(85).ToString());
        Assert.Contains("-an", args);
        Assert.Equal("out.jpg", args[^1]);
    }

    [Fact]
    public void ImageCapture_png_is_lossless_without_quality()
    {
        var args = FFmpegArguments.BuildImageCaptureArgs(DeviceInput, ImageFormat.Png, 10, warmupFrames: 5, "out.png");

        AssertAdjacent(args, "-c:v", "png");
        Assert.DoesNotContain("-q:v", args);
        Assert.DoesNotContain("-quality", args);
    }

    [Fact]
    public void ImageCapture_omits_warmup_filter_when_zero()
    {
        var args = FFmpegArguments.BuildImageCaptureArgs(DeviceInput, ImageFormat.Jpeg, 85, warmupFrames: 0, "out.jpg");
        Assert.DoesNotContain("-vf", args);
    }

    // ---- scene capture ----

    [Fact]
    public void SceneCapture_samples_at_interval_with_warmup_and_count()
    {
        var args = FFmpegArguments.BuildSceneCaptureArgs(
            DeviceInput, ImageFormat.Jpeg, 85, frameCount: 6, intervalSeconds: 0.5, warmupSeconds: 1.0, "scene-%03d.jpg");

        Assert.Equal(DeviceInput, args.Skip(args.ToList().IndexOf("-f")).Take(4));
        AssertAdjacent(args, "-ss", "1");                  // warmup discard
        AssertAdjacent(args, "-vf", "fps=1/0.5");          // one frame every 0.5s
        AssertAdjacent(args, "-frames:v", "6");
        AssertAdjacent(args, "-c:v", "mjpeg");
        Assert.Contains("-an", args);
        Assert.Equal("scene-%03d.jpg", args[^1]);
    }

    [Fact]
    public void SceneCapture_omits_warmup_when_zero()
    {
        var args = FFmpegArguments.BuildSceneCaptureArgs(
            DeviceInput, ImageFormat.Png, 85, frameCount: 3, intervalSeconds: 2, warmupSeconds: 0, "scene-%03d.png");
        Assert.DoesNotContain("-ss", args);
    }

    [Fact]
    public void SelectExpression_fires_once_per_timestamp()
    {
        var expr = FFmpegArguments.BuildSelectExpression([0, 0.7]);
        Assert.Equal(
            @"select=isnan(prev_selected_t)*gte(t\,0)+gte(t\,0.7)*lt(prev_selected_t\,0.7)",
            expr);
    }

    [Fact]
    public void TimedScene_samples_at_explicit_timestamps()
    {
        var args = FFmpegArguments.BuildTimedSceneArgs(
            DeviceInput, ImageFormat.Jpeg, 85, [0.5, 0.7, 1.2], "scene-%03d.jpg");

        var list = args.ToList();
        var vf = list[list.IndexOf("-vf") + 1];
        Assert.StartsWith("select=", vf);
        Assert.Contains(@"isnan(prev_selected_t)*gte(t\,0.5)", vf);
        Assert.Contains(@"gte(t\,1.2)*lt(prev_selected_t\,1.2)", vf);
        AssertAdjacent(args, "-fps_mode", "passthrough");
        AssertAdjacent(args, "-frames:v", "3"); // one per timestamp
        AssertAdjacent(args, "-c:v", "mjpeg");
        Assert.Equal("scene-%03d.jpg", args[^1]);
    }

    // ---- video capture ----

    [Fact]
    public void VideoCapture_includes_input_duration_codec_and_output()
    {
        var input = new[] { "-f", "dshow", "-i", "video=Cam" };
        var options = new VideoCaptureOptions { DurationSeconds = 5, Codec = VideoCodec.H264, Quality = 75, Fps = 30 };

        var args = FFmpegArguments.BuildVideoCaptureArgs(input, options, "out.mp4");

        Assert.Equal(input, args.Skip(args.ToList().IndexOf("-f")).Take(4));
        AssertAdjacent(args, "-t", "5");
        AssertAdjacent(args, "-c:v", "libx264");
        AssertAdjacent(args, "-crf", VideoCodec.H264.MapCrf(75).ToString());
        AssertAdjacent(args, "-pix_fmt", "yuv420p");
        AssertAdjacent(args, "-preset", "veryfast");
        Assert.Contains("-an", args);
        AssertAdjacent(args, "-f", "mp4");
        Assert.Equal("out.mp4", args[^1]);
    }

    [Fact]
    public void VideoCapture_vp9_crf_mode_sets_zero_bitrate_ceiling()
    {
        var input = new[] { "-f", "v4l2", "-i", "/dev/video0" };
        var options = new VideoCaptureOptions { DurationSeconds = 3, Codec = VideoCodec.Vp9, Container = VideoContainer.Webm };

        var args = FFmpegArguments.BuildVideoCaptureArgs(input, options, "out.webm");

        AssertAdjacent(args, "-crf", VideoCodec.Vp9.MapCrf(options.Quality).ToString());
        AssertAdjacent(args, "-b:v", "0");
        Assert.DoesNotContain("-preset", args); // preset is x264/x265 only
    }

    [Fact]
    public void VideoCapture_bitrate_override_replaces_crf()
    {
        var input = new[] { "-f", "dshow", "-i", "video=Cam" };
        var options = new VideoCaptureOptions { DurationSeconds = 5, BitrateKbps = 2500 };

        var args = FFmpegArguments.BuildVideoCaptureArgs(input, options, "out.mp4");

        AssertAdjacent(args, "-b:v", "2500k");
        Assert.DoesNotContain("-crf", args);
    }

    [Fact]
    public void VideoCapture_with_audio_emits_audio_codec_not_an()
    {
        var input = new[] { "-f", "dshow", "-i", "video=Cam:audio=Mic" };
        var options = new VideoCaptureOptions { DurationSeconds = 5, Audio = true, Container = VideoContainer.Mp4 };

        var args = FFmpegArguments.BuildVideoCaptureArgs(input, options, "out.mp4");

        AssertAdjacent(args, "-c:a", "aac");
        Assert.DoesNotContain("-an", args);
    }

    [Fact]
    public void MjpegStream_produces_a_continuous_multipart_stream_to_pipe()
    {
        var args = FFmpegArguments.BuildMjpegStreamArgs(DeviceInput, 70);

        Assert.Equal(DeviceInput, args.Skip(args.ToList().IndexOf("-f")).Take(4));
        AssertAdjacent(args, "-q:v", ImageFormat.MapJpegQscale(70).ToString());
        AssertAdjacent(args, "-f", "mpjpeg");
        Assert.Equal("pipe:1", args[^1]);
        Assert.DoesNotContain("-frames:v", args); // continuous, not a single frame
    }

    [Fact]
    public void PosterFrame_extracts_single_jpeg_to_pipe()
    {
        var args = FFmpegArguments.BuildPosterFrameArgs("clip.mp4");

        AssertAdjacent(args, "-i", "clip.mp4");
        AssertAdjacent(args, "-frames:v", "1");
        AssertAdjacent(args, "-c:v", "mjpeg");
        Assert.Equal("pipe:1", args[^1]);
    }

    /// <summary>Asserts <paramref name="flag"/> appears immediately followed by <paramref name="value"/> at least once.</summary>
    private static void AssertAdjacent(IReadOnlyList<string> args, string flag, string value)
    {
        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (args[i] == flag && args[i + 1] == value)
            {
                return;
            }
        }

        Assert.Fail($"expected adjacent pair '{flag} {value}' in: {string.Join(' ', args)}");
    }
}
