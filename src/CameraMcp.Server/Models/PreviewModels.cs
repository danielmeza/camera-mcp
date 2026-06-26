namespace CameraMcp.Server.Models;

/// <summary>Public-exposure tunnel provider for the live preview.</summary>
public enum TunnelProvider
{
    /// <summary>Local URL only (loopback); no public tunnel.</summary>
    None,

    /// <summary>Cloudflare quick tunnel (<c>cloudflared</c>).</summary>
    Cloudflare,

    /// <summary>Microsoft Dev Tunnels (<c>devtunnel</c>).</summary>
    DevTunnel,

    /// <summary>Use whichever tunnel tool is installed (Cloudflare preferred).</summary>
    Auto,
}

/// <summary>Validated request to start a live MJPEG preview.</summary>
public sealed class PreviewOptions
{
    public string? DeviceId { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int Fps { get; init; } = 15;
    public int Quality { get; init; } = 70;
    public TunnelProvider Tunnel { get; init; } = TunnelProvider.None;

    public static TunnelProvider ParseTunnel(string? token) => token?.Trim().ToLowerInvariant() switch
    {
        null or "" or "none" or "off" => TunnelProvider.None,
        "cloudflare" or "cloudflared" => TunnelProvider.Cloudflare,
        "devtunnel" or "dev" => TunnelProvider.DevTunnel,
        "auto" => TunnelProvider.Auto,
        _ => throw new CaptureValidationException(
            $"Unknown tunnel '{token}'. Allowed: none, cloudflare, devtunnel, auto."),
    };
}

/// <summary>Details of a running preview.</summary>
/// <param name="PreviewId">Stable id; used to stop the preview and embedded in its URLs.</param>
/// <param name="DeviceName">Camera being previewed.</param>
/// <param name="LocalUrl">Page URL (open in a browser) — includes the access token.</param>
/// <param name="StreamUrl">Direct MJPEG stream URL (for an &lt;img&gt; tag).</param>
/// <param name="TunnelUrl">Public tunnel page URL, or null if no tunnel is active.</param>
/// <param name="Tunnel">The tunnel provider in effect.</param>
/// <param name="TunnelNote">Human note about tunnel status (e.g. tool not installed).</param>
public sealed record PreviewInfo(
    string PreviewId,
    string DeviceName,
    string LocalUrl,
    string StreamUrl,
    string? TunnelUrl,
    TunnelProvider Tunnel,
    string? TunnelNote);
