using CameraMcp.Server.Services;

namespace CameraMcp.Tests.Services;

public class FFmpegLocatorTests
{
    private const string Base = "/app";
    private const string Rid = "linux-x64";
    private const string Exe = "ffmpeg";

    [Fact]
    public void Prefers_explicit_path_when_it_exists()
    {
        var result = FFmpegLocator.ResolveCore(
            explicitPath: "/opt/ffmpeg/ffmpeg",
            Base, Rid, Exe,
            pathDirectories: [],
            exists: p => p == "/opt/ffmpeg/ffmpeg");

        Assert.Equal("/opt/ffmpeg/ffmpeg", result);
    }

    [Fact]
    public void Throws_when_explicit_path_missing()
    {
        var ex = Assert.Throws<FFmpegNotFoundException>(() => FFmpegLocator.ResolveCore(
            explicitPath: "/nope/ffmpeg",
            Base, Rid, Exe,
            pathDirectories: ["/usr/bin"],
            // The explicit path is missing; every other location exists. An explicit path is
            // authoritative, so this must throw instead of silently falling back.
            exists: p => p != "/nope/ffmpeg"));

        Assert.Contains("/nope/ffmpeg", ex.Message);
    }

    [Fact]
    public void Finds_flattened_bundled_binary_next_to_app()
    {
        var expected = Path.Combine(Base, Exe);
        var result = FFmpegLocator.ResolveCore(
            explicitPath: null, Base, Rid, Exe,
            pathDirectories: ["/usr/bin"],
            exists: p => p == expected);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Finds_binary_in_ffmpeg_bin_folder()
    {
        var expected = Path.Combine(Base, "ffmpeg-bin", Exe);
        var result = FFmpegLocator.ResolveCore(
            explicitPath: null, Base, Rid, Exe,
            pathDirectories: [],
            exists: p => p == expected);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Falls_back_to_path_directories()
    {
        var expected = Path.Combine("/usr/local/bin", Exe);
        var result = FFmpegLocator.ResolveCore(
            explicitPath: null, Base, Rid, Exe,
            pathDirectories: ["/usr/bin", "/usr/local/bin"],
            exists: p => p == expected);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Throws_with_guidance_when_nothing_found()
    {
        var ex = Assert.Throws<FFmpegNotFoundException>(() => FFmpegLocator.ResolveCore(
            explicitPath: null, Base, Rid, Exe,
            pathDirectories: ["/usr/bin"],
            exists: _ => false));

        Assert.Contains("PATH", ex.Message);
    }
}
