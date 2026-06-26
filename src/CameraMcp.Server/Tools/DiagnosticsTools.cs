using System.ComponentModel;
using ModelContextProtocol.Server;

namespace CameraMcp.Server.Tools;

/// <summary>
/// Lightweight diagnostics tools used to confirm the server is reachable over the
/// MCP transport without touching any camera hardware.
/// </summary>
[McpServerToolType]
public static class DiagnosticsTools
{
    [McpServerTool(Name = "ping"), Description("Returns 'pong' to confirm the camera MCP server is responding.")]
    public static string Ping() => "pong";
}
