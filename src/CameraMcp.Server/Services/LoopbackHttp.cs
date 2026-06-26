using System.Net;
using System.Net.Sockets;

namespace CameraMcp.Server.Services;

/// <summary>Helpers for binding a loopback <see cref="HttpListener"/> on a free port.</summary>
internal static class LoopbackHttp
{
    public const string Address = "127.0.0.1";

    public static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    /// <summary>Binds a loopback listener, retrying the small race between probing and binding a port.</summary>
    public static (HttpListener Listener, int Port) StartWithRetry()
    {
        HttpListenerException? last = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var port = FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://{Address}:{port}/");
            try
            {
                listener.Start();
                return (listener, port);
            }
            catch (HttpListenerException ex)
            {
                last = ex; // lost the port race — try another
                listener.Close();
            }
        }

        throw last ?? new HttpListenerException();
    }
}
