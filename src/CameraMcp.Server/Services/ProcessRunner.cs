using System.Diagnostics;

namespace CameraMcp.Server.Services;

/// <summary>Outcome of a finished child process.</summary>
/// <param name="ExitCode">Process exit code.</param>
/// <param name="StandardOutput">Raw bytes captured from stdout (e.g. an encoded image on <c>pipe:1</c>).</param>
/// <param name="StandardError">Decoded text captured from stderr (FFmpeg diagnostics).</param>
public sealed record ProcessResult(int ExitCode, byte[] StandardOutput, string StandardError);

public interface IProcessRunner
{
    /// <summary>
    /// Starts <paramref name="executable"/> with <paramref name="arguments"/>, optionally writing
    /// <paramref name="standardInput"/> to its stdin, and returns once it exits.
    /// </summary>
    /// <exception cref="TimeoutException">The process exceeded <paramref name="timeout"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired.</exception>
    Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        byte[]? standardInput,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

/// <summary>
/// Runs a child process with binary stdout capture and text stderr capture. Arguments are passed
/// through <see cref="ProcessStartInfo.ArgumentList"/> so each token is quoted by the runtime — no
/// manual escaping of device names or paths.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        byte[]? standardInput,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout > TimeSpan.Zero)
        {
            timeoutCts.CancelAfter(timeout);
        }

        var token = timeoutCts.Token;

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process '{executable}'.");
        }

        // Drain both pipes concurrently with feeding stdin to avoid a full-buffer deadlock.
        var stdoutTask = ReadAllBytesAsync(process.StandardOutput.BaseStream, token);
        var stderrTask = process.StandardError.ReadToEndAsync(token);
        var stdinTask = WriteStandardInputAsync(process, standardInput, token);

        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask, stdinTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException(
                $"'{Path.GetFileName(executable)}' did not complete within {timeout.TotalSeconds:0} seconds.");
        }

        return new ProcessResult(process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static async Task WriteStandardInputAsync(Process process, byte[]? standardInput, CancellationToken cancellationToken)
    {
        await using var stdin = process.StandardInput.BaseStream;
        if (standardInput is { Length: > 0 })
        {
            await stdin.WriteAsync(standardInput, cancellationToken).ConfigureAwait(false);
            await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        // Closing stdin signals EOF so a process reading pipe:0 can finish.
    }

    private static void TryKill(Process process)
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
            // Process already exited between the check and the kill — nothing to do.
        }
    }
}
