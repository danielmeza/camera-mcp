using CameraMcp.Server.Models;
using CameraMcp.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace CameraMcp.Tests.Services;

public class TunnelManagerTests
{
    [Fact]
    public async Task Start_tracks_the_tunnel_and_stop_removes_it()
    {
        using var manager = new TunnelManager(new StubLauncher(), NullLogger<TunnelManager>.Instance);

        var entry = await manager.StartAsync(8080, TunnelProvider.Cloudflare, CancellationToken.None);
        Assert.StartsWith("tun_", entry.TunnelId);
        Assert.Equal(8080, entry.Port);
        Assert.Single(manager.List());

        Assert.True(manager.Stop(entry.TunnelId));
        Assert.Empty(manager.List());
        Assert.False(manager.Stop(entry.TunnelId)); // already gone
    }

    [Fact]
    public async Task Start_surfaces_the_note_when_no_tunnel_tool_is_installed()
    {
        using var manager = new TunnelManager(new StubLauncher(), NullLogger<TunnelManager>.Instance);

        var entry = await manager.StartAsync(9000, TunnelProvider.Cloudflare, CancellationToken.None);
        Assert.Null(entry.PublicUrl);
        Assert.Equal("cloudflared not installed", entry.Note);
    }

    [Fact]
    public async Task Start_rejects_an_out_of_range_port()
    {
        using var manager = new TunnelManager(new StubLauncher(), NullLogger<TunnelManager>.Instance);
        await Assert.ThrowsAsync<CaptureValidationException>(() =>
            manager.StartAsync(0, TunnelProvider.Cloudflare, CancellationToken.None));
    }

    private sealed class StubLauncher : ITunnelLauncher
    {
        public Task<(TunnelHandle? Handle, TunnelProvider Effective, string? Note)> StartAsync(int port, TunnelProvider provider, CancellationToken cancellationToken) =>
            Task.FromResult<(TunnelHandle?, TunnelProvider, string?)>((null, TunnelProvider.None, "cloudflared not installed"));
    }
}
