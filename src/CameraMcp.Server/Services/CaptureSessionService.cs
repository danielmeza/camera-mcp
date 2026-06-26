using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CameraMcp.Server.Models;
using Microsoft.Extensions.Logging;

namespace CameraMcp.Server.Services;

/// <summary>A device-triggered ("remote shutter") capture session.</summary>
public interface ICaptureSessionService
{
    Task<SessionInfo> StartAsync(SessionStartOptions options, CancellationToken cancellationToken);

    Task<bool> StopAsync(string sessionId);

    /// <summary>Long-poll for the next device-triggered capture; null if none arrives within the timeout.</summary>
    Task<TriggeredCapture?> AwaitNextAsync(string sessionId, TimeSpan timeout, CancellationToken cancellationToken);

    string? CurrentSessionId { get; }
}

/// <summary>
/// Lets a remote/embedded device drive captures: the agent starts a session (one active at a time),
/// the device POSTs <c>/trigger</c> (with the session token) whenever it wants a frame, and the agent
/// receives those frames in order via <see cref="AwaitNextAsync"/> (long-poll). Each trigger captures a
/// still through <see cref="ICameraService"/>, so it shares the per-device lock with other captures.
/// </summary>
public sealed class CaptureSessionService : ICaptureSessionService, IDisposable
{
    private readonly ICameraService _camera;
    private readonly ITunnelLauncher _tunnel;
    private readonly ILogger<CaptureSessionService> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Session? _session;

    public CaptureSessionService(ICameraService camera, ITunnelLauncher tunnel, ILogger<CaptureSessionService> logger)
    {
        _camera = camera;
        _tunnel = tunnel;
        _logger = logger;
    }

    public string? CurrentSessionId => _session?.Id;

    public async Task<SessionInfo> StartAsync(SessionStartOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        CaptureOptionsValidation.ValidateQuality(options.Quality);
        CaptureOptionsValidation.ValidateDimensions(options.Width, options.Height);

        // Validate the device up front (fail fast before opening a socket).
        var resolved = await _camera.ResolveInputAsync(options.DeviceId, options.Width, options.Height, 0, cancellationToken)
            .ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is not null)
            {
                await StopInternalAsync(_session).ConfigureAwait(false);
                _session = null;
            }

            var (listener, port) = LoopbackHttp.StartWithRetry();
            var token = Guid.NewGuid().ToString("N");

            var session = new Session(
                id: "sess_" + Guid.NewGuid().ToString("N")[..8],
                token: token,
                port: port,
                listener: listener,
                deviceName: resolved.DeviceName,
                source: options);
            session.AcceptLoop = Task.Run(() => AcceptLoopAsync(session));
            _session = session;

            TunnelProvider effective = TunnelProvider.None;
            string? tunnelTriggerUrl = null;
            string? note = null;
            if (options.Tunnel != TunnelProvider.None)
            {
                var (handle, eff, n) = await _tunnel.StartAsync(port, options.Tunnel, cancellationToken).ConfigureAwait(false);
                session.Tunnel = handle;
                effective = eff;
                note = n;
                tunnelTriggerUrl = handle is null ? null : $"{handle.PublicUrl}/trigger?token={token}";
            }

            _logger.LogInformation("Capture session {Id} started for {Device} on port {Port}.", session.Id, resolved.DeviceName, port);

