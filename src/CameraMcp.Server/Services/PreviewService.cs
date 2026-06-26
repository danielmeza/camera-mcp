using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using CameraMcp.Server.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CameraMcp.Server.Services;

/// <summary>Runs live MJPEG previews served by the shared web host.</summary>
public interface IPreviewService
{
    Task<PreviewInfo> StartAsync(PreviewOptions options, CancellationToken cancellationToken);

    Task<bool> StopAsync(string previewId);

    /// <summary>Serves the viewer HTML page for a preview (called by the web route).</summary>
    Task ServePageAsync(string previewId, string? token, HttpResponse response);

    /// <summary>Relays the live MJPEG stream to <paramref name="response"/> until the client disconnects.</summary>
    Task ServeStreamAsync(string previewId, string? token, HttpResponse response, CancellationToken cancellationToken);

    IReadOnlyList<string> ActivePreviewIds { get; }
}

/// <summary>
/// Serves live previews over the shared Kestrel host: <c>GET /preview/{id}</c> is a viewer page and
/// <c>GET /preview/{id}/stream</c> relays a continuous ffmpeg MJPEG stream (one viewer at a time; the
/// camera is only opened while someone is watching, under the shared per-device lock). Access is gated
/// by a per-preview token; a Cloudflare/Dev tunnel can expose it publicly.
/// </summary>
public sealed class PreviewService : IPreviewService, IDisposable
{
    private readonly ICameraService _camera;
    private readonly IFFmpegLocator _locator;
    private readonly ITunnelLauncher _tunnel;
    private readonly IHttpHostInfo _host;
    private readonly ILogger<PreviewService> _logger;

    private readonly ConcurrentDictionary<string, PreviewState> _previews = new(StringComparer.Ordinal);

    public PreviewService(
        ICameraService camera,
        IFFmpegLocator locator,
        ITunnelLauncher tunnel,
        IHttpHostInfo host,
        ILogger<PreviewService> logger)
    {
        _camera = camera;
        _locator = locator;
        _tunnel = tunnel;
        _host = host;
        _logger = logger;
    }

    public IReadOnlyList<string> ActivePreviewIds => _previews.Keys.ToList();

    public async Task<PreviewInfo> StartAsync(PreviewOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        CaptureOptionsValidation.ValidateQuality(options.Quality);
        CaptureOptionsValidation.ValidateFps(options.Fps);

        // Resolve the device input and confirm ffmpeg up front (clear errors before registering).
        var resolved = await _camera.ResolveInputAsync(options.DeviceId, options.Width, options.Height, options.Fps, cancellationToken)
            .ConfigureAwait(false);
        _ = _locator.Resolve();

        var id = "prev_" + Guid.NewGuid().ToString("N");   // full 128-bit id: not enumerable
        var token = Guid.NewGuid().ToString("N");
        var state = new PreviewState(id, token, resolved.LockKey, resolved.FfmpegInputArgs, options.Quality, resolved.DeviceName);

        TunnelProvider effectiveTunnel = TunnelProvider.None;
        string? tunnelUrl = null;
        string? tunnelNote = null;
        if (options.Tunnel != TunnelProvider.None)
        {
            var (handle, effective, note) = await _tunnel.StartAsync(_host.Port, options.Tunnel, cancellationToken).ConfigureAwait(false);
            state.Tunnel = handle;
            effectiveTunnel = effective;
            tunnelNote = note;
            tunnelUrl = handle is null ? null : $"{handle.PublicUrl}/preview/{id}?token={token}";
        }

        _previews[id] = state;
        _logger.LogInformation("Live preview {Id} started for {Device}.", id, resolved.DeviceName);

        return new PreviewInfo(
            id,
            resolved.DeviceName,
            LocalUrl: $"{_host.BaseUrl}/preview/{id}?token={token}",
            StreamUrl: $"{_host.BaseUrl}/preview/{id}/stream?token={token}",
            TunnelUrl: tunnelUrl,
            Tunnel: effectiveTunnel,
            TunnelNote: tunnelNote);
    }

    public Task<bool> StopAsync(string previewId)
    {
        if (!_previews.TryRemove(previewId, out var state))
        {
            return Task.FromResult(false);
        }

        state.Cts.Cancel();          // ends an active stream → releases the viewer + device lock + kills ffmpeg
        state.Tunnel?.Dispose();
        _logger.LogInformation("Live preview {Id} stopped.", previewId);
        return Task.FromResult(true);
    }

