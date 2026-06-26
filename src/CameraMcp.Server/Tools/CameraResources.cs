using System.ComponentModel;
using CameraMcp.Server.Models;
using CameraMcp.Server.Services;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CameraMcp.Server.Tools;

/// <summary>
/// MCP resources for transmitting images over the protocol by URI (no filesystem path, no HTTP):
/// a saved capture is read via <c>camera://captures/&lt;relative&gt;</c>, and a fresh frame via
/// <c>camera://device/{deviceId}/frame</c>. Clients fetch these with <c>resources/read</c>.
/// </summary>
[McpServerResourceType]
public sealed class CameraResources
{
    private readonly ICaptureStore _store;
    private readonly ICameraService _camera;

    public CameraResources(ICaptureStore store, ICameraService camera)
    {
        _store = store;
        _camera = camera;
    }

    [McpServerResource(UriTemplate = "camera://captures/{+path}", Name = "capture-file"),
     Description("A previously saved capture (image, scene frame, or video), read over MCP by its " +
                 "camera://captures/<relative-path> URI — works for remote clients that have no access to the host filesystem.")]
    public async Task<ResourceContents> ReadCaptureAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var content = await _store.ReadCaptureAsync(path, cancellationToken).ConfigureAwait(false);
            return BlobResourceContents.FromBytes(content.Bytes, content.Uri, content.Mime); // canonical URI
        }
        catch (CaptureValidationException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    [McpServerResource(UriTemplate = "camera://device/{deviceId}/frame", Name = "live-frame", MimeType = "image/jpeg"),
     Description("Captures and returns a fresh JPEG frame from the named camera (id or name from list_cameras).")]
    public async Task<ResourceContents> ReadLiveFrameAsync(string deviceId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _camera
                .CaptureImageAsync(new ImageCaptureOptions { DeviceId = deviceId, Format = ImageFormat.Jpeg }, cancellationToken)
                .ConfigureAwait(false);
            return BlobResourceContents.FromBytes(result.Bytes, $"camera://device/{deviceId}/frame", "image/jpeg");
        }
        catch (Exception ex) when (ex is CaptureValidationException or CaptureFailedException or FFmpegNotFoundException)
        {
            throw new McpException(ex.Message);
        }
    }
}
