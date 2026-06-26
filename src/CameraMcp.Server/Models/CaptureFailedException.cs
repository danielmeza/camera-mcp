namespace CameraMcp.Server.Models;

/// <summary>
/// Thrown when the capture pipeline runs but fails (e.g. ffmpeg exits non-zero, or the device cannot
/// be opened). Carries an agent-friendly message; the underlying tool diagnostics are logged to stderr.
/// </summary>
public sealed class CaptureFailedException : Exception
{
    public CaptureFailedException(string message) : base(message)
    {
    }

    public CaptureFailedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
