using CameraMcp.Server.Models;

namespace CameraMcp.Server.Services;

/// <summary>
/// Chooses which advertised capture mode to use for a request. Pure and order-preserving: it returns
/// an index into the supplied list so the caller can map back to the matching native characteristic.
/// </summary>
public static class FormatSelector
{
    /// <summary>
    /// Picks the best mode for the requested dimensions/frame rate.
    /// When width and height are given, the nearest resolution by pixel area wins (then closest fps).
    /// When they are omitted, the highest-resolution mode wins (then highest fps).
    /// </summary>
    public static int SelectIndex(IReadOnlyList<CaptureFormat> formats, int? width, int? height, int? fps)
    {
        ArgumentNullException.ThrowIfNull(formats);
        if (formats.Count == 0)
        {
            throw new CaptureValidationException("The selected device reports no capture formats.");
        }

        var indexed = formats
            .Select((format, index) => (format, index))
            .ToList();

        if (width is int w && height is int h)
        {
            long targetArea = (long)w * h;
            return indexed
                .OrderBy(x => Math.Abs((long)x.format.Width * x.format.Height - targetArea))
                .ThenBy(x => fps is int f ? Math.Abs(x.format.FramesPerSecond - f) : 0)
                .ThenByDescending(x => x.format.FramesPerSecond)
                .First()
                .index;
        }

        return indexed
            .OrderByDescending(x => (long)x.format.Width * x.format.Height)
            .ThenByDescending(x => x.format.FramesPerSecond)
            .First()
            .index;
    }
}
