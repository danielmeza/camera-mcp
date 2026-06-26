namespace CameraMcp.Server.Services;

/// <summary>Helpers for surfacing ffmpeg failure diagnostics in error messages.</summary>
internal static class FFmpegError
{
    private const int MaxLength = 600;

    /// <summary>Returns the trailing portion of ffmpeg's stderr, which carries the actual error.</summary>
    public static string Tail(string? standardError)
    {
        if (string.IsNullOrWhiteSpace(standardError))
        {
            return "ffmpeg produced no diagnostics.";
        }

        var trimmed = standardError.Trim();
        return trimmed.Length <= MaxLength
            ? trimmed
            : "..." + trimmed[^MaxLength..];
    }
}
