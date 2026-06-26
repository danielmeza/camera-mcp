namespace CameraMcp.Server.Models;

/// <summary>
/// Thrown when user-supplied capture options are invalid. Tools translate this into a
/// clean MCP error so the agent gets an actionable message instead of a stack trace.
/// </summary>
public sealed class CaptureValidationException : Exception
{
    public CaptureValidationException(string message) : base(message)
    {
    }
}
