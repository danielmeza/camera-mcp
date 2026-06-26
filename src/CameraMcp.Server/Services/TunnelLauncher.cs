using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using CameraMcp.Server.Models;

namespace CameraMcp.Server.Services;

/// <summary>A running tunnel: its public URL and the process to stop it.</summary>
public sealed class TunnelHandle(string publicUrl, Process process) : IDisposable
{
    public string PublicUrl { get; } = publicUrl;

    public void Dispose()
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // already exited
        }

        process.Dispose();
    }
}

/// <summary>The executable + arguments to launch a tunnel.</summary>
public sealed record TunnelCommand(string Executable, IReadOnlyList<string> Arguments);

public interface ITunnelLauncher
{
    /// <summary>
    /// Starts a tunnel to <paramref name="port"/> and returns its public URL handle, or null with a
    /// human note when no tunnel tool is installed or the URL could not be obtained.
    /// </summary>
    Task<(TunnelHandle? Handle, TunnelProvider Effective, string? Note)> StartAsync(
        int port, TunnelProvider provider, CancellationToken cancellationToken);
}

/// <summary>
/// Launches a Cloudflare quick tunnel or a Microsoft Dev Tunnel and parses the public URL from its
/// output. Degrades gracefully (returns a note) when the tool isn't on PATH.
/// </summary>
public sealed partial class TunnelLauncher : ITunnelLauncher
{
    public async Task<(TunnelHandle?, TunnelProvider, string?)> StartAsync(
        int port, TunnelProvider provider, CancellationToken cancellationToken)
    {
        var effective = ResolveProvider(provider);
        if (effective == TunnelProvider.None)
        {
            return (null, TunnelProvider.None,
                "No tunnel tool found on PATH. Install cloudflared or devtunnel for a public URL; the local URL still works.");
        }

        var command = BuildCommand(effective, port);
        var resolved = ResolveExecutable(command.Executable);
        if (resolved is null)
        {
            return (null, effective, $"{effective} is no longer resolvable on PATH.");
        }

        var process = new Process { StartInfo = BuildStartInfo(resolved, command.Arguments) };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            process.Dispose();
            return (null, effective, $"Failed to start {effective}: {ex.Message}");
        }

        var url = await ReadUrlAsync(process, effective, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        if (url is null)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.Dispose();
            return (null, effective, $"Started {effective} but could not read a public URL within the timeout.");
        }

        return (new TunnelHandle(url, process), effective, null);
    }

    /// <summary>The provider to actually use: for Auto, the first installed tool (Cloudflare preferred).</summary>
    internal static TunnelProvider ResolveProvider(TunnelProvider requested) => requested switch
    {
        TunnelProvider.Cloudflare => ResolveExecutable("cloudflared") is not null ? TunnelProvider.Cloudflare : TunnelProvider.None,
        TunnelProvider.DevTunnel => ResolveExecutable("devtunnel") is not null ? TunnelProvider.DevTunnel : TunnelProvider.None,
        TunnelProvider.Auto => ResolveExecutable("cloudflared") is not null ? TunnelProvider.Cloudflare
            : ResolveExecutable("devtunnel") is not null ? TunnelProvider.DevTunnel
            : TunnelProvider.None,
        _ => TunnelProvider.None,
    };

    /// <summary>Builds the process start info, invoking .cmd/.bat shims through cmd.exe on Windows.</summary>
    private static ProcessStartInfo BuildStartInfo(string resolvedExe, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var extension = Path.GetExtension(resolvedExe).ToLowerInvariant();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && extension is ".cmd" or ".bat")
        {
            info.FileName = "cmd.exe";
            info.ArgumentList.Add("/c");
            info.ArgumentList.Add(resolvedExe);
        }
        else
        {
            info.FileName = resolvedExe;
        }

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }

    internal static TunnelCommand BuildCommand(TunnelProvider provider, int port)
    {
        var target = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}";
        return provider switch
        {
            TunnelProvider.Cloudflare => new TunnelCommand("cloudflared", ["tunnel", "--url", target]),
            TunnelProvider.DevTunnel => new TunnelCommand("devtunnel", ["host", "-p", port.ToString(CultureInfo.InvariantCulture), "--allow-anonymous"]),
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };
    }

    /// <summary>Extracts the public URL from a line of tunnel-tool output, or null.</summary>
    internal static string? ExtractUrl(TunnelProvider provider, string line)
    {
        var match = provider switch
        {
            TunnelProvider.Cloudflare => CloudflareUrl().Match(line),
            TunnelProvider.DevTunnel => DevTunnelUrl().Match(line),
            _ => Match.Empty,
        };

        return match.Success ? match.Value : null;
    }

    private static async Task<string?> ReadUrlAsync(Process process, TunnelProvider provider, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        // Both cloudflared and devtunnel print the URL to stderr (cloudflared) or stdout (devtunnel);
        // scan both.
        var stdout = ScanAsync(process.StandardOutput, provider, cts.Token);
        var stderr = ScanAsync(process.StandardError, provider, cts.Token);

        try
        {
            var first = await Task.WhenAny(stdout, stderr).ConfigureAwait(false);
            var url = await first.ConfigureAwait(false);
            url ??= await (first == stdout ? stderr : stdout).ConfigureAwait(false);
            return url;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null; // our timeout fired, not the caller — report as "no URL"
        }
        // A caller cancellation propagates (OperationCanceledException) rather than masking as a timeout.
    }

    private static async Task<string?> ScanAsync(StreamReader reader, TunnelProvider provider, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            var url = ExtractUrl(provider, line);
            if (url is not null)
            {
                return url;
            }
        }

        return null;
    }

    /// <summary>Resolves an executable on PATH, honoring Windows PATHEXT (so .cmd/.bat shims are found).</summary>
    internal static string? ResolveExecutable(string exe)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            foreach (var dir in dirs)
            {
                var candidate = Path.Combine(dir, exe);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var dir in dirs)
        {
            if (Path.HasExtension(exe) && File.Exists(Path.Combine(dir, exe)))
            {
                return Path.Combine(dir, exe);
            }

            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, exe + ext);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    [GeneratedRegex(@"https://[A-Za-z0-9][A-Za-z0-9.-]*\.trycloudflare\.com", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CloudflareUrl();

    [GeneratedRegex(@"https://[A-Za-z0-9][A-Za-z0-9.-]*\.devtunnels\.ms[^\s'""]*", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DevTunnelUrl();
}
