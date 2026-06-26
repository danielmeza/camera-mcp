namespace CameraMcp.Server.Configuration;

/// <summary>
/// Server-wide configuration, bound from configuration/environment/CLI. The
/// <c>CameraMcp__</c> environment prefix maps to these properties (e.g. <c>CameraMcp__OutputDirectory</c>).
/// </summary>
public sealed class CameraMcpOptions
{
    public const string SectionName = "CameraMcp";

    /// <summary>Directory where captured video files are written when no explicit path is given.</summary>
    public string OutputDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "camera-mcp", "captures");

    /// <summary>Hard cap on requested video duration, protecting the host from runaway recordings.</summary>
    public int MaxVideoDurationSeconds { get; set; } = 300;

    /// <summary>Hard cap on the number of frames a single <c>capture_scene</c> may request.</summary>
    public int MaxSceneFrames { get; set; } = 60;

    /// <summary>Default interval (seconds) between scene frames when a call doesn't specify one.</summary>
    public double DefaultSceneIntervalSeconds { get; set; } = 1.0;

    /// <summary>Maximum delay (seconds) a capture may wait before starting.</summary>
    public int MaxStartDelaySeconds { get; set; } = 3600;

    /// <summary>
    /// Maximum number of scene frames returned inline (as images) in one response. Every frame is
    /// always saved to disk and listed by path; only the inline payload is bounded.
    /// </summary>
    public int MaxInlineSceneFrames { get; set; } = 30;

    /// <summary>Maximum total bytes of scene frames read into memory and returned inline.</summary>
    public long MaxInlineSceneBytes { get; set; } = 24L * 1024 * 1024;

    /// <summary>Explicit path to an ffmpeg executable; when null the locator searches bundled/PATH locations.</summary>
    public string? FFmpegPath { get; set; }

    /// <summary>Timeout applied to a single ffmpeg invocation beyond the recording duration itself.</summary>
    public int FFmpegTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Number of initial frames to discard before grabbing a still. Cameras (and especially
    /// phone-as-webcam / virtual devices) deliver black or unstable frames on cold open; skipping a
    /// few lets exposure and the stream settle.
    /// </summary>
    public int ImageWarmupFrames { get; set; } = 15;

    /// <summary>
    /// Address the built-in web host (device trigger endpoints, live preview, optional HTTP MCP
    /// transport) binds to: <c>127.0.0.1</c> (default, loopback only — reach off-box via a tunnel),
    /// <c>0.0.0.0</c>/<c>lan</c> (all interfaces, so same-network devices can hit it directly), or a
    /// specific interface IP.
    /// </summary>
    public string HttpBindAddress { get; set; } = "127.0.0.1";

    /// <summary>Port for the built-in web host; <c>0</c> (default) lets the OS assign one.</summary>
    public int HttpPort { get; set; }

    /// <summary>
    /// Optional public base URL (e.g. <c>https://cam.example.com</c>) used when building device-facing
    /// URLs — set this when the host is fronted by a fixed reverse proxy or custom domain. When null the
    /// bound address and port are used.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>Serve the stdio MCP transport (for local agents). Default true; set false for a pure-HTTP host.</summary>
    public bool StdioTransport { get; set; } = true;

    /// <summary>Expose the MCP server itself over Streamable HTTP (for remote agents) at <see cref="HttpMcpPath"/>.</summary>
    public bool EnableHttpMcp { get; set; }

    /// <summary>Route the HTTP MCP transport is mapped to when <see cref="EnableHttpMcp"/> is set.</summary>
    public string HttpMcpPath { get; set; } = "/mcp";

    /// <summary>
    /// Optional bearer token required on the HTTP MCP endpoint. When set, remote agents must send
    /// <c>Authorization: Bearer &lt;token&gt;</c>. Strongly recommended whenever the host is reachable
    /// beyond loopback.
    /// </summary>
    public string? HttpMcpBearerToken { get; set; }

    /// <summary>
    /// Comma-separated list of web origins (e.g. <c>https://dash.example.com,http://localhost:3000</c>)
    /// allowed to call the device/preview endpoints from a browser. Empty (default) means no browser
    /// cross-origin access — the endpoints still work for devices/servers/curl, which ignore CORS. Never
    /// a wildcard: only the listed origins are permitted, and the per-session token is still required.
    /// </summary>
    public string? AllowedWebOrigins { get; set; }
}
