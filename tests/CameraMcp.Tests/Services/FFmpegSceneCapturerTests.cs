using CameraMcp.Server.Services;

namespace CameraMcp.Tests.Services;

public class FFmpegSceneCapturerTests
{
    // Forward slashes parse as path separators on every OS (Windows accepts both; Linux/macOS use '/').
    [Fact]
    public void OrderFrames_sorts_numerically_beyond_999()
    {
        var files = new[]
        {
            "out/frame-010.jpg",
            "out/frame-002.jpg",
            "out/frame-1000.jpg", // 4 digits: an ordinal sort would place this before frame-999
            "out/frame-999.jpg",
            "out/frame-099.jpg",
            "out/notes.txt",       // non-frame, ignored
        };

        var ordered = FFmpegSceneCapturer.OrderFrames(files);

        Assert.Equal(new[] { 2, 10, 99, 999, 1000 }, ordered.Select(o => o.Index));
    }

    [Theory]
    [InlineData("out/frame-007.jpg", 7)]
    [InlineData("out/frame-1000.png", 1000)]
    [InlineData("out/other.jpg", 0)]
    [InlineData("out/frame-.jpg", 0)]
    [InlineData("out/frame-12a.jpg", 0)]
    public void ParseFrameIndex_extracts_or_rejects(string path, int expected)
    {
        Assert.Equal(expected, FFmpegSceneCapturer.ParseFrameIndex(path));
    }
}
