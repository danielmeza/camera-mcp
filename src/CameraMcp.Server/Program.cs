using CameraMcp.Server;
using CameraMcp.Server.Configuration;
using CameraMcp.Server.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithResourcesFromAssembly();

var app = builder.Build();

app.MapDeviceEndpoints();

await app.RunAsync();

// Exposed so the test host (WebApplicationFactory) can boot the same pipeline.
public partial class Program;
