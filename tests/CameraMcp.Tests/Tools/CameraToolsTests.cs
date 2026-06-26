using CameraMcp.Server.Models;
using CameraMcp.Server.Services;
using CameraMcp.Server.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace CameraMcp.Tests.Tools;

public class CameraToolsTests
{
    private static CameraTools Tools(ICameraService service) => new(service, new StubCaptureStore());

    [Fact]
    public async Task ListCameras_serializes_devices_to_json()
    {
        var service = new StubCameraService
        {
            Devices = [new CameraDevice("cam0", "Test Cam", "directshow", [new CaptureFormat(1280, 720, "MJPG", 30)])],
        };

        var json = await Tools(service).ListCamerasAsync(CancellationToken.None);

        Assert.Contains("\"count\": 1", json);
        Assert.Contains("Test Cam", json);
        Assert.Contains("cam0", json);
    }

    [Fact]
    public async Task CaptureImage_returns_text_image_then_resource_link()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var service = new StubCameraService
        {
            ImageResult = new ImageCaptureResult(bytes, ImageFormat.Png, 640, 480, "Test Cam", @"/tmp/shot.png"),
        };

        var blocks = (await Tools(service).CaptureImageAsync(format: "png", cancellationToken: CancellationToken.None)).ToList();

        Assert.Equal(3, blocks.Count);
        var text = Assert.IsType<TextContentBlock>(blocks[0]);
        Assert.Contains("\"format\": \"png\"", text.Text);
        Assert.Contains("camera://captures/shot.png", text.Text); // resourceUri in metadata
        var image = Assert.IsType<ImageContentBlock>(blocks[1]);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(bytes, image.DecodedData.ToArray());
        var link = Assert.IsType<ResourceLinkBlock>(blocks[2]);
        Assert.Equal("camera://captures/shot.png", link.Uri);
        Assert.Equal("image/png", link.MimeType);
    }

    [Fact]
    public async Task CaptureImage_translates_domain_error_to_McpException()
    {
        var service = new StubCameraService { ImageException = new CaptureFailedException("No cameras were found on this host.") };

        var ex = await Assert.ThrowsAsync<McpException>(() =>
            Tools(service).CaptureImageAsync(cancellationToken: CancellationToken.None));
        Assert.Equal("No cameras were found on this host.", ex.Message);
    }

    [Fact]
    public async Task CaptureImage_rejects_unknown_format_as_McpException()
    {
        await Assert.ThrowsAsync<McpException>(() =>
            Tools(new StubCameraService()).CaptureImageAsync(format: "gif", cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task CaptureVideo_includes_poster_and_resource_link()
    {
        var service = new StubCameraService
        {
            VideoResult = new VideoCaptureResult(
                "/tmp/clip.mp4", VideoContainer.Mp4, VideoCodec.H264, 1280, 720, 30, 5, 12345, "Test Cam",
                PosterFrame: [0xFF, 0xD8, 0xFF]),
        };

        var blocks = (await Tools(service).CaptureVideoAsync(durationSeconds: 5, cancellationToken: CancellationToken.None)).ToList();

        Assert.Equal(3, blocks.Count); // text + poster + resource_link
        Assert.Contains("/tmp/clip.mp4", Assert.IsType<TextContentBlock>(blocks[0]).Text);
        Assert.Equal("image/jpeg", Assert.IsType<ImageContentBlock>(blocks[1]).MimeType);
        var link = Assert.IsType<ResourceLinkBlock>(blocks[2]);
        Assert.Equal("camera://captures/clip.mp4", link.Uri);
        Assert.Equal("video/mp4", link.MimeType);
    }

    [Fact]
    public async Task CaptureVideo_returns_text_and_link_when_no_poster()
    {
        var service = new StubCameraService
        {
            VideoResult = new VideoCaptureResult(
                "/tmp/clip.mp4", VideoContainer.Mp4, VideoCodec.H264, 1280, 720, 30, 5, 12345, "Test Cam",
                PosterFrame: null),
        };

        var blocks = (await Tools(service).CaptureVideoAsync(durationSeconds: 5, cancellationToken: CancellationToken.None)).ToList();

        Assert.Equal(2, blocks.Count); // text + resource_link (no poster)
        Assert.IsType<TextContentBlock>(blocks[0]);
        Assert.IsType<ResourceLinkBlock>(blocks[1]);
    }

    [Fact]
    public async Task CaptureScene_returns_metadata_then_one_image_block_per_inline_frame()
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0x01 };
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

        var blocks = (await Tools(service).CaptureSceneAsync(frameCount: 3, intervalSeconds: 0.5, cancellationToken: CancellationToken.None)).ToList();

        Assert.Equal(3, blocks.Count); // 1 metadata + 2 inline frames (frame 3 is path-only)
        var text = Assert.IsType<TextContentBlock>(blocks[0]);
        Assert.Contains("\"frameCount\": 3", text.Text);
        Assert.Contains("\"inlineFrameCount\": 2", text.Text);
        Assert.Contains("camera://captures/frame-003.jpg", text.Text); // resourceUri per frame
        Assert.All(blocks.Skip(1), b => Assert.Equal("image/jpeg", Assert.IsType<ImageContentBlock>(b).MimeType));
    }

    private sealed class StubCaptureStore : ICaptureStore
    {
        public ClearResult Clear(string? directory) => throw new NotSupportedException();
        public string? ToResourceUri(string absolutePath) => "camera://captures/" + Path.GetFileName(absolutePath);
        public Task<CaptureContent> ReadCaptureAsync(string relativePath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
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

        public Task<ResolvedCaptureInput> ResolveInputAsync(string? deviceId, int? width, int? height, int fps, CancellationToken cancellationToken) =>
            Task.FromResult(new ResolvedCaptureInput("Test Cam", "directshow:Test Cam", ["-f", "dshow", "-i", "video=Test Cam"], 1280, 720));

        public Task<IAsyncDisposable> AcquireDeviceLockAsync(string lockKey, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable>(new NoopLock());

        private sealed class NoopLock : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
