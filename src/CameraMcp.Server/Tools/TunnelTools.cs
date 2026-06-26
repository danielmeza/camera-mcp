using System.ComponentModel;
using CameraMcp.Server.Models;
using CameraMcp.Server.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CameraMcp.Server.Tools;

/// <summary>
/// On-demand public tunnels. Lets the agent expose a loopback endpoint (e.g. a live preview or a
/// capture-session trigger URL that was started with tunnel='none') through a Cloudflare quick tunnel
/// or a Microsoft Dev Tunnel, then tear it down. Take the port from the local URL of the thing you want
/// to expose. Requires the matching tool (cloudflared / devtunnel) on PATH.
/// </summary>
[McpServerToolType]
public sealed class TunnelTools
{
    private readonly ITunnelManager _tunnels;

    public TunnelTools(ITunnelManager tunnels)
    {
        _tunnels = tunnels;
    }

    [McpServerTool(Name = "start_tunnel"),
     Description("Starts a public tunnel to a loopback PORT on this host and returns its public URL. " +
                 "Use it to expose a live preview or a capture session that was started without a tunnel: " +
                 "pass the port from that endpoint's local URL. provider: 'cloudflare' (default) or 'devtunnel'. " +
                 "If the tool isn't installed, returns a note and a null publicUrl.")]
    public async Task<string> StartTunnelAsync(
        [Description("The loopback port to expose (1-65535), e.g. the port in a preview/session URL.")] int port,
        [Description("'cloudflare' (default) or 'devtunnel'.")] string? provider = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var entry = await _tunnels.StartAsync(port, ParseProvider(provider), cancellationToken).ConfigureAwait(false);
            return CameraJson.Serialize(new
            {
                tunnelId = entry.TunnelId,
                port = entry.Port,
                publicUrl = entry.PublicUrl,
                provider = entry.Provider.ToString().ToLowerInvariant(),
                note = entry.Note,
            });
        }
        catch (CaptureValidationException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "stop_tunnel"), Description("Stops a tunnel started with start_tunnel.")]
    public string StopTunnel([Description("The tunnelId to stop.")] string tunnelId) =>
        CameraJson.Serialize(new { tunnelId, stopped = _tunnels.Stop(tunnelId) });

    [McpServerTool(Name = "list_tunnels"), Description("Lists the tunnels currently active and their public URLs.")]
    public string ListTunnels() =>
        CameraJson.Serialize(new
        {
            tunnels = _tunnels.List().Select(t => new
            {
                tunnelId = t.TunnelId,
                port = t.Port,
                publicUrl = t.PublicUrl,
                provider = t.Provider.ToString().ToLowerInvariant(),
            }),
        });

    private static TunnelProvider ParseProvider(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "devtunnel" or "dev" or "ms" => TunnelProvider.DevTunnel,
        _ => TunnelProvider.Cloudflare, // default to Cloudflare for the explicit tunnel tool
    };
}
