using CameraMcp.Server;
using CameraMcp.Server.Configuration;
using CameraMcp.Server.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

// stdout is reserved for the MCP stdio JSON-RPC stream. Drop the default console provider (which
// writes to stdout) and route every log record — including Kestrel/hosting messages — to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

// Bind the Kestrel host per configuration. Default is loopback on an OS-assigned port; set
// CameraMcp__HttpBindAddress=0.0.0.0 (and optionally CameraMcp__HttpPort) to reach it from the LAN.
var bindHost = NetworkHost.NormalizeBindHost(builder.Configuration[$"{CameraMcpOptions.SectionName}:HttpBindAddress"]);
var bindPort = builder.Configuration.GetValue($"{CameraMcpOptions.SectionName}:HttpPort", 0);
builder.WebHost.UseUrls($"http://{bindHost}:{bindPort}");

builder.Services
    .AddOptions<CameraMcpOptions>()
    .Bind(builder.Configuration.GetSection(CameraMcpOptions.SectionName));

builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<IFFmpegLocator, FFmpegLocator>();
builder.Services.AddSingleton<IStillCapturer, FFmpegStillCapturer>();
builder.Services.AddSingleton<IVideoRecorder, FFmpegVideoRecorder>();
builder.Services.AddSingleton<ISceneCapturer, FFmpegSceneCapturer>();
builder.Services.AddSingleton<ICaptureStore, CaptureStore>();
builder.Services.AddSingleton<ICameraService, CameraService>();
builder.Services.AddSingleton<ITunnelLauncher, TunnelLauncher>();
builder.Services.AddSingleton<ITunnelManager, TunnelManager>();
builder.Services.AddSingleton<IPreviewService, PreviewService>();
builder.Services.AddSingleton<ICaptureQueue, CaptureQueue>();
builder.Services.AddSingleton<ICaptureSessionService, CaptureSessionService>();
builder.Services.AddSingleton<IHttpHostInfo, HttpHostInfo>();

// Local agents connect over stdio (default). When CameraMcp__EnableHttpMcp=true the same tools are also
// exposed over Streamable HTTP for remote agents. A pure-HTTP deployment can set
// CameraMcp__StdioTransport=false so the process doesn't shut down on stdin EOF.
var enableHttpMcp = builder.Configuration.GetValue($"{CameraMcpOptions.SectionName}:EnableHttpMcp", false);
var enableStdio = builder.Configuration.GetValue($"{CameraMcpOptions.SectionName}:StdioTransport", true);
var mcp = builder.Services
    .AddMcpServer()
    .WithToolsFromAssembly()
    .WithResourcesFromAssembly();
if (enableStdio)
{
    mcp.WithStdioServerTransport();
}

if (enableHttpMcp)
{
    mcp.WithHttpTransport();
    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
}

var app = builder.Build();

app.MapDeviceEndpoints();

if (enableHttpMcp)
{
    var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<CameraMcpOptions>>().Value;
    app.UseCors();

    // Optional static bearer gate on the MCP endpoint — strongly recommended beyond loopback.
    if (!string.IsNullOrEmpty(options.HttpMcpBearerToken))
    {
        var expected = $"Bearer {options.HttpMcpBearerToken}";
        var mcpPath = options.HttpMcpPath;
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(mcpPath)
                && !string.Equals(context.Request.Headers.Authorization, expected, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(context);
        });
    }

    app.MapMcp(options.HttpMcpPath);
}

await app.RunAsync();

// Exposed so the test host (WebApplicationFactory) can boot the same pipeline.
public partial class Program;
