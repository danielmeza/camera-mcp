using System.Collections.Concurrent;
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

    /// <summary>Handles a device trigger (called by the web route). Validates the token and session.</summary>
    Task<TriggerResult> TriggerAsync(string sessionId, string? token, TriggerRequest request, CancellationToken cancellationToken);

    /// <summary>Describes a session for discovery (<c>GET /sessions/{id}</c>); validates the token.</summary>
    SessionDescriptor Describe(string sessionId, string? token);

    IReadOnlyList<string> ActiveSessionIds { get; }
}

/// <summary>
/// Lets remote/embedded devices drive captures. The agent starts a session; the web host exposes
/// <c>POST /sessions/{id}/trigger</c> (token-gated), which captures a still or a rapid-fire burst here
/// and hands it to the agent in order via <see cref="AwaitNextAsync"/>. Many sessions can run at once;
/// each capture goes through <see cref="ICameraService"/>, sharing the per-device lock with everything else.
/// </summary>
public sealed class CaptureSessionService : ICaptureSessionService, IDisposable
{
    private readonly ICameraService _camera;
    private readonly ITunnelLauncher _tunnel;
    private readonly IHttpHostInfo _host;
    private readonly ILogger<CaptureSessionService> _logger;

    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);

    public CaptureSessionService(
        ICameraService camera, ITunnelLauncher tunnel, IHttpHostInfo host, ILogger<CaptureSessionService> logger)
    {
        _camera = camera;
        _tunnel = tunnel;
        _host = host;
        _logger = logger;
    }

    public IReadOnlyList<string> ActiveSessionIds => _sessions.Keys.ToList();

    public async Task<SessionInfo> StartAsync(SessionStartOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        CaptureOptionsValidation.ValidateQuality(options.Quality);
        CaptureOptionsValidation.ValidateDimensions(options.Width, options.Height);

        // Validate the device up front (fail fast before registering a session).
        var resolved = await _camera.ResolveInputAsync(options.DeviceId, options.Width, options.Height, 0, cancellationToken)
            .ConfigureAwait(false);

        var id = "sess_" + Guid.NewGuid().ToString("N")[..8];
        var token = Guid.NewGuid().ToString("N");
        var state = new SessionState(id, token, options, resolved.DeviceName);

        TunnelProvider effective = TunnelProvider.None;
        string? tunnelTriggerUrl = null;
        string? note = null;
        if (options.Tunnel != TunnelProvider.None)
        {
            var (handle, eff, n) = await _tunnel.StartAsync(_host.Port, options.Tunnel, cancellationToken).ConfigureAwait(false);
            state.Tunnel = handle;
            effective = eff;
            note = n;
            tunnelTriggerUrl = handle is null ? null : $"{handle.PublicUrl}/sessions/{id}/trigger?token={token}";
        }

        _sessions[id] = state;
        _logger.LogInformation("Capture session {Id} started for {Device}.", id, resolved.DeviceName);

        return new SessionInfo(
            id,
            token,
            TriggerUrl: $"{_host.BaseUrl}/sessions/{id}/trigger?token={token}",
            TunnelTriggerUrl: tunnelTriggerUrl,
            DeviceName: resolved.DeviceName,
            Tunnel: effective,
            TunnelNote: note);
    }

    public Task<bool> StopAsync(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var state))
        {
            return Task.FromResult(false);
        }

        TearDown(state);
        _logger.LogInformation("Capture session {Id} stopped.", sessionId);
        return Task.FromResult(true);
    }

    public SessionDescriptor Describe(string sessionId, string? token)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
        {
            return new SessionDescriptor(SessionOutcome.NotFound);
        }

        if (!TokenMatches(state, token))
        {
            return new SessionDescriptor(SessionOutcome.Unauthorized);
        }

        return new SessionDescriptor(SessionOutcome.Ok, state.Id, state.DeviceName);
    }

    public async Task<TriggerResult> TriggerAsync(string sessionId, string? token, TriggerRequest request, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
        {
            return new TriggerResult(SessionOutcome.NotFound);
        }

        if (!TokenMatches(state, token))
        {
            return new TriggerResult(SessionOutcome.Unauthorized);
        }

        // The session may be stopped while this capture runs; key the work to the session's own token.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, state.Cts.Token);
        var source = state.Source;
        var count = Math.Max(1, request.Count ?? source.BurstCount);

        try
        {
            var seq = Interlocked.Increment(ref state.Seq);
            TriggeredCapture capture;
            if (count <= 1)
            {
                var still = await _camera.CaptureImageAsync(new ImageCaptureOptions
                {
                    DeviceId = source.DeviceId,
                    Width = source.Width,
                    Height = source.Height,
                    Format = source.Format,
                    Quality = source.Quality,
                }, linked.Token).ConfigureAwait(false);
                capture = new TriggeredCapture(seq, DateTimeOffset.UtcNow, request.Name, request.Description, still, null);
            }
            else
            {
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
                }, linked.Token).ConfigureAwait(false);
                capture = new TriggeredCapture(seq, DateTimeOffset.UtcNow, request.Name, request.Description, null, burst);
            }

            if (!state.Frames.Writer.TryWrite(capture))
            {
                return new TriggerResult(SessionOutcome.NotFound); // session stopped mid-capture
            }

            return new TriggerResult(SessionOutcome.Ok, seq, capture.FrameCount, capture.IsBurst, request.Name, request.Description);
        }
        catch (Exception ex) when (ex is CaptureValidationException or CaptureFailedException or FFmpegNotFoundException)
        {
            return new TriggerResult(SessionOutcome.Failed, Error: ex.Message);
        }
    }

    public async Task<TriggeredCapture?> AwaitNextAsync(string sessionId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(sessionId, out var state))
        {
            throw new CaptureValidationException($"No active capture session '{sessionId}'.");
        }

        // Zero/negative timeout is a non-blocking poll: return a buffered capture if ready, else null.
        if (timeout <= TimeSpan.Zero)
        {
            return state.Frames.Reader.TryRead(out var ready) ? ready : null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            return await state.Frames.Reader.ReadAsync(cts.Token).ConfigureAwait(false);
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

    private static bool TokenMatches(SessionState state, string? token) =>
        !string.IsNullOrEmpty(token) && string.Equals(token, state.Token, StringComparison.Ordinal);

    private void TearDown(SessionState state)
    {
        state.Cts.Cancel();                 // abort any in-flight capture keyed to this session
        state.Frames.Writer.TryComplete();  // wake any waiting AwaitNext with a clean close
        state.Tunnel?.Dispose();
        // state.Cts is intentionally not disposed: it carries no timer/wait-handle, and an in-flight
        // TriggerAsync may still hold a linked token — letting the GC reclaim it avoids a dispose race.
    }

    public void Dispose()
    {
        foreach (var id in _sessions.Keys.ToList())
        {
            if (_sessions.TryRemove(id, out var state))
            {
                TearDown(state);
            }
        }
    }

    private sealed class SessionState(string id, string token, SessionStartOptions source, string deviceName)
    {
        public string Id { get; } = id;
        public string Token { get; } = token;
        public SessionStartOptions Source { get; } = source;
        public string DeviceName { get; } = deviceName;
        public Channel<TriggeredCapture> Frames { get; } =
            Channel.CreateBounded<TriggeredCapture>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
        public CancellationTokenSource Cts { get; } = new();
        public TunnelHandle? Tunnel { get; set; }
        public int Seq;
    }
}
