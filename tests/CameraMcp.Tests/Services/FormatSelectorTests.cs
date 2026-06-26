using CameraMcp.Server.Models;
using CameraMcp.Server.Services;

namespace CameraMcp.Tests.Services;

public class FormatSelectorTests
{
    private static readonly IReadOnlyList<CaptureFormat> Formats =
    [
        new(640, 480, "YUYV", 30),    // index 0
        new(1280, 720, "MJPG", 30),   // index 1
        new(1280, 720, "MJPG", 60),   // index 2
        new(1920, 1080, "MJPG", 30),  // index 3
    ];

    [Fact]
    public void Without_dimensions_picks_highest_resolution()
    {
        Assert.Equal(3, FormatSelector.SelectIndex(Formats, null, null, null));
    }

    [Fact]
    public void Exact_dimensions_match_is_selected()
    {
        var index = FormatSelector.SelectIndex(Formats, 1920, 1080, null);
        Assert.Equal(3, index);
    }

    [Fact]
    public void Nearest_area_is_selected_when_no_exact_match()
    {
        // 800x600 = 480k px is closest to 640x480 (307k) vs 1280x720 (921k)? distance: |307200-480000|=172800 vs |921600-480000|=441600 -> 640x480 wins.
        var index = FormatSelector.SelectIndex(Formats, 800, 600, null);
        Assert.Equal(0, index);
    }

    [Fact]
    public void Fps_breaks_ties_between_same_resolution()
    {
        // Two 1280x720 modes (30 and 60). Requesting 720p @ 60 should pick index 2.
        var index = FormatSelector.SelectIndex(Formats, 1280, 720, 60);
        Assert.Equal(2, index);
    }

    [Fact]
    public void Same_resolution_without_fps_prefers_higher_fps()
    {
        var index = FormatSelector.SelectIndex(Formats, 1280, 720, null);
        Assert.Equal(2, index); // 60 fps beats 30 fps for the same area
    }

    [Fact]
    public void Throws_when_no_formats()
    {
        Assert.Throws<CaptureValidationException>(() =>
            FormatSelector.SelectIndex([], 1280, 720, null));
    }
}
