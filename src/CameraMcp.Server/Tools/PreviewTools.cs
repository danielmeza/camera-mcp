using System.ComponentModel;
using CameraMcp.Server.Models;
using CameraMcp.Server.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CameraMcp.Server.Tools;

/// <summary>
/// Starts/stops a live MJPEG preview a <em>human</em> can watch in a browser. (LLM agents can't consume
/// a live video stream — for the model, use capture_image / capture_scene.)
/// </summary>
[McpServerToolType]
public sealed class PreviewTools
{
    private readonly IPreviewService _preview;

    public PreviewTools(IPreviewService preview)
    {
        _preview = preview;
    }

    [McpServerTool(Name = "start_preview"),
     Description("Starts a LIVE MJPEG preview of a camera for a human to watch in a browser, and returns the URL. " +
                 "The stream is loopback-only (127.0.0.1) and token-gated by default; set tunnel=cloudflare|devtunnel|auto " +
                 "to expose a public URL via an installed tunnel tool. One viewer at a time; the device is held while " +
                 "watching. NOTE: an LLM agent cannot view this stream — for the model use capture_image/capture_scene.")]
    public async Task<string> StartPreviewAsync(
        [Description("Camera id or name from list_cameras. Omit to use the first camera.")]
        string? deviceId = null,
        [Description("Desired width; snapped to nearest supported mode. Provide with height.")]
        int? width = null,
        [Description("Desired height; snapped to nearest supported mode. Provide with width.")]
        int? height = null,
        [Description("Preview frame rate. Default 15.")]
        int fps = 15,
        [Description("JPEG quality 1-100. Default 70.")]
        int quality = 70,
        [Description("Public tunnel: none (default, local only), cloudflare, devtunnel, or auto.")]
        string? tunnel = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var options = new PreviewOptions
            {
                DeviceId = deviceId,
                Width = width,
                Height = height,
                Fps = fps,
                Quality = quality,
                Tunnel = PreviewOptions.ParseTunnel(tunnel),
            };

            var info = await _preview.StartAsync(options, cancellationToken).ConfigureAwait(false);

            return CameraJson.Serialize(new
            {
                device = info.DeviceName,
                localUrl = info.LocalUrl,
                streamUrl = info.StreamUrl,
                tunnelUrl = info.TunnelUrl,
                tunnel = info.Tunnel.ToString().ToLowerInvariant(),
                note = info.TunnelNote,
            });
        }
        catch (Exception ex) when (ex is CaptureValidationException or CaptureFailedException or FFmpegNotFoundException)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerTool(Name = "stop_preview"),
     Description("Stops the live preview (closes the HTTP server and any tunnel, releases the camera).")]
    public async Task<string> StopPreviewAsync()
    {
        var stopped = await _preview.StopAsync().ConfigureAwait(false);
        return CameraJson.Serialize(new { stopped });
    }
}
