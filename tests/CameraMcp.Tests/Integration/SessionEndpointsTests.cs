using System.Net;
using CameraMcp.Server;
using CameraMcp.Server.Models;
using CameraMcp.Server.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CameraMcp.Tests.Integration;

public class SessionEndpointsTests
{
    private sealed record Scope(WebApplication App, HttpClient Client, ICaptureSessionService Sessions) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private static async Task<Scope> StartHostAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ICameraService, RouteStubCamera>();
        builder.Services.AddSingleton<ITunnelLauncher, RouteStubTunnel>();
        builder.Services.AddSingleton<IHttpHostInfo, RouteStubHostInfo>();
        builder.Services.AddSingleton<ICaptureSessionService, CaptureSessionService>();

        var app = builder.Build();
        app.MapDeviceEndpoints();
        await app.StartAsync();
        return new Scope(app, app.GetTestClient(), app.Services.GetRequiredService<ICaptureSessionService>());
    }

    private static async Task<Scope> StartHostWithCorsAsync(string allowedOrigin)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ICameraService, RouteStubCamera>();
        builder.Services.AddSingleton<ITunnelLauncher, RouteStubTunnel>();
        builder.Services.AddSingleton<IHttpHostInfo, RouteStubHostInfo>();
        builder.Services.AddSingleton<ICaptureSessionService, CaptureSessionService>();
        builder.Services.AddCors(o => o.AddPolicy("device", p => p.WithOrigins(allowedOrigin).AllowAnyHeader().AllowAnyMethod()));

        var app = builder.Build();
        app.UseCors();
        app.MapDeviceEndpoints("device");
        await app.StartAsync();
        return new Scope(app, app.GetTestClient(), app.Services.GetRequiredService<ICaptureSessionService>());
    }

    [Fact]
    public async Task Allowed_web_origin_gets_a_cors_header()
    {
        await using var scope = await StartHostWithCorsAsync("https://app.example.com");
        var info = await scope.Sessions.StartAsync(new SessionStartOptions(), CancellationToken.None);

        var req = new HttpRequestMessage(HttpMethod.Get, $"/sessions/{info.SessionId}?token={info.Token}");
        req.Headers.TryAddWithoutValidation("Origin", "https://app.example.com");
        var resp = await scope.Client.SendAsync(req);

        Assert.Equal("https://app.example.com", resp.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task Disallowed_web_origin_gets_no_cors_header()
    {
        await using var scope = await StartHostWithCorsAsync("https://app.example.com");
        var info = await scope.Sessions.StartAsync(new SessionStartOptions(), CancellationToken.None);

        var req = new HttpRequestMessage(HttpMethod.Get, $"/sessions/{info.SessionId}?token={info.Token}");
        req.Headers.TryAddWithoutValidation("Origin", "https://evil.example.com");
        var resp = await scope.Client.SendAsync(req);

        Assert.False(resp.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Post_trigger_with_token_captures_a_burst()
    {
        await using var scope = await StartHostAsync();
        var info = await scope.Sessions.StartAsync(new SessionStartOptions { DeviceId = "cam0" }, CancellationToken.None);

        var resp = await scope.Client.PostAsync($"/sessions/{info.SessionId}/trigger?token={info.Token}&name=door&count=3", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var capture = await scope.Sessions.AwaitNextAsync(info.SessionId, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.True(capture!.IsBurst);
        Assert.Equal(3, capture.FrameCount);
        Assert.Equal("door", capture.Name);
    }

    [Fact]
    public async Task Post_trigger_without_token_is_401()
    {
        await using var scope = await StartHostAsync();
        var info = await scope.Sessions.StartAsync(new SessionStartOptions(), CancellationToken.None);

        var resp = await scope.Client.PostAsync($"/sessions/{info.SessionId}/trigger", null);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Post_trigger_for_unknown_session_is_404()
    {
        await using var scope = await StartHostAsync();

        var resp = await scope.Client.PostAsync("/sessions/sess_nope/trigger?token=whatever", null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Get_session_with_token_returns_the_descriptor()
    {
        await using var scope = await StartHostAsync();
        var info = await scope.Sessions.StartAsync(new SessionStartOptions { DeviceId = "cam0" }, CancellationToken.None);

        var resp = await scope.Client.GetAsync($"/sessions/{info.SessionId}?token={info.Token}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains(info.SessionId, await resp.Content.ReadAsStringAsync());
    }

    private sealed class RouteStubCamera : ICameraService
    {
        public Task<ResolvedCaptureInput> ResolveInputAsync(string? deviceId, int? width, int? height, int fps, CancellationToken cancellationToken) =>
            Task.FromResult(new ResolvedCaptureInput(deviceId ?? "cam", "lock:" + (deviceId ?? "cam"), [], 640, 480));

        public Task<ImageCaptureResult> CaptureImageAsync(ImageCaptureOptions options, CancellationToken cancellationToken) =>
            Task.FromResult(new ImageCaptureResult([1, 2, 3], ImageFormat.Jpeg, 640, 480, "cam", "/tmp/x.jpg"));

        public Task<SceneCaptureResult> CaptureSceneAsync(SceneCaptureOptions options, CancellationToken cancellationToken)
        {
            var frames = Enumerable.Range(1, options.FrameCount)
                .Select(i => new SceneFrame(i, $"/tmp/frame-{i}.jpg", 3, [1, 2, 3]))
                .ToList();
            return Task.FromResult(new SceneCaptureResult("cam", ImageFormat.Jpeg, 640, 480, "/tmp/scene", frames));
        }

        public Task<IAsyncDisposable> AcquireDeviceLockAsync(string lockKey, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable>(new NoopLock());

        public Task<IReadOnlyList<CameraDevice>> ListDevicesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<VideoCaptureResult> CaptureVideoAsync(VideoCaptureOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();

        private sealed class NoopLock : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class RouteStubTunnel : ITunnelLauncher
    {
        public Task<(TunnelHandle? Handle, TunnelProvider Effective, string? Note)> StartAsync(int port, TunnelProvider provider, CancellationToken cancellationToken) =>
            Task.FromResult<(TunnelHandle?, TunnelProvider, string?)>((null, TunnelProvider.None, null));
    }

    private sealed class RouteStubHostInfo : IHttpHostInfo
    {
        public int Port => 5005;
        public string BaseUrl => "http://127.0.0.1:5005";
    }
}
