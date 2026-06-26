using CameraMcp.Server.Models;
using CameraMcp.Server.Services;

namespace CameraMcp.Tests.Services;

public class PlatformDeviceMapperTests
{

    [Fact]
    public void DirectShow_builds_video_input_with_friendly_name()
    {
        var args = PlatformDeviceMapper.BuildVideoInput(
            CapturePlatform.DirectShow, "Integrated Camera", 1280, 720, 30);

        Assert.Equal(
            new[] { "-f", "dshow", "-framerate", "30", "-video_size", "1280x720", "-i", "video=Integrated Camera" },
            args);
    }

    [Fact]
    public void DirectShow_combines_audio_into_single_input()
    {
        var args = PlatformDeviceMapper.BuildVideoInput(
            CapturePlatform.DirectShow, "Cam", null, null, 30, audioTarget: "Microphone");

        Assert.Contains("-i", args);
        Assert.Equal("video=Cam:audio=Microphone", args[^1]);
    }

    [Fact]
    public void V4L2_uses_device_path_and_separate_alsa_audio()
    {
        var args = PlatformDeviceMapper.BuildVideoInput(
            CapturePlatform.V4L2, "/dev/video0", 640, 480, 25, audioTarget: "default");

        Assert.Equal(
            new[] { "-f", "v4l2", "-framerate", "25", "-video_size", "640x480", "-i", "/dev/video0", "-f", "alsa", "-i", "default" },
            args);
    }

    [Fact]
    public void AvFoundation_combines_video_and_audio_with_colon()
    {
        var args = PlatformDeviceMapper.BuildVideoInput(
            CapturePlatform.AvFoundation, "FaceTime HD Camera", null, null, 30, audioTarget: "default");

        Assert.Equal("FaceTime HD Camera:default", args[^1]);
    }

    [Fact]
    public void Omits_video_size_when_dimensions_unset()
    {
        var args = PlatformDeviceMapper.BuildVideoInput(
            CapturePlatform.V4L2, "/dev/video0", null, null, 30);

        Assert.DoesNotContain("-video_size", args);
    }

    [Fact]
    public void Omits_framerate_when_fps_not_positive()
    {
        var args = PlatformDeviceMapper.BuildVideoInput(
            CapturePlatform.V4L2, "/dev/video0", null, null, 0);

        Assert.DoesNotContain("-framerate", args);
    }

    [Fact]
    public void Throws_on_blank_video_target()
    {
        Assert.Throws<ArgumentException>(() =>
            PlatformDeviceMapper.BuildVideoInput(CapturePlatform.DirectShow, "  ", null, null, 30));
    }
}
