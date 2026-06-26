using CameraMcp.Server.Configuration;
using CameraMcp.Server.Models;
using Microsoft.Extensions.Options;

namespace CameraMcp.Server.Services;

/// <summary>Manages the on-disk capture output directory.</summary>
public interface ICaptureStore
{
    /// <summary>
    /// Deletes captured files. With no argument, clears the entire configured output directory
    /// (keeping the directory itself). A <paramref name="directory"/> may target a sub-directory,
    /// but only within the configured output directory.
    /// </summary>
    ClearResult Clear(string? directory);
}

/// <summary>
/// Clears the capture output directory. A path guard ensures the operation can never escape the
/// configured output directory, so it cannot delete arbitrary locations on the host.
/// </summary>
public sealed class CaptureStore : ICaptureStore
{
    private readonly CameraMcpOptions _options;

    public CaptureStore(IOptions<CameraMcpOptions> options)
    {
        _options = options.Value;
    }

    public ClearResult Clear(string? directory)
    {
        var root = Normalize(_options.OutputDirectory);
        var target = string.IsNullOrWhiteSpace(directory) ? root : Normalize(directory);

        if (!IsWithin(root, target))
        {
            throw new CaptureValidationException(
                $"Refusing to clear '{target}': it is outside the configured output directory '{root}'.");
        }

        if (!Directory.Exists(target))
        {
            return new ClearResult(target, 0, 0, 0);
        }

        var files = Directory.GetFiles(target, "*", SearchOption.AllDirectories);
        long bytesFreed = 0;
        foreach (var file in files)
        {
            bytesFreed += new FileInfo(file).Length;
        }

        var fileCount = files.Length;
        var directoryCount = Directory.GetDirectories(target, "*", SearchOption.AllDirectories).Length;

        if (PathEquals(target, root))
        {
            // Clear the contents but keep the output directory itself.
            foreach (var sub in Directory.GetDirectories(target))
            {
                Directory.Delete(sub, recursive: true);
            }

            foreach (var file in Directory.GetFiles(target))
            {
                File.Delete(file);
            }
        }
        else
        {
            // A sub-directory: remove it entirely (it counts toward the directories removed).
            Directory.Delete(target, recursive: true);
            directoryCount += 1;
        }

        return new ClearResult(target, fileCount, directoryCount, bytesFreed);
    }

    /// <summary>True when <paramref name="candidate"/> is the same as, or nested under, <paramref name="root"/>.</summary>
    internal static bool IsWithin(string root, string candidate)
    {
        var r = Normalize(root);
        var c = Normalize(candidate);
        var comparison = PathComparison;
        return string.Equals(r, c, comparison)
            || c.StartsWith(r + Path.DirectorySeparatorChar, comparison);
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), PathComparison);

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