            return new SessionInfo(
                session.Id,
                token,
                TriggerUrl: $"http://{LoopbackHttp.Address}:{port}/trigger?token={token}",
                TunnelTriggerUrl: tunnelTriggerUrl,
                DeviceName: resolved.DeviceName,
                Tunnel: effective,
                TunnelNote: note);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> StopAsync(string sessionId)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_session is null || !string.Equals(_session.Id, sessionId, StringComparison.Ordinal))
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

    public async Task<TriggeredCapture?> AwaitNextAsync(string sessionId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var session = _session;
        if (session is null || !string.Equals(session.Id, sessionId, StringComparison.Ordinal))
        {
            throw new CaptureValidationException($"No active capture session '{sessionId}'.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout > TimeSpan.Zero)
        {
            cts.CancelAfter(timeout);
        }

        try
        {
            return await session.Frames.Reader.ReadAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null; // timed out waiting for the device to trigger
        }
        catch (ChannelClosedException)
        {
            return null; // session stopped
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
                break;
            }

            _ = Task.Run(() => HandleRequestAsync(session, context));
        }
    }

    private async Task HandleRequestAsync(Session session, HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            var token = context.Request.QueryString["token"] ?? context.Request.Headers["X-Session-Token"];
            if (!string.Equals(token, session.Token, StringComparison.Ordinal))
            {
                WriteJson(context, 401, new { error = "invalid or missing token" });
                return;
            }

            if (path == "/session" && context.Request.HttpMethod == "GET")
            {
                WriteJson(context, 200, new { sessionId = session.Id, device = session.DeviceName });
            }
            else if (path == "/trigger" && context.Request.HttpMethod == "POST")
            {
                await HandleTriggerAsync(session, context).ConfigureAwait(false);
            }
            else
            {
                WriteJson(context, 404, new { error = "not found" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Session request failed.");
            try { context.Response.Abort(); } catch (Exception) { /* ignore */ }
        }
    }

    private async Task HandleTriggerAsync(Session session, HttpListenerContext context)
    {
        try
        {
            var request = await ReadTriggerRequestAsync(context).ConfigureAwait(false);
            var source = session.Source;
            var count = Math.Max(1, request.Count ?? source.BurstCount);
            var seq = Interlocked.Increment(ref session.Seq);

            TriggeredCapture capture;
            if (count <= 1)
            {
                // Single still.
                var still = await _camera.CaptureImageAsync(new ImageCaptureOptions
                {
                    DeviceId = source.DeviceId,
                    Width = source.Width,
                    Height = source.Height,
                    Format = source.Format,
                    Quality = source.Quality,
                }, session.Cts.Token).ConfigureAwait(false);
                capture = new TriggeredCapture(seq, DateTimeOffset.UtcNow, request.Name, request.Description, still, null);
            }
            else
            {
                // Rapid-fire burst — a scene capture (bounded by the server's MaxSceneFrames).
                var interval = request.IntervalSeconds ?? source.BurstIntervalSeconds;
                var burst = await _camera.CaptureSceneAsync(new SceneCaptureOptions
                {
                    DeviceId = source.DeviceId,
                    FrameCount = count,
                    IntervalSeconds = interval,
                    Width = source.Width,
                    Height = source.Height,
                    Format = source.Format,
                    Quality = source.Quality,
                }, session.Cts.Token).ConfigureAwait(false);
                capture = new TriggeredCapture(seq, DateTimeOffset.UtcNow, request.Name, request.Description, null, burst);
            }

            session.Frames.Writer.TryWrite(capture);
            WriteJson(context, 200, new
            {
                seq,
                name = request.Name,
                description = request.Description,
                frameCount = capture.FrameCount,
                kind = capture.IsBurst ? "burst" : "still",
            });
        }
        catch (Exception ex) when (ex is CaptureValidationException or CaptureFailedException or FFmpegNotFoundException)
        {
            WriteJson(context, 500, new { error = ex.Message });
        }
    }

    /// <summary>Reads optional name/description/count/interval overrides from the query string and/or a JSON body.</summary>
    private static async Task<TriggerRequest> ReadTriggerRequestAsync(HttpListenerContext context)
    {
        var query = context.Request.QueryString;
        string? name = query["name"];
        string? description = query["description"];
        int? count = TryInt(query["count"]);
        double? interval = TryDouble(query["interval"]);

        if (context.Request.HasEntityBody)
        {
            try
            {
                using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                var body = await reader.ReadToEndAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(body) && body.AsSpan().TrimStart().StartsWith("{"))
                {
                    using var doc = JsonDocument.Parse(body);
                    var root = doc.RootElement;
                    name ??= GetJsonString(root, "name");
                    description ??= GetJsonString(root, "description");
                    count ??= GetJsonInt(root, "count");
                    interval ??= GetJsonDouble(root, "interval");
                }
            }
            catch (Exception) { /* ignore a malformed body — query params still apply */ }
        }

        return new TriggerRequest { Name = name, Description = description, Count = count, IntervalSeconds = interval };
    }

    private static int? TryInt(string? s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static double? TryDouble(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string? GetJsonString(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? GetJsonInt(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static double? GetJsonDouble(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    private static void WriteJson(HttpListenerContext context, int status, object body)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body));
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.Close();
    }

    private async Task StopInternalAsync(Session session)
    {
        session.Cts.Cancel();
        session.Frames.Writer.TryComplete();
        session.Tunnel?.Dispose();
        try { session.Listener.Stop(); } catch (Exception) { /* ignore */ }
        try { session.Listener.Close(); } catch (Exception) { /* ignore */ }
        try { await session.AcceptLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (Exception) { /* best effort */ }
        session.Cts.Dispose();
        _logger.LogInformation("Capture session {Id} stopped.", session.Id);
    }

    public void Dispose()
    {
        var session = _session;
        if (session is not null)
        {
            try { StopInternalAsync(session).GetAwaiter().GetResult(); } catch (Exception) { /* ignore */ }
            _session = null;
        }

        _gate.Dispose();
    }

    private sealed class Session(
        string id, string token, int port, HttpListener listener, string deviceName, SessionStartOptions source)
    {
        public string Id { get; } = id;
        public string Token { get; } = token;
        public int Port { get; } = port;
        public HttpListener Listener { get; } = listener;
        public string DeviceName { get; } = deviceName;
        public SessionStartOptions Source { get; } = source;
        public Channel<TriggeredCapture> Frames { get; } =
            Channel.CreateBounded<TriggeredCapture>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
        public CancellationTokenSource Cts { get; } = new();
        public TunnelHandle? Tunnel { get; set; }
        public Task AcceptLoop { get; set; } = Task.CompletedTask;
        public int Seq;
    }
}
