using CameraMcp.Server.Configuration;
using CameraMcp.Server.Models;

namespace CameraMcp.Server.Services;

/// <summary>
/// Estimates how long a capture will take, for queue ETAs. Approximate by design: the dominant terms
/// (start delay, recording duration, scene span) are exact; the rest is a small fixed overhead.
/// </summary>
public static class CaptureEstimator
{
    private const double DeviceOpenOverhead = 1.5;
    private const double AssumedFps = 30.0;

    public static double EstimateImage(ImageCaptureOptions options, CameraMcpOptions config) =>
        options.StartDelaySeconds + WarmupSeconds(config) + DeviceOpenOverhead;

    public static double EstimateScene(SceneCaptureOptions options, CameraMcpOptions config) =>
        options.StartDelaySeconds + WarmupSeconds(config)
        + options.SpanSeconds(config.DefaultSceneIntervalSeconds) + DeviceOpenOverhead;

    public static double EstimateVideo(VideoCaptureOptions options) =>
        options.StartDelaySeconds + options.DurationSeconds + (options.DurationSeconds * 0.2) + 2.0;

    private static double WarmupSeconds(CameraMcpOptions config) =>
        Math.Round(config.ImageWarmupFrames / AssumedFps, 2);
}
