using System.Diagnostics;
using System.Net;
using System.Text;
using CameraMcp.Server.Models;
using Microsoft.Extensions.Logging;

namespace CameraMcp.Server.Services;

/// <summary>Runs a single live MJPEG preview (loopback HTTP, optional public tunnel).</summary>
public interface IPreviewService
{
    Task<PreviewInfo> StartAsync(PreviewOptions options, CancellationToken cancellationToken);

    Task<bool> StopAsync();

    bool IsRunning { get; }
}

/// <summary>
/// Serves a live preview: a loopback <see cref="HttpListener"/> relays a continuous ffmpeg MJPEG stream
/// to a browser (one viewer at a time; the device is only opened while someone is watching), optionally
/// fronted by a Cloudflare/Dev tunnel. Access is gated by a per-session token.
/// </summary>
public sealed class PreviewService : IPreviewService, IDisposable
{
    private const string Loopback = "127.0.0.1";

    private readonly ICameraService _camera;
    private readonly IFFmpegLocator _locator;
    private readonly ITunnelLauncher _tunnel;
    private readonly ILogger<PreviewService> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Session? _session;

    public PreviewService(
        ICameraService camera,
        IFFmpegLocator locator,
        ITunnelLauncher tunnel,
        ILogger<PreviewService> logger)
    {
        _camera = camera;
        _locator = locator;
        _tunnel = tunnel;
        _logger = logger;
    }

    public bool IsRunning => _session is not null;

    public async Task<PreviewInfo> StartAsync(PreviewOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        CaptureOptionsValidation.ValidateQuality(options.Quality);
        CaptureOptionsValidation.ValidateFps(options.Fps);

        // Resolve the device input and confirm ffmpeg up front (clear errors before we open a socket).
        var resolved = await _camera.ResolveInputAsync(options.DeviceId, options.Width, options.Height, options.Fps, cancellationToken)
            .ConfigureAwait(false);
        _ = _locator.Resolve();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is not null)
            {
                await StopInternalAsync(_session).ConfigureAwait(false);
                _session = null;
            }

            var token = Guid.NewGuid().ToString("N");
            var (listener, port) = LoopbackHttp.StartWithRetry();

            var session = new Session(
                listener, token, port, resolved.LockKey, resolved.FfmpegInputArgs, options.Quality, resolved.DeviceName);
            session.AcceptLoop = Task.Run(() => AcceptLoopAsync(session));
            _session = session;

            TunnelProvider effectiveTunnel = TunnelProvider.None;
            string? tunnelUrl = null;
            string? tunnelNote = null;
            if (options.Tunnel != TunnelProvider.None)
            {
                var (handle, effective, note) = await _tunnel.StartAsync(port, options.Tunnel, cancellationToken).ConfigureAwait(false);
                session.Tunnel = handle;
                effectiveTunnel = effective;
                tunnelNote = note;
                tunnelUrl = handle is null ? null : $"{handle.PublicUrl}/?token={token}";
            }

            _logger.LogInformation("Live preview started for {Device} on {Url}.", resolved.DeviceName, $"http://{Loopback}:{port}/");

