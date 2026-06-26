using CameraMcp.Server.Models;
using FlashCap;

namespace CameraMcp.Server.Services;

/// <summary>
/// Translates FlashCap's device taxonomy into our capture model: the OS backend, the ffmpeg device
/// target, and an enumeration priority used to prefer modern backends over legacy ones.
/// </summary>
internal static class FlashCapDeviceMapping
{
    public static CapturePlatform ToPlatform(DeviceTypes deviceType) => deviceType switch
    {
        // Both Windows backends are driven through ffmpeg's dshow input by friendly name.
        DeviceTypes.DirectShow or DeviceTypes.VideoForWindows => CapturePlatform.DirectShow,
        DeviceTypes.V4L2 => CapturePlatform.V4L2,
        DeviceTypes.AVFoundation => CapturePlatform.AvFoundation,
        _ => throw new ArgumentOutOfRangeException(nameof(deviceType), deviceType, "Unsupported FlashCap device type."),
    };

    /// <summary>
    /// The ffmpeg device target string. V4L2 addresses by device path (the FlashCap identity, e.g.
    /// <c>/dev/video0</c>); DirectShow and AVFoundation address by friendly name.
    /// </summary>
    public static string ToVideoTarget(CapturePlatform platform, string name, object? identity) =>
        platform == CapturePlatform.V4L2 ? IdentityString(identity) ?? name : name;

    /// <summary>Lower sorts first. DirectShow/V4L2/AVFoundation outrank legacy Video for Windows.</summary>
    public static int Priority(DeviceTypes deviceType) => deviceType switch
    {
        DeviceTypes.DirectShow or DeviceTypes.V4L2 or DeviceTypes.AVFoundation => 0,
        DeviceTypes.VideoForWindows => 1,
        _ => 2,
    };

    private static string? IdentityString(object? identity)
    {
        var text = identity?.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
