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

    /// <summary>Default frames captured per trigger (1 = single still). A trigger can override it.</summary>
    public int BurstCount { get; init; } = 1;

    /// <summary>Default seconds between burst frames when the trigger doesn't specify one.</summary>
    public double BurstIntervalSeconds { get; init; } = 0.3;
}

/// <summary>Optional per-trigger overrides a device can send (query string or JSON body).</summary>
public sealed class TriggerRequest
{
    public string? Name { get; init; }
    public string? Description { get; init; }
    public int? Count { get; init; }
    public double? IntervalSeconds { get; init; }
}

/// <summary>Outcome of an HTTP-facing session operation, mapped to a status code by the route.</summary>
public enum SessionOutcome
{
    Ok,
    Unauthorized,
    NotFound,
    Failed,
}

/// <summary>Result of a device trigger, returned to the route to shape the HTTP response.</summary>
public sealed record TriggerResult(
    SessionOutcome Outcome,
    int Seq = 0,
    int FrameCount = 0,
    bool IsBurst = false,
    string? Name = null,
    string? Description = null,
    string? Error = null);

/// <summary>Result of a session discovery query (<c>GET /sessions/{id}</c>).</summary>
public sealed record SessionDescriptor(SessionOutcome Outcome, string? SessionId = null, string? Device = null);

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

/// <summary>A capture produced by one device trigger — a single still or a rapid-fire burst.</summary>
/// <param name="Seq">1-based trigger sequence number within the session.</param>
/// <param name="TriggeredAt">When the device fired the trigger.</param>
/// <param name="Name">Optional caller-supplied label for this capture.</param>
/// <param name="Description">Optional caller-supplied description for this capture.</param>
/// <param name="Still">The captured still (single-frame triggers), else null.</param>
/// <param name="Burst">The captured sequence (rapid-fire triggers), else null.</param>
public sealed record TriggeredCapture(
    int Seq,
    DateTimeOffset TriggeredAt,
    string? Name,
    string? Description,
    ImageCaptureResult? Still,
    SceneCaptureResult? Burst)
{
    public bool IsBurst => Burst is not null;

    public int FrameCount => Burst?.Frames.Count ?? (Still is not null ? 1 : 0);
}
