namespace CameraMcp.Server.Services;

/// <summary>Thrown when no usable ffmpeg executable can be located.</summary>
public sealed class FFmpegNotFoundException : Exception
{
    public FFmpegNotFoundException(string message) : base(message)
    {
    }
}
