using CameraMcp.Server.Models;

namespace CameraMcp.Tests.Models;

public class SceneCaptureOptionsTests
{
    private const int MaxFrames = 60;
    private const int MaxDuration = 300;
    private const double DefaultInterval = 1.0;
    private const int MaxStartDelay = 3600;

    private static void Validate(SceneCaptureOptions o) =>
        o.Validate(MaxFrames, MaxDuration, DefaultInterval, MaxStartDelay);

    // ---- uniform ----

    [Fact]
    public void Uniform_with_explicit_interval_is_valid()
    {
        Validate(new SceneCaptureOptions { FrameCount = 6, IntervalSeconds = 0.5 });
    }

    [Fact]
    public void Uniform_falls_back_to_default_interval_when_unset()
    {
        var options = new SceneCaptureOptions { FrameCount = 4 };
        Validate(options); // no IntervalSeconds
        Assert.Equal(DefaultInterval, options.ResolveUniformInterval(DefaultInterval));
        Assert.Equal(4, options.ResolveFrameCount());
        Assert.False(options.IsNonUniform);
    }

    [Fact]
    public void Uniform_requires_frame_count()
    {
        var ex = Assert.Throws<CaptureValidationException>(() => Validate(new SceneCaptureOptions()));
        Assert.Contains("frameCount", ex.Message);
    }

    [Fact]
    public void Uniform_rejects_more_than_max_frames()
    {
        Assert.Throws<CaptureValidationException>(() =>
            Validate(new SceneCaptureOptions { FrameCount = 61, IntervalSeconds = 0.1 }));
    }

    [Fact]
    public void Uniform_rejects_span_over_duration_cap()
    {
        // 10 frames span 9 gaps x 40s = 360s > 300s cap.
        Assert.Throws<CaptureValidationException>(() =>
            Validate(new SceneCaptureOptions { FrameCount = 10, IntervalSeconds = 40 }));
    }

    [Fact]
    public void Uniform_span_counts_gaps_not_frames()
    {
        // N frames span (N-1) intervals: 8 frames x 40s gaps = 280s, which is under the 300s cap and
        // would be wrongly rejected by an off-by-one N*interval (= 320s) calculation.
        var options = new SceneCaptureOptions { FrameCount = 8, IntervalSeconds = 40 };
        Assert.Equal(280, options.SpanSeconds(DefaultInterval));
        Validate(options); // must not throw
    }

    // ---- non-uniform ----

    [Fact]
    public void NonUniform_derives_frame_count_and_timestamps()
    {
        var options = new SceneCaptureOptions { Intervals = [0.2, 0.5, 1.0] };
        Validate(options);

        Assert.True(options.IsNonUniform);
        Assert.Equal(4, options.ResolveFrameCount()); // 3 gaps -> 4 frames

        // Warmup baked into the first timestamp, then cumulative gaps.
        Assert.Equal(new[] { 0.5, 0.7, 1.2, 2.2 }, options.ResolveTimestamps(0.5));
    }

    [Fact]
    public void NonUniform_overrides_frame_count_and_interval()
    {
        var options = new SceneCaptureOptions { FrameCount = 99, IntervalSeconds = 99, Intervals = [1, 2] };
        Validate(options);
        Assert.Equal(3, options.ResolveFrameCount()); // from intervals, not FrameCount
    }

    [Fact]
    public void NonUniform_rejects_non_positive_intervals()
    {
        Assert.Throws<CaptureValidationException>(() =>
            Validate(new SceneCaptureOptions { Intervals = [0.5, 0, 1] }));
    }

    [Fact]
    public void NonUniform_rejects_span_over_duration_cap()
    {
        Assert.Throws<CaptureValidationException>(() =>
            Validate(new SceneCaptureOptions { Intervals = [200, 200] })); // 400s span
    }

    // ---- start delay ----

    [Fact]
    public void Rejects_negative_start_delay()
    {
        Assert.Throws<CaptureValidationException>(() =>
            Validate(new SceneCaptureOptions { FrameCount = 3, IntervalSeconds = 1, StartDelaySeconds = -1 }));
    }

    [Fact]
    public void Rejects_start_delay_over_cap()
    {
        Assert.Throws<CaptureValidationException>(() =>
            Validate(new SceneCaptureOptions { FrameCount = 3, IntervalSeconds = 1, StartDelaySeconds = 4000 }));
    }
}