            return new PreviewInfo(
                resolved.DeviceName,
                LocalUrl: $"http://{Loopback}:{port}/?token={token}",
                StreamUrl: $"http://{Loopback}:{port}/stream?token={token}",
                TunnelUrl: tunnelUrl,
                Tunnel: effectiveTunnel,
                TunnelNote: tunnelNote);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_session is null)
            {
                return false;
            }

            await StopInternalAsync(_session).ConfigureAwait(false);
            _session = null;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task AcceptLoopAsync(Session session)
    {
        while (session.Listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await session.Listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                break; // listener stopped
            }

            _ = Task.Run(() => HandleRequestAsync(session, context));
        }
    }

    private async Task HandleRequestAsync(Session session, HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            var token = context.Request.QueryString["token"];

            if (!string.Equals(token, session.Token, StringComparison.Ordinal))
            {
                context.Response.StatusCode = 403;
                context.Response.Close();
                return;
            }

            if (path == "/")
            {
                ServeIndex(session, context);
            }
            else if (path == "/stream")
            {
                await ServeStreamAsync(session, context).ConfigureAwait(false);
            }
            else
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebugSafe(ex);
            try { context.Response.Abort(); } catch (Exception) { /* ignore */ }
        }
    }

    private void ServeIndex(Session session, HttpListenerContext context)
    {
        var html =
            $"<!doctype html><html><head><meta charset=utf-8><title>camera-mcp · {WebUtility.HtmlEncode(session.DeviceName)}</title></head>" +
            "<body style=\"margin:0;background:#111;display:flex;align-items:center;justify-content:center;height:100vh\">" +
            $"<img src=\"/stream?token={session.Token}\" style=\"max-width:100%;max-height:100%\" alt=\"live preview\"></body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers["Referrer-Policy"] = "no-referrer"; // don't leak the token via Referer
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.Close();
    }

    private async Task ServeStreamAsync(Session session, HttpListenerContext context)
    {
        if (!session.Viewer.Wait(0))
        {
            context.Response.StatusCode = 503;
            var msg = Encoding.UTF8.GetBytes("Preview is busy: one viewer at a time.");
            context.Response.OutputStream.Write(msg, 0, msg.Length);
            context.Response.Close();
            return;
        }

        Process? ffmpeg = null;
        IAsyncDisposable? deviceLock = null;
        var token = session.Cts.Token;
        try
        {
            // Share the device serialization lock with captures so the camera isn't opened twice.
            deviceLock = await _camera.AcquireDeviceLockAsync(session.LockKey, token).ConfigureAwait(false);

            ffmpeg = StartStreamProcess(session);
            DrainStderr(ffmpeg);

            context.Response.SendChunked = true;
            context.Response.ContentType = $"multipart/x-mixed-replace; boundary={FFmpegArguments.MjpegBoundary}";
            context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            context.Response.Headers["Referrer-Policy"] = "no-referrer"; // don't leak the token via Referer

            var buffer = new byte[64 * 1024];
            var source = ffmpeg.StandardOutput.BaseStream;
            int read;
            while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                await context.Response.OutputStream.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                await context.Response.OutputStream.FlushAsync(token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Client disconnect, stop, or ffmpeg end — normal for a live stream.
            _logger.LogDebugSafe(ex);
        }
        finally
        {
            KillQuietly(ffmpeg);
            if (deviceLock is not null)
            {
                await deviceLock.DisposeAsync().ConfigureAwait(false);
            }

            try { context.Response.Close(); } catch (Exception) { /* already closed */ }
            try { session.Viewer.Release(); } catch (ObjectDisposedException) { /* stopped */ } catch (SemaphoreFullException) { /* drained */ }
        }
    }

    private Process StartStreamProcess(Session session)
    {
        var args = FFmpegArguments.BuildMjpegStreamArgs(session.InputArgs, session.Quality);
        var startInfo = new ProcessStartInfo
        {
            FileName = _locator.Resolve(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        var process = new Process { StartInfo = startInfo };
        process.Start();
        return process;
    }

    private static void DrainStderr(Process process) =>
        _ = Task.Run(async () =>
        {
            try { await process.StandardError.ReadToEndAsync().ConfigureAwait(false); }
            catch (Exception) { /* ignore */ }
        });

    private static void KillQuietly(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { /* already exited */ }
        finally
        {
            process.Dispose();
        }
    }

    private async Task StopInternalAsync(Session session)
    {
        session.Cts.Cancel(); // end any in-flight stream (its read + device-lock wait) so it releases
        session.Tunnel?.Dispose();
        try { session.Listener.Stop(); } catch (Exception) { /* ignore */ }
        try { session.Listener.Close(); } catch (Exception) { /* ignore */ }

        try
        {
            await session.AcceptLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception) { /* best effort */ }

        // Drain: wait for an active stream to release the viewer slot before disposing the semaphore.
        try { session.Viewer.Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { /* best effort */ }

        session.Viewer.Dispose();
        session.Cts.Dispose();
        _logger.LogInformation("Live preview stopped for {Device}.", session.DeviceName);
    }

    public void Dispose()
    {
        try { StopAsync().GetAwaiter().GetResult(); } catch (Exception) { /* ignore */ }
        _gate.Dispose();
    }

    private sealed class Session(
        HttpListener listener, string token, int port, string lockKey,
        IReadOnlyList<string> inputArgs, int quality, string deviceName)
    {
        public HttpListener Listener { get; } = listener;
        public string Token { get; } = token;
        public int Port { get; } = port;
        public string LockKey { get; } = lockKey;
        public IReadOnlyList<string> InputArgs { get; } = inputArgs;
        public int Quality { get; } = quality;
        public string DeviceName { get; } = deviceName;
        public SemaphoreSlim Viewer { get; } = new(1, 1);
        public CancellationTokenSource Cts { get; } = new();
        public TunnelHandle? Tunnel { get; set; }
        public Task AcceptLoop { get; set; } = Task.CompletedTask;
    }
}

internal static class PreviewLogExtensions
{
    public static void LogDebugSafe(this ILogger logger, Exception ex) =>
        logger.LogDebug(ex, "Preview request ended.");
}
