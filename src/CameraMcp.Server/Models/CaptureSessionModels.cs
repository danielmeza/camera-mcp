namespace CameraMcp.Server.Models;

/// <summary>Request to start a device-triggered ("remote shutter") capture session.</summary>
public sealed class SessionStartOptions
{
    public string? DeviceId { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public ImageFormat Format { get; init; } = ImageFormat.Jpeg;
    public int Quality { get; init; } = 85;
    public TunnelProvider Tunnel { get; init; } = TunnelProvider.None;
}

/// <summary>Details of a running capture session returned to the agent.</summary>
/// <param name="SessionId">Stable id; the device can also discover it via <c>GET /session</c>.</param>
/// <param name="Token">Per-session secret the device must present to trigger or query.</param>
/// <param name="TriggerUrl">Loopback POST endpoint the device hits to capture (includes the token).</param>
/// <param name="TunnelTriggerUrl">Public POST endpoint via a tunnel, or null.</param>
/// <param name="DeviceName">Camera the session captures from.</param>
/// <param name="Tunnel">Tunnel provider in effect.</param>
/// <param name="TunnelNote">Human note about tunnel status (e.g. tool not installed).</param>
public sealed record SessionInfo(
    string SessionId,
    string Token,
    string TriggerUrl,
    string? TunnelTriggerUrl,
    string DeviceName,
    TunnelProvider Tunnel,
    string? TunnelNote);

/// <summary>A single capture produced by a device trigger.</summary>
/// <param name="Seq">1-based trigger sequence number within the session.</param>
/// <param name="Result">The captured still.</param>
/// <param name="TriggeredAt">When the device fired the trigger.</param>
public sealed record TriggeredFrame(int Seq, ImageCaptureResult Result, DateTimeOffset TriggeredAt);
