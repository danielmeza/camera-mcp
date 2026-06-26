using CameraMcp.Server.Configuration;
using CameraMcp.Server.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CameraMcp.Server.Services;

/// <summary>Outcome of a recording: the file size on disk and an optional inline poster frame.</summary>
public sealed record RecordedVideo(long FileSizeBytes, byte[]? PosterFrame);

/// <summary>Records a fixed-duration clip from a device and extracts a poster frame.</summary>
public interface IVideoRecorder
{
    Task<RecordedVideo> RecordAsync(
        IReadOnlyList<string> inputArguments,
        VideoCaptureOptions options,
        string outputPath,
        CancellationToken cancellationToken);
}

/// <summary>
/// Drives ffmpeg to capture and encode a clip to <paramref name="outputPath"/>, then runs a second,
/// quick ffmpeg pass to pull the first frame as a JPEG poster the agent can view.
/// </summary>
public sealed class FFmpegVideoRecorder : IVideoRecorder
{
    private readonly IFFmpegLocator _locator;
    private readonly IProcessRunner _runner;
    private readonly CameraMcpOptions _options;
    private readonly ILogger<FFmpegVideoRecorder> _logger;

    public FFmpegVideoRecorder(
        IFFmpegLocator locator,
        IProcessRunner runner,
        IOptions<CameraMcpOptions> options,
        ILogger<FFmpegVideoRecorder> logger)
    {
        _locator = locator;
        _runner = runner;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<RecordedVideo> RecordAsync(
        IReadOnlyList<string> inputArguments,
        VideoCaptureOptions options,
        string outputPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputArguments);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var ffmpeg = _locator.Resolve();
        var args = FFmpegArguments.BuildVideoCaptureArgs(inputArguments, options, outputPath);

        // Allow the full recording plus encode headroom before giving up.
        var timeout = TimeSpan.FromSeconds(options.DurationSeconds + _options.FFmpegTimeoutSeconds);

        var result = await _runner.RunAsync(ffmpeg, args, standardInput: null, timeout, cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new CaptureFailedException($"Video capture failed. {FFmpegError.Tail(result.StandardError)}");
        }

        if (!File.Exists(outputPath))
        {
            throw new CaptureFailedException(
                $"Video capture reported success but no file was written to '{outputPath}'.");
        }

        var fileSize = new FileInfo(outputPath).Length;
        var poster = await TryExtractPosterAsync(ffmpeg, outputPath, cancellationToken).ConfigureAwait(false);

        return new RecordedVideo(fileSize, poster);
    }

    private async Task<byte[]?> TryExtractPosterAsync(string ffmpeg, string outputPath, CancellationToken cancellationToken)
    {
        try
        {
            var args = FFmpegArguments.BuildPosterFrameArgs(outputPath);
            var timeout = TimeSpan.FromSeconds(_options.FFmpegTimeoutSeconds);
            var result = await _runner.RunAsync(ffmpeg, args, standardInput: null, timeout, cancellationToken).ConfigureAwait(false);

            if (result.ExitCode == 0 && result.StandardOutput.Length > 0)
            {
                return result.StandardOutput;
            }

            _logger.LogWarning("Poster frame extraction returned no image: {Error}", FFmpegError.Tail(result.StandardError));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A missing poster is non-fatal: the recording itself succeeded.
            _logger.LogWarning(ex, "Poster frame extraction failed for {OutputPath}.", outputPath);
        }

        return null;
    }
}
