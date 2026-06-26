using System.ComponentModel;
using CameraMcp.Server.Models;
using CameraMcp.Server.Services;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CameraMcp.Server.Tools;

/// <summary>
/// Device-triggered ("remote shutter") capture sessions. The agent starts a session and hands the
/// returned trigger URL + token to a remote/embedded device (or a person). Whenever that device POSTs
/// the trigger endpoint, a still is captured here; the agent receives each one in order via
/// await_capture (long-poll). Use this when something OTHER than the agent decides the moment to shoot.
/// </summary>
[McpServerToolType]
public sealed class SessionTools
{
    private const int MaxWaitSeconds = 300;

    private readonly ICaptureSessionService _sessions;
    private readonly ICaptureStore _store;

    public SessionTools(ICaptureSessionService sessions, ICaptureStore store)
    {
        _sessions = sessions;
        _store = store;
    }

    [McpServerTool(Name = "start_capture_session"),
     Description("Starts a device-triggered capture session and returns { sessionId, token, triggerUrl, " +
                 "tunnelTriggerUrl }. Give the trigger URL + token to the device; it POSTs that URL " +
                 "(token in ?token= or the X-Session-Token header) to capture a still. It can also GET " +
                 "/session (with the token) to discover the current sessionId. Receive frames with " +
                 "await_capture. Only one session is active at a time; starting a new one replaces it.")]
    public async Task<string> StartCaptureSessionAsync(
        [Description("Camera id or name. Omit to use the first camera.")] string? deviceId = null,
        int? width = null, int? height = null,
        [Description("jpeg, png, or webp. Default jpeg.")] string? format = null,
        [Description("Quality 1-100. Default 85.")] int quality = 85,
        [Description("Expose the trigger endpoint publicly: 'cloudflare', 'devtunnel', or 'none' (default).")]
        string? tunnel = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var info = await _sessions.StartAsync(new SessionStartOptions
            {
                DeviceId = deviceId,
                Width = width,
                Height = height,
                Format = ImageFormat.FromToken(format, ImageFormat.Jpeg),
                Quality = quality,
                Tunnel = ParseTunnel(tunnel),
            }, cancellationToken).ConfigureAwait(false);

            return CameraJson.Serialize(new
            {
                sessionId = info.SessionId,
                token = info.Token,
                triggerUrl = info.TriggerUrl,
                tunnelTriggerUrl = info.TunnelTriggerUrl,
                device = info.DeviceName,
                tunnel = info.Tunnel.ToString().ToLowerInvariant(),
                tunnelNote = info.TunnelNote,
                howToTrigger = "POST the trigger URL (token in ?token= or X-Session-Token header) to capture a still.",
            });
        }
        catch (Exception ex) when (ex is CaptureValidationException or CaptureFailedException or FFmpegNotFoundException)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "await_capture"),
     Description("Long-polls for the next device-triggered still in a session. Returns the captured image " +
                 "(inline + resource link) the instant the device triggers, or a 'waiting' status if none " +
                 "arrives within waitSeconds. Call it in a loop to receive the stream of triggered frames.")]
    public async Task<IEnumerable<ContentBlock>> AwaitCaptureAsync(
        [Description("The sessionId from start_capture_session.")] string sessionId,
        [Description("Seconds to wait for the next trigger before returning (capped at 300).")]
        double waitSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var timeout = TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 0, MaxWaitSeconds));
            var frame = await _sessions.AwaitNextAsync(sessionId, timeout, cancellationToken).ConfigureAwait(false);

            if (frame is null)
            {
                return new List<ContentBlock>
                {
                    new TextContentBlock
                    {
                        Text = CameraJson.Serialize(new { sessionId, status = "waiting", message = "no trigger within the wait window; call await_capture again" }),
                    },
                };
            }

            var blocks = new List<ContentBlock>
            {
                new TextContentBlock
                {
                    Text = CameraJson.Serialize(new { sessionId, status = "captured", seq = frame.Seq, triggeredAt = frame.TriggeredAt }),
                },
            };
            blocks.AddRange(CaptureRendering.Image(frame.Result, _store));
            return blocks;
        }
        catch (CaptureValidationException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "stop_capture_session"),
     Description("Stops a device-triggered capture session and tears down its trigger endpoint and tunnel.")]
    public async Task<string> StopCaptureSessionAsync(
        [Description("The sessionId to stop.")] string sessionId)
    {
        var stopped = await _sessions.StopAsync(sessionId).ConfigureAwait(false);
        return CameraJson.Serialize(new { sessionId, stopped });
    }

    private static TunnelProvider ParseTunnel(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "none" => TunnelProvider.None,
        "cloudflare" or "cloudflared" => TunnelProvider.Cloudflare,
        "devtunnel" or "dev" or "ms" => TunnelProvider.DevTunnel,
        _ => TunnelProvider.None,
    };
}
