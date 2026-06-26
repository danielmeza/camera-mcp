using CameraMcp.Server.Configuration;
using CameraMcp.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// stdio transport reserves stdout for the JSON-RPC protocol stream, so every log
// record (all levels) must be routed to stderr instead.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

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
builder.Services.AddSingleton<IPreviewService, PreviewService>();
builder.Services.AddSingleton<ICaptureQueue, CaptureQueue>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithResourcesFromAssembly();

await builder.Build().RunAsync();
