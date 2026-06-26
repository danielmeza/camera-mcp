using CameraMcp.Server.Models;

namespace CameraMcp.Tests.Models;

public class CaptureOptionsValidationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(-5)]
    public void ValidateQuality_rejects_out_of_range(int quality)
    {
        Assert.Throws<CaptureValidationException>(() => CaptureOptionsValidation.ValidateQuality(quality));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(85)]
    [InlineData(100)]
    public void ValidateQuality_accepts_in_range(int quality)
    {
        CaptureOptionsValidation.ValidateQuality(quality); // does not throw
    }

    [Fact]
    public void ValidateDimensions_allows_both_unset()
    {
        CaptureOptionsValidation.ValidateDimensions(null, null);
    }

    [Fact]
    public void ValidateDimensions_allows_both_set_positive()
    {
        CaptureOptionsValidation.ValidateDimensions(1280, 720);
    }

    [Theory]
    [InlineData(1280, null)]
    [InlineData(null, 720)]
    public void ValidateDimensions_rejects_one_without_the_other(int? w, int? h)
    {
        Assert.Throws<CaptureValidationException>(() => CaptureOptionsValidation.ValidateDimensions(w, h));
    }

    [Theory]
    [InlineData(0, 480)]
    [InlineData(640, -1)]
    public void ValidateDimensions_rejects_non_positive(int? w, int? h)
    {
        Assert.Throws<CaptureValidationException>(() => CaptureOptionsValidation.ValidateDimensions(w, h));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(241)]
    public void ValidateFps_rejects_out_of_range(int fps)
    {
        Assert.Throws<CaptureValidationException>(() => CaptureOptionsValidation.ValidateFps(fps));
    }

    [Fact]
    public void ValidateDuration_rejects_non_positive()
    {
        Assert.Throws<CaptureValidationException>(() => CaptureOptionsValidation.ValidateDuration(0, 300));
    }

    [Fact]
    public void ValidateDuration_rejects_over_cap()
    {
        var ex = Assert.Throws<CaptureValidationException>(() => CaptureOptionsValidation.ValidateDuration(301, 300));
        Assert.Contains("maximum", ex.Message);
    }

    [Fact]
    public void ValidateDuration_accepts_at_cap()
    {
        CaptureOptionsValidation.ValidateDuration(300, 300);
    }

    [Theory]
    [InlineData("vp9", "webm")]
    [InlineData("h264", "mp4")]
    [InlineData("h265", "mp4")]
    [InlineData("h264", "mkv")]
    [InlineData("vp9", "mkv")]
    public void ValidateCodecContainer_accepts_compatible(string codec, string container)
    {
        CaptureOptionsValidation.ValidateCodecContainer(VideoCodec.FromName(codec), VideoContainer.FromName(container));
    }

    [Theory]
    [InlineData("h264", "webm")]
    [InlineData("h265", "webm")]
    [InlineData("vp9", "mp4")]
    public void ValidateCodecContainer_rejects_incompatible(string codec, string container)
    {
        Assert.Throws<CaptureValidationException>(() =>
            CaptureOptionsValidation.ValidateCodecContainer(VideoCodec.FromName(codec), VideoContainer.FromName(container)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    public void ValidateStartDelay_accepts_in_range(double delay)
    {
        CaptureOptionsValidation.ValidateStartDelay(delay, 3600);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4000)]
    public void ValidateStartDelay_rejects_out_of_range(double delay)
    {
        Assert.Throws<CaptureValidationException>(() => CaptureOptionsValidation.ValidateStartDelay(delay, 3600));
    }

    [Fact]
    public void ImageCaptureOptions_Validate_passes_for_defaults()
    {
        new ImageCaptureOptions().Validate(maxStartDelaySeconds: 3600);
    }

    [Fact]
    public void VideoCaptureOptions_Validate_passes_for_minimal_valid_request()
    {
        new VideoCaptureOptions { DurationSeconds = 5 }.Validate(maxDurationSeconds: 300, maxStartDelaySeconds: 3600);
    }

    [Fact]
    public void VideoCaptureOptions_Validate_rejects_zero_bitrate()
    {
        var options = new VideoCaptureOptions { DurationSeconds = 5, BitrateKbps = 0 };
        Assert.Throws<CaptureValidationException>(() => options.Validate(maxDurationSeconds: 300, maxStartDelaySeconds: 3600));
    }
}
