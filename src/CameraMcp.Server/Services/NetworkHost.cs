using System.Net;
using System.Net.Sockets;

namespace CameraMcp.Server.Services;

/// <summary>Helpers for the built-in web host's bind address and the host a client should use to reach it.</summary>
internal static class NetworkHost
{
    /// <summary>Normalizes a configured bind value into a host Kestrel can bind (loopback / all / specific IP).</summary>
    public static string NormalizeBindHost(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "loopback" or "localhost" or "127.0.0.1" => "127.0.0.1",
        "lan" or "any" or "all" or "0.0.0.0" or "*" or "+" => "0.0.0.0",
        var s => s,
    };

    /// <summary>The host a client should target to reach a server that bound to <paramref name="boundHost"/>.</summary>
    public static string ReachableHost(string boundHost) => boundHost.Trim().Trim('[', ']').ToLowerInvariant() switch
    {
        "0.0.0.0" or "::" or "+" or "*" => PrimaryLanIPv4() ?? "127.0.0.1",
        "localhost" or "127.0.0.1" or "::1" => "127.0.0.1",
        _ => boundHost.Trim('[', ']'),
    };

    /// <summary>The host's primary outbound IPv4, or null if it can't be determined (offline).</summary>
    public static string? PrimaryLanIPv4()
    {
        try
        {
            // Connecting a UDP socket sends no packets; it just selects the outbound interface.
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530);
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch (Exception)
        {
            try
            {
                return Array.Find(
                    Dns.GetHostAddresses(Dns.GetHostName()),
                    a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))?.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
