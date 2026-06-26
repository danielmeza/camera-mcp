using System.ComponentModel;
using CameraMcp.Server.Models;
using CameraMcp.Server.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CameraMcp.Server.Tools;

/// <summary>Maintenance tools for the on-disk capture output directory.</summary>
[McpServerToolType]
public sealed class CaptureStoreTools
{
    private readonly ICaptureStore _store;

    public CaptureStoreTools(ICaptureStore store)
    {
        _store = store;
    }

    [McpServerTool(Name = "clear_captures"),
     Description("Deletes captured files (images, videos, and scene folders) from the server's output " +
                 "directory to reclaim space. DESTRUCTIVE: removes all contents of the output directory, " +
                 "or of a specified sub-directory within it. Returns the number of files and folders " +
                 "removed and the bytes freed. Cannot delete anything outside the configured output directory.")]
    public string ClearCaptures(
        [Description("Optional sub-directory within the output directory to clear (e.g. a specific scene folder). " +
                     "Omit to clear the entire output directory.")]
        string? directory = null)
    {
        try
        {
            var result = _store.Clear(directory);
            return CameraJson.Serialize(new
            {
                directory = result.Directory,
                filesDeleted = result.FilesDeleted,
                directoriesDeleted = result.DirectoriesDeleted,
                bytesFreed = result.BytesFreed,
            });
        }
        catch (Exception ex) when (ex is CaptureValidationException or CaptureFailedException)
        {
            throw new McpException(ex.Message);
        }
    }
}
