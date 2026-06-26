using System.Runtime.InteropServices;
using CameraMcp.Server.Configuration;
using Microsoft.Extensions.Options;

namespace CameraMcp.Server.Services;

public interface IFFmpegLocator
{
    /// <summary>Returns a usable ffmpeg executable path or throws <see cref="FFmpegNotFoundException"/>.</summary>
    string Resolve();
}

/// <summary>
/// Locates the ffmpeg executable, preferring (1) an explicit configured path, then (2) binaries
/// bundled next to the app, then (3) ffmpeg on the system PATH. The resolution logic is a pure
/// static method so it can be unit-tested against a fake filesystem.
/// </summary>
public sealed class FFmpegLocator : IFFmpegLocator
{
    private readonly CameraMcpOptions _options;
    private string? _cached;

    public FFmpegLocator(IOptions<CameraMcpOptions> options)
    {
        _options = options.Value;
    }

    public string Resolve()
    {
        return _cached ??= ResolveCore(
            _options.FFmpegPath,
            AppContext.BaseDirectory,
            RuntimeInformation.RuntimeIdentifier,
            ExecutableName(),
            GetPathDirectories(),
            File.Exists);
    }

    internal static string ResolveCore(
        string? explicitPath,
        string baseDirectory,
        string runtimeIdentifier,
        string executableName,
        IEnumerable<string> pathDirectories,
        Func<string, bool> exists)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            if (exists(explicitPath))
            {
                return explicitPath;
            }

            throw new FFmpegNotFoundException(
                $"Configured FFmpegPath '{explicitPath}' was not found on disk.");
        }

        // Bundled layouts: flattened (self-contained publish), our MSBuild bundle folder, and the
        // NuGet-style runtimes/<rid>/native path.
        string[] bundledCandidates =
        [
            Path.Combine(baseDirectory, executableName),
            Path.Combine(baseDirectory, "ffmpeg-bin", executableName),
            Path.Combine(baseDirectory, "ffmpeg-bin", runtimeIdentifier, executableName),
            Path.Combine(baseDirectory, "runtimes", runtimeIdentifier, "native", executableName),
        ];

        foreach (var candidate in bundledCandidates)
        {
            if (exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var directory in pathDirectories)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            var candidate = Path.Combine(directory, executableName);
            if (exists(candidate))
            {
                return candidate;
            }
        }

        throw new FFmpegNotFoundException(
            "Could not locate an ffmpeg executable. Install ffmpeg and add it to PATH, set " +
            "CameraMcp__FFmpegPath to its full path, or publish with the bundled per-RID binaries.");
    }

    internal static string ExecutableName() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";

    private static IEnumerable<string> GetPathDirectories() =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
