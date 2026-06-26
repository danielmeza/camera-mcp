using CameraMcp.Server.Models;

namespace CameraMcp.Tests.Models;

public class SmartEnumsTests
{
    // ---- ImageFormat ----

    [Theory]
    [InlineData("jpeg", "jpeg")]
    [InlineData("JPG", "jpeg")]
    [InlineData("Png", "png")]
    [InlineData("webp", "webp")]
    public void ImageFormat_FromToken_accepts_known_values_and_jpg_alias(string input, string expectedName)
    {
        Assert.Equal(expectedName, ImageFormat.FromToken(input, ImageFormat.Png).Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ImageFormat_FromToken_uses_fallback_for_empty(string? input)
    {
        Assert.Same(ImageFormat.Webp, ImageFormat.FromToken(input, ImageFormat.Webp));
    }

    [Fact]
    public void ImageFormat_FromToken_throws_on_unknown()
    {
        var ex = Assert.Throws<CaptureValidationException>(() => ImageFormat.FromToken("gif", ImageFormat.Jpeg));
        Assert.Contains("jpeg, png, webp", ex.Message);
    }

    [Fact]
    public void ImageFormat_carries_its_data()
    {
        Assert.Equal("image/jpeg", ImageFormat.Jpeg.MimeType);
        Assert.Equal("jpg", ImageFormat.Jpeg.FileExtension);
        Assert.Equal("mjpeg", ImageFormat.Jpeg.FfmpegCodec);
        Assert.False(ImageFormat.Jpeg.IsLossless);
        Assert.True(ImageFormat.Png.IsLossless);
        Assert.Equal("image/webp", ImageFormat.Webp.MimeType);
    }

    [Fact]
    public void ImageFormat_MapJpegQscale_spans_best_to_worst()
    {
        Assert.Equal(2, ImageFormat.MapJpegQscale(100));
        Assert.Equal(31, ImageFormat.MapJpegQscale(1));
    }

    [Fact]
    public void ImageFormat_AppendEncoderArgs_per_format()
    {
        var jpeg = new List<string>();
        ImageFormat.Jpeg.AppendEncoderArgs(jpeg, 85);
        Assert.Equal(new[] { "-c:v", "mjpeg", "-q:v", ImageFormat.MapJpegQscale(85).ToString() }, jpeg);

        var png = new List<string>();
        ImageFormat.Png.AppendEncoderArgs(png, 10);
        Assert.Equal(new[] { "-c:v", "png" }, png); // lossless: no quality knob

        var webp = new List<string>();
        ImageFormat.Webp.AppendEncoderArgs(webp, 60);
        Assert.Equal(new[] { "-c:v", "libwebp", "-quality", "60" }, webp);
    }

    // ---- VideoContainer ----

    [Theory]
    [InlineData("mp4", "mp4")]
    [InlineData("WEBM", "webm")]
    [InlineData("mkv", "mkv")]
    public void VideoContainer_FromToken_accepts_known_values(string input, string expectedName)
    {
        Assert.Equal(expectedName, VideoContainer.FromToken(input, VideoContainer.Mp4).Name);
    }

    [Fact]
    public void VideoContainer_FromToken_throws_on_unknown()
    {
        Assert.Throws<CaptureValidationException>(() => VideoContainer.FromToken("avi", VideoContainer.Mp4));
    }

    [Fact]
    public void VideoContainer_carries_muxer_audio_extension()
    {
        Assert.Equal("matroska", VideoContainer.Mkv.FfmpegMuxer);
        Assert.Equal("libopus", VideoContainer.Webm.AudioEncoder);
        Assert.Equal("aac", VideoContainer.Mp4.AudioEncoder);
        Assert.Equal("webm", VideoContainer.Webm.FileExtension);
    }

    [Theory]
    [InlineData("vp9", "webm", true)]
    [InlineData("h264", "mp4", true)]
    [InlineData("h265", "mp4", true)]
    [InlineData("vp9", "mkv", true)]
    [InlineData("h264", "mkv", true)]
    [InlineData("h264", "webm", false)]
    [InlineData("vp9", "mp4", false)]
    public void VideoContainer_Supports_matrix(string codecName, string containerName, bool expected)
    {
        var codec = VideoCodec.FromName(codecName);
        var container = VideoContainer.FromName(containerName);
        Assert.Equal(expected, container.Supports(codec));
    }

    // ---- VideoCodec ----

    [Theory]
    [InlineData("h264", "h264")]
    [InlineData("avc", "h264")]
    [InlineData("hevc", "h265")]
    [InlineData("H265", "h265")]
    [InlineData("vp9", "vp9")]
    public void VideoCodec_FromToken_accepts_values_and_aliases(string input, string expectedName)
    {
        Assert.Equal(expectedName, VideoCodec.FromToken(input, VideoCodec.H264).Name);
    }

    [Fact]
    public void VideoCodec_FromToken_throws_on_unknown()
    {
        Assert.Throws<CaptureValidationException>(() => VideoCodec.FromToken("av1", VideoCodec.H264));
    }

    [Theory]
    [InlineData("h264", "libx264")]
    [InlineData("h265", "libx265")]
    [InlineData("vp9", "libvpx-vp9")]
    public void VideoCodec_FfmpegEncoder(string codecName, string expected)
    {
        Assert.Equal(expected, VideoCodec.FromName(codecName).FfmpegEncoder);
    }

    [Theory]
    [InlineData("h264", 100, 18)]
    [InlineData("h264", 1, 40)]
    [InlineData("h265", 100, 18)]
    [InlineData("vp9", 100, 20)]
    [InlineData("vp9", 1, 45)]
    public void VideoCodec_MapCrf_hits_range_endpoints(string codecName, int quality, int expected)
    {
        Assert.Equal(expected, VideoCodec.FromName(codecName).MapCrf(quality));
    }

    [Fact]
    public void VideoCodec_preset_and_bitrate_flags()
    {
        Assert.True(VideoCodec.H264.UsesPreset);
        Assert.False(VideoCodec.Vp9.UsesPreset);
        Assert.True(VideoCodec.Vp9.RequiresZeroBitrateForCrf);
        Assert.False(VideoCodec.H264.RequiresZeroBitrateForCrf);
    }

    // ---- CapturePlatform ----

    [Theory]
    [InlineData("directshow", "dshow")]
    [InlineData("v4l2", "v4l2")]
    [InlineData("avfoundation", "avfoundation")]
    public void CapturePlatform_FfmpegFormat(string platformName, string expected)
    {
        Assert.Equal(expected, CapturePlatform.FromName(platformName).FfmpegFormat);
    }
}
