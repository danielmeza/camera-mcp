namespace CameraMcp.Server.Models;

/// <summary>Summary of a <c>clear_captures</c> operation.</summary>
/// <param name="Directory">The directory that was cleared.</param>
/// <param name="FilesDeleted">Number of files removed (recursively).</param>
/// <param name="DirectoriesDeleted">Number of sub-directories removed.</param>
/// <param name="BytesFreed">Total bytes reclaimed.</param>
public sealed record ClearResult(string Directory, int FilesDeleted, int DirectoriesDeleted, long BytesFreed);
