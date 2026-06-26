using CameraMcp.Server.Configuration;
using CameraMcp.Server.Models;
using CameraMcp.Server.Services;

namespace CameraMcp.Tests.Services;

public class CaptureEstimatorTests
{
    private static readonly CameraMcpOptions Config = new() { ImageWarmupFrames = 15, DefaultSceneIntervalSeconds = 1.0 };

    [Fact]
    public void EstimateVideo_is_dominated_by_duration_plus_delay()
    {
        var est = CaptureEstimator.EstimateVideo(new VideoCaptureOptions { DurationSeconds = 10, StartDelaySeconds = 3 });
        Assert.True(est >= 13, $"expected >= duration+delay, got {est}");
        Assert.True(est < 20, "encode headroom should be modest");
    }

    [Fact]
    public void EstimateScene_includes_span_and_delay()
    {
        var est = CaptureEstimator.EstimateScene(
            new SceneCaptureOptions { FrameCount = 5, IntervalSeconds = 2, StartDelaySeconds = 4 }, Config);
        // span = 5 * 2 = 10, plus startDelay 4 -> at least 14
        Assert.True(est >= 14, $"got {est}");
    }

    [Fact]
    public void EstimateImage_accounts_for_start_delay()
    {
        var withDelay = CaptureEstimator.EstimateImage(new ImageCaptureOptions { StartDelaySeconds = 5 }, Config);
        var without = CaptureEstimator.EstimateImage(new ImageCaptureOptions(), Config);
        Assert.Equal(5, Math.Round(withDelay - without));
    }
}
