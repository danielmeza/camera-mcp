using CameraMcp.Server.Models;
using CameraMcp.Server.Services;

namespace CameraMcp.Tests.Services;

public class TunnelLauncherTests
{
    [Fact]
    public void BuildCommand_cloudflare_points_at_the_loopback_port()
    {
        var command = TunnelLauncher.BuildCommand(TunnelProvider.Cloudflare, 8123);
        Assert.Equal("cloudflared", command.Executable);
        Assert.Equal(new[] { "tunnel", "--url", "http://127.0.0.1:8123" }, command.Arguments);
    }

    [Fact]
    public void BuildCommand_devtunnel_hosts_the_port_anonymously()
    {
        var command = TunnelLauncher.BuildCommand(TunnelProvider.DevTunnel, 8123);
        Assert.Equal("devtunnel", command.Executable);
        Assert.Contains("8123", command.Arguments);
        Assert.Contains("--allow-anonymous", command.Arguments);
    }

    [Fact]
    public void ExtractUrl_cloudflare_pulls_the_trycloudflare_url()
    {
        var line = "2026-06-26 INF |  https://small-green-cat-1234.trycloudflare.com  | proxying";
        Assert.Equal("https://small-green-cat-1234.trycloudflare.com",
            TunnelLauncher.ExtractUrl(TunnelProvider.Cloudflare, line));
    }

    [Fact]
    public void ExtractUrl_devtunnel_pulls_the_dotted_host_url()
    {
        var line = "Connect via browser: https://abcd-8123.usw2.devtunnels.ms/";
        var url = TunnelLauncher.ExtractUrl(TunnelProvider.DevTunnel, line);
        Assert.StartsWith("https://abcd-8123.usw2.devtunnels.ms", url);
    }

    [Theory]
    [InlineData(TunnelProvider.Cloudflare, "no url here")]
    [InlineData(TunnelProvider.DevTunnel, "still nothing")]
    [InlineData(TunnelProvider.None, "https://x.trycloudflare.com")]
    public void ExtractUrl_returns_null_when_no_match(TunnelProvider provider, string line)
    {
        Assert.Null(TunnelLauncher.ExtractUrl(provider, line));
    }
}
