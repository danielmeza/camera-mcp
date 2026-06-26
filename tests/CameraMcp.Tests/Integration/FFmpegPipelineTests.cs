using CameraMcp.Server.Configuration;
using CameraMcp.Server.Models;
using CameraMcp.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CameraMcp.Tests.Integration;

/// <summary>
/// Exercises the real ffmpeg-backed pipeline (ProcessRunner + arg builders + encoder/recorder) against
/// a synthetic <c>lavfi</c> source, so the capture machinery is proven end-to-end without a camera.
/// Skipped when no ffmpeg is available.
/// </summary>
[Trait("Category", "Integration")]
public class FFmpegPipelineTests
{
    private static readonly byte[] JpegMagic = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47];
    private static readonly byte[] WebpRiff = [0x52, 0x49, 0x46, 0x46]; // "RIFF"

    private static readonly IOptions<CameraMcpOptions> Options =
        Microsoft.Extensions.Options.Options.Create(new CameraMcpOptions { FFmpegTimeoutSeconds = 60 });

    private static readonly IProcessRunner Runner = new ProcessRunner();

    private static string? LocateFFmpeg()
    {
        try
        {
            return new FFmpegLocator(Options).Resolve();
        }
        catch (FFmpegNotFoundException)
        {
            return null;
        }
    }

    private static byte[] MagicFor(ImageFormat format) =>
        format == ImageFormat.Jpeg ? JpegMagic : format == ImageFormat.Png ? PngMagic : WebpRiff;

    [Theory]
    [InlineData("jpeg", "jpg")]
    [InlineData("png", "png")]
    [InlineData("webp", "webp")]
    public async Task StillCapturer_captures_each_format_from_synthetic_source(string formatName, string ext)
    {
        var ffmpeg = LocateFFmpeg();
        if (ffmpeg is null)
        {
            return; // ffmpeg unavailable on this host: integration test is not applicable.
        }

        var format = ImageFormat.FromName(formatName);
        var capturer = new FFmpegStillCapturer(new FFmpegLocator(Options), Runner, Options);
        var outputPath = Path.Combine(Path.GetTempPath(), $"camera-mcp-test-{Guid.NewGuid():N}.{ext}");

        // Synthetic capture source standing in for a real device's input arguments.
        var input = new[] { "-f", "lavfi", "-i", "testsrc=size=320x240:rate=15" };
        var options = new ImageCaptureOptions { Format = format, Quality = 80 };

        try
        {
            var bytes = await capturer.CaptureAsync(input, options, outputPath, CancellationToken.None);

            Assert.True(File.Exists(outputPath), "capture file was not created");
            Assert.NotEmpty(bytes);
            var expectedMagic = MagicFor(format);
            Assert.True(bytes.Take(expectedMagic.Length).SequenceEqual(expectedMagic),
                $"{format.Name} output did not start with the expected file signature.");
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [Fact]
    public async Task VideoRecorder_records_clip_and_extracts_poster()
    {
        var ffmpeg = LocateFFmpeg();
        if (ffmpeg is null)
        {
            return; // ffmpeg unavailable on this host: integration test is not applicable.
        }

        var recorder = new FFmpegVideoRecorder(
            new FFmpegLocator(Options), Runner, Options, NullLogger<FFmpegVideoRecorder>.Instance);

        var outputPath = Path.Combine(Path.GetTempPath(), $"camera-mcp-test-{Guid.NewGuid():N}.mp4");
        var options = new VideoCaptureOptions { DurationSeconds = 1, Container = VideoContainer.Mp4, Codec = VideoCodec.H264, Fps = 15 };

        // Synthetic capture source standing in for a real device's input arguments.
        var input = new[] { "-f", "lavfi", "-i", "testsrc=size=320x240:rate=15" };

        try
        {
            var recorded = await recorder.RecordAsync(input, options, outputPath, CancellationToken.None);

            Assert.True(File.Exists(outputPath), "recording file was not created");
            Assert.True(recorded.FileSizeBytes > 0, "recording file is empty");
            Assert.NotNull(recorded.PosterFrame);
            Assert.True(recorded.PosterFrame!.Take(JpegMagic.Length).SequenceEqual(JpegMagic),
                "poster frame is not a JPEG");
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [Fact]
    public async Task SceneCapturer_produces_a_sequence_of_frames()
    {
        var ffmpeg = LocateFFmpeg();
        if (ffmpeg is null)
        {
            return; // ffmpeg unavailable on this host: integration test is not applicable.
        }

        var capturer = new FFmpegSceneCapturer(new FFmpegLocator(Options), Runner, Options);
        var outputDir = Path.Combine(Path.GetTempPath(), $"camera-mcp-scene-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);

        var input = new[] { "-f", "lavfi", "-i", "testsrc=size=320x240:rate=15" };
        var options = new SceneCaptureOptions { FrameCount = 4, IntervalSeconds = 0.2, Format = ImageFormat.Jpeg, Quality = 80 };

        try
        {
            var frames = await capturer.CaptureAsync(input, options, outputDir, warmupSeconds: 0, CancellationToken.None);

            Assert.Equal(4, frames.Count);
            Assert.Equal(new[] { 1, 2, 3, 4 }, frames.Select(f => f.Index));
            Assert.All(frames, f =>
            {
                Assert.True(File.Exists(f.FilePath), "frame file missing");
                Assert.True(f.SizeBytes > 0, "frame size not recorded");
                Assert.NotNull(f.Bytes); // 4 frames is below the inline cap, so all are inlined
                Assert.True(f.Bytes!.Take(JpegMagic.Length).SequenceEqual(JpegMagic), "frame is not a JPEG");
            });

            // testsrc animates, so sampling across time must yield genuinely different frames
            // (guards against the sampler duplicating a single frame).
            var distinct = frames.Select(f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(f.Bytes!))).Distinct().Count();
            Assert.True(distinct > 1, "scene frames from an animated source should not all be identical");
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SceneCapturer_caps_inline_frames_but_lists_all_on_disk()
    {
        var ffmpeg = LocateFFmpeg();
        if (ffmpeg is null)
        {
            return;
        }

        var cappedOptions = Microsoft.Extensions.Options.Options.Create(
            new CameraMcpOptions { FFmpegTimeoutSeconds = 60, MaxInlineSceneFrames = 2 });
        var capturer = new FFmpegSceneCapturer(new FFmpegLocator(cappedOptions), Runner, cappedOptions);
        var outputDir = Path.Combine(Path.GetTempPath(), $"camera-mcp-scene-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var input = new[] { "-f", "lavfi", "-i", "testsrc=size=160x120:rate=15" };
        var options = new SceneCaptureOptions { FrameCount = 5, IntervalSeconds = 0.2, Format = ImageFormat.Jpeg, Quality = 80 };

        try
        {
            var frames = await capturer.CaptureAsync(input, options, outputDir, warmupSeconds: 0, CancellationToken.None);

            Assert.Equal(5, frames.Count);                                  // all 5 are reported (with paths)
            Assert.All(frames, f => Assert.True(File.Exists(f.FilePath)));  // all 5 saved to disk
            Assert.Equal(2, frames.Count(f => f.Bytes is not null));        // only 2 inlined
            Assert.Equal(2, frames.Take(2).Count(f => f.Bytes is not null)); // the inlined ones are the prefix
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SceneCapturer_clears_stale_frames_from_a_reused_directory()
    {
        var ffmpeg = LocateFFmpeg();
        if (ffmpeg is null)
        {
            return;
        }

        var capturer = new FFmpegSceneCapturer(new FFmpegLocator(Options), Runner, Options);
        var outputDir = Path.Combine(Path.GetTempPath(), $"camera-mcp-scene-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);

        // Simulate a prior, larger capture leaving higher-indexed frames behind.
        await File.WriteAllBytesAsync(Path.Combine(outputDir, "frame-007.jpg"), JpegMagic);
        await File.WriteAllBytesAsync(Path.Combine(outputDir, "frame-008.jpg"), JpegMagic);

        var input = new[] { "-f", "lavfi", "-i", "testsrc=size=160x120:rate=15" };
        var options = new SceneCaptureOptions { FrameCount = 3, IntervalSeconds = 0.2, Format = ImageFormat.Jpeg, Quality = 80 };

        try
        {
            var frames = await capturer.CaptureAsync(input, options, outputDir, warmupSeconds: 0, CancellationToken.None);

            Assert.Equal(3, frames.Count); // exactly the requested count — stale frame-007/008 were cleared
            Assert.Equal(new[] { 1, 2, 3 }, frames.Select(f => f.Index));
            Assert.False(File.Exists(Path.Combine(outputDir, "frame-007.jpg")));
            Assert.False(File.Exists(Path.Combine(outputDir, "frame-008.jpg")));
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SceneCapturer_supports_non_uniform_intervals()
    {
        var ffmpeg = LocateFFmpeg();
        if (ffmpeg is null)
        {
            return;
        }

        var capturer = new FFmpegSceneCapturer(new FFmpegLocator(Options), Runner, Options);
        var outputDir = Path.Combine(Path.GetTempPath(), $"camera-mcp-scene-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        var input = new[] { "-f", "lavfi", "-i", "testsrc=size=160x120:rate=30" };
        // 3 gaps -> 4 frames at t = 0, 0.2, 0.7, 1.0
        var options = new SceneCaptureOptions { Intervals = [0.2, 0.5, 0.3], Format = ImageFormat.Jpeg, Quality = 80 };

        try
        {
            var frames = await capturer.CaptureAsync(input, options, outputDir, warmupSeconds: 0, CancellationToken.None);

            Assert.Equal(4, frames.Count);
            Assert.Equal(new[] { 1, 2, 3, 4 }, frames.Select(f => f.Index));
            // Animated source sampled at distinct times -> distinct frames.
            var distinct = frames.Select(f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(f.Bytes!))).Distinct().Count();
            Assert.True(distinct > 1, "non-uniform scene frames should not all be identical");
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, recursive: true);
            }
        }
    }
}