    public async Task ServePageAsync(string previewId, string? token, HttpResponse response)
    {
        if (!_previews.TryGetValue(previewId, out var state))
        {
            response.StatusCode = 404;
            return;
        }

        if (!TokenMatches(state, token))
        {
            response.StatusCode = 401;
            return;
        }

        var html =
            $"<!doctype html><html><head><meta charset=utf-8><title>camera-mcp · {WebUtility.HtmlEncode(state.DeviceName)}</title></head>" +
            "<body style=\"margin:0;background:#111;display:flex;align-items:center;justify-content:center;height:100vh\">" +
            $"<img src=\"/preview/{previewId}/stream?token={state.Token}\" style=\"max-width:100%;max-height:100%\" alt=\"live preview\"></body></html>";
        response.ContentType = "text/html; charset=utf-8";
        response.Headers["Referrer-Policy"] = "no-referrer"; // don't leak the token via Referer
        await response.WriteAsync(html).ConfigureAwait(false);
    }

    public async Task ServeStreamAsync(string previewId, string? token, HttpResponse response, CancellationToken cancellationToken)
    {
        if (!_previews.TryGetValue(previewId, out var state))
        {
            response.StatusCode = 404;
            return;
        }

        if (!TokenMatches(state, token))
        {
            response.StatusCode = 401;
            return;
        }

        if (!state.Viewer.Wait(0))
        {
            response.StatusCode = 503;
            await response.WriteAsync("Preview is busy: one viewer at a time.").ConfigureAwait(false);
            return;
        }

        Process? ffmpeg = null;
        IAsyncDisposable? deviceLock = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, state.Cts.Token);
        var token2 = linked.Token;
        try
        {
            // Share the device serialization lock with captures so the camera isn't opened twice.
            deviceLock = await _camera.AcquireDeviceLockAsync(state.LockKey, token2).ConfigureAwait(false);

            ffmpeg = StartStreamProcess(state);
            DrainStderr(ffmpeg);

            response.ContentType = $"multipart/x-mixed-replace; boundary={FFmpegArguments.MjpegBoundary}";
            response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            response.Headers["Referrer-Policy"] = "no-referrer";

            var buffer = new byte[64 * 1024];
            var source = ffmpeg.StandardOutput.BaseStream;
            int read;
            while ((read = await source.ReadAsync(buffer, token2).ConfigureAwait(false)) > 0)
            {
                await response.Body.WriteAsync(buffer.AsMemory(0, read), token2).ConfigureAwait(false);
                await response.Body.FlushAsync(token2).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // Client disconnect, stop, or ffmpeg end — normal for a live stream.
            _logger.LogDebug(ex, "Preview stream ended.");
        }
        finally
        {
            KillQuietly(ffmpeg);
            if (deviceLock is not null)
            {
                await deviceLock.DisposeAsync().ConfigureAwait(false);
            }

            try { state.Viewer.Release(); } catch (ObjectDisposedException) { /* stopped */ } catch (SemaphoreFullException) { /* drained */ }
        }
    }

    private static bool TokenMatches(PreviewState state, string? token) =>
        !string.IsNullOrEmpty(token) && string.Equals(token, state.Token, StringComparison.Ordinal);

    private Process StartStreamProcess(PreviewState state)
    {
        var args = FFmpegArguments.BuildMjpegStreamArgs(state.InputArgs, state.Quality);
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

    public void Dispose()
    {
        foreach (var id in _previews.Keys.ToList())
        {
            if (_previews.TryRemove(id, out var state))
            {
                state.Cts.Cancel();
                state.Tunnel?.Dispose();
            }
        }
    }

    private sealed class PreviewState(
        string id, string token, string lockKey, IReadOnlyList<string> inputArgs, int quality, string deviceName)
    {
        public string Id { get; } = id;
        public string Token { get; } = token;
        public string LockKey { get; } = lockKey;
        public IReadOnlyList<string> InputArgs { get; } = inputArgs;
        public int Quality { get; } = quality;
        public string DeviceName { get; } = deviceName;
        public SemaphoreSlim Viewer { get; } = new(1, 1);
        public CancellationTokenSource Cts { get; } = new();
        public TunnelHandle? Tunnel { get; set; }
    }
}
