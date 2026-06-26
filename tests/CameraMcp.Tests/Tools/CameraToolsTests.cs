using CameraMcp.Server.Models;
using CameraMcp.Server.Services;
using CameraMcp.Server.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace CameraMcp.Tests.Tools;

public class CameraToolsTests
{
    [Fact]
    public async Task ListCameras_serializes_devices_to_json()
    {
        var service = new StubCameraService
        {
            Devices =
            [
                new CameraDevice("cam0", "Test Cam", "directshow", [new CaptureFormat(1280, 720, "MJPG", 30)]),
            ],
        };
        var tools = new CameraTools(service);

        var json = await tools.ListCamerasAsync(CancellationToken.None);

        Assert.Contains("\"count\": 1", json);
        Assert.Contains("Test Cam", json);
        Assert.Contains("cam0", json);
    }

    [Fact]
    public async Task CaptureImage_returns_text_then_image_block()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var service = new StubCameraService
        {
            ImageResult = new ImageCaptureResult(bytes, ImageFormat.Png, 640, 480, "Test Cam", @"/tmp/shot.png"),
        };
        var tools = new CameraTools(service);

        var blocks = (await tools.CaptureImageAsync(format: "png", cancellationToken: CancellationToken.None)).ToList();

        Assert.Equal(2, blocks.Count);
        var text = Assert.IsType<TextContentBlock>(blocks[0]);
        Assert.Contains("\"format\": \"png\"", text.Text);
        Assert.Contains("/tmp/shot.png", text.Text);
        var image = Assert.IsType<ImageContentBlock>(blocks[1]);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(bytes, image.DecodedData.ToArray());
    }

    [Fact]
    public async Task CaptureImage_translates_domain_error_to_McpException()
    {
        var service = new StubCameraService { ImageException = new CaptureFailedException("No cameras were found on this host.") };
        var tools = new CameraTools(service);

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            tools.CaptureImageAsync(cancellationToken: CancellationToken.None));
        Assert.Equal("No cameras were found on this host.", ex.Message);
    }

    [Fact]
    public async Task CaptureImage_rejects_unknown_format_as_McpException()
    {
        var tools = new CameraTools(new StubCameraService());

        await Assert.ThrowsAsync<McpException>(() =>
            tools.CaptureImageAsync(format: "gif", cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task CaptureVideo_includes_poster_image_when_present()
    {
        var service = new StubCameraService
        {
            VideoResult = new VideoCaptureResult(
                "/tmp/clip.mp4", VideoContainer.Mp4, VideoCodec.H264, 1280, 720, 30, 5, 12345, "Test Cam",
                PosterFrame: [0xFF, 0xD8, 0xFF]),
        };
        var tools = new CameraTools(service);

        var blocks = (await tools.CaptureVideoAsync(durationSeconds: 5, cancellationToken: CancellationToken.None)).ToList();

        Assert.Equal(2, blocks.Count);
        Assert.Contains("/tmp/clip.mp4", Assert.IsType<TextContentBlock>(blocks[0]).Text);
        Assert.Equal("image/jpeg", Assert.IsType<ImageContentBlock>(blocks[1]).MimeType);
    }

    [Fact]
    public async Task CaptureVideo_returns_only_text_when_no_poster()
    {
        var service = new StubCameraService
        {
            VideoResult = new VideoCaptureResult(
                "/tmp/clip.mp4", VideoContainer.Mp4, VideoCodec.H264, 1280, 720, 30, 5, 12345, "Test Cam",
                PosterFrame: null),
        };
        var tools = new CameraTools(service);

        var blocks = (await tools.CaptureVideoAsync(durationSeconds: 5, cancellationToken: CancellationToken.None)).ToList();

        Assert.Single(blocks);
        Assert.IsType<TextContentBlock>(blocks[0]);
    }

    [Fact]
    public async Task CaptureScene_returns_metadata_then_one_image_block_per_frame()
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0x01 };
        // Frames 1 and 2 are inline (Bytes set); frame 3 exceeded the inline cap (path-only).
        var frames = new List<SceneFrame>
        {
            new(1, "/tmp/scene/frame-001.jpg", jpeg.Length, jpeg),
            new(2, "/tmp/scene/frame-002.jpg", jpeg.Length, jpeg),
            new(3, "/tmp/scene/frame-003.jpg", jpeg.Length, Bytes: null),
        };
        var service = new StubCameraService
        {
            SceneResult = new SceneCaptureResult("Test Cam", ImageFormat.Jpeg, 640, 480, "/tmp/scene", frames),
        };
        var tools = new CameraTools(service);

        var blocks = (await tools.CaptureSceneAsync(frameCount: 3, intervalSeconds: 0.5, cancellationToken: CancellationToken.None)).ToList();

        Assert.Equal(3, blocks.Count); // 1 metadata + 2 inline frames (frame 3 is path-only)
        var text = Assert.IsType<TextContentBlock>(blocks[0]);
        Assert.Contains("\"frameCount\": 3", text.Text);
        Assert.Contains("\"inlineFrameCount\": 2", text.Text);
        Assert.Contains("frame-003.jpg", text.Text); // all frames listed by path, even path-only ones
        Assert.All(blocks.Skip(1), b => Assert.Equal("image/jpeg", Assert.IsType<ImageContentBlock>(b).MimeType));
    }

    private sealed class StubCameraService : ICameraService
    {
        public IReadOnlyList<CameraDevice> Devices { get; init; } = [];
        public ImageCaptureResult? ImageResult { get; init; }
        public Exception? ImageException { get; init; }
        public VideoCaptureResult? VideoResult { get; init; }
        public SceneCaptureResult? SceneResult { get; init; }

        public Task<IReadOnlyList<CameraDevice>> ListDevicesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Devices);

        public Task<ImageCaptureResult> CaptureImageAsync(ImageCaptureOptions options, CancellationToken cancellationToken)
        {
            if (ImageException is not null)
            {
                throw ImageException;
            }

            return Task.FromResult(ImageResult ?? throw new InvalidOperationException("no stub result configured"));
        }

        public Task<VideoCaptureResult> CaptureVideoAsync(VideoCaptureOptions options, CancellationToken cancellationToken) =>
            Task.FromResult(VideoResult ?? throw new InvalidOperationException("no stub result configured"));

        public Task<SceneCaptureResult> CaptureSceneAsync(SceneCaptureOptions options, CancellationToken cancellationToken) =>
            Task.FromResult(SceneResult ?? throw new InvalidOperationException("no stub result configured"));
    }
}
