using System.ComponentModel;
using CameraMcp.Server.Models;
using CameraMcp.Server.Services;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CameraMcp.Server.Tools;

/// <summary>
/// The MCP tool surface for the camera server. Each method is a thin adapter: it builds a validated
/// options object, calls <see cref="ICameraService"/>, and shapes the result into MCP content. Domain
/// errors are translated into <see cref="McpException"/> so the agent receives an actionable message.
/// </summary>
[McpServerToolType]
public sealed class CameraTools
{
    private readonly ICameraService _camera;

    public CameraTools(ICameraService camera)
    {
        _camera = camera;
    }

    [McpServerTool(Name = "list_cameras"),
     Description("Lists the cameras attached to this host and the capture formats each supports. " +
                 "Use the returned 'id' (or the device name) with capture_image and capture_video.")]
    public async Task<string> ListCamerasAsync(CancellationToken cancellationToken)
    {
        try
        {
            var devices = await _camera.ListDevicesAsync(cancellationToken).ConfigureAwait(false);
            return CameraJson.Serialize(new { count = devices.Count, cameras = devices });
        }
        catch (Exception ex) when (IsDomainError(ex))
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "capture_image"),
     Description("Captures a single still image from a camera and returns it inline. " +
                 "Choose format (jpeg/png/webp) and quality (1-100).")]
    public async Task<IEnumerable<ContentBlock>> CaptureImageAsync(
        [Description("Camera id or name from list_cameras. Omit to use the first camera.")]
        string? deviceId = null,
        [Description("Desired frame width in pixels; snapped to the nearest supported mode. Provide with height.")]
        int? width = null,
        [Description("Desired frame height in pixels; snapped to the nearest supported mode. Provide with width.")]
        int? height = null,
        [Description("Output image format: jpeg, png, or webp. Default jpeg.")]
        string? format = null,
        [Description("Encoder quality from 1 (smallest) to 100 (best). Default 85. Ignored for png (lossless).")]
        int quality = 85,
        [Description("Seconds to wait before capturing. Default 0 (as soon as possible).")]
        double startDelaySeconds = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var options = new ImageCaptureOptions
            {
                DeviceId = deviceId,
                Width = width,
                Height = height,
                Format = ImageFormat.FromToken(format, ImageFormat.Jpeg),
                Quality = quality,
                StartDelaySeconds = startDelaySeconds,
            };

            var result = await _camera.CaptureImageAsync(options, cancellationToken).ConfigureAwait(false);

            var metadata = CameraJson.Serialize(new
            {
                device = result.DeviceName,
                format = result.Format.Name,
                width = result.Width,
                height = result.Height,
                bytes = result.Bytes.Length,
                path = result.FilePath,
            });

            return
            [
                new TextContentBlock { Text = metadata },
                ImageContentBlock.FromBytes(result.Bytes, result.MimeType),
            ];
        }
        catch (Exception ex) when (IsDomainError(ex))
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "capture_video"),
     Description("Records a fixed-duration video clip from a camera to a file on the host, and returns " +
                 "the file path, metadata, and a single poster frame inline. Choose container " +
                 "(mp4/webm/mkv), codec (h264/h265/vp9), fps, and quality (1-100).")]
    public async Task<IEnumerable<ContentBlock>> CaptureVideoAsync(
        [Description("Recording length in seconds. Required; capped by the server's configured maximum.")]
        double durationSeconds,
        [Description("Camera id or name from list_cameras. Omit to use the first camera.")]
        string? deviceId = null,
        [Description("Desired frame width in pixels; snapped to the nearest supported mode. Provide with height.")]
        int? width = null,
        [Description("Desired frame height in pixels; snapped to the nearest supported mode. Provide with width.")]
        int? height = null,
        [Description("Output frame rate. Default 30.")]
        int fps = 30,
        [Description("Container format: mp4, webm, or mkv. Default mp4. (webm requires the vp9 codec.)")]
        string? container = null,
        [Description("Video codec: h264, h265, or vp9. Default h264. (mp4 needs h264/h265; webm needs vp9.)")]
        string? codec = null,
        [Description("Quality from 1 (smallest) to 100 (best). Default 75. Ignored when bitrateKbps is set.")]
        int quality = 75,
        [Description("Optional constant target bitrate in kbit/s; overrides quality when set.")]
        int? bitrateKbps = null,
        [Description("Optional output file path. Omit to write into the server's capture directory.")]
        string? outputPath = null,
        [Description("Seconds to wait before recording starts. Default 0 (as soon as possible).")]
        double startDelaySeconds = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var options = new VideoCaptureOptions
            {
                DeviceId = deviceId,
                DurationSeconds = durationSeconds,
                Width = width,
                Height = height,
                Fps = fps,
                Container = VideoContainer.FromToken(container, VideoContainer.Mp4),
                Codec = VideoCodec.FromToken(codec, VideoCodec.H264),
                Quality = quality,
                BitrateKbps = bitrateKbps,
                OutputPath = outputPath,
                StartDelaySeconds = startDelaySeconds,
            };

            var result = await _camera.CaptureVideoAsync(options, cancellationToken).ConfigureAwait(false);

            var metadata = CameraJson.Serialize(new
            {
                path = result.FilePath,
                device = result.DeviceName,
                container = result.Container.Name,
                codec = result.Codec.Name,
                width = result.Width,
                height = result.Height,
                fps = result.Fps,
                durationSeconds = result.DurationSeconds,
                fileSizeBytes = result.FileSizeBytes,
                posterIncluded = result.PosterFrame is not null,
            });

            var blocks = new List<ContentBlock> { new TextContentBlock { Text = metadata } };
            if (result.PosterFrame is { Length: > 0 } poster)
            {
                blocks.Add(ImageContentBlock.FromBytes(poster, VideoCaptureResult.PosterMimeType));
            }

            return blocks;
        }
        catch (Exception ex) when (IsDomainError(ex))
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "capture_scene"),
     Description("Captures a sequence of still frames from a camera and returns them as inline images in " +
                 "order — a lightweight, model-readable alternative to video. Each frame is also saved to " +
                 "disk. Timing is uniform (frameCount + intervalSeconds, which defaults to the server's " +
                 "configured interval) OR non-uniform (an 'intervals' array giving the gap before each " +
                 "subsequent frame, producing intervals.length + 1 frames). Use to observe motion, " +
                 "animation, or UI changes over time.")]
    public async Task<IEnumerable<ContentBlock>> CaptureSceneAsync(
        [Description("Number of frames for uniform timing (at least 2; capped by the server). Omit when using 'intervals'.")]
        int? frameCount = null,
        [Description("Uniform seconds between frames, e.g. 0.5 for two frames per second. Omit to use the server default.")]
        double? intervalSeconds = null,
        [Description("Non-uniform per-gap seconds, e.g. [0.2, 0.5, 1.0] captures 4 frames at t=0, 0.2, 0.7, 1.7. Overrides frameCount/intervalSeconds.")]
        double[]? intervals = null,
        [Description("Camera id or name from list_cameras. Omit to use the first camera.")]
        string? deviceId = null,
        [Description("Desired frame width in pixels; snapped to the nearest supported mode. Provide with height.")]
        int? width = null,
        [Description("Desired frame height in pixels; snapped to the nearest supported mode. Provide with width.")]
        int? height = null,
        [Description("Image format for every frame: jpeg, png, or webp. Default jpeg.")]
        string? format = null,
        [Description("Encoder quality from 1 (smallest) to 100 (best). Default 85. Ignored for png.")]
        int quality = 85,
        [Description("Optional output directory for the frame files; otherwise a per-scene folder is created.")]
        string? outputDirectory = null,
        [Description("Seconds to wait before the scene begins. Default 0 (as soon as possible).")]
        double startDelaySeconds = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var options = new SceneCaptureOptions
            {
                DeviceId = deviceId,
                FrameCount = frameCount ?? 0,
                IntervalSeconds = intervalSeconds,
                Intervals = intervals,
                Width = width,
                Height = height,
                Format = ImageFormat.FromToken(format, ImageFormat.Jpeg),
                Quality = quality,
                OutputDirectory = outputDirectory,
                StartDelaySeconds = startDelaySeconds,
            };

            var result = await _camera.CaptureSceneAsync(options, cancellationToken).ConfigureAwait(false);

            var inlineCount = result.Frames.Count(f => f.Bytes is not null);
            var metadata = CameraJson.Serialize(new
            {
                device = result.DeviceName,
                format = result.Format.Name,
                frameCount = result.Frames.Count,
                inlineFrameCount = inlineCount,
                timing = options.IsNonUniform ? "non-uniform" : "uniform",
                width = result.Width,
                height = result.Height,
                outputDirectory = result.OutputDirectory,
                frames = result.Frames.Select(f => new { index = f.Index, path = f.FilePath, bytes = f.SizeBytes, inline = f.Bytes is not null }),
            });

            // All frames are on disk (paths above); a bounded prefix is also returned inline so the
            // agent can read the sequence without re-fetching, without an unbounded response.
            var blocks = new List<ContentBlock> { new TextContentBlock { Text = metadata } };
            foreach (var frame in result.Frames)
            {
                if (frame.Bytes is { Length: > 0 } bytes)
                {
                    blocks.Add(ImageContentBlock.FromBytes(bytes, result.Format.MimeType));
                }
            }

            return blocks;
        }
        catch (Exception ex) when (IsDomainError(ex))
        {
            throw new McpException(ex.Message);
        }
    }

    private static bool IsDomainError(Exception ex) =>
        ex is CaptureValidationException or CaptureFailedException or FFmpegNotFoundException;
}
