using CameraMcp.Server.Configuration;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

namespace CameraMcp.Server.Services;

/// <summary>Exposes the built-in web host's device-reachable base URL and port (resolved after Kestrel binds).</summary>
public interface IHttpHostInfo
{
    /// <summary>The actual port Kestrel bound (resolves an OS-assigned port 0 to the real value).</summary>
    int Port { get; }

    /// <summary>A base URL a device/browser can use to reach this host, e.g. <c>http://192.168.1.50:5005</c>.</summary>
    string BaseUrl { get; }
}

/// <summary>
/// Reads the address Kestrel actually bound from <see cref="IServerAddressesFeature"/> and turns it into a
/// device-reachable base URL: loopback binds stay <c>127.0.0.1</c>, all-interfaces binds resolve to the
/// host's LAN IP, and an explicit <see cref="CameraMcpOptions.PublicBaseUrl"/> overrides everything.
/// </summary>
internal sealed class HttpHostInfo : IHttpHostInfo
{
    private readonly IServer _server;
    private readonly CameraMcpOptions _options;
    private readonly object _lock = new();
    private string? _baseUrl;
    private int _port;

    public HttpHostInfo(IServer server, IOptions<CameraMcpOptions> options)
    {
        _server = server;
        _options = options.Value;
    }

    public int Port { get { Resolve(); return _port; } }

    public string BaseUrl { get { Resolve(); return _baseUrl!; } }

    private void Resolve()
    {
        if (_baseUrl is not null)
        {
            return;
        }

        lock (_lock)
        {
            if (_baseUrl is not null)
            {
                return;
            }

            var address = _server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
                ?? "http://127.0.0.1:0";
            var uri = new Uri(address.Replace("://+", "://0.0.0.0").Replace("://*", "://0.0.0.0"));
            _port = uri.Port;

            if (!string.IsNullOrWhiteSpace(_options.PublicBaseUrl))
            {
                _baseUrl = _options.PublicBaseUrl.TrimEnd('/');
            }
            else
            {
                var host = NetworkHost.ReachableHost(uri.Host);
                _baseUrl = $"{uri.Scheme}://{host}:{_port}";
            }
        }
    }
}
