using CameraMcp.Server.Configuration;
using CameraMcp.Server.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

// stdout is reserved for the MCP stdio JSON-RPC stream. Drop the default console provider (which
// writes to stdout) and route every log record — including Kestrel/hosting messages — to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

// Default to a loopback Kestrel endpoint on an OS-assigned port. The device side-channel and the
// optional HTTP MCP transport ride on this host; LAN/internet binding is opt-in via configuration.
builder.WebHost.UseUrls("http://127.0.0.1:0");

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

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithResourcesFromAssembly();

var app = builder.Build();

await app.RunAsync();
