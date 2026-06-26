using System.Collections.Concurrent;
using System.Globalization;
using CameraMcp.Server.Models;
using Microsoft.Extensions.Logging;

namespace CameraMcp.Server.Services;

/// <summary>A tunnel the agent started on demand to expose a loopback port publicly.</summary>
/// <param name="TunnelId">Handle used to stop it.</param>
/// <param name="Port">The loopback port being exposed.</param>
/// <param name="PublicUrl">The public URL, or null when no tunnel tool is installed.</param>
/// <param name="Provider">The provider that actually ran.</param>
/// <param name="Note">Human note (e.g. tool-not-installed) when there's no public URL.</param>
public sealed record TunnelEntry(string TunnelId, int Port, string? PublicUrl, TunnelProvider Provider, string? Note);

/// <summary>Starts/stops public tunnels on demand so an agent can expose a preview or session endpoint.</summary>
public interface ITunnelManager
{
    Task<TunnelEntry> StartAsync(int port, TunnelProvider provider, CancellationToken cancellationToken);
    bool Stop(string tunnelId);
    IReadOnlyList<TunnelEntry> List();
}

/// <summary>Tracks agent-started tunnels (id → live handle) and tears them down on stop/dispose.</summary>
public sealed class TunnelManager : ITunnelManager, IDisposable
{
    private readonly ITunnelLauncher _launcher;
    private readonly ILogger<TunnelManager> _logger;
    private readonly ConcurrentDictionary<string, (TunnelHandle? Handle, TunnelEntry Entry)> _tunnels = new();
    private int _counter;

    public TunnelManager(ITunnelLauncher launcher, ILogger<TunnelManager> logger)
    {
        _launcher = launcher;
        _logger = logger;
    }

    public async Task<TunnelEntry> StartAsync(int port, TunnelProvider provider, CancellationToken cancellationToken)
    {
        if (port is < 1 or > 65535)
        {
            throw new CaptureValidationException("port must be between 1 and 65535.");
        }

        var (handle, effective, note) = await _launcher.StartAsync(port, provider, cancellationToken).ConfigureAwait(false);
        var id = "tun_" + Interlocked.Increment(ref _counter).ToString("D3", CultureInfo.InvariantCulture);
        var entry = new TunnelEntry(id, port, handle?.PublicUrl, effective, note);
        _tunnels[id] = (handle, entry);
        _logger.LogInformation("Tunnel {Id} → port {Port}: {Url}", id, port, handle?.PublicUrl ?? "(no public url)");
        return entry;
    }

    public bool Stop(string tunnelId)
    {
        if (_tunnels.TryRemove(tunnelId, out var tunnel))
        {
            tunnel.Handle?.Dispose();
            _logger.LogInformation("Tunnel {Id} stopped.", tunnelId);
            return true;
        }

        return false;
    }

    public IReadOnlyList<TunnelEntry> List() => _tunnels.Values.Select(v => v.Entry).ToList();

    public void Dispose()
    {
        foreach (var tunnel in _tunnels.Values)
        {
            tunnel.Handle?.Dispose();
        }

        _tunnels.Clear();
    }
}
