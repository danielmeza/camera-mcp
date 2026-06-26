using CameraMcp.Server.Configuration;
using CameraMcp.Server.Models;
using Microsoft.Extensions.Options;

namespace CameraMcp.Server.Services;

/// <summary>Grabs a single still directly from a device (with warmup) and writes it to disk.</summary>
public interface IStillCapturer
{
    /// <summary>
    /// Captures one frame using the supplied device input arguments, encodes it per
    /// <paramref name="options"/>, writes it to <paramref name="outputPath"/>, and returns its bytes.
    /// </summary>
    Task<byte[]> CaptureAsync(
        IReadOnlyList<string> inputArgs,
        ImageCaptureOptions options,
        string outputPath,
        CancellationToken cancellationToken);
}

/// <summary>
/// Captures a still by driving ffmpeg against the device, discarding a few warmup frames so the
/// result is a settled image rather than a cold black frame.
/// </summary>
public sealed class FFmpegStillCapturer : IStillCapturer
{
    private readonly IFFmpegLocator _locator;
    private readonly IProcessRunner _runner;
    private readonly CameraMcpOptions _options;

    public FFmpegStillCapturer(IFFmpegLocator locator, IProcessRunner runner, IOptions<CameraMcpOptions> options)
    {
        _locator = locator;
        _runner = runner;
        _options = options.Value;
    }

    public async Task<byte[]> CaptureAsync(
        IReadOnlyList<string> inputArgs,
        ImageCaptureOptions options,
        string outputPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputArgs);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var ffmpeg = _locator.Resolve();
        var args = FFmpegArguments.BuildImageCaptureArgs(
            inputArgs, options.Format, options.Quality, _options.ImageWarmupFrames, outputPath);
        var timeout = TimeSpan.FromSeconds(_options.FFmpegTimeoutSeconds);

        var result = await _runner.RunAsync(ffmpeg, args, standardInput: null, timeout, cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0 || !File.Exists(outputPath))
        {
            throw new CaptureFailedException(
                $"Image capture from the device failed. {FFmpegError.Tail(result.StandardError)}");
        }

        return await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);
    }
}
