using CameraMcp.Server.Models;
using CameraMcp.Server.Services;
using FlashCap;

namespace CameraMcp.Tests.Services;

public class FlashCapDeviceMappingTests
{
    [Theory]
    [InlineData(DeviceTypes.DirectShow, "directshow")]
    [InlineData(DeviceTypes.VideoForWindows, "directshow")]
    [InlineData(DeviceTypes.V4L2, "v4l2")]
    [InlineData(DeviceTypes.AVFoundation, "avfoundation")]
    public void ToPlatform_maps_device_type(DeviceTypes type, string expectedPlatformName)
    {
        Assert.Equal(expectedPlatformName, FlashCapDeviceMapping.ToPlatform(type).Name);
    }

    [Fact]
    public void V4L2_target_uses_identity_path()
    {
        var target = FlashCapDeviceMapping.ToVideoTarget(CapturePlatform.V4L2, "USB Camera", "/dev/video2");
        Assert.Equal("/dev/video2", target);
    }

    [Fact]
    public void V4L2_target_falls_back_to_name_when_identity_blank()
    {
        var target = FlashCapDeviceMapping.ToVideoTarget(CapturePlatform.V4L2, "USB Camera", identity: null);
        Assert.Equal("USB Camera", target);
    }

    [Fact]
    public void DirectShow_target_uses_friendly_name()
    {
        Assert.Equal("Integrated Camera",
            FlashCapDeviceMapping.ToVideoTarget(CapturePlatform.DirectShow, "Integrated Camera", "ignored-identity"));
    }

    [Fact]
    public void AvFoundation_target_uses_friendly_name()
    {
        Assert.Equal("FaceTime HD Camera",
            FlashCapDeviceMapping.ToVideoTarget(CapturePlatform.AvFoundation, "FaceTime HD Camera", "ignored-identity"));
    }

    [Fact]
    public void Priority_prefers_modern_backends_over_vfw()
    {
        Assert.True(FlashCapDeviceMapping.Priority(DeviceTypes.DirectShow) < FlashCapDeviceMapping.Priority(DeviceTypes.VideoForWindows));
    }
}
